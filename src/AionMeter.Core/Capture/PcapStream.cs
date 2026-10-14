using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using LinkLayers = PacketDotNet.LinkLayers;
using SharpPcap;

namespace AionMeter.Core.Capture;

/// <summary>A2M-27: libpcap's file format, the same bytes --record writes, read and written without SharpPcap (the receiving PC may have no Npcap).</summary>
public static class PcapFormat
{
    public const int GlobalHeaderBytes = 24;
    public const int RecordHeaderBytes = 16;
    public const uint Magic = 0xA1B2C3D4;
    public const uint MagicNanos = 0xA1B23C4D;
    public const int SnapLen = 262_144;

    public static void WriteGlobalHeader(Span<byte> d, int linkType)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(d[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 4);
        BinaryPrimitives.WriteUInt64LittleEndian(d[8..], 0);   // thiszone + sigfigs
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], SnapLen);
        BinaryPrimitives.WriteUInt32LittleEndian(d[20..], (uint)linkType);
    }

    public static void WriteRecordHeader(Span<byte> d, uint seconds, uint micros, int capLen, int origLen)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, seconds);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], micros);
        BinaryPrimitives.WriteUInt32LittleEndian(d[8..], (uint)capLen);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], (uint)origLen);
    }

    /// <summary>False when the bytes are not a pcap global header (either byte order, µs or ns timestamps).</summary>
    public static bool TryReadGlobalHeader(ReadOnlySpan<byte> h, out bool bigEndian, out bool nanos, out int linkType)
    {
        bigEndian = false;
        nanos = false;
        linkType = 0;
        var m = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (m == MagicNanos) nanos = true;
        else if (m != Magic)
        {
            m = BinaryPrimitives.ReadUInt32BigEndian(h);
            if (m == MagicNanos) nanos = true;
            else if (m != Magic) return false;
            bigEndian = true;
        }
        linkType = (int)(Read32(h[20..], bigEndian) & 0x0FFF_FFFF);   // the top bits carry FCS info
        return true;
    }

    public static uint Read32(ReadOnlySpan<byte> s, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
}

/// <summary>A2M-27: "tcp://host:port" (capture source of a receiving meter) and "ip:port" (the address a forwarding daemon listens on).</summary>
public static class CaptureEndpoint
{
    public const string Scheme = "tcp://";

    public static bool IsStream(string? source) => source is not null && source.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>"tcp://host:port" or "host:port"; an IPv6 literal goes in brackets ("[fd00::2]:47013").</summary>
    public static bool TryParse(string? text, out string host, out int port) => TryParse(text, false, out host, out port);

    private static bool TryParse(string? text, bool anyPort, out string host, out int port)
    {
        host = "";
        port = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.AsSpan().Trim();
        if (t.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) t = t[Scheme.Length..];
        t = t.TrimEnd('/');
        var colon = t.LastIndexOf(':');
        if (colon <= 0 || colon == t.Length - 1) return false;
        var h = t[..colon];
        if (h.Length > 2 && h[0] == '[' && h[^1] == ']') h = h[1..^1];
        else if (h.Contains(':')) return false;
        if (!int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < (anyPort ? 0 : 1) || port > 65535) return false;
        host = h.ToString();
        return true;
    }

    /// <summary>The forwarder's bind address: an IP literal and a port, never a name — the address Diego picks for the LAN/VM side. Port 0 = any (tests).</summary>
    public static bool TryParseBind(string? text, out IPEndPoint endPoint)
    {
        endPoint = null!;
        if (!TryParse(text, true, out var host, out var port) || !IPAddress.TryParse(host, out var ip)) return false;
        endPoint = new IPEndPoint(ip, port);
        return true;
    }

    /// <summary>"host:port" when <paramref name="source"/> is a usable "tcp://host:port", else null (the capture source is this PC's adapters).</summary>
    public static string? StreamEndpoint(string? source) => IsStream(source) && TryParse(source, out var host, out var port) ? Format(host, port) : null;

