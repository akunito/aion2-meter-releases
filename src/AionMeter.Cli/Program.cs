using System.Globalization;
using AionMeter.Core;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Protocol;

// AionMeter developer tool: inspect devices / connections, capture live, replay and dump recorded captures.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
AionMeter.Core.Net.CloudflareHttp.SolverUrl = Environment.GetEnvironmentVariable("AION2_SOLVER_URL"); // optional CF bypass for scrapers
var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
var langAt = Array.IndexOf(args, "--lang");
var data = GameData.Load(dataDir, langAt >= 0 && langAt + 1 < args.Length ? args[langAt + 1] : "en");

return args.FirstOrDefault() switch
{
    "devices" => Devices(),
    "conns" => Connections(),
    "live" => Live(args.Skip(1).ToArray()),
    "replay" when args.Length > 1 => Replay(args[1]),
    "dump" when args.Length > 1 => Dump(args[1], args.Length > 2 ? args[2] : null),
    "ach" when args.Length > 1 => Ach(args[1], args.Length > 2 ? args[2] : null),
    "find" when args.Length > 2 => Find(args[1], args[2]),
    "crits" when args.Length > 1 => Crits(args[1]),
    "entities" when args.Length > 1 => Entities(args[1]),
    "casts" when args.Length > 1 => Casts(args[1]),
    "bosslist" when args.Length > 1 => BossList(args[1]),
    "times" when args.Length > 1 => Times(args[1]),
    "fieldbosses" when args.Length > 1 => FieldBosses(args[1]),
    "nodes" when args.Length > 1 => Nodes(args[1]),
    "atlas" when args.Length > 1 => Atlas(args[1], args.Length > 2 ? args[2] : null),
    "top" when args.Length > 1 => Top(args[1], args.Length > 2 ? args[2] : null),
    "armory" when args.Length > 1 => Armory(args[1], args.Length > 2 ? int.Parse(args[2]) : 1303).GetAwaiter().GetResult(),
    "inventory" when args.Length > 1 => Inventory(args[1]).GetAwaiter().GetResult(),
    "market" when args.Length > 1 => Market(args[1]).GetAwaiter().GetResult(),
    "lag" when args.Length > 1 => Lag(args[1]),
    "leaderboard" => Leaderboard(args.Length > 1 ? args[1] : "eu", args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null).GetAwaiter().GetResult(),
    "opcodes" when args.Length > 1 => OpcodeHistogram(args[1]),
    "grep" when args.Length > 2 => Grep(args[1], args[2]),
    "export" when args.Length > 2 => Export(args[1], args[2]),
    "hex" when args.Length > 2 => HexDump(args[1], Convert.ToUInt16(args[2], 16), args.Length > 3 ? int.Parse(args[3]) : 5),
    "update-check" => UpdateCheck(args.Skip(1).ToArray()),
    _ => Usage(),
};

int Usage()
{
    Console.WriteLine("""
        aionmeter-cli devices                       list Npcap adapters
        aionmeter-cli conns                         show the AION 2 process and its TCP connections
        aionmeter-cli live [seconds] [--record]     capture live, print what was decoded
        aionmeter-cli replay <file.pcap>            run a capture through the meter, print fights
        aionmeter-cli replay tcp://<host>:<port>    the same over a daemon's --forward stream, until the daemon closes it
        aionmeter-cli dump <file.pcap> [opcode]     print decoded packets (opcode as hex, e.g. 0438)
        aionmeter-cli replay <file.pcap> --boss --brief   player counts per fight only
        aionmeter-cli crits <file.pcap>             damage-type / modifier statistics per class (crit check)
        aionmeter-cli entities <file.pcap>          damage dealers that are not announced players, by spawn kind
        aionmeter-cli casts <file.pcap>             tie ownerless skill effects to the casts that made them
        aionmeter-cli opcodes <file.pcap>           histogram: opcode, count, min/max/avg length, first seen, first 48 bytes of 3 packets
        aionmeter-cli grep <file.pcap> <pattern>    packets holding hex bytes (aabbcc), a u32 (u32:14010000) or a u32 range (u32:310000-319999)
        aionmeter-cli export <file.pcap> <out.gz>   every packet as "unixMs opcode hexbody" lines, gzip (offline analysis)
        aionmeter-cli update-check [--pretend 0.0.1] [--download <dir>]
                                                    what the meter's update check sees on GitHub; download + verify the installer
        """);
    return 1;
}

int UpdateCheck(string[] a)
{
    string? Opt(string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    var current = AionMeter.Core.Updates.UpdateFeed.ParseVersion(Opt("--pretend")) ?? new Version(0, 0, 0);
    using var feed = new AionMeter.Core.Updates.UpdateFeed("AION2DpsMeter-cli/dev");
    var latest = feed.GetLatestAsync().GetAwaiter().GetResult();
    if (latest is null)
    {
        Console.WriteLine("No release published yet.");
        return 1;
    }
    Console.WriteLine($"Latest:    {latest.Tag} = {latest.Version}, published {latest.PublishedAt:u}");
    Console.WriteLine($"Page:      {latest.PageUrl}");
    Console.WriteLine($"Installer: {latest.Installer?.Name} {latest.Installer?.Size:N0} B sha256={latest.Installer?.Sha256 ?? "-"}");
    Console.WriteLine($"Portable:  {latest.Portable?.Name} {latest.Portable?.Size:N0} B");
    Console.WriteLine(latest.Version > current ? $"Newer than {current}: the meter offers it." : $"Not newer than {current}: nothing offered.");
    if (Opt("--download") is not { } dir || latest.Installer is not { } installer) return 0;

    var shown = -1;
    var path = feed.DownloadAsync(installer, dir, new ConsoleProgress(f =>
    {
        if ((int)(f * 10) == shown) return;
        shown = (int)(f * 10);
        Console.Write($"{shown * 10}% ");
    })).GetAwaiter().GetResult();
    Console.WriteLine($"\nDownloaded, size and SHA-256 match GitHub: {path}");
    return 0;
}

int Devices()
{
    foreach (var (name, desc) in LiveCapture.ListDevices()) Console.WriteLine($"{desc,-60} {name}");
    return 0;
}

int Connections()
{
    var pids = GameProcessLocator.FindGameProcessIds();
    Console.WriteLine($"AION 2 processes: {string.Join(", ", pids)}");
    foreach (var c in GameProcessLocator.FindConnections(pids))
        Console.WriteLine($"  {c.LocalIp}:{c.LocalPort} -> {c.RemoteIp}:{c.RemotePort}");
    return 0;
}

int Live(string[] a)
{
    var seconds = a.Length > 0 && int.TryParse(a[0], out var s) ? s : 20;
    var record = a.Contains("--record");
    var stats = new EventStats(data);
    var tracker = new CombatTracker(data, new MeterOptions { TargetMode = TargetMode.All });
    using var capture = new LiveCapture(data, new LiveCaptureOptions
    {
        RecordDirectory = record ? Path.Combine(Environment.CurrentDirectory, "captures") : null,
    });
    capture.EventDecoded += e =>
    {
        lock (stats)
        {
            stats.Add(e);
            tracker.Process(e);
        }
    };
    capture.Start();
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        Thread.Sleep(2_000);
        var p = capture.Pipeline;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {capture.Status.Message} · segments {p.Segments} · game flows {p.GameFlows} · game bytes {p.GameBytes} · events {stats.Total}");
    }
    capture.Stop();
    if (capture.RecordingPath is { } rec) Console.WriteLine($"Recorded to {rec}");
    lock (stats) stats.Print(capture.Pipeline);
    PrintFights(tracker);
    return 0;
}

