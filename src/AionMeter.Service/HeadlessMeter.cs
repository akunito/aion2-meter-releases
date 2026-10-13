using System.Buffers;
using System.Globalization;
using System.Text.Json;
using AionMeter.Core;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Game;

namespace AionMeter.Service;

public sealed class HeadlessOptions
{
    /// <summary>Overrides <see cref="AppSettings.AppDataDir"/> (default %AppData%\AionMeter / $XDG_DATA_HOME/AionMeter).</summary>
    public string? DataDir { get; init; }
    /// <summary>Overrides the shipped data/ folder (tests).</summary>
    public string? GameDataDir { get; init; }
    /// <summary>libpcap/Npcap device; null = settings.CaptureDevice, else LiveCapture's own pick (the adapter carrying the game's flow).</summary>
    public string? Device { get; init; }
    /// <summary>A recording fed through the engine as if it were the live capture (stores bound, state written) instead of LiveCapture.</summary>
    public string? Replay { get; init; }
    /// <summary>Stop after this long; 0 = until cancelled (SIGTERM / Ctrl+C). With <see cref="Replay"/> it also stops when the file ends.</summary>
    public int OnceSeconds { get; init; }
    public bool JsonEvents { get; init; }
    public bool Offline { get; init; }
    public bool Verbose { get; init; }
    public int StatusEverySeconds { get; init; } = 10;
}

/// <summary>
/// A2M-48: what the Windows App does in the background, without a window — capture, tracker, fight history, stores, boss timers, price
/// polls, profile sync, telemetry/backup under SharePolicy, every state file — for the Linux daemon (and its smoke test).
/// Human lines go to <c>human</c>; with JsonEvents one JSON object per line goes to <c>json</c> (the daemon then sends human lines to stderr).
/// </summary>
public sealed class HeadlessMeter : IDisposable
{
    private readonly HeadlessOptions _o;
    private readonly TextWriter _human;
    private readonly TextWriter? _json;
    private readonly object _outGate = new();
    private readonly ArrayBufferWriter<byte> _buf = new(1024);
    private readonly Utf8JsonWriter _w;

    public MeterEngine Engine { get; }
    public AppSettings Settings { get; }

    public HeadlessMeter(HeadlessOptions options, TextWriter human, TextWriter? json)
    {
        _o = options;
        _human = human;
        _json = options.JsonEvents ? json : null;
        _w = new Utf8JsonWriter(_buf);
        if (options.DataDir is { Length: > 0 } dir) AppSettings.UseAppDataDir(dir);   // before the first Log line fixes its path
        if (options.GameDataDir is { Length: > 0 } gd) MeterEngine.DataDirectory = gd;
        if (options.Verbose) Log.Mirror = (level, msg) => Human($"[{level}] {msg}");

        Settings = AppSettings.Load();
        Settings.Offline = options.Offline;
        Settings.Save();   // persist any migration done while loading (the App does the same)
        UiText.Use(Settings.Language);
        Core.Net.CloudflareHttp.SolverUrl = Settings.CloudflareSolverUrl;
        var availability = CaptureAvailability.OfThisPc();
        Func<GameData, IEventSource> factory = options.Replay is { } pcap
            ? data => new PcapReplaySource(data, pcap, realtime: false, () => availability.Present)
            : data => new LiveCapture(data, new LiveCaptureOptions
            {
                DeviceName = options.Device ?? Settings.CaptureDevice,
                RecordDirectory = Settings.RecordPackets ? Path.Combine(AppSettings.AppDataDir, "captures") : null,
                LibraryPresent = () => availability.Present,
            });
        Engine = new MeterEngine(Settings, factory, availability);
        Engine.FightClosed += OnFightClosed;
        Engine.CharacterChanged += () => Event("character", w => w.WriteString("key", Engine.BoundKey), $"character {Engine.BoundKey}");
        Log.Info($"Daemon started. Data: {Engine.Data.Skills.Count} skills, {Engine.Data.Npcs.Count} npcs; state in {AppSettings.AppDataDir}");
        Human($"AionMeter daemon · state {AppSettings.AppDataDir} · {Engine.Data.Skills.Count} skills, {Engine.Data.Npcs.Count} npcs{(options.Offline ? " · offline" : "")}");
    }

