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

/// <summary>The capture's one-off diagnostic lines. Outside LiveCapture so the forwarded-stream source (A2M-27) never loads a type that holds
/// SharpPcap devices: under Wine there is no wpcap.dll at all.</summary>
public static class CaptureLog
{
    /// <summary>Null = stderr with a HH:mm:ss prefix (the journal for the Linux daemon, nowhere for the WPF app).</summary>
    public static Action<string>? Sink { get; set; }

    public static void Write(string line)
    {
        if (Sink is { } d) d(line);
        else Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
    }
}