int Replay(string path)
{
    var stats = new EventStats(data);
    var tracker = new CombatTracker(data, new MeterOptions { TargetMode = args.Contains("--boss") ? TargetMode.BossOnly : TargetMode.All });
    void OnEvent(GameEvent e)
    {
        stats.Add(e);
        tracker.Process(e);
    }
    long n;
    PacketPipeline pipeline;
    // A2M-27: tcp://host:port reads a daemon's --forward stream (one connection, until the daemon closes it) instead of a file
    if (PcapStreamSource.From(data, path, reconnect: false) is { } stream)
    {
        stream.EventDecoded += OnEvent;
        n = stream.RunToEnd();
        pipeline = stream.Pipeline;
        Console.WriteLine(stream.Status.Message);
    }
    else
    {
        var replay = new PcapReplaySource(data, path, realtime: false);
        replay.EventDecoded += OnEvent;
        n = replay.RunToEnd();
        pipeline = replay.Pipeline;
    }
    tracker.Tick(long.MaxValue / 2);
    Console.WriteLine($"{n} packets");
    stats.Print(pipeline);
    PrintFights(tracker);
    return 0;
}

int Dump(string path, string? opcodeHex)
{
    ushort? filter = opcodeHex is null ? null : Convert.ToUInt16(opcodeHex, 16);
    var replay = new PcapReplaySource(data, path, realtime: false);
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        if (filter is { } f && f != op) return;
        if (filter is null && op == Opcodes.Heartbeat) return;
        Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime():HH:mm:ss.fff} {Opcodes.Name(op),-12} [{body.Length,5}] {Wire.Hex(body, 96)}");
    };
    replay.EventDecoded += e => Console.WriteLine($"    -> {e}");
    replay.RunToEnd();
    return 0;
}

