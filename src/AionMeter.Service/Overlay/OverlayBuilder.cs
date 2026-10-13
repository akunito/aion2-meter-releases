using System.Globalization;
using AionMeter.Core;
using AionMeter.Core.Atlas;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Storage;

namespace AionMeter.Service.Overlay;

/// <summary>Which fight the card shows: live (default), an in-memory segment, or a saved fight (takes precedence).</summary>
public sealed class OverlaySelection
{
    public sealed record SavedFight(HistoryEntry Entry, FightRecord Record, string Title);

    public Guid? Segment { get; private set; }
    public SavedFight? Saved { get; private set; }
    /// <summary>When the player last picked a fight: a pull that starts after it jumps back to live.</summary>
    public DateTimeOffset SelectedAt { get; private set; } = DateTimeOffset.MinValue;
    public bool IsLive => Segment is null && Saved is null;

    public void SelectLive()
    {
        Segment = null;
        Saved = null;
    }

    public void SelectSegment(Guid? segment)
    {
        SelectedAt = DateTimeOffset.Now;
        Saved = null;
        Segment = segment;
    }

    public void SelectSaved(HistoryEntry entry, FightRecord record, string title)
    {
        SelectedAt = DateTimeOffset.Now;
        Saved = new SavedFight(entry, record, title);
        Segment = null;
    }

    /// <summary>`show-fight` ids: "live", "s:&lt;guid&gt;", "h:&lt;history file name&gt;". False when the id names nothing.</summary>
    public bool Select(MeterEngine meter, string id)
    {
        if (id is "live" or "") { SelectLive(); return true; }
        if (id.StartsWith("s:", StringComparison.Ordinal) && Guid.TryParse(id.AsSpan(2), out var g)) { SelectSegment(g); return true; }
        if (!id.StartsWith("h:", StringComparison.Ordinal)) return false;
        var file = id[2..];
        foreach (var h in meter.History.List())
        {
            if (h.FileName != file) continue;
            if (meter.History.Load(h.Path) is not { } record) return false;
            SelectSaved(h, record, meter.DisplayTitle(h));
            return true;
        }
        return false;
    }

    public string Id => Saved is { } s ? "h:" + s.Entry.FileName : Segment is { } g ? "s:" + g.ToString("N") : "live";
}

/// <summary>
/// Builds the overlay card from the engine: the one code path behind the WPF OverlayViewModel and the daemon's `overlay` socket
/// messages (A2M-48 phase 3). Not thread-safe: call it from the thread that ticks the engine (WPF dispatcher / daemon loop).
/// </summary>
public sealed class OverlayBuilder
{
    private readonly List<BossSpawn> _spawns = new();
    private readonly List<OverlayPill> _pills = new();
    private long _timersDue;   // TickCount64 of the next recompute; 0 = now
    private string _timersLabel = "";
    private OverlayPill[] _pillArray = [];
    private InstanceTimerEvent? _limitTimer;
    private string _limitTip = "";

    public string Version { get; init; } = "v" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>Pills and the boss-timer label recompute once a second; this forces the next Build to redo them (after a sync by hand).</summary>
    public void InvalidateTimers()
    {
        _timersDue = 0;
        _limitTimer = null;   // its tooltip is in the UI language too
    }

