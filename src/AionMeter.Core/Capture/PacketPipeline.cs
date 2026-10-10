using System.Net;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Protocol;

namespace AionMeter.Core.Capture;

public readonly record struct FlowKey(IPAddress Src, ushort SrcPort, IPAddress Dst, ushort DstPort)
{
    public override string ToString() => $"{Src}:{SrcPort} → {Dst}:{DstPort}";
}

/// <summary>
/// TCP segments in, game events out. Every server→client flow is watched for the heartbeat frame
/// (<c>0E 00 36</c>, ~19/s). Once a flow shows enough of them it is locked as a game stream and everything buffered
/// so far is replayed through reassembly → framing → parsing, so records sent right at connect (our own character)
/// are not lost. TLS flows on the same server are recognised and ignored.
/// </summary>
public sealed class PacketPipeline
{
    public const int GamePort = 13328;
    private const int PreLockBufferBytes = 512 * 1024;
    private const int FlowIdleMs = 120_000;

    private static readonly byte[][] HeartbeatSignatures = [[0x0E, 0x00, 0x36], [0x06, 0x00, 0x36]];

    private readonly Dictionary<FlowKey, Flow> _flows = new();
    private readonly PacketParser _parser;
    private long _lastPruneMs;

    public PacketPipeline(GameData data, Action<GameEvent> sink)
    {
        _parser = new PacketParser(data, sink);
    }

    public PacketParser Parser => _parser;

    /// <summary>Heartbeats needed before a flow is trusted. Lower when the capture is already filtered to the game.</summary>
    public int LockThreshold { get; set; } = 6;

    public long Segments { get; private set; }
    public long GameBytes { get; private set; }
    public int GameFlows => _flows.Values.Count(f => f.IsGame);
    public IEnumerable<FlowKey> LockedFlows => _flows.Values.Where(f => f.IsGame).Select(f => f.Key);
    /// <summary>Holes in the game streams skipped since start (lost packets): each costs whatever the hole held.</summary>
    public long Gaps { get; private set; }
    public long LastGameDataMs { get; private set; }

    public event Action<FlowKey>? FlowLocked;

    public void OnSegment(long timeMs, FlowKey key, uint seq, bool syn, bool finOrRst, ReadOnlySpan<byte> payload)
    {
        Segments++;
        if (timeMs - _lastPruneMs > 10_000) Prune(timeMs);

        if (!_flows.TryGetValue(key, out var flow))
        {
            if (payload.IsEmpty && !syn) return;
            flow = new Flow(key);
            _flows[key] = flow;
        }
        flow.LastSeenMs = timeMs;

        if (syn)
        {
            flow.SynNext = seq + 1;
            return;
        }
        if (finOrRst && payload.IsEmpty)
        {
            if (!flow.IsGame) _flows.Remove(key);
            return;
        }
        if (payload.IsEmpty || flow.IsTls) return;

        if (!flow.IsGame)
        {
            if (flow.PreLock.Count == 0 && LooksLikeTls(payload))
            {
                flow.IsTls = true;
                return;
            }
            flow.Heartbeats += CountHeartbeats(payload);
            flow.PreLock.Add((seq, payload.ToArray(), timeMs));
            flow.PreLockBytes += payload.Length;
            while (flow.PreLockBytes > PreLockBufferBytes && flow.PreLock.Count > 1)
            {
                flow.PreLockBytes -= flow.PreLock[0].Data.Length;
                flow.PreLock.RemoveAt(0);
                flow.SynNext = null; // the start of the stream is gone
            }

            var threshold = key.SrcPort == GamePort ? Math.Min(3, LockThreshold) : LockThreshold;
            if (flow.Heartbeats < threshold) return;
            Lock(flow);
            return;
        }

        Feed(flow, seq, payload, timeMs);
    }

    private void Lock(Flow flow)
    {
        flow.IsGame = true;
        var first = flow.PreLock[0];
        var aligned = flow.SynNext is { } isn && isn == first.Seq;
        flow.Decoder = new FrameDecoder(Handle, aligned);
        var decoder = flow.Decoder;
        flow.Reassembler = new TcpReassembler(d => decoder.Feed(d.Span), () =>
        {
            Gaps++;
            decoder.Reset();
        });
        flow.Reassembler.Start(first.Seq);

        var buffered = flow.PreLock;
        flow.PreLock = new();
        flow.PreLockBytes = 0;
        foreach (var (seq, data, time) in buffered) Feed(flow, seq, data, time);
        FlowLocked?.Invoke(flow.Key);
    }

    private void Feed(Flow flow, uint seq, ReadOnlySpan<byte> payload, long timeMs)
    {
        _parser.TimeMs = timeMs;
        GameBytes += payload.Length;
        LastGameDataMs = timeMs;
        flow.Reassembler!.Push(seq, payload, timeMs);
    }

    private void Handle(ushort opcode, ReadOnlySpan<byte> body) => _parser.Handle(opcode, body);

    private static int CountHeartbeats(ReadOnlySpan<byte> payload)
    {
        var count = 0;
        foreach (var sig in HeartbeatSignatures)
        {
            var span = payload;
            int i;
            while ((i = span.IndexOf(sig)) >= 0)
            {
                // A heartbeat frame is exactly 11 bytes; the next byte must start another frame (or end the segment).
                var end = i + 11;
                if (end == span.Length || (end < span.Length && span[end] != 0xFF)) count++;
                span = span[(i + 3)..];
            }
        }
        return count;
    }

    private static bool LooksLikeTls(ReadOnlySpan<byte> p) =>
        p.Length >= 5 && p[0] is >= 0x14 and <= 0x17 && p[1] == 0x03 && p[2] <= 0x04;

    private void Prune(long nowMs)
    {
        _lastPruneMs = nowMs;
        List<FlowKey>? dead = null;
        foreach (var (k, f) in _flows)
            if (nowMs - f.LastSeenMs > FlowIdleMs) (dead ??= new()).Add(k);
        if (dead is not null)
            foreach (var k in dead) _flows.Remove(k);
    }

    private sealed class Flow(FlowKey key)
    {
        public FlowKey Key { get; } = key;
        public bool IsGame;
        public bool IsTls;
        public int Heartbeats;
        public uint? SynNext;
        public long LastSeenMs;
        public List<(uint Seq, byte[] Data, long TimeMs)> PreLock = new();
        public int PreLockBytes;
        public FrameDecoder? Decoder;
        public TcpReassembler? Reassembler;
    }
}