// A2M-15: every achievement list (id count step completedMs), live update, claim and content-ticket record of a capture; with a catalogue
// (data/gamedb/achievements.json) each login list also prints the window summary (tiers claimed/total, completed, claimable, in progress).
int Ach(string path, string? catalogPath)
{
    var catalog = catalogPath is null ? AionMeter.Core.Character.AchievementCatalog.Empty : AionMeter.Core.Character.AchievementCatalog.Load(catalogPath);
    var replay = new PcapReplaySource(data, path, realtime: false);
    var lists = 0;
    string T(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm:ss");
    replay.EventDecoded += e =>
    {
        switch (e)
        {
            case AionMeter.Core.Events.AchievementListEvent a:
                Console.WriteLine($"LIST {++lists} {T(a.TimeMs)} rows {a.Rows.Count} groups {a.Groups?.Count.ToString() ?? "-"}");
                if (catalog.Count > 0)
                {
                    var s = catalog.Summarise(a.Rows);
                    Console.WriteLine($"  window: {s.Achievements} achievements, tiers {s.TiersClaimed}/{s.TiersTotal}, completed {s.Completed}, claimable {s.Claimable}, in progress {s.InProgress}, 3xx rows without catalogue {s.Unmatched}");
                    foreach (var p in s.ClaimableList) Console.WriteLine($"  claimable {p.Row.Id} {p.Info!.NameEn} tier {p.NextTier}/{p.Tiers} {p.Row.Count}/{p.Goal}");
                }
                foreach (var r in a.Rows) Console.WriteLine($"{r.Id} {r.Count} {r.Step} {r.CompletedMs}");
                break;
            case AionMeter.Core.Events.AchievementUpdateEvent u:
                foreach (var r in u.Rows) Console.WriteLine($"UPD {T(u.TimeMs)} {r.Id} {r.Count} {r.Step} {catalog.Get(r.Id)?.NameEn}");
                break;
            case AionMeter.Core.Events.AchievementClaimEvent c:
                foreach (var (g, id, step) in c.Claims) Console.WriteLine($"CLAIM {T(c.TimeMs)} group {g} {id} step {step} {catalog.Get(id)?.NameEn}");
                break;
            case AionMeter.Core.Events.ExpeditionChestsEvent x:
                Console.WriteLine($"CHESTS {T(x.TimeMs)} " + string.Join(" ", x.Chests.Select(c => $"{c.Dungeon}:{c.Progress}/{c.Flag}")));
                break;
            case AionMeter.Core.Events.ContentTicketsEvent k:
                foreach (var t in k.Tickets)
                    if (!k.Snapshot || !AionMeter.Core.Character.TicketBook.IsStub(t.Key))
                        Console.WriteLine($"{(k.Snapshot ? "TICKETS" : "TICKET")} {T(k.TimeMs)} {t.Key} mask {t.Mask:x} time {t.Time} base {t.Base} bonus {t.Bonus} {AionMeter.Core.Character.TicketBook.Of(t.Key).En}");
                break;
        }
    };
    replay.RunToEnd();
    Console.WriteLine($"lists {lists}");
    return 0;
}

// Runs the atlas store over a capture (optionally persisting into <dir>) and prints the spots per map.
int Atlas(string path, string? dir)
{
    var store = new AionMeter.Core.Atlas.NodeStore(dir ?? Path.Combine(Path.GetTempPath(), "aionmeter-atlas"));
    var replay = new PcapReplaySource(data, path, realtime: false);
    replay.EventDecoded += store.OnEvent;
    replay.RunToEnd();
    store.Save();
    var spots = store.Spots(store.MapId);
    Console.WriteLine($"map {store.MapId}: {spots.Count} spots, self {store.SelfId} at ({store.SelfX:0},{store.SelfY:0},{store.SelfZ:0})");
    foreach (var sp in spots.OrderBy(s => s.Code).ThenBy(s => s.FirstSeenMs))
        Console.WriteLine($"  {data.ObjectName(sp.Code),-16} ({sp.X,7:0},{sp.Y,7:0}) spawns {sp.Spawns} {(sp.Alive ? $"alive, {sp.UnitsLeft} left" : "exhausted")}{(sp.RespawnMinutes > 0 ? $", respawn {sp.RespawnMinutes} min" : "")}");
    return 0;
}

// Gathering nodes of a capture: every object seen, who gathered it, units taken, exhausted or just out of view.
// Ten best DPS per class from a fights directory (%AppData%\AionMeter\fights), with each player's rotation summary.
int Top(string fightsDir, string? onlyClass)
{
    var store = new AionMeter.Core.Ranking.TopDpsStore(null);
    var n = store.Rebuild(new AionMeter.Core.Storage.HistoryStore(fightsDir), data);
    Console.WriteLine($"{n} ranking updates from {fightsDir}");
    foreach (var c in store.Classes)
    {
        if (onlyClass is not null && !string.Equals(c.ToString(), onlyClass, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"== {c}");
        var rank = 0;
        foreach (var e in store.For(c))
        {
            Console.WriteLine($"  {++rank,2}. {e.Name,-16} {e.Dps,9:0}/s  {e.CombatMs / 1000,4}s  {e.Title}  {e.StartedAt.ToLocalTime():MM-dd HH:mm}  casts {e.Casts.Count}");
            Console.WriteLine("      share : " + string.Join("  ", e.Skills.OrderByDescending(s => s.Share).Take(8).Select(s => $"{s.Name} {s.Share * 100:0}%")));
            Console.WriteLine("      opener: " + string.Join(" > ", AionMeter.Core.Ranking.TopDpsStore.Opening(e, 12).Select(data.SkillName)));
            foreach (var (skills, count) in AionMeter.Core.Ranking.TopDpsStore.Frequent(e, 3, 3))
                Console.WriteLine($"      loop  : {string.Join(" > ", skills.Select(data.SkillName))}  x{count}");
        }
    }
    return 0;
}

// Official character pages: profile, stats, gear, Dp skills and Daevanion boards of one character (default server Vaizel 1303).
async Task<int> Armory(string name, int serverId)
{
    using var client = new AionMeter.Core.Armory.ArmoryClient(Path.Combine(Path.GetTempPath(), "aionmeter-armory"));
    var p = await client.LookupAsync(name, serverId, withDaevanion: true, force: true);
    if (p is null) { Console.WriteLine($"{name} not found on server {serverId}"); return 1; }
    Console.WriteLine($"{p.Ref.Name} ({p.Ref.ServerName}) {p.ClassName} lv{p.Level}  CP {p.CombatPower:N0}  item level {p.ItemLevel}  legion {p.Legion}  title {p.Title}  [{client.Requests} requests]");
    foreach (var s in p.Stats) Console.WriteLine($"  {s.Type,-12} {s.Value,6}  {string.Join(", ", s.Effects)}");
    Console.WriteLine("  boards: " + string.Join("  ", p.Boards.Select(b => $"{b.Name} {b.Open}/{b.Total}")));
    foreach (var e in p.Equipment.OrderBy(e => e.SlotPos)) Console.WriteLine($"  {e.Slot,-10} {e.Name,-28} {e.Grade,-8} +{e.Enchant,-3} id {e.Id}");
    Console.WriteLine("  pet " + p.Pet + "   wing " + p.Wing);
    foreach (var k in p.Skills.Where(k => k.Equipped || k.Category == "Passive")) Console.WriteLine($"  {k.Category,-8} {k.Name,-28} lv{k.Level,-3} {(k.Equipped ? "equipped" : "")} {k.Id}");
    Console.WriteLine("  daevanion effects: " + string.Join(", ", p.DaevanionEffects));
    foreach (var g in p.Daevanion.Where(n => n.Open && n.Effects.Count > 0).GroupBy(n => n.BoardId))
        Console.WriteLine($"  board {g.Key}: {string.Join(" | ", g.Select(n => n.Name.Length > 0 ? n.Name : string.Join(",", n.Effects)).Take(40))}");
    return 0;
}

// aion2index.com combat-power ranking: top 10 of a region, optionally one class and one server (e.g. leaderboard eu templar vaizel).
async Task<int> Leaderboard(string region, string? cls, string? server)
{
    using var client = new AionMeter.Core.Leaderboard.Aion2IndexClient(Path.Combine(Path.GetTempPath(), "aionmeter-leaderboard"));
    var gc = cls is null ? AionMeter.Core.Events.GameClass.Unknown : AionMeter.Core.Leaderboard.LeaderboardRow.ClassOf(cls);
    var rows = await client.TopAsync(region, gc, server);
    Console.WriteLine($"{rows.Count} rows ({client.Requests} requests)");
    foreach (var r in rows) Console.WriteLine($"  #{r.Rank,-5} {r.Name,-16} {r.ServerName,-10} {r.ClassKey,-13} lv{r.Level} CP {r.CombatPower,8:N0}  item level {r.ItemLevel}");
    return 0;
}

// The character's containers from a recording that includes a login (11 56) or container refreshes (1b 56), names from aion2hub.me (cached in the temp dir).
async Task<int> Inventory(string path)
{
    var store = new AionMeter.Core.Inventory.InventoryStore(null);
    var replay = new PcapReplaySource(data, path, realtime: false);
    replay.EventDecoded += store.OnEvent;
    replay.RunToEnd();
    using var catalog = new AionMeter.Core.GameDb.ItemCatalog(Path.Combine(Path.GetTempPath(), "aionmeter-gamedb", "items.json"));
    var all = store.All();
    Console.WriteLine($"{all.Count} stacks in {store.ContainerIds.Count} containers; resolving names…");
    await catalog.ResolveAsync(all.Select(s => s.Item));
    foreach (var c in store.ContainerIds)
    {
        var items = store.Items(c);
        Console.WriteLine($"== {AionMeter.Core.Inventory.Containers.Name(c)} ({items.Count})");
        foreach (var s in items)
        {
            var info = catalog.Cached(s.Item);
            Console.WriteLine($"   {s.Slot,3}  x{s.Count,-7} {s.Item}  {info?.Name ?? "?"}{(s.Enchant > 0 ? $" +{s.Enchant}" : "")}  {info?.Grade ?? ""} {info?.Category ?? ""}");
        }
    }
    Console.WriteLine($"[{catalog.Requests} catalogue requests]");
    return 0;
}

// Black Cloud shop pages (91 56) in a recording: every offer with both prices, names from the item catalogue.
async Task<int> Market(string path)
{
    var store = new AionMeter.Core.Market.ShopStore(null);
    var replay = new PcapReplaySource(data, path, realtime: false);
    replay.EventDecoded += store.OnEvent;
    replay.RunToEnd();
    using var catalog = new AionMeter.Core.GameDb.ItemCatalog(Path.Combine(Path.GetTempPath(), "aionmeter-gamedb", "items.json"));
    var all = store.All();
    await catalog.ResolveAsync(all.Select(l => l.Item));
    Console.WriteLine($"{all.Count} listings");
    foreach (var l in all.OrderBy(l => l.Item).ThenBy(l => l.Price))
        Console.WriteLine($"  {DateTimeOffset.FromUnixTimeMilliseconds(l.SeenMs).ToLocalTime():HH:mm:ss} page {l.Page,5}  {l.Item} {catalog.NameOf(l.Item),-44} x{l.Quantity,-4} {l.Price,12:N0}  now {l.Current,12:N0}  lvl {l.Level}");
    return 0;
}

// Lag analysis of a recording: server silences ≥ 400 ms (with the time) and the round-trip time per minute measured from the
// client's own segments and the server's ACKs. Measured 2026-10-09: RTT 4–20 ms median, p99 45 ms (delayed-ACK bound), no loss;
// ~30 % of server segments reappear with the same seq and a larger length ~28 ms later with no duplicate ACKs — a stack artifact
// seen in every capture, good or bad, not loss.
int Lag(string path)
{
    using var dev = new SharpPcap.LibPcap.CaptureFileReaderDevice(path);
    dev.Open(new SharpPcap.DeviceConfiguration());
    long lastServer = 0; var silences = new List<(long, int)>();
    var pending = new List<(uint End, long T)>();
    var rtt = new SortedDictionary<string, List<double>>();
    while (dev.GetNextPacket(out var pc) == SharpPcap.GetPacketStatus.PacketRead)
    {
        if (!SegmentReader.TryRead(pc.GetPacket(), out var seg)) continue;
        if (seg.Key.SrcPort != 13328 && seg.Key.DstPort != 13328) continue;
        if (seg.Key.SrcPort == 13328)
        {
            if (seg.Payload.Length > 0)
            {
                if (lastServer > 0 && seg.TimeMs - lastServer >= 400) silences.Add((lastServer, (int)(seg.TimeMs - lastServer)));
                lastServer = seg.TimeMs;
            }
            if (pending.Count > 0 && seg.Ack is { } ack)
            {
                for (var i = pending.Count - 1; i >= 0; i--)
                    if ((int)(ack - pending[i].End) >= 0)
                    {
                        var minute = DateTimeOffset.FromUnixTimeMilliseconds(pending[i].T).ToLocalTime().ToString("HH:mm");
                        if (!rtt.TryGetValue(minute, out var l)) rtt[minute] = l = new();
                        l.Add(seg.TimeMs - pending[i].T);
                        pending.RemoveAt(i);
                    }
            }
        }
        else if (seg.Payload.Length > 0) pending.Add(((uint)(seg.Seq + seg.Payload.Length), seg.TimeMs));
    }
    Console.WriteLine($"server silences ≥ 400 ms: {silences.Count}");
    foreach (var (start, ms) in silences.OrderByDescending(x => x.Item2).Take(20))
        Console.WriteLine($"   {DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime():HH:mm:ss.fff}  {ms,6} ms");
    Console.WriteLine("RTT per minute (ms): median / p90 / max");
    foreach (var (minute, l) in rtt)
    {
        l.Sort();
        Console.WriteLine($"   {minute}  n={l.Count,5}  {l[l.Count / 2],6:0.0}  {l[(int)(l.Count * 0.9)],6:0.0}  {l[^1],7:0.0}");
    }
    return 0;
}

int Nodes(string path)
{
    var replay = new PcapReplaySource(data, path, realtime: false);
    var nodes = new Dictionary<uint, (ObjectSeenEvent First, int Taken, string Log, bool Exhausted, int Seen)>();
    var self = 0u;
    replay.EventDecoded += e =>
    {
        switch (e)
        {
            case SelfIdentifiedEvent s: self = s.ActorId; break;
            case ObjectSeenEvent o:
                if (nodes.TryGetValue(o.ActorId, out var n)) nodes[o.ActorId] = (n.First, Math.Max(n.Taken, o.Taken), n.Log, n.Exhausted, n.Seen + 1);
                else nodes[o.ActorId] = (o, o.Taken, "", false, 1);
                break;
            case GatherEvent g when nodes.TryGetValue(g.ObjectId, out var ng):
                var who = g.PlayerId == self ? "self" : g.PlayerId.ToString();
                nodes[g.ObjectId] = (ng.First, g.Started ? ng.Taken : Math.Max(ng.Taken, g.Count), ng.Log + (g.Started ? $" {who}:start" : $" {who}:{g.Count}"), ng.Exhausted, ng.Seen);
                break;
            case ObjectGoneEvent x when x.Exhausted && nodes.TryGetValue(x.ActorId, out var nx):
                nodes[x.ActorId] = (nx.First, nx.Taken, nx.Log, true, nx.Seen);
                break;
        }
    };
    replay.RunToEnd();
    Console.WriteLine($"{nodes.Count} objects (self id {self})");
    foreach (var (id, n) in nodes.OrderBy(kv => kv.Value.First.ObjectCode).ThenBy(kv => kv.Value.First.TimeMs))
    {
        var f = n.First;
        Console.WriteLine($"  {f.ObjectCode} kind {f.Kind:x2} id {id,6} at ({f.X,8:0},{f.Y,8:0},{f.Z,6:0}) spawned {DateTimeOffset.FromUnixTimeMilliseconds(f.SpawnedAtMs).ToLocalTime():HH:mm:ss} seen {n.Seen}x taken {n.Taken}{(n.Exhausted ? " EXHAUSTED" : "")}{n.Log}");
    }
    return 0;
}

// Every decoded packet (bundles unpacked) that contains the UTF-8 text, with its opcode and the bytes around it.
int Find(string path, string text)
{
    var needle = System.Text.Encoding.UTF8.GetBytes(text);
    var utf16 = System.Text.Encoding.Unicode.GetBytes(text);
    var replay = new PcapReplaySource(data, path, realtime: false);
    var hits = 0;
    var embedded = 0;
    var perOpcode = new Dictionary<ushort, int>();
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        if (body.IndexOf(utf16) >= 0) Console.WriteLine($"   UTF-16 match in {Opcodes.Name(op)}");
        // LZ4 bundles carried inside other packets (not opened by the framing).
        for (var i = 1; i + 8 < body.Length; i++)
        {
            if (body[i] != 0xFF || body[i + 1] != 0xFF) continue;
            var raw = (int)Wire.U32(body, i + 2);
            if (raw is <= 0 or > 1 << 22) continue;
            var buf = new byte[raw];
            var n = K4os.Compression.LZ4.LZ4Codec.Decode(body[(i + 6)..], buf);
            if (n > 0 && buf.AsSpan(0, n).IndexOf(needle) >= 0)
            {
                embedded++;
                Console.WriteLine($"   embedded bundle in {Opcodes.Name(op)} at {i}: {Wire.Hex(buf.AsSpan(0, n)[Math.Max(0, buf.AsSpan(0, n).IndexOf(needle) - 30)..], 70)}");
            }
        }
        var at = body.IndexOf(needle);
        if (at < 0) return;
        perOpcode[op] = perOpcode.GetValueOrDefault(op) + 1;
        if (hits++ >= 25) return;
        var from = Math.Max(0, at - 40);
        Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime():HH:mm:ss.fff} {Opcodes.Name(op),-12} len={body.Length,5} at={at,4}");
        Console.WriteLine($"   head: {Wire.Hex(body[..Math.Min(body.Length, 24)], 24)}");
        Console.WriteLine($"   near: {Wire.Hex(body[from..Math.Min(body.Length, at + needle.Length + 24)], 120)}");
    };
    replay.RunToEnd();
    Console.WriteLine($"\n{hits} packets contain \"{text}\", {embedded} embedded bundles");
    foreach (var (op, n) in perOpcode.OrderByDescending(x => x.Value)) Console.WriteLine($"   {Opcodes.Name(op),-12} {n}");
    return 0;
}