    public OverlayModel Build(MeterEngine meter, OverlaySelection sel, long nowMs, string? updateBanner = null, bool gearFits = true)
    {
        var settings = meter.Settings;
        var t = UiText.Current;
        // Looking at an old fight when a new pull starts: jump back to live so the fight is never missed.
        if (!sel.IsLive && meter.Tracker.Segments() is [{ IsActive: true } running, ..] && running.StartedAt > sel.SelectedAt)
            sel.SelectLive();

        OverlayFight fight;
        OverlayView view;
        string? when;
        if (sel.Saved is { } saved)
        {
            fight = Fight(saved.Record.Summary with { Title = saved.Title, Zone = meter.DisplayZone(saved.Record.Summary.Zone) },
                settings.MaxRows, settings.BarsRelativeToTop, settings.ShowGear && gearFits);
            view = OverlayView.Saved;
            when = saved.Entry.StartedAt.ToString("dd.MM HH:mm");
        }
        else
        {
            var snap = meter.Tracker.Snapshot(sel.Segment, nowMs);
            if (snap is null && sel.Segment is not null)
            {
                sel.SelectLive();   // the selected segment fell out of the in-memory list
                snap = meter.Tracker.Snapshot(null, nowMs);
            }
            fight = Fight(snap, settings.MaxRows, settings.BarsRelativeToTop, settings.ShowGear && gearFits);
            view = sel.Segment is not null ? OverlayView.Session : !fight.HasData ? OverlayView.Waiting : fight.IsActive ? OverlayView.Live : OverlayView.Last;
            when = snap?.StartedAt.ToString("HH:mm");
        }
        if (!settings.ShowBossPanel && fight.Boss is not null) fight = fight with { Boss = null };
        var label = view switch
        {
            OverlayView.Waiting => t.WaitingLabel,
            OverlayView.Live => t.LiveFight,
            OverlayView.Last => t.LastFight,
            OverlayView.Saved => t.SavedFight,
            _ => t.ThisSession,
        };

        // The instance's time limit (or the wait after entering), whatever fight is shown: it is about where you are now.
        var limit = Limit(meter.Tracker.NextTimer(nowMs), nowMs);
        if (Environment.TickCount64 >= _timersDue) UpdateTimers(meter);

        var status = meter.DemoRunning ? new CaptureStatus(CaptureState.Capturing, "Demo fight running") : meter.CaptureStatus;
        // Names are only sent on a loading screen: players already around when the meter started show as #id.
        var unnamed = false;
        foreach (var r in fight.Rows)
            if (r.Name.Contains('#')) { unnamed = true; break; }   // "Templar #10388": the server has not sent this name yet
        var statusText = status.State != CaptureState.Capturing ? t.CaptureStatusText(status.Message)
            : fight.MostDamageUnsent && fight.HasData ? t.DamageUnsentTip   // the footer says it, its tooltip says why
            : unnamed ? "Names appear after the next loading screen"
            : status.Message;
        // Footer reads like the card's caption; capture problems take its place so they are never missed.
        var footer = status.State is CaptureState.Capturing && fight.HasData
            ? (unnamed ? fight.Footer + " · " + t.NamesLater : fight.Footer)
            : statusText;
        var tone = status.State switch
        {
            CaptureState.Capturing => StatusTone.Good,
            CaptureState.Error => StatusTone.Error,
            CaptureState.WaitingForGame or CaptureState.Starting => StatusTone.Warn,
            _ => StatusTone.Mute,
        };
        // A2M-38: the last kill's Abyss Logs line for 60 s, under the status line
        var abyss = meter.LastAbyssLogs is { } al && Environment.TickCount64 - al.At < 60_000 ? al.Text : null;

        return new OverlayModel(fight, view, label.ToUpperInvariant(), view is OverlayView.Saved or OverlayView.Session ? when : null, sel.Id,
            limit, status.State, status.Message, statusText, tone, footer, abyss, _timersLabel, _pillArray, updateBanner,
            settings.TargetMode == TargetMode.BossOnly ? t.ModeBoss : t.ModeAll, settings.TargetMode, t.Code, Version, meter.BoundKey, nowMs);
    }

    private OverlayLimit? Limit(InstanceTimerEvent? timer, long nowMs)
    {
        if (timer is null) return null;
        var left = timer.EndMs - nowMs;
        // 5 refreshes a second: the tooltip only changes with the timer (records compare by value).
        if (timer != _limitTimer)
        {
            _limitTimer = timer;
            _limitTip = string.Format(timer.Kind == InstanceTimerEvent.BossLimit ? UiText.Current.BossLimitTip : UiText.Current.TimeLimitTip,
                DateTimeOffset.FromUnixTimeMilliseconds(timer.EndMs).ToLocalTime().ToString("HH:mm:ss"));
        }
        return new OverlayLimit(Format.ClockShort(left + 999), _limitTip,   // 0:00 only when it is over
            left < 60_000 || left < (timer.EndMs - timer.StartMs) / 10, timer.Kind);
    }