    public static string Format(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
}

/// <summary>
/// A2M-27: the Linux daemon's --forward. Listens on one TCP address and streams the captured game packets to every client as a pcap stream
/// (global header once, then record header + frame per packet), so a Windows meter in a VM or under Wine reads the game without Npcap.
/// Plaintext game traffic, no authentication: it binds only the address it is given. Passive: nothing goes towards the game.
/// <see cref="Publish(RawCapture)"/> runs on the capture thread and never blocks it: each client has its own queue capped at
/// <see cref="DefaultQueueBytes"/>, and a client that falls that far behind is dropped (it reconnects and resyncs) instead of slowing capture.
/// </summary>
public sealed class PcapForwarder : IDisposable
{
    // Same size as the capture's kernel buffer. Measured 2026-10-14 on the login fixture: 50 pkt/s and 25 KB/s on average, 84 pkt/s and
    // 42 KB/s in its busiest second, so this holds minutes of normal play and still seconds of a burst a hundred times busier.
    public const int DefaultQueueBytes = 16 * 1024 * 1024;
    public const int MaxClients = 4;
    private const int SendBufferBytes = 64 * 1024;

    private readonly IPEndPoint _bind;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly int _queueBytes;
    private readonly Task _accept;
    private readonly TaskCompletionSource<IPEndPoint> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Client[] _clients = [];
    private bool _disposed;
    private long _forwarded;
    private long _droppedSlow;

    /// <summary>Never throws on a bind failure: a VM bridge address (virbr0) does not exist before libvirtd brings it up, so the bind is retried
    /// every <see cref="BindRetryMs"/> and <see cref="Note"/> says why once.</summary>
    public PcapForwarder(IPEndPoint bind, int queueBytes = DefaultQueueBytes, Action<string>? note = null)
    {
        _bind = bind;
        _queueBytes = queueBytes;
        Note = note;
        _listener = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _accept = Task.Run(AcceptLoop);
    }

    public const int BindRetryMs = 5_000;
    public IPEndPoint Bind => _bind;
    /// <summary>Completes with the bound address (the real port when 0 was asked) once listening.</summary>
    public Task<IPEndPoint> Listening => _listening.Task;
    public int ClientCount => Volatile.Read(ref _clients).Length;
    /// <summary>Packets handed to at least one client.</summary>
    public long Forwarded => Interlocked.Read(ref _forwarded);
    /// <summary>Clients cut because their queue passed the cap.</summary>
    public long DroppedSlow => Interlocked.Read(ref _droppedSlow);
    /// <summary>Listen / connect / disconnect / drop lines (the daemon logs them).</summary>
    public Action<string>? Note { get; set; }

    public void Publish(RawCapture raw)
    {
        var t = raw.Timeval;
        Publish((int)raw.LinkLayerType, (uint)t.Seconds, (uint)t.MicroSeconds, raw.Data, raw.PacketLength);
    }

    /// <summary>The frame array is queued as is, not copied: the caller must not reuse it (SharpPcap's GetPacket hands out a fresh one).</summary>
    public void Publish(int linkType, uint seconds, uint micros, byte[] data, int origLen)
    {
        var clients = Volatile.Read(ref _clients);
        if (clients.Length == 0) return;
        var frame = new Frame(data, seconds, micros, origLen < data.Length ? data.Length : origLen, linkType);
        for (var i = 0; i < clients.Length; i++)
        {
            var c = clients[i];
            if (c.Dropping != 0 || c.TryEnqueue(frame)) continue;
            // closing a socket took up to 2.7 ms (measured 2026-10-14): not on the capture thread
            if (Interlocked.Exchange(ref c.Dropping, 1) != 0) continue;
            Interlocked.Increment(ref _droppedSlow);
            ThreadPool.UnsafeQueueUserWorkItem(static s => s.Self.Close(s.Client, s.Why),
                (Self: this, Client: c, Why: $"dropped: more than {_queueBytes / 1024} KB queued (client too slow)"), preferLocal: false);
        }
        Interlocked.Increment(ref _forwarded);
    }

