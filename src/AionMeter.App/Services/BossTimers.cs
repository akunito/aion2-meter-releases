using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Game;

namespace AionMeter.App.Services;

/// <summary>One tracked world boss.</summary>
/// <param name="NpcCode">The boss; a list slot whose boss is not known yet is keyed by <c>-SlotId</c>.</param>
/// <param name="RespawnMinutes">Time from a kill to the next spawn; 0 = not known yet.</param>
/// <param name="RespawnLearned">True when the meter measured it; false when the user set it (never overwritten).</param>
/// <param name="AlertedFor">The respawn time already announced, so each one notifies once.</param>
/// <param name="ListedAt">When the in-game map list last showed this boss; <paramref name="ListedTime"/> is then when it
/// appeared (<paramref name="ListedAlive"/>) or when it comes back.</param>
/// <param name="Watch">Notify before it respawns (set by a kill you were near, by "Killed", or by the bell).</param>
/// <param name="ServerId">The server this timer belongs to: every server runs its own bosses on its own clock.</param>
public sealed record BossTimer(
    int NpcCode,
    int MapId,
    string? Zone,
    DateTimeOffset? LastKill,
    DateTimeOffset? LastSeenAlive,
    DateTimeOffset? LastSeenDead,
    int RespawnMinutes,
    bool RespawnLearned,
    DateTimeOffset? AlertedFor,
    int SlotId = 0,
    DateTimeOffset? ListedAt = null,
    bool ListedAlive = false,
    DateTimeOffset? ListedTime = null,
    bool Watch = false,
    int ServerId = 0)
{
    /// <summary>Latest sign of death: a kill, a corpse, or the game list saying it is down.</summary>
    [JsonIgnore]
    public DateTimeOffset? DeadAt => Max(LastKill, LastSeenDead, ListedAt is { } at && !ListedAlive ? at : null);

    /// <summary>Latest sign of life: seen in view, or listed alive.</summary>
    [JsonIgnore]
    public DateTimeOffset? AliveAt => Max(LastSeenAlive, ListedAt is { } at && ListedAlive ? at : null);

    [JsonIgnore]
    public bool AliveNow => AliveAt is { } alive && (DeadAt is not { } dead || alive > dead);

    /// <summary>When a living boss appeared, as far as known.</summary>
    [JsonIgnore]
    public DateTimeOffset? AliveSince => ListedAlive && ListedAt >= LastSeenAlive.GetValueOrDefault() ? ListedTime : LastSeenAlive;

    /// <summary>The game's own respawn time while the list is the newest word on it; else the last kill + the interval.</summary>
    [JsonIgnore]
    public DateTimeOffset? NextSpawn =>
        ListedAt is { } listed && !ListedAlive && ListedTime is { } back && (LastKill is not { } k || k <= listed) ? back
        : LastKill is { } kill && RespawnMinutes > 0 ? kill.AddMinutes(RespawnMinutes)
        : null;

    /// <summary>The state comes from the in-game list rather than from the meter's own observations.</summary>
    [JsonIgnore]
    public bool FromGame => ListedAt is { } listed && (LastKill is not { } k || k <= listed);

    private static DateTimeOffset? Max(params DateTimeOffset?[] values) => values.Where(v => v is not null).Max();
}

/// <summary>
/// Respawn timers for world bosses. The in-game map's boss list (<see cref="FieldBossListEvent"/>) is the primary
/// source: every boss of that map with the server's own respawn time. Between list updates, kills the meter sees
/// restart a countdown from the learned interval.
/// <para>
/// The list names no NPC: a slot is map × 100 + the boss's place in code order. Which boss holds a slot comes from
/// (1) the map's code block when it holds exactly as many bosses as the list (shipped in data/field_boss_maps.json or
/// learned from a boss seen on the map), else (2) per slot, from what the meter saw: a living boss at the slot's
/// position, a boss spawning when the slot says "alive since", or a kill a whole number of minutes before the slot's
/// "back at" — with the gaps between known slots filled in code order when the counts agree.
/// </para>
/// <para>
/// Every server runs its own bosses, so timers are kept per server — the local character's (<see cref="CurrentServer"/>).
/// Which boss sits in which slot is game data and shared by all servers.
/// </para>
/// Saved in %AppData%\AionMeter\boss-timers.json and field-boss-learned.json.
/// </summary>
public sealed class BossTimers
{
    private static readonly TimeSpan SameKill = TimeSpan.FromMinutes(2);
    private const int MaxTracked = 400;
    private const float NearSpawn = 3_000;           // 30 m (positions are in centimetres)
    private const long ClockSlackMs = 5_000;         // the server's clock against ours

