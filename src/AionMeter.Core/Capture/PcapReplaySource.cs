using AionMeter.Core.Events;
using AionMeter.Core.Game;
using SharpPcap;
using SharpPcap.LibPcap;

namespace AionMeter.Core.Capture;

/// <summary>Feeds a recorded .pcap through the same pipeline as live capture — for testing parsers on real fights.</summary>
public sealed class PcapReplaySource : IEventSource
{
    private readonly string _path;
    private readonly bool _realtime;
    private readonly PacketPipeline _pipeline;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private CaptureStatus _status;

    private readonly Func<bool> _libraryPresent;

    /// <param name="libraryPresent">SharpPcap reads files through wpcap.dll too: without Npcap the replay reports <see cref="NeedsNpcapMessage"/>
    /// and never calls it. Null = <see cref="CaptureAvailability.DefaultProbe"/>.</param>
    public PcapReplaySource(GameData data, string path, bool realtime, Func<bool>? libraryPresent = null)
    {
        _path = path;
        _libraryPresent = libraryPresent ?? CaptureAvailability.DefaultProbe;
        _realtime = realtime;
        _pipeline = new PacketPipeline(data, e => EventDecoded?.Invoke(e));
        _status = new CaptureStatus(CaptureState.Stopped, "Replay ready");
    }

    public event Action<GameEvent>? EventDecoded;
    public event Action<CaptureStatus>? StatusChanged;
    public CaptureStatus Status => _status;
    public PacketPipeline Pipeline => _pipeline;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => Run(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Synchronous replay (CLI / tests). Returns the number of packets read.</summary>
    public long RunToEnd() => Run(CancellationToken.None);

    public const string NeedsNpcapMessage = "Replay needs Npcap (it reads .pcap files through it)";

    private long Run(CancellationToken ct)
    {
        // Stopped, not Error: the meter leaves replay mode on Stopped, and a source that never stops would keep it there.
        if (!_libraryPresent())
        {
            SetStatus(CaptureState.Stopped, NeedsNpcapMessage);
            return 0;
        }
        try
        {
            return Read(ct);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PcapException or IOException)
        {
            SetStatus(CaptureState.Stopped, "Replay failed: " + ex.Message);
            return 0;
        }
    }

    private long Read(CancellationToken ct)
    {
        long count = 0;
        SetStatus(CaptureState.Capturing, $"Replaying {Path.GetFileName(_path)}");
        using var reader = new CaptureFileReaderDevice(_path);
        reader.Open();
        long? firstPacketMs = null;
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && reader.GetNextPacket(out var e) == GetPacketStatus.PacketRead)
        {
            count++;
            var raw = e.GetPacket();
            if (!SegmentReader.TryRead(raw, out var seg)) continue;
            if (_realtime)
            {
                firstPacketMs ??= seg.TimeMs;
                var due = started.AddMilliseconds(seg.TimeMs - firstPacketMs.Value);
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            }
            _pipeline.OnSegment(seg.TimeMs, seg.Key, seg.Seq, seg.Syn, seg.FinOrRst, seg.Payload);
        }
        SetStatus(CaptureState.Stopped, $"Replay finished · {count} packets");
        return count;
    }

    private void SetStatus(CaptureState state, string message)
    {
        _status = new CaptureStatus(state, message);
        StatusChanged?.Invoke(_status);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _task?.Wait(1_000); } catch (AggregateException) { }
        _cts?.Dispose();
    }
}