    /// <summary>The event pills ("Shugo 39m", "Shugo ● 7m" while it runs) and the next field boss to spawn on the player's map, else
    /// on the server: "Gartua 12:34" (rule in FieldBossPill.Pick).</summary>
    private void UpdateTimers(MeterEngine meter)
    {
        _timersDue = Environment.TickCount64 + 1_000;
        var now = DateTimeOffset.Now;
        var s = meter.Settings;
        _pills.Clear();
        foreach (var ev in meter.Events)
        {
            var (start, end, running) = ev.Next(now, s.EventAnchors.TryGetValue(ev.Key, out var anchor) ? anchor : null, s.EventPeriods.TryGetValue(ev.Key, out var pm) ? pm : null);
            var left = running ? end - now : start - now;
            var span = ScheduledEvent.Countdown(left);
            var name = UiText.Current.EventName(ev.Key, ev.Name);
            _pills.Add(new OverlayPill(ev.Key, name, running ? $"{name} ● {span}" : $"{name} {span}", ToneOf(left, running), running,
                ev.EveryMinutes > 0, (long)left.TotalSeconds));
        }
        _pillArray = _pills.ToArray();

        var timers = meter.Timers.ForServer(meter.Timers.CurrentServer);
        _spawns.Clear();
        for (var i = 0; i < timers.Count; i++) { var x = timers[i]; _spawns.Add(new(x.NpcCode, x.MapId, x.NextSpawn, x.AliveNow)); }
        var pick = FieldBossPill.Pick(_spawns, meter.Atlas.MapId, now);
        var next = pick >= 0 ? timers[pick] : null;
        if (next?.NextSpawn is not { } at)
        {
            _timersLabel = UiText.Current.TimersButton;
            return;
        }
        var bossName = next.NpcCode > 0 ? meter.Data.NpcName(next.NpcCode) : string.Format(UiText.Current.UnknownSlotBoss, -next.NpcCode % 100);
        var shortName = bossName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? bossName;
        var leftBoss = at - now;
        _timersLabel = $"{shortName} {(leftBoss.TotalHours >= 1 ? leftBoss.ToString(@"h\:mm\:ss") : leftBoss.ToString(@"mm\:ss"))}";
    }

    /// <summary>Colour step for a time to the start (or to the end while running). Measured against the game's own timer colours only by eye.</summary>
    public static PillTone ToneOf(TimeSpan left, bool running) =>
        running || left.TotalMinutes < 5 ? PillTone.Now
        : left.TotalMinutes < 10 ? PillTone.Close
        : left.TotalMinutes < 20 ? PillTone.Soon
        : PillTone.Calm;