    private readonly string _path;
    private readonly string _learnedPath;
    private readonly GameData _data;
    private readonly object _gate = new();
    private readonly Dictionary<(int Server, int Code), BossTimer> _timers = new();
    private readonly Dictionary<int, MapInfo> _knownMaps = new();                               // shipped
    private readonly Dictionary<(int Server, int Map), FieldBossListEvent> _lastLists = new();  // newest list (this run)
    private Learned _learned = new(new(), new(), new());
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <param name="Bosses">The list's bosses in slot order, when the map's bosses do not fit one code block.</param>
    public sealed record MapInfo(int Block, string? En, string? Ru, int[]? Bosses = null);

    /// <summary>Where a boss was last seen alive.</summary>
    public sealed record Sighting(int MapId, float X, float Y, float Z, DateTimeOffset At);

    /// <param name="Blocks">Map → NPC code blocks of bosses seen there.</param>
    /// <param name="Slots">List slot → NPC code, worked out from what the meter saw.</param>
    /// <param name="Sightings">Boss → where it was last seen alive.</param>
    /// <param name="LastServer">The local character's server last seen: whose timers to show.</param>
    private sealed record Learned(Dictionary<int, HashSet<int>> Blocks, Dictionary<int, int> Slots, Dictionary<int, Sighting> Sightings)
    {
        public int LastServer { get; set; }

        /// <summary>Maps whose in-game boss list has been seen: field maps, where bosses have world respawn timers.</summary>
        public HashSet<int> ListedMaps { get; set; } = new();
    }

    public BossTimers(string path, GameData data, string dataDirectory, bool persistent = true)
    {
        _path = path;
        _learnedPath = Path.Combine(Path.GetDirectoryName(path)!, "field-boss-learned.json");
        _data = data;
        Persistent = persistent;
        LoadKnownMaps(Path.Combine(dataDirectory, "field_boss_maps.json"));
        if (persistent) Load();
        if (DropInstanceBosses() > 0) Save();
    }

    public bool Persistent { get; }

    /// <summary>Raised (on any thread) when the list or a timer changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<BossTimer> All()
    {
        lock (_gate) return _timers.Values.ToList();
    }

    /// <summary>The local character's server (0 until one is seen).</summary>
    public int CurrentServer
    {
        get { lock (_gate) return _learned.LastServer; }
    }

    /// <summary>
    /// A field map has an in-game boss list (shipped, or seen); its bosses respawn on world timers. Bosses elsewhere —
    /// the small solo maps of quests and sealed areas — come back with the instance, not on a clock.
    /// </summary>
    private bool IsFieldMap(int mapId) => _knownMaps.ContainsKey(mapId) || _learned.ListedMaps.Contains(mapId);

    // Kills of the last two days on any map, for naming list slots ("back at" = kill + interval), timer or not.
    private readonly List<(int Server, int Code, int Map, long Ms)> _recentKills = new();

    /// <summary>Removes timers that can never count down: not in any game list, off the field maps, no interval set
    /// by hand. Returns how many went.</summary>
    private int DropInstanceBosses()
    {
        lock (_gate)
        {
            var useless = _timers
                .Where(kv => kv.Value.ListedAt is null && !IsFieldMap(kv.Value.MapId) &&
                             !(kv.Value.RespawnMinutes > 0 && !kv.Value.RespawnLearned))
                .Select(kv => kv.Key).ToList();
            foreach (var key in useless) _timers.Remove(key);
            return useless.Count;
        }
    }

