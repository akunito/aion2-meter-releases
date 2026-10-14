using AionMeter.Core.Events;

namespace AionMeter.Core.Capture;

public enum CaptureState
{
    Stopped,
    Starting,
    WaitingForGame,
    Capturing,
    Error,
}

public sealed record CaptureStatus(CaptureState State, string Message, long Packets = 0, long Events = 0)
{
    public static CaptureStatus Stopped { get; } = new(CaptureState.Stopped, "Capture stopped");
}

/// <summary>Anything that produces decoded game events: live Npcap capture, pcap replay, the demo fight.</summary>
public interface IEventSource : IDisposable
{
    event Action<GameEvent>? EventDecoded;
    event Action<CaptureStatus>? StatusChanged;
    CaptureStatus Status { get; }
    void Start();
    void Stop();
}

/// <summary>A source with capture counters: the live adapter (Npcap/libpcap) or the forwarded stream (A2M-27).</summary>
public interface ICaptureHealthSource
{
    CaptureHealth? Health();
}