// Damage-type field per class: which values occur, how often, and how hard those hits are relative to the class's
// type-0 hits of the same skill (a crit multiplies damage, so its ratio stands out).
int Crits(string path)
{
    var replay = new PcapReplaySource(data, path, realtime: false);
    var byClass = new Dictionary<string, Dictionary<string, (int N, double Ratio, int RatioN)>>();
    var plainAvg = new Dictionary<long, (double Sum, int N)>();
    static long BaseKey(DamageEvent d) => ((long)d.SourceId << 32) | (uint)d.SkillCode;
    var all = new List<(string Cls, string Key, DamageEvent D)>();
    replay.Pipeline.Parser.DamageTap = (d, layout, type, mods, dir) =>
    {
        var cls = GameData.ClassFromSkill(d.SkillCode);
        var label = cls != GameClass.Unknown ? cls.ToString() : d.SkillCode < 1_000_000 ? "summon/6-digit" : "npc/other";
        var key = $"L{layout} type={type} mods={mods:X2}";
        all.Add((label, key, d));
        if (type == 2 && mods == 0 && d.HitCount == 1)
        {
            var a = plainAvg.GetValueOrDefault(BaseKey(d));
            plainAvg[BaseKey(d)] = (a.Sum + d.Damage, a.N + 1);
        }
    };
    replay.RunToEnd();
    foreach (var (cls, key, d) in all)
    {
        if (!byClass.TryGetValue(cls, out var keys)) byClass[cls] = keys = new();
        var e = keys.GetValueOrDefault(key);
        e.N++;
        if (d.HitCount == 1 && plainAvg.TryGetValue(BaseKey(d), out var p) && p.N >= 3)
        {
            e.Ratio += d.Damage / (p.Sum / p.N);
            e.RatioN++;
        }
        keys[key] = e;
    }
    Console.WriteLine("crit rate by class × target (targets with ≥ 40 player hits):");
    var targets = all.Where(x => x.Cls is not ("npc/other" or "summon/6-digit")).GroupBy(x => x.D.TargetId).Where(g => g.Count() >= 40);
    foreach (var t in targets.OrderByDescending(g => g.Count()).Take(8))
    {
        var parts = t.GroupBy(x => x.Cls).OrderBy(g => g.Key)
            .Select(g => $"{g.Key[..4]} {g.Count(x => x.Key.Contains("type=3"))}/{g.Count()}");
        Console.WriteLine($"   target {t.Key,6} ({t.Count(),5} hits): {string.Join(" · ", parts)}");
    }
    foreach (var (cls, keys) in byClass.OrderBy(k => k.Key))
    {
        var total = keys.Values.Sum(v => v.N);
        Console.WriteLine($"\n{cls}  ({total} records)");
        foreach (var (key, v) in keys.OrderByDescending(k => k.Value.N).Take(14))
            Console.WriteLine($"   {key,-26} {v.N,6}  {100.0 * v.N / total,5:0.0}%   dmg vs plain same skill: {(v.RatioN > 0 ? (v.Ratio / v.RatioN).ToString("0.00") + "x" : "-")}");
    }
    return 0;
}