    private async Task<bool> ListenAsync()
    {
        string? reported = null;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _listener.Bind(_bind);
                _listener.Listen(MaxClients);
                var at = (IPEndPoint)_listener.LocalEndPoint!;
                Note?.Invoke($"forward: listening on {at} (plaintext pcap stream of the game traffic, no authentication)");
                _listening.TrySetResult(at);
                return true;
            }
            catch (SocketException ex)
            {
                if (ex.Message != reported) Note?.Invoke($"forward: cannot listen on {_bind}: {ex.Message}; retrying every {BindRetryMs / 1000} s");
                reported = ex.Message;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            try { await Task.Delay(BindRetryMs, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        _listening.TrySetCanceled();
        return false;
    }

    private async Task AcceptLoop()
    {
        if (!await ListenAsync().ConfigureAwait(false)) return;
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try
            {
                s = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (_disposed) break;
                Note?.Invoke("forward: accept failed: " + ex.Message);
                try { await Task.Delay(1_000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                continue;
            }
            s.NoDelay = true;
            var name = s.RemoteEndPoint?.ToString() ?? "?";
            Client c;
            lock (_gate)
            {
                if (_disposed || _clients.Length >= MaxClients)
                {
                    s.Dispose();
                    Note?.Invoke($"forward: refused {name} ({MaxClients} clients already)");
                    continue;
                }
                c = new Client(s, name, _queueBytes);
                _clients = [.. _clients, c];
            }
            Note?.Invoke($"forward: {name} connected");
            c.Watch = Task.Run(() => WatchAsync(c));
            c.Pump = Task.Run(() => PumpAsync(c));
        }
    }

    private async Task PumpAsync(Client c)
    {
        var buf = new byte[SendBufferBytes];
        var n = 0;
        var link = -1;
        var reader = c.Queue.Reader;
        var ct = _cts.Token;
        try
        {
            using var stream = new NetworkStream(c.Socket, ownsSocket: false);
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var f))
                {
                    var len = f.Data.Length;
                    Interlocked.Add(ref c.QueuedBytes, -(len + PcapFormat.RecordHeaderBytes));
                    if (link < 0)
                    {
                        // the header waits for the first frame: its link type is the open adapter's
                        link = f.LinkType;
                        PcapFormat.WriteGlobalHeader(buf, link);
                        n = PcapFormat.GlobalHeaderBytes;
                    }
                    else if (f.LinkType != link)
                    {
                        Close(c, "closed: the capture moved to an adapter of another link type (the client reconnects for a new header)");
                        return;
                    }
                    if (n + PcapFormat.RecordHeaderBytes + len > buf.Length)
                    {
                        await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        n = 0;
                    }
                    PcapFormat.WriteRecordHeader(buf.AsSpan(n), f.Seconds, f.Micros, len, f.OrigLen);
                    n += PcapFormat.RecordHeaderBytes;
                    if (len > buf.Length - n)
                    {
                        await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        n = 0;
                        await stream.WriteAsync(f.Data, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        f.Data.CopyTo(buf, n);
                        n += len;
                    }
                }
                if (n > 0)
                {
                    await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    n = 0;
                }
            }
            // queue completed and drained (Dispose): FIN after the last byte, so the reader sees a clean end. The socket is disposed only once the
            // reader closed too (or 2 s): disposing it with the watcher's receive pending is an abortive close on Linux, and the RST threw away
            // the unsent tail ("Connection reset by peer" in the reader, measured 2026-10-14).
            c.Socket.Shutdown(SocketShutdown.Send);
            if (c.Watch is { } watch) await Task.WhenAny(watch, Task.Delay(2_000)).ConfigureAwait(false);
            Close(c, null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Close(c, ct.IsCancellationRequested ? null : "connection lost: " + ex.Message);
        }
    }

    // The client never sends: a read that returns is its FIN (or an error). Without this a client that left while no game traffic
    // flowed would hold its slot until the next packet.
    private async Task WatchAsync(Client c)
    {
        var one = new byte[1];
        try
        {
            while (await c.Socket.ReceiveAsync(one, SocketFlags.None, _cts.Token).ConfigureAwait(false) > 0) { }
            Close(c, "disconnected");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Close(c, _cts.IsCancellationRequested ? null : "disconnected");
        }
    }

    private void Close(Client c, string? why)
    {
        if (Interlocked.Exchange(ref c.Closed, 1) != 0) return;
        lock (_gate)
        {
            var i = Array.IndexOf(_clients, c);
            if (i >= 0)
            {
                var next = new Client[_clients.Length - 1];
                Array.Copy(_clients, 0, next, 0, i);
                Array.Copy(_clients, i + 1, next, i, next.Length - i);
                _clients = next;
            }
        }
        c.Queue.Writer.TryComplete();
        c.Socket.Dispose();
        if (why is not null) Note?.Invoke($"forward: {c.Name} {why}");
    }

    public void Dispose()
    {
        Client[] clients;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            clients = _clients;
        }
        _listener.Dispose();
        // what is queued still goes out (the end of a forwarded replay), for up to 2 s; then every socket is cut
        var pumps = new Task[clients.Length];
        for (var i = 0; i < clients.Length; i++)
        {
            clients[i].Queue.Writer.TryComplete();
            pumps[i] = clients[i].Pump ?? Task.CompletedTask;
        }
        try { Task.WaitAll(pumps, 2_000); } catch (AggregateException) { }
        _cts.Cancel();
        foreach (var c in clients) Close(c, null);
        try { _accept.Wait(1_000); } catch (AggregateException) { }
        _cts.Dispose();
    }

    private readonly record struct Frame(byte[] Data, uint Seconds, uint Micros, int OrigLen, int LinkType);

    private sealed class Client(Socket socket, string name, int capBytes)
    {
        public readonly Socket Socket = socket;
        public readonly string Name = name;
        // unbounded channel + byte cap: the cap is what matters (frames vary 60–1500 B), and TryWrite never fails on count
        public readonly Channel<Frame> Queue = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        public long QueuedBytes;
        public int Closed;
        public int Dropping;
        public Task? Pump;
        public Task? Watch;

        public bool TryEnqueue(in Frame f)
        {
            if (Interlocked.Add(ref QueuedBytes, f.Data.Length + PcapFormat.RecordHeaderBytes) > capBytes) return false;
            return Queue.Writer.TryWrite(f) || Volatile.Read(ref Closed) != 0;
        }
    }
}

/// <summary>
/// A2M-27: the capture source of a meter that receives another meter's --forward stream (a Windows meter in a VM or under Wine, where the
/// game is not running and Npcap may be missing). Reads the pcap stream without SharpPcap and feeds the same pipeline as live capture.
/// Reconnects with backoff (1 s doubling to 30 s, back to 1 s after a connection that delivered packets).
/// </summary>
public sealed class PcapStreamSource : IEventSource, ICaptureHealthSource
{
    private const int ReadBufferBytes = 64 * 1024;
    // above this the receiving PC's clock (or a forwarded replay) makes live idle checks close fights early: worth one log line
    private const long SkewNoteMs = 5_000;