    /// <summary>The local character is on this server (at start-up and on every login).</summary>
    public void SetCurrentServer(int server)
    {
        if (server == 0) return;
        var learned = false;
        lock (_gate)
        {
            var before = _timers.Count(kv => kv.Key.Server == 0);
            ServerOf(server, ref learned);
            if (before == 0 && !learned) return;
        }
        if (learned) SaveLearned();
        Save();
    }

    /// <summary>The timers of one server.</summary>
    public IReadOnlyList<BossTimer> ForServer(int server)
    {
        lock (_gate) return _timers.Values.Where(t => t.ServerId == server).ToList();
    }

    /// <summary>Servers that have timers, the current one first.</summary>
    public IReadOnlyList<int> Servers()
    {
        lock (_gate)
            return _timers.Values.Select(t => t.ServerId).Append(_learned.LastServer).Where(s => s != 0).Distinct()
                .OrderBy(s => s == _learned.LastServer ? 0 : 1).ThenBy(s => s).ToList();
    }

    /// <summary>
    /// The server an observation belongs to: the one it names, else the last known. A newly seen server becomes the
    /// current one; timers saved before servers were told apart are adopted by the first server seen.
    /// </summary>
    private int ServerOf(int server, ref bool learned)
    {
        if (server == 0) return _learned.LastServer;
        if (_learned.LastServer != server)
        {
            _learned.LastServer = server;
            learned = true;
        }
        foreach (var old in _timers.Where(kv => kv.Key.Server == 0).ToList())
        {
            _timers.Remove(old.Key);
            _timers.TryAdd((server, old.Key.Code), old.Value with { ServerId = server });
        }
        return server;
    }

    /// <summary>"Altgard" / "Альтгард" for maps the tables name, else null.</summary>
    public string? MapName(int mapId, string language)
    {
        if (!_knownMaps.TryGetValue(mapId, out var m)) return null;
        return language == "ru" ? m.Ru ?? m.En : m.En;
    }

    // ------------------------------------------------------------------ the meter's own observations

