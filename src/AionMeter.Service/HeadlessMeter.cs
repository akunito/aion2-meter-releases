using System.Buffers;
using System.Globalization;
using System.Text.Json;
using AionMeter.Core;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Game;
using AionMeter.Service.Overlay;

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
    /// <summary>Unix socket for the Linux UI (OverlayServer.DefaultPath() in the daemon); null = no socket.</summary>
    public string? Socket { get; init; }
    /// <summary>With <see cref="Replay"/>: keep running (and serving the socket) after the file ends, until cancelled.</summary>
    public bool Hold { get; init; }
    /// <summary>With <see cref="Replay"/>: feed the packets at their recorded pace instead of as fast as possible.</summary>
    public bool RealtimeReplay { get; init; }
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
    private readonly OverlayServer? _server;
    private readonly OverlaySelection _sel = new();
    private readonly OverlayBuilder _builder = new();
    private readonly PositionBuilder _position = new();
    private PositionModel? _lastPosition;
    private long _lastPositionAt;
    private string? _announcement;
    private long _announcementAt;

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
            ? data => new PcapReplaySource(data, pcap, realtime: options.RealtimeReplay, () => availability.Present)
            : data => new LiveCapture(data, new LiveCaptureOptions
            {
                DeviceName = options.Device ?? Settings.CaptureDevice,
                RecordDirectory = Settings.RecordPackets ? Path.Combine(AppSettings.AppDataDir, "captures") : null,
                RecordKeepBytes = (long)(Settings.CaptureKeepGb * 1_073_741_824),
                LibraryPresent = () => availability.Present,
            });
        Engine = new MeterEngine(Settings, factory, availability);
        Engine.FightClosed += OnFightClosed;
        Engine.CharacterChanged += () => Event("character", w => w.WriteString("key", Engine.BoundKey), $"character {Engine.BoundKey}");
        // A2M-48: the WPF NameNodeWindow prompt, for the Linux UI (the atlas asks for the kind; the name lands in atlas/objects.json, read back by mtime)
        Engine.NodeNameWanted += code => Event("node-name-wanted", w => w.WriteNumber("code", code), $"atlas: unnamed node {code} gathered", "node-name-wanted");
        Log.Info($"Daemon started. Data: {Engine.Data.Skills.Count} skills, {Engine.Data.Npcs.Count} npcs; state in {AppSettings.AppDataDir}");
        if (options.Socket is { Length: > 0 } sock)
        {
            try { _server = new OverlayServer(sock); }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or UnauthorizedAccessException) { Log.Error($"UI socket {sock}", e); Human($"UI socket {sock} failed: {e.Message}"); }
        }
        Human($"AionMeter daemon · state {AppSettings.AppDataDir} · {Engine.Data.Skills.Count} skills, {Engine.Data.Npcs.Count} npcs{(options.Offline ? " · offline" : "")}{(_server is null ? "" : " · ui " + _server.Path)}");
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
                    _server?.Broadcast(StatusLine(st));
                }
                // A2M-48: the Linux UI saves settings.json / the price-watch list itself; a 2 s mtime poll picks them up (`reload-settings` is the fast path)
                if (ticks % 10 == 0) WatchFiles();
                if (_server is not null) Serve(st, ticks);
                if (++ticks % 5 == 0) Alerts();
                if (_o.StatusEverySeconds > 0 && now - lastStatus >= _o.StatusEverySeconds * 1000L) { lastStatus = now; Heartbeat(st); }
                if (now >= nextTelemetry) { nextTelemetry = now + 15 * 60_000; _ = Engine.SendTelemetryIfDueAsync(); }
                if (_o.OnceSeconds > 0 && now - start >= _o.OnceSeconds * 1000L) break;
                if (_o.Replay is not null)
                {
                    replayRan |= st.State == CaptureState.Capturing;
                    if (st.State is CaptureState.Stopped or CaptureState.Error && (replayRan || now - start > 2_000) && !_o.Hold) break;
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
            _server?.Dispose();
            Log.Info("Daemon stopped");
        }
        return 0;
    }

    private bool _forceOverlay;

    private void WatchFiles()
    {
        if (Engine.ReloadSettingsIfChanged()) { _forceOverlay = true; Human($"settings reloaded (language {Settings.Language}, measure {Settings.TargetMode})"); }
        if (Engine.Prices.ReloadIfChanged()) Human($"price watch list reloaded ({Engine.Prices.Count} items)");
        if (Engine.ReloadObjectNamesIfChanged()) Human("atlas node names reloaded");
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
            var minutes = (int)Math.Ceiling((next - DateTimeOffset.Now).TotalMinutes);
            var t = UiText.Current;
            // the App's balloon text; the Linux card shows it as a line for 60 s (overlay `announcement`)
            var text = minutes > 0 ? string.Format(t.AlertSoon, name, minutes, next.ToLocalTime().ToString("HH:mm")) : string.Format(t.AlertNow, name, next.ToLocalTime().ToString("HH:mm"));
            _announcement = text;
            _announcementAt = Environment.TickCount64;
            _forceOverlay = true;
            Event("boss-alert", w => { w.WriteNumber("npc", timer.NpcCode); w.WriteString("name", name); w.WriteString("spawn", next); w.WriteString("text", text); },
                $"boss {name} respawns {next.ToLocalTime():HH:mm}", "boss-alert");
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
        Event("fight", Body, human, "fight-closed");
        void Body(Utf8JsonWriter w)
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
        }
    }

    private void Human(string line)
    {
        lock (_outGate) { _human.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"); _human.Flush(); }
    }

    /// <summary>A human line, the --json-events object (flat) and, with <paramref name="socketType"/>, the same body to the UI socket as `data`.</summary>
    private void Event(string type, Action<Utf8JsonWriter>? body, string human, string? socketType = null)
    {
        Human(human);
        if (socketType is not null && _server is { ClientCount: > 0 } srv)
            srv.Broadcast(SocketLine(socketType, w => { w.WriteStartObject(); body?.Invoke(w); w.WriteEndObject(); }));
        if (_json is null) return;
        lock (_outGate)
        {
            if (_disposed) return;
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

    // ------------------------------------------------------------ UI socket (A2M-48 phase 3)

    /// <summary>`{"type":…,"ts":…,"data":…}\n`, one array shared by every client's queue.</summary>
    private byte[] SocketLine(string type, Action<Utf8JsonWriter>? data)
    {
        lock (_outGate)
        {
            if (_disposed) return [];   // Client.Send / Broadcast skip an empty line
            _buf.ResetWrittenCount();
            _w.Reset(_buf);
            _w.WriteStartObject();
            _w.WriteString("type", type);
            _w.WriteNumber("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (data is not null)
            {
                _w.WritePropertyName("data");
                data(_w);
            }
            _w.WriteEndObject();
            _w.Flush();
            var line = new byte[_buf.WrittenCount + 1];
            _buf.WrittenSpan.CopyTo(line);
            line[^1] = (byte)'\n';
            return line;
        }
    }

    private byte[] StatusLine(CaptureStatus st)
    {
        var status = new OverlayStatus(st.State, st.Message, Engine.BoundKey, _builder.Version, AppSettings.AppDataDir, _o.Replay is not null);
        return SocketLine("status", w => JsonSerializer.Serialize(w, status, OverlayJson.Default.OverlayStatus));
    }

    /// <summary>Each 200 ms tick: run the clients' commands, then an `overlay` snapshot — every tick (5 Hz) while a fight is live, every
    /// fifth (1 Hz) when idle, at once after a command or a new client.</summary>
    private void Serve(CaptureStatus st, int ticks)
    {
        var srv = _server!;
        var force = _forceOverlay;
        _forceOverlay = false;
        while (srv.Commands.TryDequeue(out var c))
        {
            switch (c.Cmd)
            {
                case "ping":
                    c.From.Send(SocketLine("pong", null));
                    break;
                case "status":
                    c.From.Send(StatusLine(st));
                    break;
                case "reset":
                    Engine.Restart();
                    _sel.SelectLive();
                    Log.Info("UI: meter restarted");
                    force = true;
                    break;
                case "show-fight":
                    if (!_sel.Select(Engine, c.Arg ?? "live")) c.From.Send(Error($"no fight {c.Arg}"));
                    force = true;
                    break;
                case "set-measure":
                    TargetMode? mode = c.Arg?.ToLowerInvariant() switch { "boss" or "bossonly" => TargetMode.BossOnly, "all" => TargetMode.All, _ => null };
                    if (mode is not { } m) { c.From.Send(Error("mode is boss or all")); break; }
                    Engine.ReloadSettingsIfChanged();   // never write back over a save the UI made in the last 2 s
                    Settings.TargetMode = m;
                    Engine.ApplySettings();
                    Settings.Save();
                    force = true;
                    break;
                case "fight-detail":
                    // the breakdown of a live/session fight (saved ones the UI may read from fights/ itself, "h:" works here too)
                    if (FightDetail.Resolve(Engine, _sel, c.Arg ?? "live") is not { } fd) { c.From.Send(Error($"no fight {c.Arg}")); break; }
                    var asked = c.Arg ?? "live";
                    c.From.Send(SocketLine("fight-detail", w =>
                    {
                        w.WriteStartObject();
                        w.WriteString("id", fd.Id);
                        w.WriteString("asked", asked);
                        w.WriteBoolean("live", fd.Live);
                        w.WritePropertyName("record");
                        JsonSerializer.Serialize(w, fd.Record, Core.Storage.HistoryStore.JsonOptions);
                        w.WriteEndObject();
                    }));
                    break;
                case "reload-settings":
                    var reloaded = Engine.ReloadSettingsIfChanged(force: true);
                    Engine.Prices.ReloadIfChanged();
                    c.From.Send(SocketLine("settings-reloaded", w => { w.WriteStartObject(); w.WriteString("language", Settings.Language); w.WriteBoolean("reloaded", reloaded); w.WriteEndObject(); }));
                    force = true;
                    break;
                case "price-poll":
                    // Check now from the Linux UI ("mode":"added" = a watch was just added: no cooldown). Answered when the poll is over and
                    // price-watch.json / price-history.json are written, so the UI re-reads them on the reply.
                    _ = PricePoll(c.From, c.Arg == "added");
                    break;
                case "timer":
                    // A2M-48: the Linux UI's Boss Timers buttons; the daemon owns boss-timers.json (the UI's copy is read-only and follows the file)
                    if (!TimerCommand(c)) { c.From.Send(Error("timer: id is killed|watch|unwatch|respawn|remove, npc > 0")); break; }
                    srv.Broadcast(SocketLine("timers-changed", w => { w.WriteStartObject(); w.WriteNumber("server", c.Server); w.WriteNumber("npc", c.Npc); w.WriteEndObject(); }));
                    break;
                case "position":
                    _lastPosition = null;   // sent below, whatever changed
                    break;
                case "fights":
                    var list = OverlayBuilder.FightList(Engine, c.Max > 0 ? Math.Min(c.Max, 500) : 40).ToArray();
                    c.From.Send(SocketLine("fights", w => JsonSerializer.Serialize(w, list, OverlayJson.Default.OverlayFightItemArray)));
                    break;
                default:
                    c.From.Send(Error($"unknown cmd {c.Cmd}"));
                    break;
            }
        }
        if (srv.ClientCount == 0) return;
        if (srv.TakeNewClient()) { srv.Broadcast(StatusLine(st)); force = true; _lastPosition = null; }
        Position(srv, ticks);
        if (!force && ticks % 5 != 0 && Engine.Tracker.Segments() is not [{ IsActive: true }, ..]) return;
        var model = _builder.Build(Engine, _sel, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_announcement is not null)
        {
            if (Environment.TickCount64 - _announcementAt < 60_000) model = model with { Announcement = _announcement };
            else _announcement = null;
        }
        srv.Broadcast(SocketLine("overlay", w => JsonSerializer.Serialize(w, model, OverlayJson.Default.OverlayModel)));
    }

    /// <summary>
    /// A2M-48 `position`: every third tick (600 ms, ≤ 2 Hz) when the drawn point, the heading or the spots in view changed, and every 5 s
    /// regardless (a UI that just opened the atlas catches up). A replay measures ages on its own clock (the newest event), live on UtcNow.
    /// </summary>
    private void Position(OverlayServer srv, int ticks)
    {
        if (ticks % 3 != 0 && _lastPosition is not null) return;
        var now = Environment.TickCount64;
        var clock = _o.Replay is not null && Engine.Atlas.LastEventMs > 0 ? Engine.Atlas.LastEventMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var p = _position.Build(Engine.Atlas, clock);
        if (PositionBuilder.Same(_lastPosition, p) && now - _lastPositionAt < 5_000) return;
        _lastPosition = p;
        _lastPositionAt = now;
        srv.Broadcast(SocketLine("position", w => JsonSerializer.Serialize(w, p, OverlayJson.Default.PositionModel)));
    }

    private bool TimerCommand(OverlayCommand c)
    {
        if (c.Npc == 0) return false;
        var t = Engine.Timers;
        switch (c.Arg)
        {
            case "killed": t.MarkKilled(c.Server, c.Npc); break;
            case "watch": t.SetWatch(c.Server, c.Npc, true); break;
            case "unwatch": t.SetWatch(c.Server, c.Npc, false); break;
            case "respawn": t.SetRespawn(c.Server, c.Npc, c.Value); break;
            case "remove": t.Remove(c.Server, c.Npc); break;
            default: return false;
        }
        Log.Info($"UI: timer {c.Arg} server {c.Server} npc {c.Npc}{(c.Arg == "respawn" ? $" {c.Value} min" : "")}");
        return true;
    }

    private async Task PricePoll(OverlayServer.Client to, bool added)
    {
        MeterEngine.PricePollResult r;
        try { r = await Engine.PollPricesNowAsync(added).ConfigureAwait(false); }
        catch (Exception e) { Log.Error("price-poll", e); r = new MeterEngine.PricePollResult(false, e.Message, 0, 0, 0, Engine.PricesManualUntil); }
        if (r.Ran) Human($"price poll (ui): {r.Items} items, {r.Errors} failed, {r.Ms} ms");
        to.Send(SocketLine("price-polled", w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ran", r.Ran);
            w.WriteString("reason", r.Reason);
            w.WriteNumber("items", r.Items);
            w.WriteNumber("errors", r.Errors);
            w.WriteNumber("ms", r.Ms);
            w.WriteNumber("next", r.NextManual == DateTimeOffset.MinValue ? 0 : r.NextManual.ToUnixTimeMilliseconds());
            w.WriteEndObject();
        }));
    }

    private byte[] Error(string message) => SocketLine("error", w => { w.WriteStartObject(); w.WriteString("message", message); w.WriteEndObject(); });

    private bool _disposed;

    public void Dispose()
    {
        Log.Mirror = null;
        // the last fight's save runs on the thread pool (MeterEngine.OnEncounterFinished) and may call Event after RunAsync returned:
        // disposing the writer outside the gate crashed the test host with ObjectDisposedException (measured 2026-10-13 under load)
        lock (_outGate) { _disposed = true; _w.Dispose(); }
    }
}
