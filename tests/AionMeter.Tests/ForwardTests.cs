using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AionMeter.Core;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Service;
using Xunit.Abstractions;

namespace AionMeter.Tests;

/// <summary>
/// A2M-27: the Linux daemon's --forward (PcapForwarder) and the receiving meter's capture source (PcapStreamSource): the login fixture
/// replayed into the forwarder and read back over loopback decodes to exactly what the file replay decodes. Needs libpcap (build.sh).
/// </summary>
public class ForwardTests(ITestOutputHelper output)
{
    private static string Pcap => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pcaps", "login-20261009.pcap");
    private static readonly IPEndPoint Loopback0 = new(IPAddress.Loopback, 0);

    private static async Task<int> WaitFor(Func<bool> cond, int ms = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met in " + ms + " ms");
            await Task.Delay(10);
        }
        return (int)sw.ElapsedMilliseconds;
    }

    private static List<string> DirectEvents()
    {
        var events = new List<string>(200_000);
        var replay = new PcapReplaySource(GameData.Empty, Pcap, realtime: false);
        replay.Pipeline.LockThreshold = 3;   // the stream source's (live capture's) threshold
        replay.EventDecoded += e => events.Add(e.ToString());
        replay.RunToEnd();
        return events;
    }

    [Fact]
    public async Task ForwardedReplay_DecodesTheSameEventsAsTheFile()
    {
        var direct = DirectEvents();
        Assert.True(direct.Count > 10_000, $"{direct.Count} events");

        using var fwd = new PcapForwarder(Loopback0);
        var at = await fwd.Listening;
        var forwarded = new List<string>(direct.Count);
        var stream = new PcapStreamSource(GameData.Empty, "127.0.0.1", at.Port, reconnect: false);
        stream.EventDecoded += e => forwarded.Add(e.ToString());
        var reading = Task.Run(stream.RunToEnd);
        await WaitFor(() => fwd.ClientCount == 1);

        var sw = Stopwatch.StartNew();
        var feeder = new PcapReplaySource(GameData.Empty, Pcap, realtime: false) { Forward = fwd };
        var packets = feeder.RunToEnd();
        fwd.Dispose();   // drains the queue, then FIN: the reader ends cleanly
        Assert.Equal(packets, await reading.WaitAsync(TimeSpan.FromSeconds(30)));
        output.WriteLine($"{packets} packets forwarded and decoded over loopback in {sw.ElapsedMilliseconds} ms; dropped clients {fwd.DroppedSlow}");

        Assert.Equal(0, fwd.DroppedSlow);
        Assert.Equal(packets, stream.Packets);
        Assert.Equal(0, stream.Pipeline.Gaps);
        Assert.Equal(direct.Count, forwarded.Count);
        for (var i = 0; i < direct.Count; i++) Assert.True(direct[i] == forwarded[i], $"event {i}: {direct[i]} != {forwarded[i]}");
        Assert.Equal(CaptureState.Stopped, stream.Status.State);
    }

    /// <summary>The stream is the file: the same record headers and frames byte for byte (only the global header's snaplen differs).</summary>
    [Fact]
    public async Task StreamBytes_AreTheRecordsOfTheFile()
    {
        var file = File.ReadAllBytes(Pcap);
        using var fwd = new PcapForwarder(Loopback0);
        var at = await fwd.Listening;
        using var client = new TcpClient();
        await client.ConnectAsync(at.Address, at.Port);
        await WaitFor(() => fwd.ClientCount == 1);
        var ms = new MemoryStream(file.Length);
        var copy = client.GetStream().CopyToAsync(ms);
        new PcapReplaySource(GameData.Empty, Pcap, realtime: false) { Forward = fwd }.RunToEnd();
        var disposing = Task.Run(fwd.Dispose);
        await copy.WaitAsync(TimeSpan.FromSeconds(30));
        client.Dispose();
        await disposing;
        var got = ms.ToArray();

        Assert.True(PcapFormat.TryReadGlobalHeader(got, out var be, out var nanos, out var link));
        Assert.False(be);
        Assert.False(nanos);
        Assert.True(PcapFormat.TryReadGlobalHeader(file, out _, out _, out var fileLink));
        Assert.Equal(fileLink, link);
        Assert.Equal(file.Length, got.Length);
        Assert.True(file.AsSpan(PcapFormat.GlobalHeaderBytes).SequenceEqual(got.AsSpan(PcapFormat.GlobalHeaderBytes)), "records differ");
    }

    [Fact]
    public async Task SlowClient_IsDropped_AndPublishNeverBlocks()
    {
        const int cap = 256 * 1024;
        using var fwd = new PcapForwarder(Loopback0, cap, note: l => output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {l}"));
        var at = await fwd.Listening;
        using var stalled = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 4096 };
        await stalled.ConnectAsync(at);   // connects and never reads
        using var reader = new TcpClient();
        await reader.ConnectAsync(at.Address, at.Port);
        await WaitFor(() => fwd.ClientCount == 2);
        var drain = reader.GetStream().CopyToAsync(Stream.Null);

        var frame = new byte[1400];
        var sw = Stopwatch.StartNew();
        long worstTicks = 0;
        var published = 0;
        while (fwd.DroppedSlow == 0 && published < 200_000)
        {
            var t0 = Stopwatch.GetTimestamp();
            fwd.Publish(1, 1_760_000_000, 0, frame, frame.Length);
            worstTicks = Math.Max(worstTicks, Stopwatch.GetTimestamp() - t0);
            published++;
            if ((published & 31) == 0) await Task.Delay(1);   // ≤ 45 MB/s: the reading client keeps up, only the stalled one falls behind
        }
        var worstUs = worstTicks * 1_000_000 / Stopwatch.Frequency;
        output.WriteLine($"stalled client dropped after {published} frames of 1400 B ({published * 1416L / 1024} KB; queue cap {cap / 1024} KB + socket buffers) in {sw.ElapsedMilliseconds} ms; slowest Publish {worstUs} µs");
        Assert.Equal(1, fwd.DroppedSlow);
        await WaitFor(() => fwd.ClientCount == 1);
        Assert.True(worstUs < 50_000, $"Publish blocked {worstUs} µs");
        fwd.Publish(1, 1_760_000_000, 0, frame, frame.Length);   // the reading client is still served
        var disposing = Task.Run(fwd.Dispose);
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        reader.Dispose();
        await disposing;
    }

    /// <summary>Measurement (A2M-27): the default cap against a client that never reads, and the cost of Publish with one reading client.</summary>
    [Fact]
    public async Task DefaultCap_Measured()
    {
        using var fwd = new PcapForwarder(Loopback0, note: l => output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {l}"));
        var at = await fwd.Listening;
        using var stalled = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await stalled.ConnectAsync(at);
        await WaitFor(() => fwd.ClientCount == 1);
        var frame = new byte[1400];
        var published = 0;
        var sw = Stopwatch.StartNew();
        while (fwd.DroppedSlow == 0 && published < 1_000_000) { fwd.Publish(1, 1_760_000_000, 0, frame, frame.Length); published++; }
        var dropMs = sw.ElapsedMilliseconds;
        Assert.Equal(1, fwd.DroppedSlow);
        // 84 pkt/s and 42 KB/s: the login fixture's busiest second
        output.WriteLine($"default cap {PcapForwarder.DefaultQueueBytes / 1048576} MiB: stalled client dropped after {published} frames of 1400 B " +
                         $"(= {published / 84} s of the fixture's busiest second by packets, {published * 1416L / 42_028} s by bytes), took {dropMs} ms");
        Assert.InRange(published, PcapForwarder.DefaultQueueBytes / 1416, PcapForwarder.DefaultQueueBytes / 1416 + 20_000);

        using var reader = new TcpClient();
        await reader.ConnectAsync(at.Address, at.Port);
        await WaitFor(() => fwd.ClientCount == 1);
        var drain = reader.GetStream().CopyToAsync(Stream.Null);
        var before = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        const int n = 10_000;   // 14 MB: under the cap even if the reader never got scheduled
        for (var i = 0; i < n; i++) fwd.Publish(1, 1_760_000_000, 0, frame, frame.Length);
        var ns = sw.Elapsed.TotalNanoseconds / n;
        var alloc = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)n;
        output.WriteLine($"Publish with one reading client: {ns:0} ns and {alloc:0.0} B allocated per frame on the capture thread");
        var disposing = Task.Run(fwd.Dispose);
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        reader.Dispose();
        await disposing;
        Assert.True(alloc < 64, $"{alloc} B per Publish");
    }

    [Fact]
    public async Task Source_ReconnectsWithBackoff()
    {
        Assert.Equal(1_000, PcapStreamSource.Backoff(0, 1_000, 30_000));
        Assert.Equal(2_000, PcapStreamSource.Backoff(1, 1_000, 30_000));
        Assert.Equal(16_000, PcapStreamSource.Backoff(4, 1_000, 30_000));
        Assert.Equal(30_000, PcapStreamSource.Backoff(5, 1_000, 30_000));
        Assert.Equal(30_000, PcapStreamSource.Backoff(400, 1_000, 30_000));

        int port;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(Loopback0);
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
        var statuses = new List<string>();
        using var source = new PcapStreamSource(GameData.Empty, "127.0.0.1", port) { BackoffMinMs = 20, BackoffMaxMs = 200, ConnectTimeoutMs = 1_000 };
        source.StatusChanged += s => { lock (statuses) statuses.Add(s.Message); };
        source.Start();
        await WaitFor(() => { lock (statuses) return statuses.Count(m => m.Contains("retrying")) >= 3; });
        Assert.Equal(CaptureState.WaitingForGame, source.Status.State);
        Assert.Equal(0, source.Connections);

        var frame = SynFrame();
        for (var round = 1; round <= 2; round++)
        {
            using var fwd = new PcapForwarder(new IPEndPoint(IPAddress.Loopback, port));
            await fwd.Listening;
            await WaitFor(() => fwd.ClientCount == 1);
            Assert.Equal(round, source.Connections);
            var before = source.Packets;
            for (var i = 0; i < 10; i++) fwd.Publish(1, 1_760_000_000, (uint)i, frame, frame.Length);
            await WaitFor(() => source.Packets == before + 10);
        }
        // the forwarder is gone again: the source keeps retrying, never stops by itself
        await WaitFor(() => source.Status.Message.Contains("retrying"));
        Assert.NotEqual(CaptureState.Stopped, source.Status.State);
        source.Stop();
        Assert.Equal(CaptureState.Stopped, source.Status.State);
    }

    [Fact]
    public async Task Forwarder_RetriesABindThatFails_AndDropsClientsThatLeave()
    {
        using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        holder.Bind(Loopback0);
        holder.Listen();
        var taken = (IPEndPoint)holder.LocalEndPoint!;
        var notes = new List<string>();
        using var fwd = new PcapForwarder(taken, note: l => { lock (notes) notes.Add(l); });
        await WaitFor(() => { lock (notes) return notes.Any(n => n.Contains("cannot listen")); });
        Assert.False(fwd.Listening.IsCompleted);
        holder.Dispose();
        var at = await fwd.Listening.WaitAsync(TimeSpan.FromSeconds(PcapForwarder.BindRetryMs / 1000 + 5));
        Assert.Equal(taken.Port, at.Port);

        var c = new TcpClient();
        await c.ConnectAsync(at.Address, at.Port);
        await WaitFor(() => fwd.ClientCount == 1);
        c.Dispose();   // gone while no packet flows: the slot is freed anyway
        await WaitFor(() => fwd.ClientCount == 0);
        await WaitFor(() => { lock (notes) return notes.Any(n => n.Contains("disconnected")); });
        Assert.Equal(0, fwd.DroppedSlow);
    }

    [Fact]
    public void Endpoints_Parse()
    {
        Assert.True(CaptureEndpoint.TryParse("tcp://192.168.122.1:47013", out var h, out var p));
        Assert.Equal(("192.168.122.1", 47013), (h, p));
        Assert.True(CaptureEndpoint.TryParse("TCP://desk.lan:9/", out h, out p));
        Assert.Equal(("desk.lan", 9), (h, p));
        Assert.True(CaptureEndpoint.TryParse(" [fd00::2]:47013 ", out h, out p));
        Assert.Equal(("fd00::2", 47013), (h, p));
        foreach (var bad in new[] { null, "", "host", "host:", ":80", "host:0", "host:65536", "fd00::2:80", "host:8x" })
            Assert.False(CaptureEndpoint.TryParse(bad, out _, out _), bad);

        Assert.True(CaptureEndpoint.TryParseBind("192.168.122.1:47013", out var ep));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.122.1"), 47013), ep);
        Assert.False(CaptureEndpoint.TryParseBind("desk.lan:47013", out _));
        Assert.True(CaptureEndpoint.TryParseBind("127.0.0.1:0", out ep));   // any port: tests   // a name is never bound: the address is chosen explicitly

        Assert.Equal("192.168.122.1:47013", CaptureEndpoint.StreamEndpoint("tcp://192.168.122.1:47013"));
        Assert.Null(CaptureEndpoint.StreamEndpoint("192.168.122.1:47013"));   // a settings value without the scheme is not a source
        Assert.Null(CaptureEndpoint.StreamEndpoint(null));
        Assert.Equal("[fd00::2]:1", CaptureEndpoint.Format("fd00::2", 1));
    }

    [Fact]
    public void PcapHeaders_RoundTrip_BothByteOrders()
    {
        var h = new byte[24];
        PcapFormat.WriteGlobalHeader(h, 113);
        Assert.True(PcapFormat.TryReadGlobalHeader(h, out var be, out var ns, out var link));
        Assert.Equal((false, false, 113), (be, ns, link));
        var big = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(big, PcapFormat.MagicNanos);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(big.AsSpan(20), 1);
        Assert.True(PcapFormat.TryReadGlobalHeader(big, out be, out ns, out link));
        Assert.Equal((true, true, 1), (be, ns, link));
        Assert.False(PcapFormat.TryReadGlobalHeader(new byte[24], out _, out _, out _));
    }

    [Fact]
    public void Availability_ForwardedCapture_UnblocksTheLiveFeatures()
    {
        var a = new CaptureAvailability(static () => false) { ForwardedFrom = "192.168.122.1:47013" };
        Assert.False(a.Present);
        Assert.True(a.Live);
        Assert.True(a.Has(CaptureFeature.Dps));
        Assert.True(a.Has(CaptureFeature.AtlasPosition));
        Assert.False(a.Has(CaptureFeature.PacketRecording));   // recording still writes through SharpPcap
        Assert.Equal("", a.SettingsText(CaptureAvailability.EnglishLabels, "h", "lost", "kept"));
        Assert.Equal("forwarded capture from 192.168.122.1:47013", a.HealthText(null));
        Assert.Equal("forwarded capture from 192.168.122.1:47013: 1,200 packets, 3 stream gaps", a.HealthText(new CaptureHealth(1200, 0, 3)));
        a.ForwardedFrom = null;
        Assert.False(a.Live);
        Assert.Equal(CaptureAvailability.NpcapNotInstalled, a.HealthText(null));
    }

    // Ethernet + IPv4 + TCP SYN from the game port: parses to a segment, carries no payload
    private static byte[] SynFrame()
    {
        var f = new byte[54];
        f[12] = 0x08; f[14] = 0x45; f[17] = 40; f[22] = 64; f[23] = 6;
        f[26] = 10; f[29] = 1; f[30] = 10; f[33] = 2;
        f[34] = 13328 >> 8; f[35] = 13328 & 0xFF; f[36] = 0xC0; f[37] = 0x01;
        f[46] = 0x50; f[47] = 0x02;
        return f;
    }
}