    private readonly string _host;
    private readonly int _port;
    private readonly bool _reconnect;
    private readonly PacketPipeline _pipeline;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private CaptureStatus _status;
    private long _packets;
    private long _events;
    private long _statusAt;

    /// <param name="reconnect">False: one connection, stop at its end (CLI, tests).</param>
    public PcapStreamSource(GameData data, string host, int port, bool reconnect = true)
    {
        _host = host;
        _port = port;
        _reconnect = reconnect;
        Endpoint = CaptureEndpoint.Format(host, port);
        _pipeline = new PacketPipeline(data, OnEvent) { LockThreshold = 3 };   // the stream is already filtered to the game, as live capture is
        _status = new CaptureStatus(CaptureState.Stopped, "Capture stopped");
    }

    /// <summary>Null when <paramref name="source"/> is not "tcp://host:port".</summary>
    public static PcapStreamSource? From(GameData data, string? source, bool reconnect = true) =>
        CaptureEndpoint.IsStream(source) && CaptureEndpoint.TryParse(source, out var host, out var port) ? new PcapStreamSource(data, host, port, reconnect) : null;

    public event Action<GameEvent>? EventDecoded;
    public event Action<CaptureStatus>? StatusChanged;

    public string Endpoint { get; }

    /// <summary>Like live capture: "Capturing" turns into a waiting line after 10 s without a packet (the stream stays open while the game is closed).</summary>
    public CaptureStatus Status =>
        _status.State == CaptureState.Capturing && Environment.TickCount64 - Volatile.Read(ref _lastPacketAt) > 10_000
            ? _idle ??= new CaptureStatus(CaptureState.WaitingForGame, $"Connected to {Endpoint} · no game traffic for a while", Packets, _events)
            : _status;
    private CaptureStatus? _idle;
    private long _lastPacketAt;
    public PacketPipeline Pipeline => _pipeline;
    public long Packets => Interlocked.Read(ref _packets);
    /// <summary>Connections that got through (tests: reconnect).</summary>
    public int Connections { get; private set; }
    public int BackoffMinMs { get; init; } = 1_000;
    public int BackoffMaxMs { get; init; } = 30_000;
    public int ConnectTimeoutMs { get; init; } = 5_000;
    /// <summary>Packets whose handling threw (their records were lost).</summary>
    public int Faults { get; private set; }
    public Exception? LastFault { get; private set; }

    /// <summary>Dropped is always 0: the forwarder cuts a slow client instead of dropping packets, and the reconnect shows up as a gap.</summary>
    public CaptureHealth? Health() => new CaptureHealth(Packets, 0, _pipeline.Gaps);

    public void Start()
    {
        if (_task is not null) return;
        _cts = new CancellationTokenSource();
        SetStatus(CaptureState.Starting, $"Connecting to the forwarding meter at {Endpoint}…");
        var ct = _cts.Token;
        _task = Task.Run(() => RunAsync(ct));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(2_000); } catch (AggregateException) { }
        _task = null;
        SetStatus(CaptureState.Stopped, "Capture stopped");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    /// <summary>Synchronous (CLI / tests): with reconnect off, one connection until the forwarder closes it. Returns the packets read.</summary>
    public long RunToEnd()
    {
        RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        return Packets;
    }