// Damage dealers that are not announced players: how each one spawned (41 36 kind byte, inline name, owner links).
int Entities(string path)
{
    var replay = new PcapReplaySource(data, path, realtime: false);
    var spawns = new Dictionary<uint, (byte Kind, string Hex)>();
    var players = new HashSet<uint>();
    var summons = new Dictionary<uint, string>();
    var dealt = new Dictionary<uint, (HashSet<int> Skills, int Hits, long Damage)>();
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        if (op != Opcodes.Spawn) return;
        var o = 0;
        if (!Wire.TryVarint(body, ref o, out var raw) || o >= body.Length) return;
        var id = raw > 1_000_000 ? (raw & 0x3FFF) | 0x4000 : raw;
        spawns[id] = (body[o], Wire.Hex(body, 72));
    };
    replay.EventDecoded += e =>
    {
        switch (e)
        {
            case SelfIdentifiedEvent s: players.Add(s.ActorId); break;
            case PlayerSeenEvent p: players.Add(p.ActorId); break;
            case SummonSeenEvent s: summons[s.ActorId] = s.OwnerId != 0 ? $"owner {s.OwnerId}" : $"owner name {s.OwnerName}"; break;
            case DamageEvent d when GameData.ClassFromSkill(d.SkillCode) != GameClass.Unknown || d.SkillCode < 1_000_000:
                var x = dealt.GetValueOrDefault(d.SourceId, (Skills: new HashSet<int>(), Hits: 0, Damage: 0L));
                x.Skills.Add(d.SkillCode);
                dealt[d.SourceId] = (x.Skills, x.Hits + 1, x.Damage + d.Damage);
                break;
        }
    };
    replay.RunToEnd();
    var targets = new Dictionary<uint, long>();
    var replay2 = new PcapReplaySource(data, path, realtime: false);
    replay2.EventDecoded += e =>
    {
        if (e is DamageEvent d && GameData.ClassFromSkill(d.SkillCode) != GameClass.Unknown)
            targets[d.TargetId] = targets.GetValueOrDefault(d.TargetId) + d.Damage;
    };
    replay2.RunToEnd();
    Console.WriteLine("spawn kinds — as damage dealers (class skills) / as targets of class-skill damage:");
    foreach (var g in spawns.GroupBy(s => s.Value.Kind).OrderBy(g => g.Key))
        Console.WriteLine($"   {g.Key:X2}: spawned {g.Count(),5}, dealt damage {g.Count(s => dealt.ContainsKey(s.Key)),4}, took damage {g.Count(s => targets.ContainsKey(s.Key)),4} ({Format.Compact(g.Sum(s => targets.GetValueOrDefault(s.Key)))})");
    var unknown = dealt.Where(kv => !players.Contains(kv.Key)).OrderByDescending(kv => kv.Value.Damage).ToList();
    Console.WriteLine($"class-skill damage dealers: {dealt.Count}, announced players {dealt.Keys.Count(players.Contains)}, others {unknown.Count}");
    var sixDigit = dealt.Where(kv => players.Contains(kv.Key) && kv.Value.Skills.Any(s => s < 1_000_000)).ToList();
    Console.WriteLine($"announced players using 6-digit skills: {sixDigit.Count} " +
                      string.Join(", ", sixDigit.SelectMany(kv => kv.Value.Skills.Where(s => s < 1_000_000)).Distinct().Take(8).Select(s => $"{s} {data.SkillName(s)}")));
    var byKind = unknown.GroupBy(kv => spawns.TryGetValue(kv.Key, out var s) ? $"{s.Kind:X2}" : "no spawn")
        .Select(g => $"{g.Key}: {g.Count()} actors, {g.Count(kv => summons.ContainsKey(kv.Key))} with owner");
    Console.WriteLine("by spawn kind: " + string.Join(" | ", byKind));
    var suspicious = unknown.Where(kv => !summons.ContainsKey(kv.Key) && (spawns.ContainsKey(kv.Key) || kv.Value.Skills.Count <= 2)).ToList();
    Console.WriteLine($"not linked to an owner and spawned or ≤ 2 skills: {suspicious.Count}");
    foreach (var g in suspicious.GroupBy(kv => string.Join(",", kv.Value.Skills.Order().Take(3).Select(s => data.SkillName(s)))).OrderByDescending(g => g.Count()).Take(15))
        Console.WriteLine($"   {g.Count(),4}× [{g.Key}]  spawned: {g.Count(kv => spawns.ContainsKey(kv.Key))}");
    foreach (var (id, v) in suspicious.Take(25))
    {
        var skills = string.Join(",", v.Skills.Take(4).Select(s => data.SkillName(s)));
        var sp = spawns.TryGetValue(id, out var s) ? s.Hex : "-";
        Console.WriteLine($"{id,6} {v.Hits,4} hits {Format.Compact(v.Damage),7} [{skills}] {(summons.TryGetValue(id, out var own) ? own : "")}\n        spawn: {sp}");
    }
    return 0;
}