    /// <summary>Ticks every 200 ms (the App's DispatcherTimer) until cancelled, the --once time is up or a replay ended; then disposes the engine (closing and saving the running fight).</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        Engine.StartCapture();
        var start = Environment.TickCount64;
        long lastStatus = 0, nextTelemetry = start + 5 * 60_000;   // the App: first after 5 min, then every 15
        var lastState = "";
        var ticks = 0;
        var replayRan = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                Engine.Tick();
                var st = Engine.CaptureStatus;
                if (st.Message != lastState)
                {
                    lastState = st.Message;
                    Event("status", w => { w.WriteString("state", st.State.ToString()); w.WriteString("message", st.Message); }, $"capture: {st.Message}");
                }
                if (++ticks % 5 == 0) Alerts();
                if (_o.StatusEverySeconds > 0 && now - lastStatus >= _o.StatusEverySeconds * 1000L) { lastStatus = now; Heartbeat(st); }
                if (now >= nextTelemetry) { nextTelemetry = now + 15 * 60_000; _ = Engine.SendTelemetryIfDueAsync(); }
                if (_o.OnceSeconds > 0 && now - start >= _o.OnceSeconds * 1000L) break;
                if (_o.Replay is not null)
                {
                    replayRan |= st.State == CaptureState.Capturing;
                    if (st.State is CaptureState.Stopped or CaptureState.Error && (replayRan || now - start > 2_000)) break;
                }
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch (TaskCanceledException) { }
            }
        }
        finally
        {
            Engine.FlushState();
            Heartbeat(Engine.CaptureStatus);
            Engine.Dispose();

            Event("stopped", null, "stopped");
            Log.Info("Daemon stopped");
        }
        return 0;
    }

    private void Heartbeat(CaptureStatus st)
    {
        var a = Engine.Atlas;
        var h = Engine.CaptureHealth();
        Event("heartbeat", w =>
        {
            w.WriteString("state", st.State.ToString());
            w.WriteNumber("packets", st.Packets);
            w.WriteNumber("events", st.Events);
            if (h is { } hh) { w.WriteNumber("received", hh.Received); w.WriteNumber("dropped", hh.Dropped); w.WriteNumber("gaps", hh.Gaps); }
            w.WriteString("character", Engine.BoundKey);
            w.WriteNumber("server", Engine.Tracker.SelfServerId);
            w.WriteNumber("map", a.MapId);
            w.WriteNumber("x", a.SelfX);
            w.WriteNumber("y", a.SelfY);
            w.WriteNumber("stacks", Engine.Inventory.All().Count);
        }, string.Create(CultureInfo.InvariantCulture,
            $"{st.State} · events {st.Events} · {(Engine.BoundKey.Length > 0 ? Engine.BoundKey : "no character yet")} · map {a.MapId} ({a.SelfX:0},{a.SelfY:0}) · {Engine.Inventory.All().Count} stacks{(h is { } x ? $" · pcap {x.Received} rx {x.Dropped} drop {x.Gaps} gaps" : "")}"));
    }

    /// <summary>What the App turns into toasts and chimes: due boss respawns and price hits, as lines/events here.</summary>
    private void Alerts()
    {
        foreach (var timer in Engine.Timers.TakeDueAlerts(DateTimeOffset.Now, Settings.BossAlertMinutes))
        {
            var next = timer.NextSpawn!.Value;
            var name = Engine.Data.NpcName(timer.NpcCode);
            Event("boss-alert", w => { w.WriteNumber("npc", timer.NpcCode); w.WriteString("name", name); w.WriteString("spawn", next); },
                $"boss {name} respawns {next.ToLocalTime():HH:mm}");
        }
        foreach (var p in Engine.TakePriceHits())
            Event("price-hit", w => { w.WriteNumber("item", p.Item); w.WriteString("name", p.Name); w.WriteNumber("price", p.LastPrice ?? 0); w.WriteNumber("max", p.MaxPrice); w.WriteBoolean("sell", p.Sell); },
                $"price {p.Name}: {p.LastPrice ?? 0:N0} (max {p.MaxPrice:N0}, {p.LastListings ?? 0} listings)");
    }

    private void OnFightClosed(FightRecord record, string? path)
    {
        var s = record.Summary;
        CombatantSnapshot? self = null, top = null;
        foreach (var c in s.Combatants)
        {
            if (c.IsUnknownSummons) continue;
            if (c.IsSelf) self = c;
            if (top is null || c.Dps > top.Dps) top = c;
        }
        var title = s.Boss is { } b ? "boss " + b.Name : s.Title;
        var human = string.Create(CultureInfo.InvariantCulture,
            $"fight {Format.ClockShort(s.CombatMs)} {title}: {(self is null ? "you -" : "you " + Format.Compact(self.Dps) + "/s")}{(top is null || ReferenceEquals(top, self) ? "" : $", top {top.Name} {Format.Compact(top.Dps)}/s")} · {s.PlayerCount} players · {s.Reason}{(path is null ? " (not kept)" : "")}");
        Event("fight", w =>
        {
            w.WriteString("id", s.Id);
            w.WriteString("title", s.Title);
            if (s.Boss is { } bb) { w.WriteNumber("boss", bb.NpcCode); w.WriteString("bossName", bb.Name); }
            w.WriteString("zone", s.Zone);
            w.WriteString("startedAt", s.StartedAt);
            w.WriteNumber("combatMs", s.CombatMs);
            w.WriteString("reason", s.Reason.ToString());
            w.WriteNumber("totalDamage", s.TotalDamage);
            w.WriteNumber("partyDps", Math.Round(s.PartyDps, 1));
            if (self is not null) w.WriteNumber("selfDps", Math.Round(self.Dps, 1));
            if (top is not null) { w.WriteString("topName", top.Name); w.WriteString("topClass", top.Class.ToString()); w.WriteNumber("topDps", Math.Round(top.Dps, 1)); }
            w.WriteNumber("players", s.PlayerCount);
            w.WriteString("file", path);
        }, human);
    }

    private void Human(string line)
    {
        lock (_outGate) { _human.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"); _human.Flush(); }
    }

    private void Event(string type, Action<Utf8JsonWriter>? body, string human)
    {
        Human(human);
        if (_json is null) return;
        lock (_outGate)
        {
            _buf.ResetWrittenCount();
            _w.Reset(_buf);
            _w.WriteStartObject();
            _w.WriteString("type", type);
            _w.WriteString("ts", DateTimeOffset.Now);
            body?.Invoke(_w);
            _w.WriteEndObject();
            _w.Flush();
            _json.WriteLine(System.Text.Encoding.UTF8.GetString(_buf.WrittenSpan));
            _json.Flush();
        }
    }

    public void Dispose()
    {
        Log.Mirror = null;
        _w.Dispose();
    }
}