/// <summary>A2M-27, the daemon half: HeadlessMeter moves process-wide statics (AppSettings.AppDataDir, Log), so it runs in that collection.</summary>
[Collection(ProcessStaticsCollection.Name)]
public class ForwardDaemonTests(ITestOutputHelper output)
{
    private static string Pcap => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pcaps", "login-20261009.pcap");

    /// <summary>The daemon path: HeadlessMeter with --replay --forward waits for the client, forwards every packet, and a receiver's
    /// fights (the CLI's `replay tcp://`) equal the fights of the file.</summary>
    [Fact]
    public async Task Daemon_ForwardsItsReplay_ReceiverPrintsTheSameFights()
    {
        var data = GameData.Load(RepoData(), "en");
        string Fights(Action<Action<GameEvent>> run)
        {
            var tracker = new CombatTracker(data, new MeterOptions { TargetMode = TargetMode.All });
            run(tracker.Process);
            tracker.Tick(long.MaxValue / 2);
            var sb = new System.Text.StringBuilder();
            foreach (var seg in tracker.Segments())
            {
                var s = tracker.Snapshot(seg.Id, long.MaxValue / 2)!;
                sb.Append($"{s.Title}|{s.CombatMs}|{s.TotalDamage}|{s.PlayerCount}|{s.Reason}\n");
                foreach (var c in s.Combatants) sb.Append($"  {c.Name}|{c.Class}|{c.Damage}|{c.Hits}\n");
            }
            return sb.ToString();
        }

        var direct = Fights(sink =>
        {
            var r = new PcapReplaySource(data, Pcap, realtime: false);
            r.Pipeline.LockThreshold = 3;
            r.EventDecoded += sink;
            r.RunToEnd();
        });
        Assert.Contains("|", direct);

        var received = await RunWithDaemon(async port =>
        {
            string? text = null;
            long packets = 0;
            await Task.Run(() => text = Fights(sink =>
            {
                var s = new PcapStreamSource(data, "127.0.0.1", port, reconnect: false);
                s.EventDecoded += sink;
                packets = s.RunToEnd();
            }));
            Assert.Equal(57_423, packets);
            return text!;
        });
        Assert.Equal(direct, received);
        output.WriteLine(direct.Split('\n').Count(l => !l.StartsWith(' ')) - 1 + " fights equal");
    }