// For each spawned effect entity without an owner: the player casts (02 38) of the same skill family just before it
// appeared — how many candidates there are and how far back.
int Casts(string path)
{
    var replay = new PcapReplaySource(data, path, realtime: false);
    var casts = new List<(long T, uint Actor, int Skill, uint Target)>();
    var spawns = new Dictionary<uint, (long T, byte Kind, int Code)>();
    var owned = new HashSet<uint>();
    var firstSkill = new Dictionary<uint, int>();
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        var o = 0;
        if (op == Opcodes.Of(0x02, 0x38))
        {
            if (!Wire.TryVarint(body, ref o, out var actor) || !Wire.TryVarint(body, ref o, out _) || !Wire.TryU32(body, ref o, out var skill)) return;
            if (!Wire.TryVarint(body, ref o, out _) || !Wire.TryVarint(body, ref o, out _) || !Wire.TryVarint(body, ref o, out var target)) return;
            casts.Add((t, actor, (int)skill, target));
        }
        else if (op == Opcodes.Spawn)
        {
            if (!Wire.TryVarint(body, ref o, out var raw) || o + 12 > body.Length) return;
            var id = raw > 1_000_000 ? (raw & 0x3FFF) | 0x4000 : raw;
            spawns[id] = (t, body[o], (int)Wire.U24(body, o + 3));
        }
    };
    replay.EventDecoded += e =>
    {
        if (e is SummonSeenEvent s) owned.Add(s.ActorId);
        if (e is DamageEvent d && GameData.ClassFromSkill(d.SkillCode) != GameClass.Unknown) firstSkill.TryAdd(d.SourceId, d.SkillCode);
    };
    replay.RunToEnd();
    Console.WriteLine($"casts {casts.Count}, spawns {spawns.Count}");
    var sample = casts.Where(c => c.Actor != c.Target).Take(5);
    foreach (var c in sample) Console.WriteLine($"   cast {c.Actor} skill {c.Skill} ({data.SkillName(c.Skill)}) -> {c.Target}");
    int unique = 0, ambiguous = 0, none = 0;
    // An effect entity announces its own ticks (actor = target = itself) with the caster's skill variant + 1..9.
    var selfCode = casts.Where(c => c.Actor == c.Target).GroupBy(c => c.Actor).ToDictionary(g => g.Key, g => g.First().Skill);
    foreach (var (id, sp) in spawns.OrderBy(s => s.Value.T))
    {
        if (owned.Contains(id) || !firstSkill.TryGetValue(id, out var skill)) continue;
        if (!selfCode.TryGetValue(id, out var tick)) continue;
        var variant = tick / 10;
        var cands = casts.Where(c => c.Actor != id && c.Actor != c.Target && c.Skill / 10 == variant && c.T <= sp.T + 300 && c.T >= sp.T - 3_000)
            .GroupBy(c => c.Actor).Select(g => (Actor: g.Key, Delta: sp.T - g.Max(c => c.T), Code: g.Last().Skill)).OrderBy(c => c.Delta).ToList();
        if (cands.Count == 0) none++; else if (cands.Count == 1) unique++; else ambiguous++;
        if (unique + ambiguous + none <= 30)
            Console.WriteLine($"{id,6} kind {sp.Kind:X2} code {sp.Code} skill {skill} ({data.SkillName(skill)}): " +
                              string.Join(" | ", cands.Take(4).Select(c => $"{c.Actor} Δ{c.Delta}ms {c.Code}")));
    }
    Console.WriteLine($"owner by cast: unique {unique}, ambiguous {ambiguous}, none {none}");
    return 0;
}

// Packets that mention several world bosses at once (the in-game map's field boss list): NPC codes as u32 / u24 /
// varint at any offset. Prints the opcode, which codes sit where, and the bytes around the first few.
int BossList(string path)
{
    var minBosses = args.Length > 2 && int.TryParse(args[2], out var mb) ? mb : 3;
    var bosses = data.Npcs.Values.Where(n => n.IsBoss && !n.IsDummy).Select(n => n.Code).ToHashSet();
    var replay = new PcapReplaySource(data, path, realtime: false);
    var shown = 0;
    var perOpcode = new Dictionary<ushort, int>();
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        var hits = new List<(int At, int Code, string As)>();
        for (var i = 0; i + 3 <= body.Length; i++)
        {
            var u24 = (int)Wire.U24(body, i);
            if (bosses.Contains(u24)) hits.Add((i, u24, i + 4 <= body.Length && body[i + 3] == 0 ? "u32" : "u24"));
            else if (Wire.TryVarint(body, i, out var v, out var len) && len >= 3 && bosses.Contains((int)v)) hits.Add((i, (int)v, "varint"));
        }
        if (hits.Select(h => h.Code).Distinct().Count() < minBosses || op is Opcodes.Spawn or Opcodes.Damage or Opcodes.Dot) return;
        perOpcode[op] = perOpcode.GetValueOrDefault(op) + 1;
        if (shown++ >= 6) return;
        Console.WriteLine($"\n{DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime():HH:mm:ss.fff} {Opcodes.Name(op)} len={body.Length} bosses={hits.Select(h => h.Code).Distinct().Count()}");
        Console.WriteLine($"   head: {Wire.Hex(body[..Math.Min(body.Length, 32)], 32)}");
        foreach (var h in hits.Take(14))
        {
            var from = Math.Max(0, h.At - 6);
            Console.WriteLine($"   @{h.At,5} {h.As,-6} {h.Code} {data.NpcName(h.Code),-34} {Wire.Hex(body[from..Math.Min(body.Length, h.At + 26)], 32)}");
        }
    };
    replay.RunToEnd();
    Console.WriteLine($"\npackets with ≥ 3 boss codes: {shown}");
    foreach (var (op, n) in perOpcode.OrderByDescending(x => x.Value)) Console.WriteLine($"   {Opcodes.Name(op),-12} {n}");
    return 0;
}