    internal static int Backoff(int failures, int minMs, int maxMs) =>
        failures <= 0 ? minMs : (int)Math.Min(maxMs, (long)minMs << Math.Min(failures, 20));

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var before = Packets;
            string why;
            try
            {
                why = await ReceiveAsync(ct).ConfigureAwait(false) ? "the forwarding meter closed the stream" : "not a pcap stream";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException)
            {
                why = ex.Message;
            }
            if (!_reconnect)
            {
                SetStatus(CaptureState.Stopped, $"Forwarded capture ended · {Packets} packets ({why})");
                return;
            }
            failures = Packets > before ? 0 : failures + 1;
            var wait = Backoff(failures, BackoffMinMs, BackoffMaxMs);
            SetStatus(CaptureState.WaitingForGame, string.Create(CultureInfo.InvariantCulture, $"Forwarded capture: {why} · retrying {Endpoint} in {wait / 1000.0:0.#} s"));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One connection. True at a clean end of stream, false when the peer does not speak pcap.</summary>
    private async Task<bool> ReceiveAsync(CancellationToken ct)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(ConnectTimeoutMs);
            try
            {
                await socket.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SocketException((int)SocketError.TimedOut);
            }
        }
        // a VM suspended or a cable pulled leaves a half-open connection that no read would ever end
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        Connections++;
        SetStatus(CaptureState.WaitingForGame, $"Connected to {Endpoint} · waiting for game traffic");

        await using var stream = new BufferedStream(new NetworkStream(socket, ownsSocket: false), ReadBufferBytes);
        var hdr = new byte[PcapFormat.GlobalHeaderBytes];
        if (await stream.ReadAtLeastAsync(hdr, hdr.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false) < hdr.Length)
            return true;   // closed before the first packet (the forwarder shut down while idle)
        if (!PcapFormat.TryReadGlobalHeader(hdr, out var be, out var nanos, out var linkType)) return false;
        var link = (LinkLayers)linkType;
        var rec = hdr.AsMemory(0, PcapFormat.RecordHeaderBytes);
        var first = true;
        while (true)
        {
            var got = await stream.ReadAtLeastAsync(rec, rec.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (got == 0) return true;
            if (got < rec.Length) throw new EndOfStreamException($"{Endpoint} closed in the middle of a packet header");
            var sec = PcapFormat.Read32(hdr, be);
            var frac = PcapFormat.Read32(hdr.AsSpan(4), be);
            var cap = PcapFormat.Read32(hdr.AsSpan(8), be);
            if (cap > PcapFormat.SnapLen) throw new InvalidDataException($"{Endpoint}: packet of {cap} bytes, the stream is corrupt");
            var data = new byte[cap];
            await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            var ms = (long)sec * 1000 + (nanos ? frac / 1_000_000 : frac / 1000);
            Interlocked.Increment(ref _packets);
            var now = Environment.TickCount64;
            Volatile.Write(ref _lastPacketAt, now);
            if (first)
            {
                first = false;
                var skew = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms;
                if (Math.Abs(skew) > SkewNoteMs)
                    LiveCapture.Diag($"forwarded capture: packet clock {skew / 1000} s behind this PC's (another clock, or the forwarder replays a recording)");
            }
            Handle(link, ms, data);
            if (now - _statusAt >= 1_000)
            {
                _statusAt = now;
                if (_pipeline.GameFlows > 0) SetStatus(CaptureState.Capturing, $"Capturing · forwarded from {Endpoint}");
            }
        }
    }

    private void Handle(LinkLayers link, long ms, byte[] data)
    {
        try
        {
            if (SegmentReader.TryRead(link, ms, data, out var seg))
                _pipeline.OnSegment(seg.TimeMs, seg.Key, seg.Seq, seg.Syn, seg.FinOrRst, seg.Payload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Faults++;
            LastFault = ex;
        }
    }

    private void OnEvent(GameEvent e)
    {
        _events++;
        EventDecoded?.Invoke(e);
    }

    private void SetStatus(CaptureState state, string message)
    {
        var s = new CaptureStatus(state, message, Interlocked.Read(ref _packets), _events);
        _status = s;
        _idle = null;
        StatusChanged?.Invoke(s);
    }
}