    private static string RepoData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "servers.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/servers.json above " + AppContext.BaseDirectory);
    }

    private static async Task<string> RunWithDaemon(Func<int, Task<string>> client)
    {
        var root = Path.Combine(Path.GetTempPath(), "aionmeter-fwd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var human = new StringWriter();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var m = new HeadlessMeter(new HeadlessOptions
            {
                DataDir = root, GameDataDir = RepoData(), Offline = true, StatusEverySeconds = 0, Socket = null, Replay = Pcap, Forward = "127.0.0.1:0",
            }, human, null);
            Assert.NotNull(m.Forwarder);
            var run = m.RunAsync(cts.Token);
            var at = await m.Forwarder!.Listening.WaitAsync(cts.Token);
            var text = await client(at.Port);
            Assert.Equal(0, await run.WaitAsync(cts.Token));
            Assert.Contains("replay waits", human.ToString());
            Assert.Contains("forward: listening on 127.0.0.1:", human.ToString());
            Assert.Contains("forward: 127.0.0.1:", human.ToString());   // the client's connect line
            return text;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The Wine / no-Npcap case: a meter whose capture library probe says "missing" (LibraryPresent = () =&gt; false) reads the
    /// forwarded stream anyway — packets decoded, character bound, no "Npcap missing" status, health line from the stream.</summary>
    [Fact]
    public async Task Receiver_WithoutCaptureLibrary_DecodesTheForwardedStream()
    {
        var root = Path.Combine(Path.GetTempPath(), "aionmeter-fwdrx-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var fwd = new PcapForwarder(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var at = await fwd.Listening;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var m = new HeadlessMeter(new HeadlessOptions
            {
                DataDir = root, GameDataDir = RepoData(), Offline = true, StatusEverySeconds = 0, Socket = null,
                Source = $"tcp://127.0.0.1:{at.Port}", LibraryPresent = static () => false,
            }, TextWriter.Null, null);
            var run = m.RunAsync(cts.Token);
            for (var i = 0; i < 500 && fwd.ClientCount == 0; i++) await Task.Delay(20, cts.Token);
            Assert.Equal(1, fwd.ClientCount);

            var a = m.Engine.Availability;
            Assert.False(a.Present);
            Assert.True(a.Live);
            Assert.Equal("127.0.0.1:" + at.Port, a.ForwardedFrom);

            var packets = new PcapReplaySource(GameData.Empty, Pcap, realtime: false) { Forward = fwd }.RunToEnd();
            for (var i = 0; i < 500 && m.Engine.CaptureHealth()?.Received != packets; i++) await Task.Delay(20, cts.Token);
            Assert.Equal(packets, m.Engine.CaptureHealth()?.Received);
            for (var i = 0; i < 100 && m.Engine.BoundKey.Length == 0; i++) await Task.Delay(50, cts.Token);
            Assert.Equal("1303-Akunita", m.Engine.BoundKey);
            var status = m.Engine.CaptureStatus.Message;
            Assert.NotEqual(CaptureAvailability.NoCaptureStatus, status);
            Assert.DoesNotContain("Npcap", status);
            Assert.Equal($"Capturing · forwarded from 127.0.0.1:{at.Port}", status);
            Assert.StartsWith($"forwarded capture from 127.0.0.1:{at.Port}: {packets:N0} packets", m.Engine.CaptureHealthText());
            output.WriteLine($"no capture library: {packets} packets forwarded, status \"{status}\", health \"{m.Engine.CaptureHealthText()}\"");
            cts.Cancel();
            Assert.Equal(0, await run);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
