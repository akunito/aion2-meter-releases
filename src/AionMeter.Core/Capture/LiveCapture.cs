using System.Net;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace AionMeter.Core.Capture;

public sealed class LiveCaptureOptions
{
    /// <summary>Npcap device name to force (e.g. a VPN adapter). Null = pick automatically from the game's connections.</summary>
    public string? DeviceName { get; set; }

    /// <summary>When set, every captured game packet is also written to a .pcap file in this folder.</summary>
    public string? RecordDirectory { get; set; }
    /// <summary>Size budget for <see cref="RecordDirectory"/> (CaptureRetention): oldest recordings go first; 0 = unlimited.</summary>
    public long RecordKeepBytes { get; set; }

    /// <summary>Whether the capture library can be loaded; null = <see cref="CaptureAvailability.DefaultProbe"/>. The app passes its
    /// <see cref="CaptureAvailability"/>, tests a stub. While it answers false the monitor never touches SharpPcap (A2M-27).</summary>
    public Func<bool>? LibraryPresent { get; set; }

    /// <summary>How often the monitor re-checks for the library while it is missing (an install during the session is picked up without a restart).</summary>
    public int LibraryPollMs { get; set; } = 10_000;

    /// <summary>A2M-27: every captured game packet is also handed to this forwarder (the Linux daemon's --forward, read by a Windows meter).</summary>
    public PcapForwarder? Forward { get; set; }
}

/// <summary>
/// Passive capture of the AION 2 connection through Npcap. It looks up the game's own TCP connections in the
/// Windows TCP table, opens only the adapter that carries them (the loopback adapter when a ping accelerator relays
/// the game locally) and filters to those servers. Nothing is ever sent or injected.
/// </summary>
public sealed class LiveCapture : IEventSource, ICaptureHealthSource
{
    private readonly LiveCaptureOptions _options;
    private readonly PacketPipeline _pipeline;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _monitor;
    private LibPcapLiveDevice? _device;
    private string? _filter;
    private CaptureFileWriterDevice? _writer;
    private long _packets;
    private long _events;
    private CaptureStatus _status = new(CaptureState.Stopped, "Capture stopped");

    public LiveCapture(GameData data, LiveCaptureOptions options)
    {
        _options = options;
        _pipeline = new PacketPipeline(data, OnEvent) { LockThreshold = 3 };
    }

    public event Action<GameEvent>? EventDecoded;
    public event Action<CaptureStatus>? StatusChanged;

    public CaptureStatus Status => _status;
    public PacketPipeline Pipeline => _pipeline;
    public string? RecordingPath { get; private set; }

    public static bool IsNpcapInstalled() => CaptureAvailability.IsNpcapInstalled();

    private const int KernelBufferBytes = 16 * 1024 * 1024;

    /// <summary>Packets Npcap delivered and dropped on the open adapter, and holes in the game streams; null while no
    /// adapter is open. The adapter's counters restart when it is reopened.</summary>
    public CaptureHealth? Health()
    {
        lock (_gate)
        {
            if (_device is null) return null;
            try
            {
                var s = _device.Statistics;
                return new CaptureHealth(s.ReceivedPackets, s.DroppedPackets + s.InterfaceDroppedPackets, _pipeline.Gaps);
            }
            catch (PcapException)
            {
                return null;
            }
        }
    }

    /// <summary>The capture adapters; empty without the library (SharpPcap is then never called) or when it fails to load.</summary>
    public static IReadOnlyList<(string Name, string Description)> ListDevices(Func<bool>? libraryPresent = null)
    {
        if (!(libraryPresent ?? CaptureAvailability.DefaultProbe)()) return [];
        try
        {
            var list = new List<(string, string)>();
            foreach (var d in CaptureDeviceList.Instance)
                if (d is LibPcapLiveDevice live) list.Add((live.Name, Describe(live)));
            return list;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException or PcapException)
        {
            return [];
        }
    }