    public void OnNotice(BossNotice n)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(n.TimeMs).ToLocalTime();
        var learned = false;
        lock (_gate)
        {
            var server = ServerOf(n.ServerId, ref learned);
            if (n.MapId != 0 && !_knownMaps.ContainsKey(n.MapId))
            {
                if (!_learned.Blocks.TryGetValue(n.MapId, out var blocks)) _learned.Blocks[n.MapId] = blocks = new();
                learned |= blocks.Add(n.NpcCode / 1000);
            }
            if (n.Kind == BossNoticeKind.Alive && n.MapId != 0 && (n.X != 0 || n.Y != 0))
            {
                _learned.Sightings[n.NpcCode] = new Sighting(n.MapId, n.X, n.Y, n.Z, at);
                learned = true;
            }

            _timers.TryGetValue((server, n.NpcCode), out var t);
            switch (n.Kind)
            {
                case BossNoticeKind.Killed:
                    // HP 0 and the death record report the same kill.
                    if (_recentKills.Any(k => k.Server == server && k.Code == n.NpcCode && Math.Abs(k.Ms - n.TimeMs) < SameKill.TotalMilliseconds))
                        return;
                    _recentKills.RemoveAll(k => n.TimeMs - k.Ms > 48 * 3_600_000L);
                    _recentKills.Add((server, n.NpcCode, n.MapId, n.TimeMs));
                    // Off the field maps a boss comes back with its instance, not on a clock: no timer for it.
                    t = t is null && !IsFieldMap(n.MapId)
                        ? null
                        : (t ?? New(server, n.NpcCode)) with { LastKill = at, AlertedFor = null, Watch = true };
                    break;
                case BossNoticeKind.Alive when t is not null:
                    t = Learn(t, at) with { LastSeenAlive = at };
                    break;
                case BossNoticeKind.SeenDead when t is not null:
                    t = t with { LastSeenDead = at };
                    break;
                default:
                    t = null;
                    break;
            }
            if (t is not null)
                _timers[(server, n.NpcCode)] = t with { MapId = n.MapId != 0 ? n.MapId : t.MapId, Zone = n.Zone ?? t.Zone };

            // A new clue may name slots of the map's newest list.
            if (learned && _lastLists.TryGetValue((server, n.MapId), out var list) && LearnSlots(list, server))
                Apply(list, server);
            Trim();
        }
        if (learned) SaveLearned();
        Save();
    }

    /// <summary>The first sighting after a kill bounds the respawn time from above; keep the shortest seen.</summary>
    private static BossTimer Learn(BossTimer t, DateTimeOffset seen)
    {
        if (t.LastKill is not { } kill || t.AliveNow || seen <= kill || t.FromGame) return t;
        if (t.RespawnMinutes > 0 && !t.RespawnLearned) return t; // set by the user
        var minutes = (int)Math.Round((seen - kill).TotalMinutes / 5.0) * 5;
        if (minutes is < 5 or > 48 * 60) return t;
        if (t.RespawnMinutes > 0 && t.RespawnMinutes <= minutes) return t;
        return t with { RespawnMinutes = minutes, RespawnLearned = true };
    }

    // ------------------------------------------------------------------ the in-game map list

    public void OnList(FieldBossListEvent list)
    {
        var learned = false;
        lock (_gate)
        {
            var server = ServerOf(list.ServerId, ref learned);
            // Sent about once a second while the list is open: an unchanged list changes nothing.
            if (_lastLists.TryGetValue((server, list.MapId), out var previous) && previous.Raw.AsSpan().SequenceEqual(list.Raw)) return;
            _lastLists[(server, list.MapId)] = list;
            learned |= _learned.ListedMaps.Add(list.MapId);
            learned |= LearnSlots(list, server);
            Apply(list, server);
            Trim();
        }
        if (learned) SaveLearned();
        Save();
    }

    private void Apply(FieldBossListEvent list, int server)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(list.TimeMs).ToLocalTime();
        var block = BlockOf(list.MapId, list.Count);
        foreach (var slot in list.Slots)
        {
            var code = CodeOf(slot.SlotId, block, list);
            var key = (server, code != 0 ? code : -slot.SlotId);
            if (code != 0 && _timers.Remove((server, -slot.SlotId), out var unnamed) && !_timers.ContainsKey(key))
                _timers[key] = unnamed with { NpcCode = code }; // listed before its boss was known: keep the bell
            var t = _timers.TryGetValue(key, out var old) ? old : New(server, key.Item2);
            // 0 = the game keeps no time for this boss (scheduled, or not spawned since the server started).
            DateTimeOffset? time = slot.AtMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(slot.AtMs).ToLocalTime() : null;

            if (!slot.Alive && time is { } back)
                t = LearnInterval(t, at, back);
            t = t with
            {
                MapId = list.MapId,
                SlotId = slot.SlotId,
                ListedAt = at,
                ListedAlive = slot.Alive,
                ListedTime = time,
            };
            if (!slot.Alive && t.AlertedFor is { } alerted && alerted != time) t = t with { AlertedFor = null };
            _timers[key] = t;
        }
    }

    private int CodeOf(int slotId, int block, FieldBossListEvent list) =>
        _learned.Slots.TryGetValue(slotId, out var learned) ? learned
        : ShippedSlots(list.MapId, list.Count) is { } bosses ? GameData.FieldBossInSlot(bosses, list.MapId, slotId, list.Count)
        : block != 0 ? _data.FieldBossInSlot(block, list.MapId, slotId, list.Count)
        : 0;

    /// <summary>The shipped slot list of a map, when it has one that matches the list's length.</summary>
    private int[]? ShippedSlots(int mapId, int slotCount) =>
        _knownMaps.TryGetValue(mapId, out var known) && known.Bosses is { } b && b.Length == slotCount ? b : null;

    /// <summary>
    /// A boss that just went down shows its comeback time: with a fresh death (a kill the meter saw, or alive in the
    /// list shortly before) that gives the respawn interval to the minute.
    /// </summary>
    private static BossTimer LearnInterval(BossTimer t, DateTimeOffset listedAt, DateTimeOffset back)
    {
        if (t.RespawnMinutes > 0 && !t.RespawnLearned) return t; // set by the user
        DateTimeOffset? died = null;
        if (t.LastKill is { } kill && kill <= listedAt && listedAt - kill < TimeSpan.FromMinutes(30) && back > kill)
            died = kill;
        else if (t.ListedAt is { } prev && t.ListedAlive && listedAt - prev < TimeSpan.FromMinutes(3))
            died = listedAt;
        if (died is not { } d) return t;
        var minutes = (int)Math.Round((back - d).TotalMinutes / 5.0) * 5;
        return minutes is >= 5 and <= 48 * 60 ? t with { RespawnMinutes = minutes, RespawnLearned = true } : t;
    }

    /// <summary>The code block of a map's field bosses: shipped, or learned from bosses seen there whose block holds
    /// exactly as many bosses as the list.</summary>
    private int BlockOf(int mapId, int slotCount)
    {
        if (_knownMaps.TryGetValue(mapId, out var known)) return known.Block;
        if (!_learned.Blocks.TryGetValue(mapId, out var blocks)) return 0;
        foreach (var b in blocks)
            if (_data.FieldBossesInBlock(b).Count == slotCount) return b;
        return 0;
    }

    // ------------------------------------------------------------------ naming slots from what the meter saw

    /// <summary>Names unknown slots of a list from sightings and kills, then fills gaps in code order. True if any.</summary>
    private bool LearnSlots(FieldBossListEvent list, int server)
    {
        var map = list.MapId;
        if (BlockOf(map, list.Count) != 0 || ShippedSlots(map, list.Count) is not null) return false; // the whole list resolves already
        var taken = list.Slots.Select(s => _learned.Slots.GetValueOrDefault(s.SlotId)).Where(c => c != 0).ToHashSet();
        var unknown = list.Slots.Where(s => !_learned.Slots.ContainsKey(s.SlotId)).ToList();
        var changed = false;

        // A living boss: the boss seen standing at the slot's position, or seen the moment the slot says it appeared.
        foreach (var s in unknown.Where(s => s.Alive))
        {
            var code = Nearest(map, s, taken);
            if (code == 0 && s.AtMs > 0) code = SpawnedAt(map, s.AtMs, taken);
            if (code != 0 && Assign(s.SlotId, code, taken)) changed = true;
        }

        // A dead boss: "back at" is its death time plus a whole respawn interval, to the second.
        var kills = _timers.Values
            .Where(t => t.ServerId == server && t.NpcCode > 0 && t.MapId == map && t.LastKill is not null)
            .Select(t => (Code: t.NpcCode, Ms: t.LastKill!.Value.ToUnixTimeMilliseconds()))
            .Concat(_recentKills.Where(k => k.Server == server && k.Map == map).Select(k => (k.Code, k.Ms)))
            .Where(k => !taken.Contains(k.Code) && k.Ms <= list.TimeMs + ClockSlackMs)
            .Distinct()
            .ToList();
        var pairs = (from s in unknown.Where(s => !s.Alive && s.AtMs > 0 && !_learned.Slots.ContainsKey(s.SlotId))
                     from k in kills
                     where WholeInterval(s.AtMs - k.Ms)
                     select (s.SlotId, k.Code)).ToList();
        foreach (var (slot, code) in pairs)
        {
            if (pairs.Count(p => p.SlotId == slot) == 1 && pairs.Count(p => p.Code == code) == 1 && Assign(slot, code, taken))
                changed = true;
        }

        return FillGaps(list, taken) | changed;
    }

    private bool Assign(int slot, int code, HashSet<int> taken)
    {
        if (!taken.Add(code)) return false;
        _learned.Slots[slot] = code;
        return true;
    }

    private int Nearest(int map, FieldBossSlot s, HashSet<int> taken)
    {
        if (s.X == 0 && s.Y == 0) return 0;
        var best = 0;
        var bestDistance = NearSpawn;
        foreach (var (code, seen) in _learned.Sightings)
        {
            if (seen.MapId != map || taken.Contains(code)) continue;
            var d = MathF.Sqrt((seen.X - s.X) * (seen.X - s.X) + (seen.Y - s.Y) * (seen.Y - s.Y) + (seen.Z - s.Z) * (seen.Z - s.Z));
            if (d < bestDistance)
            {
                best = code;
                bestDistance = d;
            }
        }
        return best;
    }

    private int SpawnedAt(int map, long sinceMs, HashSet<int> taken)
    {
        var matches = _learned.Sightings
            .Where(kv => kv.Value.MapId == map && !taken.Contains(kv.Key) &&
                         Math.Abs(kv.Value.At.ToUnixTimeMilliseconds() - sinceMs) <= ClockSlackMs)
            .Select(kv => kv.Key).ToList();
        return matches.Count == 1 ? matches[0] : 0;
    }

    /// <summary>5 min to 2 days, a whole number of 5 minutes give or take the clock slack.</summary>
    private static bool WholeInterval(long ms)
    {
        if (ms is < 5 * 60_000 - ClockSlackMs or > 48 * 3_600_000L) return false;
        var rest = ms % (5 * 60_000);
        return rest <= ClockSlackMs || rest >= 5 * 60_000 - ClockSlackMs;
    }

    /// <summary>
    /// Between two known slots the list holds the bosses whose codes lie between theirs: when the data tables have
    /// exactly as many bosses there as there are slots, they are those bosses in order. The ends work the same within
    /// the known boss's thousand-code block.
    /// </summary>
    private bool FillGaps(FieldBossListEvent list, HashSet<int> taken)
    {
        var baseId = list.MapId * 100;
        var anchors = list.Slots
            .Select(s => (Place: s.SlotId - baseId, Code: _learned.Slots.GetValueOrDefault(s.SlotId)))
            .Where(a => a.Code != 0 && a.Place >= 1 && a.Place <= list.Count)
            .OrderBy(a => a.Place).ToList();
        if (anchors.Count == 0) return false;
        for (var i = 1; i < anchors.Count; i++)
            if (anchors[i].Code <= anchors[i - 1].Code) return false; // contradicting clues: guess nothing

        var changed = false;
        var bounds = new List<(int FromPlace, int ToPlace, int AboveCode, int BelowCode)>();
        var first = anchors[0];
        bounds.Add((1, first.Place - 1, first.Code / 1000 * 1000 - 1, first.Code));
        for (var i = 1; i < anchors.Count; i++)
            bounds.Add((anchors[i - 1].Place + 1, anchors[i].Place - 1, anchors[i - 1].Code, anchors[i].Code));
        var last = anchors[^1];
        bounds.Add((last.Place + 1, list.Count, last.Code, last.Code / 1000 * 1000 + 1000));

        foreach (var (from, to, above, below) in bounds)
        {
            if (to < from) continue;
            var codes = _data.Npcs.Values.Where(n => n.IsBoss && !n.IsDummy && n.Code > above && n.Code < below)
                .Select(n => n.Code).Order().ToList();
            if (codes.Count != to - from + 1 || codes.Any(taken.Contains)) continue;
            for (var place = from; place <= to; place++)
                changed |= Assign(baseId + place, codes[place - from], taken);
        }
        return changed;
    }

    // ------------------------------------------------------------------ user actions

    public void MarkKilled(int server, int npcCode)
    {
        lock (_gate)
        {
            var t = _timers.TryGetValue((server, npcCode), out var old) ? old : New(server, npcCode);
            _timers[(server, npcCode)] = t with { LastKill = DateTimeOffset.Now, AlertedFor = null, Watch = true };
        }
        Save();
    }

    public void SetRespawn(int server, int npcCode, int minutes)
    {
        lock (_gate)
        {
            if (!_timers.TryGetValue((server, npcCode), out var t)) return;
            _timers[(server, npcCode)] = t with { RespawnMinutes = Math.Max(0, minutes), RespawnLearned = false, AlertedFor = null };
        }
        Save();
    }

    public void SetWatch(int server, int npcCode, bool watch)
    {
        lock (_gate)
        {
            if (!_timers.TryGetValue((server, npcCode), out var t)) return;
            _timers[(server, npcCode)] = t with { Watch = watch, AlertedFor = null };
        }
        Save();
    }

    public void Remove(int server, int npcCode)
    {
        lock (_gate)
            if (!_timers.Remove((server, npcCode))) return;
        Save();
    }

    /// <summary>Watched timers of the current server whose respawn is <paramref name="leadMinutes"/> away (or just
    /// passed) and not announced yet; marks them announced.</summary>
    public List<BossTimer> TakeDueAlerts(DateTimeOffset now, int leadMinutes)
    {
        var due = new List<BossTimer>();
        if (leadMinutes <= 0) return due;
        lock (_gate)
        {
            foreach (var t in _timers.Values.Where(t => t.ServerId == _learned.LastServer).ToList())
            {
                if (!t.Watch || t.NextSpawn is not { } next || t.AlertedFor == next || t.AliveNow) continue;
                if (now < next.AddMinutes(-leadMinutes) || now > next.AddMinutes(10)) continue;
                _timers[(t.ServerId, t.NpcCode)] = t with { AlertedFor = next };
                due.Add(t);
            }
        }
        if (due.Count > 0) Save();
        return due;
    }

    private static BossTimer New(int server, int code) => new(code, 0, null, null, null, null, 0, false, null, ServerId: server);

    private void Trim()
    {
        if (_timers.Count <= MaxTracked) return;
        var stale = _timers.Values.Where(t => !t.Watch)
            .OrderBy(t => t.ListedAt ?? t.LastKill ?? t.LastSeenAlive ?? DateTimeOffset.MinValue)
            .Take(_timers.Count - MaxTracked).ToList();
        foreach (var old in stale) _timers.Remove((old.ServerId, old.NpcCode));
    }

    // ------------------------------------------------------------------ files

    private void LoadKnownMaps(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("maps", out var maps)) return;
            foreach (var m in maps.EnumerateObject())
            {
                if (!int.TryParse(m.Name, out var id)) continue;
                var hasBlock = m.Value.TryGetProperty("block", out var block);
                int[]? bosses = m.Value.TryGetProperty("bosses", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(e => e.GetInt32()).ToArray()
                    : null;
                if (!hasBlock && bosses is null) continue;
                _knownMaps[id] = new MapInfo(hasBlock ? block.GetInt32() : 0,
                    m.Value.TryGetProperty("en", out var en) ? en.GetString() : null,
                    m.Value.TryGetProperty("ru", out var ru) ? ru.GetString() : null, bosses);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Log.Info($"field_boss_maps.json ignored: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<BossTimer>>(File.ReadAllText(_path), Json);
                foreach (var t in list ?? []) _timers[(t.ServerId, t.NpcCode)] = t;
            }
            if (File.Exists(_learnedPath))
                _learned = JsonSerializer.Deserialize<Learned>(File.ReadAllText(_learnedPath), Json) ?? _learned;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Info($"boss timers ignored: {ex.Message}");
        }
    }

    private void SaveLearned()
    {
        if (!Persistent) return;
        try
        {
            string text;
            lock (_gate) text = JsonSerializer.Serialize(_learned, Json);
            var tmp = _learnedPath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _learnedPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Save()
    {
        if (Persistent)
        {
            try
            {
                string text;
                lock (_gate) text = JsonSerializer.Serialize(_timers.Values.OrderBy(t => t.NpcCode).ToList(), Json);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"boss-timers.json not saved: {ex.Message}");
            }
        }
        Changed?.Invoke();
    }
}