// Packets carrying several timestamps in the future (unix seconds as u32/varint, unix ms as u64/varint): respawn lists.
int Times(string path)
{
    var replay = new PcapReplaySource(data, path, realtime: false);
    var perOpcode = new Dictionary<ushort, int>();
    var shown = 0;
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        var nowSec = t / 1000;
        var hits = new List<(int At, string Kind, long Value)>();
        for (var i = 0; i + 4 <= body.Length; i++)
        {
            long u32 = Wire.U32(body, i);
            if (u32 > nowSec - 600 && u32 < nowSec + 12 * 3600) hits.Add((i, "s32", u32));
            if (i + 8 <= body.Length)
            {
                var u64 = (long)BitConverter.ToUInt64(body.Slice(i, 8));
                if (u64 > t - 600_000 && u64 < t + 12 * 3_600_000L) hits.Add((i, "ms64", u64));
            }
            if (Wire.TryVarint(body, i, out var v, out var len) && len >= 4)
            {
                if (v > nowSec - 600 && v < nowSec + 12 * 3600) hits.Add((i, "sVar", v));
            }
        }
        if (hits.Count < 3) return;
        perOpcode[op] = perOpcode.GetValueOrDefault(op) + 1;
        if (shown++ >= 8) return;
        Console.WriteLine($"\n{DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime():HH:mm:ss.fff} {Opcodes.Name(op)} len={body.Length} future stamps={hits.Count}");
        Console.WriteLine($"   head: {Wire.Hex(body[..Math.Min(body.Length, 40)], 40)}");
        foreach (var h in hits.Take(16))
        {
            var at = h.Kind == "ms64" ? DateTimeOffset.FromUnixTimeMilliseconds(h.Value) : DateTimeOffset.FromUnixTimeSeconds(h.Value);
            var from = Math.Max(0, h.At - 10);
            Console.WriteLine($"   @{h.At,5} {h.Kind,-5} {at.ToLocalTime():HH:mm:ss} (in {(at.ToUnixTimeMilliseconds() - t) / 1000,6} s)  {Wire.Hex(body[from..Math.Min(body.Length, h.At + 14)], 26)}");
        }
    };
    replay.RunToEnd();
    Console.WriteLine($"\npackets with ≥ 3 future timestamps: {shown}");
    foreach (var (op, n) in perOpcode.OrderByDescending(x => x.Value)) Console.WriteLine($"   {Opcodes.Name(op),-12} {n}");
    return 0;
}

// The map's field boss list (01 91) decoded at each change, next to the world bosses that spawned / died in view
// (with their spawn position) — to tie list slots to NPC codes.
int FieldBosses(string path)
{
    var replay = new PcapReplaySource(data, path, realtime: false);
    string? last = null;
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime();
        if (op == 0x0191)
        {
            var key = Convert.ToHexString(body);
            if (key == last) return;
            last = key;
            var map = Wire.U32(body, 2);
            int count = body[6];
            Console.WriteLine($"\n{at:HH:mm:ss} LIST map {map}, {count} slots, len {body.Length}");
            var o = 7;
            for (var n = 0; n < count && o < body.Length; n++)
            {
                var alive = body[o++];
                if (!Wire.TryVarint(body, ref o, out var slot)) break;
                string pos = "";
                if (alive == 1)
                {
                    pos = $"pos {BitConverter.ToSingle(body.Slice(o, 4)):0} {BitConverter.ToSingle(body.Slice(o + 4, 4)):0} {BitConverter.ToSingle(body.Slice(o + 8, 4)):0}";
                    o += 12;
                }
                var extra = "";
                long ts = BitConverter.ToInt64(body.Slice(o, 8));
                if (Math.Abs(ts - t) > 40L * 86_400_000)
                {
                    extra = $" extra {body[o]:x2}";
                    o++;
                    ts = BitConverter.ToInt64(body.Slice(o, 8));
                }
                o += 8;
                var when = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime();
                var rel = (ts - t) / 1000;
                Console.WriteLine($"   {slot} {(alive == 1 ? "ALIVE" : "dead "),-5} {(alive == 1 ? "since" : "back at")} {when:HH:mm:ss} ({(rel >= 0 ? "in " : "")}{Math.Abs(rel) / 3600}h{Math.Abs(rel) / 60 % 60:00}m{Math.Abs(rel) % 60:00}s{(rel < 0 ? " ago" : "")}){extra} {pos}");
            }
            Console.WriteLine($"   tail at {o}: {Wire.Hex(body[Math.Min(o, body.Length)..], 8)}");
        }
        else if (op == Opcodes.Spawn)
        {
            var o = 0;
            if (!Wire.TryVarint(body, ref o, out var raw)) return;
            var end = Math.Min(body.Length - 14, o + 60);
            for (var i = o + 3; i < end; i++)
            {
                if (body[i] != 0 || (body[i + 1] != 0x40 && body[i + 1] != 0) || body[i + 2] != 2) continue;
                var code = (int)Wire.U24(body, i - 3);
                if (!data.Npcs.TryGetValue(code, out var def) || !def.IsBoss) return;
                Console.WriteLine($"{at:HH:mm:ss} spawn  {code} {def.Name,-32} pos {BitConverter.ToSingle(body.Slice(i + 3, 4)):0} {BitConverter.ToSingle(body.Slice(i + 7, 4)):0} {BitConverter.ToSingle(body.Slice(i + 11, 4)):0}");
                return;
            }
        }
    };
    var tracker = new CombatTracker(data, new MeterOptions());
    tracker.BossNoticed += n => Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(n.TimeMs).ToLocalTime():HH:mm:ss} {n.Kind,-7} {n.NpcCode} {data.NpcName(n.NpcCode)} (map {n.MapId})");
    replay.EventDecoded += tracker.Process;
    replay.RunToEnd();
    return 0;
}

// Full bodies of one opcode (first N packets), 32 bytes per line.
int OpcodeHistogram(string path)
{
    var stats = new Dictionary<ushort, (int N, int Min, int Max, long Sum, long First, List<string> Heads)>();
    var replay = new PcapReplaySource(data, path, realtime: false);
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        if (!stats.TryGetValue(op, out var s)) s = (0, int.MaxValue, 0, 0, t, new List<string>(3));
        if (s.Heads.Count < 3) s.Heads.Add(Wire.Hex(body, 48));
        stats[op] = (s.N + 1, Math.Min(s.Min, body.Length), Math.Max(s.Max, body.Length), s.Sum + body.Length, s.First, s.Heads);
    };
    replay.RunToEnd();
    foreach (var (op, s) in stats.OrderByDescending(kv => kv.Value.N))
    {
        Console.WriteLine($"{op >> 8:x2} {op & 0xFF:x2} {Opcodes.Name(op),-12} n={s.N,7} len={s.Min}..{s.Max} avg={s.Sum / s.N} first={DateTimeOffset.FromUnixTimeMilliseconds(s.First).ToLocalTime():HH:mm:ss.fff}");
        foreach (var h in s.Heads) Console.WriteLine($"      {h}");
    }
    return 0;
}