    public void Start()
    {
        if (_monitor is not null) return;
        _cts = new CancellationTokenSource();
        SetStatus(CaptureState.Starting, "Starting capture…");
        _monitor = Task.Run(() => MonitorLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _monitor?.Wait(2_000); } catch (AggregateException) { }
        _monitor = null;
        CloseDevice();
        SetStatus(CaptureState.Stopped, "Capture stopped");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    public static readonly string NpcapMissingMessage = CaptureAvailability.NoCaptureStatus;

    /// <summary>One-off diagnostic lines (why the library failed to load, which path picked the adapter). Null = stderr with a
    /// HH:mm:ss prefix, which lands in the journal for the Linux daemon and nowhere for the WPF app. A host may point it at its log.</summary>
    public static Action<string>? Diagnostic { get; set; }

    internal static void Diag(string line)
    {
        if (Diagnostic is { } d) d(line);
        else Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
    }

    /// <summary>The loader's own reason the capture library failed (dlopen's message on Linux); null until it fails.</summary>
    public string? LibraryLoadError { get; private set; }

    // A2M-48: the status line alone said "Npcap missing" while the real cause (no libpcap.so on LD_LIBRARY_PATH) sat in the
    // exception; the TypeInitializationException then repeats every 2 s, so the reason is logged once per distinct message.
    private void ReportLoadFailure(Exception ex)
    {
        SetStatus(CaptureState.Error, NpcapMissingMessage);
        var msg = LoadFailureReason(ex);
        if (msg == LibraryLoadError) return;
        LibraryLoadError = msg;
        Diag("capture library failed to load: " + msg);
    }

    internal static string LoadFailureReason(Exception ex)
    {
        var e = ex;
        while (e is TypeInitializationException && e.InnerException is { } inner) e = inner;
        return $"{e.GetType().Name}: {e.Message}";
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        // Without the library SharpPcap's static initialiser fails once and the TypeInitializationException then repeats for the life of
        // the process (an install mid-session needed a restart). So the DLL is checked first and SharpPcap is left alone until it exists.
        var present = _options.LibraryPresent ?? CaptureAvailability.DefaultProbe;
        while (!ct.IsCancellationRequested)
        {
            if (!present())
            {
                if (_status.State != CaptureState.Error || _status.Message != NpcapMissingMessage) SetStatus(CaptureState.Error, NpcapMissingMessage);
                try { await Task.Delay(_options.LibraryPollMs, ct).ConfigureAwait(false); } catch (TaskCanceledException) { break; }
                continue;
            }
            try
            {
                Refresh();
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
            {
                ReportLoadFailure(ex);
            }
            catch (PcapException ex)
            {
                SetStatus(CaptureState.Error, "Capture error: " + ex.Message);
                CloseDevice();
            }
            try
            {
                await Task.Delay(2_000, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void Refresh()
    {
        if (_options.DeviceName is { Length: > 0 } forced)
        {
            var dev = CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>().FirstOrDefault(d => d.Name == forced);
            if (dev is null)
            {
                SetStatus(CaptureState.Error, "Selected network adapter not found");
                return;
            }
            NoteAdapterPath(dev, "forced by --device / settings CaptureDevice");
            EnsureDevice(dev, "tcp");
            ReportFlowStatus(dev);
            return;
        }

        var pids = GameProcessLocator.FindGameProcessIds();
        if (pids.Length == 0)
        {
            if (TryPortFallback()) return;
            CloseDevice();
            SetStatus(CaptureState.WaitingForGame, "AION 2 is not running");
            return;
        }

        var connections = GameProcessLocator.FindConnections(pids);
        if (connections.Count == 0)
        {
            if (TryPortFallback()) return;
            SetStatus(CaptureState.WaitingForGame, "Waiting for the game to connect…");
            return;
        }
        _locatorEmptySinceMs = 0;

        var devices = CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>().ToList();
        LibPcapLiveDevice? device = null;
        foreach (var c in connections.OrderByDescending(c => c.RemotePort == PacketPipeline.GamePort))
        {
            device = IPAddress.IsLoopback(c.RemoteIp) || IPAddress.IsLoopback(c.LocalIp)
                ? devices.FirstOrDefault(IsLoopback)
                : devices.FirstOrDefault(d => d.Addresses.Any(a => c.LocalIp.Equals(a.Addr?.ipAddress)));
            if (device is not null) break;
        }
        if (device is null)
        {
            SetStatus(CaptureState.Error, "No Npcap adapter carries the game connection (VPN? pick one in Settings)");
            return;
        }

        NoteAdapterPath(device, "from the game process's connections", $" (pid {string.Join(",", pids)})");
        EnsureDevice(device, HostFilter(connections));
        ReportFlowStatus(device);
    }

    private static string HostFilter(List<GameConnection> connections) =>
        "tcp and (" + string.Join(" or ", connections.Select(c => $"(host {c.RemoteIp} and port {c.RemotePort})").Distinct().OrderBy(s => s)) + ")";

    /// <summary>How long the locator must find nothing before the Linux port fallback looks at /proc/net/tcp.</summary>
    internal const int PortFallbackAfterMs = 30_000;
    private long _locatorEmptySinceMs;
    private string? _adapterPath;

    // A2M-48: on Linux the pid locator misses a game in its own pid namespace (pressure-vessel --unshare-pid, gamescope wrapper with
    // GAMESCOPE_UNSHARE_PID=1) or one it cannot read /proc/<pid>/fd of; the network namespace is still the host's, so its flows sit in
    // /proc/net/tcp. After 30 s of nothing, an adapter is taken when exactly one non-loopback interface carries ESTAB flows to 13328.
    private bool TryPortFallback()
    {
        if (!OperatingSystem.IsLinux()) return false;
        var now = Environment.TickCount64;
        if (_locatorEmptySinceMs == 0) { _locatorEmptySinceMs = now; return false; }
        if (now - _locatorEmptySinceMs < PortFallbackAfterMs) return false;
        string table;
        try { table = File.ReadAllText("/proc/net/tcp"); } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        var flows = GameProcessLocator.FlowsToPort(table, PacketPipeline.GamePort);
        if (flows.Count == 0) return false;
        var devices = CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>().ToList();
        LibPcapLiveDevice? device = null;
        for (var i = flows.Count - 1; i >= 0; i--)
        {
            var c = flows[i];
            var d = IPAddress.IsLoopback(c.LocalIp) || IPAddress.IsLoopback(c.RemoteIp) ? null
                : devices.FirstOrDefault(x => !IsLoopback(x) && x.Addresses.Any(a => c.LocalIp.Equals(a.Addr?.ipAddress)));
            if (d is null) { flows.RemoveAt(i); continue; }
            if (device is null) device = d;
            else if (device.Name != d.Name) return false;   // two adapters carry game flows: ambiguous, wait for the locator or --device
        }
        if (device is null) return false;
        NoteAdapterPath(device, $"by /proc/net/tcp flows to port {PacketPipeline.GamePort} (game process not visible for {PortFallbackAfterMs / 1000} s)");
        EnsureDevice(device, HostFilter(flows));
        ReportFlowStatus(device);
        return true;
    }

    // Logged when the adapter or the path changes; the pid list is left out of the key so a game relaunch does not re-log the same choice.
    private void NoteAdapterPath(LibPcapLiveDevice device, string how, string? detail = null)
    {
        var key = device.Name + " " + how;
        if (key == _adapterPath) return;
        _adapterPath = key;
        Diag($"capture adapter {device.Name} chosen {how}{detail}");
    }

    private void ReportFlowStatus(LibPcapLiveDevice device)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var flows = _pipeline.GameFlows;
        if (flows > 0 && now - _pipeline.LastGameDataMs < 10_000)
        {
            var server = _pipeline.LockedFlows.Select(f => f.Src.ToString()).FirstOrDefault();
            SetStatus(CaptureState.Capturing, $"Capturing · {server}" + (RecordingPath is null ? "" : " · REC"));
        }
        else if (flows > 0)
        {
            SetStatus(CaptureState.WaitingForGame, "Connected · no game traffic for a while");
        }
        else
        {
            SetStatus(CaptureState.WaitingForGame, $"Listening on {Describe(device)}…");
        }
    }

    private void EnsureDevice(LibPcapLiveDevice device, string filter)
    {
        lock (_gate)
        {
            if (_device is not null && _device.Name != device.Name) CloseDeviceLocked();
            if (_device is null)
            {
                device.OnPacketArrival += OnPacketArrival;
                // A world boss with a crowd floods the game connection: Npcap's default 1 MB kernel buffer overflows
                // in the bursts, and every packet it drops is a hole in the stream.
                device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 100, BufferSize = KernelBufferBytes });
                device.Filter = filter;
                _filter = filter;
                _device = device;
                OpenRecorder(device);
                device.StartCapture();
            }
            else if (_filter != filter)
            {
                _device.Filter = filter;
                _filter = filter;
            }
        }
    }

    private void OpenRecorder(LibPcapLiveDevice device)
    {
        if (_options.RecordDirectory is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        CaptureRetention.Prune(dir, _options.RecordKeepBytes, null);
        RecordingPath = Path.Combine(dir, $"aion2_{DateTime.Now:yyyyMMdd_HHmmss}.pcap");
        _writer = new CaptureFileWriterDevice(RecordingPath);
        _writer.Open(new DeviceConfiguration { LinkLayerType = device.LinkType });
        _nextPruneMs = Environment.TickCount64 + PruneEveryMs;
    }

    // a long session is one growing file: re-check the budget every 10 min from the capture thread (one directory listing, no allocation per packet)
    private const long PruneEveryMs = 600_000;
    private long _nextPruneMs;

    private void PruneRecordingsIfDue()
    {
        if (_writer is null || _options.RecordKeepBytes <= 0 || Environment.TickCount64 < _nextPruneMs) return;
        _nextPruneMs = Environment.TickCount64 + PruneEveryMs;
        CaptureRetention.Prune(_options.RecordDirectory!, _options.RecordKeepBytes, RecordingPath);
    }

    private void CloseDevice()
    {
        lock (_gate) CloseDeviceLocked();
    }

    private void CloseDeviceLocked()
    {
        if (_device is null) return;
        try
        {
            _device.StopCapture();
        }
        catch (PcapException)
        {
        }
        _device.OnPacketArrival -= OnPacketArrival;
        _device.Close();
        _device = null;
        _filter = null;
        _writer?.Close();
        _writer = null;
        RecordingPath = null;
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        var raw = e.GetPacket();
        Interlocked.Increment(ref _packets);
        try
        {
            if (_writer is not null) { _writer.Write(raw); PruneRecordingsIfDue(); }
            _options.Forward?.Publish(raw);
            if (SegmentReader.TryRead(raw, out var seg))
            {
                if (seg.Key.SrcPort == 13328 && seg.Payload.Length > 0) NoteServerPacket(seg.TimeMs);
                _pipeline.OnSegment(seg.TimeMs, seg.Key, seg.Seq, seg.Syn, seg.FinOrRst, seg.Payload);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A malformed packet must never take the capture thread down; but a handler bug here loses the packet's records, so count it.
            Faults++;
            LastFault = ex;
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>Packets whose handling threw (their records were lost); the status line shows it when non-zero.</summary>
    public int Faults { get; private set; }

    // ---- lag probe: the server streams ~30 packets/s while anything is in view; a silence ≥ SilenceMs is a stall somewhere
    // between the server and this NIC (the RTT measured on the same captures was 4–20 ms with no loss, 2026-10-09, so the
    // stalls are not the route). Each one is kept with its start and length; the service logs them.
    public const int SilenceMs = 400;
    private long _lastServerMs;
    private readonly Queue<(long StartMs, int Ms)> _silences = new();

    private void NoteServerPacket(long timeMs)
    {
        if (_lastServerMs > 0 && timeMs - _lastServerMs >= SilenceMs)
            lock (_silences) { _silences.Enqueue((_lastServerMs, (int)(timeMs - _lastServerMs))); if (_silences.Count > 200) _silences.Dequeue(); }
        _lastServerMs = timeMs;
    }

    /// <summary>Server silences recorded since the last call (start Unix ms, length ms).</summary>
    public List<(long StartMs, int Ms)> TakeSilences()
    {
        lock (_silences) { var l = _silences.ToList(); _silences.Clear(); return l; }
    }

    /// <summary>Milliseconds since the last server packet (0 before the first).</summary>
    public long ServerSilenceNowMs => _lastServerMs == 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastServerMs;
    public Exception? LastFault { get; private set; }

    private void OnEvent(GameEvent e)
    {
        _events++;
        EventDecoded?.Invoke(e);
    }

    private void SetStatus(CaptureState state, string message)
    {
        var s = new CaptureStatus(state, message, Interlocked.Read(ref _packets), _events);
        _status = s;
        StatusChanged?.Invoke(s);
    }

    private static bool IsLoopback(LibPcapLiveDevice d) =>
        d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
        (d.Description?.Contains("loopback", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Describe(LibPcapLiveDevice d) =>
        IsLoopback(d) ? "Loopback" : string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description!;
}

public readonly record struct CaptureHealth(long Received, long Dropped, long Gaps);

/// <summary>Link layer → TCP segment, shared by live capture and pcap replay.</summary>
public static class SegmentReader
{
    public readonly record struct Segment(long TimeMs, FlowKey Key, uint Seq, bool Syn, bool FinOrRst, byte[] Payload, uint? Ack = null);

    public static bool TryRead(RawCapture raw, out Segment segment)
    {
        var t = raw.Timeval;
        return TryRead(raw.LinkLayerType, (long)t.Seconds * 1000 + (long)t.MicroSeconds / 1000, raw.Data, out segment);
    }

    /// <summary>PacketDotNet only, no SharpPcap type: the forwarded stream (A2M-27) is read on PCs without Npcap, where touching SharpPcap throws.</summary>
    public static bool TryRead(LinkLayers link, long timeMs, byte[] data, out Segment segment)
    {
        segment = default;
        Packet packet;
        try
        {
            packet = Packet.ParsePacket(link, data);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or NotImplementedException)
        {
            return false;
        }
        var ip = packet.Extract<IPPacket>();
        var tcp = packet.Extract<TcpPacket>();
        if (ip is null || tcp is null) return false;

        var key = new FlowKey(ip.SourceAddress, tcp.SourcePort, ip.DestinationAddress, tcp.DestinationPort);
        segment = new Segment(timeMs, key, tcp.SequenceNumber, tcp.Synchronize, tcp.Finished || tcp.Reset, tcp.PayloadData ?? [], tcp.Acknowledgment ? tcp.AcknowledgmentNumber : null);
        return true;
    }
}