    /// <summary>The fight part: title, clock, boss bar and ranking (top <paramref name="maxRows"/>, plus your own row when you are below them).</summary>
    public static OverlayFight Fight(EncounterSnapshot? snap, int maxRows, bool relativeToTop, bool showGear)
    {
        var T = UiText.Current;
        if (snap is null)
            return new OverlayFight(null, false, false, T.WaitingTitle, T.TipScrollFights, T.WaitingDetail, "0:00", "—", "—", "", false, "", "",
                null, [], false, 0, "", 0);

        // Running: wall clock since the pull. Finished: the fight's real length (first to last hit).
        var clock = Format.ClockShort(snap.IsActive ? snap.ClockMs : snap.CombatMs);
        var partyDps = Format.Compact(snap.PartyDps) + "/s";
        var result = snap.Reason switch
        {
            EncounterEndReason.Kill => T.Kill,
            EncounterEndReason.Wipe => T.Wipe,
            EncounterEndReason.None => T.InProgress,
            _ => T.Ended,
        };
        var zone = snap.Zone == "Open world" ? T.OpenWorld : snap.Zone;
        // Part of the boss's HP went before the meter could count it (started or restarted mid-fight): say how much,
        // in place of the zone, so the numbers are not taken for the whole fight.
        var partial = snap.Boss switch
        {
            { MaxHpKnown: false, Hp: not 0 } => T.HpUnknown,
            { CountedFrom: { } from } => string.Format(T.CountedFrom, Format.Percent(from, 0)),
            _ => null,
        };
        var detail = partial is not null ? $"{result} · {partial}" : string.IsNullOrEmpty(zone) ? result : $"{zone} · {result}";
        // A world boss whose crowd the server kept from us: the party's DPS would be a fraction of the real one.
        var unsent = snap.MostDamageUnsent;
        var footer = unsent ? T.DamageUnsentLine(snap) : $"{T.Players(snap.PlayerCount)} · {T.Party} {partyDps}";

        var list = snap.Combatants;
        CombatantSnapshot? top = null;
        for (var i = 0; i < list.Count; i++)
            if (top is null || list[i].MaxHit > top.MaxHit) top = list[i];
        var topHit = top is { MaxHit: > 0 } ? Format.Grouped(top.MaxHit) : "";
        var topHitBy = top is { MaxHit: > 0 } ? string.Format(T.TopHitBy, top.Name) : "";
        // The top line holds only the title: the biggest hit lives in its tooltip.
        var titleTip = (topHit.Length > 0 ? $"{topHitBy}: {topHit}\n" : "") + T.TipScrollFights;

        OverlayBoss? bossModel = null;
        if (snap.Boss is { } boss && boss.MaxHp > 0 && boss.Hp >= 0)
        {
            // "counted from 82% HP" rides in the bar now that there is no detail line.
            var hpText = boss.Hp == 0 ? T.Defeated : Format.Grouped(boss.Hp) + " " + T.HpUnit + (partial is null ? "" : " · " + partial);
            var percent = boss.MaxHpKnown || boss.Hp == 0 ? Format.Percent(boss.HpFraction) : "?";
            bossModel = new OverlayBoss(boss.Name, boss.NpcCode, boss.Hp, boss.MaxHp, boss.HpFraction, hpText, percent);
        }

        var count = Math.Min(maxRows, list.Count);
        // Always keep the local player visible even when they are outside the top N.
        var selfIndex = -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].IsSelf) { selfIndex = i; break; }
        var showSelfExtra = selfIndex >= count;
        var rows = new OverlayRow[count + (showSelfExtra ? 1 : 0)];
        var best = list.Count > 0 ? Math.Max(1, list[0].Damage) : 1;
        var hasGear = false;
        for (var r = 0; r < rows.Length; r++)
        {
            var i = showSelfExtra && r == rows.Length - 1 ? selfIndex : r;
            var cb = list[i];
            var heading = cb.IsUnknownSummons ? T.UnknownSummonsTip : $"#{i + 1} {cb.Name} · {T.ClassName(cb.Class)}";
            var gs = showGear && cb.GearScore > 0 ? cb.GearScore.ToString(CultureInfo.InvariantCulture) : "";
            var cp = showGear && cb.CombatPower > 0 ? Format.Power(cb.CombatPower) : "";
            hasGear |= gs.Length > 0 || cp.Length > 0;
            rows[r] = new OverlayRow(cb.ActorId, cb.IsUnknownSummons ? 0 : i + 1, T.CombatantLabel(cb.ActorId, cb.Name, cb.Class), cb.Class, cb.IsSelf,
                cb.IsUnknownSummons, gs, cp, Format.Compact(cb.Dps) + "/s", Format.Compact(cb.Damage), Format.Share(cb.Share),
                relativeToTop ? (double)cb.Damage / best : cb.Share,
                $"{heading}\n" +
                $"{T.RowDamage} {Format.Grouped(cb.Damage)} ({Format.Share(cb.Share)}) · DPS {Format.Compact(cb.Dps)}\n" +
                $"{T.RowHits} {cb.Hits} · {T.RowCrit} {Format.Percent(cb.CritRate)} · {T.RowMax} {Format.Grouped(cb.MaxHit)}");
        }
        var players = snap.PlayerCount;
        return new OverlayFight(snap.Id, true, snap.IsActive, T.FightTitle(snap), titleTip, detail, clock, partyDps, Format.Compact(snap.TotalDamage),
            footer, unsent, topHit, topHitBy, bossModel, rows, hasGear, selfIndex + 1, selfIndex >= 0 ? $"{selfIndex + 1} / {players}" : "", players);
    }

    /// <summary>Newest first: this session's segments, then saved fights not already in the session list (the overlay's Fights menu).</summary>
    public static List<OverlayFightItem> FightList(MeterEngine meter, int maxSaved)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<OverlayFightItem>();
        var sessionStarts = new HashSet<long>();
        foreach (var s in meter.Tracker.Segments())
        {
            var snap = meter.Tracker.Snapshot(s.Id, now);
            var selfIndex = -1;
            if (snap is not null)
                for (var i = 0; i < snap.Combatants.Count; i++)
                    if (snap.Combatants[i].IsSelf) { selfIndex = i; break; }
            var self = selfIndex >= 0 ? snap!.Combatants[selfIndex] : null;
            list.Add(new OverlayFightItem("s:" + s.Id.ToString("N"), s.Title, s.StartedAt, s.CombatMs, s.Reason, s.IsActive,
                self?.Dps ?? 0, selfIndex + 1, s.TotalDamage / Math.Max(1.0, s.CombatMs / 1000.0), false) { Segment = s.Id, Snapshot = snap });
            sessionStarts.Add(s.StartedAt.ToUnixTimeSeconds());
        }
        var saved = 0;
        foreach (var h in meter.History.List(maxSaved + sessionStarts.Count))
        {
            if (sessionStarts.Contains(h.StartedAt.ToUnixTimeSeconds())) continue;
            list.Add(new OverlayFightItem("h:" + h.FileName, meter.DisplayTitle(h), h.StartedAt, h.CombatMs, h.Reason, false,
                h.SelfDps, h.SelfPlace, h.PartyDps, true) { Entry = h });
            if (++saved >= maxSaved) break;
        }
        return list;
    }
}