// Pattern: hex bytes, "u32:N" (little-endian u32) or "u32:A-B" (any aligned-or-not u32 in the range; first hit per packet printed with context).
int Grep(string path, string pattern)
{
    byte[]? needle = null;
    uint lo = 0, hi = 0;
    if (pattern.StartsWith("u32:"))
    {
        var r = pattern[4..].Split('-');
        lo = uint.Parse(r[0]);
        hi = r.Length > 1 ? uint.Parse(r[1]) : lo;
        if (lo == hi) needle = BitConverter.GetBytes(lo);
    }
    else needle = Wire.FromHex(pattern);
    var replay = new PcapReplaySource(data, path, realtime: false);
    var hits = 0;
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        var at = -1;
        if (needle is not null) at = body.IndexOf(needle);
        else
            for (var i = 0; i + 4 <= body.Length; i++)
            {
                var v = Wire.U32(body, i);
                if (v >= lo && v <= hi) { at = i; break; }
            }
        if (at < 0) return;
        hits++;
        var from = Math.Max(0, at - 16);
        Console.WriteLine($"{DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime():HH:mm:ss.fff} {op >> 8:x2} {op & 0xFF:x2} [{body.Length,5}] @{at}: {Wire.Hex(body[from..Math.Min(body.Length, at + 32)], 48)}");
    };
    replay.RunToEnd();
    Console.WriteLine($"{hits} packets");
    return 0;
}

int Export(string path, string outPath)
{
    using var file = File.Create(outPath);
    using var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest);
    using var w = new StreamWriter(gz);
    var n = 0;
    replay(path);
    void replay(string p)
    {
        var r = new PcapReplaySource(data, p, realtime: false);
        r.Pipeline.Parser.Tap = (t, op, body) =>
        {
            if (op == Opcodes.Heartbeat) return;
            n++;
            w.Write(t);
            w.Write(' ');
            w.Write(op.ToString("x4"));
            w.Write(' ');
            w.WriteLine(Convert.ToHexString(body));
        };
        r.RunToEnd();
    }
    Console.WriteLine($"{n} packets -> {outPath}");
    return 0;
}

int HexDump(string path, ushort opcode, int max)
{
    var replay = new PcapReplaySource(data, path, realtime: false);
    var n = 0;
    var seen = new HashSet<string>();
    replay.Pipeline.Parser.Tap = (t, op, body) =>
    {
        if (op != opcode) return;
        // Repeats of an identical packet are skipped: only distinct contents count towards the limit.
        if (!seen.Add(Convert.ToHexString(body)) || n++ >= max) return;
        Console.WriteLine($"\n{DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime():HH:mm:ss.fff} {Opcodes.Name(op)} len={body.Length}");
        for (var i = 0; i < body.Length; i += 32)
            Console.WriteLine($"   {i,4}: {Wire.Hex(body[i..Math.Min(body.Length, i + 32)], 32)}");
    };
    replay.RunToEnd();
    Console.WriteLine($"\n{n} packets");
    return 0;
}

void PrintFights(CombatTracker tracker)
{
    foreach (var seg in tracker.Segments().Reverse())
    {
        var snap = tracker.Snapshot(seg.Id, long.MaxValue / 2)!;
        Console.WriteLine($"\n== {snap.Title} · {Format.ClockPrecise(snap.CombatMs)} · {Format.Compact(snap.TotalDamage)} dmg · {Format.Compact(snap.PartyDps)}/s · {snap.Reason} · {snap.PlayerCount} players");
        if (args.Contains("--brief"))
        {
            var small = snap.Combatants.Count(c => !c.IsUnknownSummons && c.Share < 0.0005);
            var pets = snap.Combatants.FirstOrDefault(c => c.IsUnknownSummons);
            Console.WriteLine($"   under 0.05%: {small} · unknown summons: {(pets is null ? "-" : $"{Format.Compact(pets.Damage)} ({Format.Percent(pets.Share)})")}");
            continue;
        }
        foreach (var c in snap.Combatants)
            Console.WriteLine($"   {c.Name,-16} {c.Class,-13} {Format.Compact(c.Dps),9}/s {Format.Compact(c.Damage),9} {Format.Percent(c.Share),7}  crit {Format.Percent(c.CritRate)}");
    }
}

sealed class EventStats(GameData data)
{
    private readonly Dictionary<string, int> _byType = new();
    private readonly List<string> _identities = new();
    private readonly List<string> _samples = new();
    public int Total;

    public void Add(GameEvent e)
    {
        Total++;
        var type = e.GetType().Name;
        _byType[type] = _byType.GetValueOrDefault(type) + 1;
        switch (e)
        {
            case SelfIdentifiedEvent s:
                _identities.Add($"SELF  {s.Name} id={s.ActorId} server={s.ServerId} class={s.Class} lvl={s.Level}");
                break;
            case PlayerSeenEvent p when _identities.Count < 60:
                _identities.Add($"PLAYER {p.Name} id={p.ActorId} server={p.ServerId} class={p.Class}");
                break;
            case ZoneChangedEvent z:
                _identities.Add($"ZONE  {z.MapId} {z.ZoneName} teleport={z.IsTeleport}");
                break;
            case DamageEvent d when _samples.Count < 40:
                _samples.Add($"{d.SourceId,7} -> {d.TargetId,7}  {data.SkillName(d.SkillCode),-28} {d.Damage,10:#,0} x{d.HitCount} scalar={d.PowerScalar} {d.Flags}");
                break;
        }
    }

    public void Print(PacketPipeline p)
    {
        Console.WriteLine("\n-- events");
        foreach (var (k, v) in _byType.OrderByDescending(x => x.Value)) Console.WriteLine($"   {k,-22} {v}");
        Console.WriteLine("-- identities / zones");
        foreach (var s in _identities.Distinct()) Console.WriteLine("   " + s);
        Console.WriteLine("-- damage samples");
        foreach (var s in _samples) Console.WriteLine("   " + s);
        Console.WriteLine($"-- parser: damage records {p.Parser.DamageRecords}, rejected {p.Parser.DamageRejected}, cast markers {p.Parser.CastMarkers}, " +
                          $"embedded bundles {p.Parser.EmbeddedBundles}, heartbeats {p.Parser.Heartbeats}, gaps {p.Gaps}");
        Console.WriteLine("-- top opcodes");
        foreach (var (op, n) in p.Parser.OpcodeCounts.OrderByDescending(x => x.Value).Take(25))
            Console.WriteLine($"   {Opcodes.Name(op),-14} {n}");
        Console.WriteLine("-- locked flows");
        foreach (var f in p.LockedFlows) Console.WriteLine("   " + f);
    }
}

/// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post it to the thread pool, out of order).</summary>
sealed class ConsoleProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
