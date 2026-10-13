using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Events;

namespace AionMeter.Core.Atlas;

/// <summary>
/// One gathering spot. Nodes respawn at the exact same position with a new entity id (measured 2026-10-08: Orichalcum
/// at (-36729, -34461) came back three times in 14 min as three ids), so a spot is keyed by map, object code and
/// position rounded to 1 m; <see cref="LiveId"/> is only valid while the current spawn lives.
/// </summary>
public sealed class NodeSpot
{
    public int Code { get; set; }
    public byte Kind { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public long FirstSeenMs { get; set; }
    public long LastSeenMs { get; set; }
    /// <summary>The current spawn's own time (server, Unix ms); 0 when the packet had none.</summary>
    public long SpawnedAtMs { get; set; }
    /// <summary>Units taken from the current spawn (0–3).</summary>
    public int Taken { get; set; }
    public bool Alive { get; set; }
    public long ExhaustedAtMs { get; set; }
    /// <summary>Exhausted while nobody watched: ExhaustedAtMs is when the player stood next to the empty spot, not the real time.</summary>
    public bool GoneUnseen { get; set; }
    /// <summary>Minutes from exhausted to the next spawn at this spot, learned from two consecutive spawns; 0 = unknown.</summary>
    public int RespawnMinutes { get; set; }
    public int Spawns { get; set; }
    [JsonIgnore] public uint LiveId { get; set; }
    [JsonIgnore] public bool InView { get; set; }
    public string? Name { get; set; }
    /// <summary>Known from the map data (shugo.gg markers, every node of the zone), never seen by this meter yet; the kind is in Name.</summary>
    public bool Seeded { get; set; }

    public const int MaxUnits = 3;

    /// <summary>
    /// Gathering nodes are object codes 15xxxxx; 11/12/14xxxxx objects (scenery, quest pickups) also arrive as 34 36 but
    /// nobody ever gathers them (471 spots, 2026-10-08: every gathered code was 15xxxxx). Gather evidence overrides. A
    /// repeat spawn alone does not (camp objects 1100001 re-announced after leaving view showed up as "3" nodes, 2026-10-09).
    /// </summary>
    public bool IsNode => Seeded || Code / 100_000 == 15 || Taken > 0 || ExhaustedAtMs > 0;
    public int UnitsLeft => Alive ? Math.Max(0, MaxUnits - Taken) : 0;
    public long NextSpawnMs => !Alive && ExhaustedAtMs > 0 && RespawnMinutes > 0 ? ExhaustedAtMs + RespawnMinutes * 60_000L : 0;
}

/// <summary>A spot in view in the daemon (A2M-48 `position` message): object code, world position (cm), units taken.</summary>
public readonly record struct LiveSpot(int Code, float X, float Y, int Taken);

/// <summary>
/// The atlas: every gathering spot seen, per map, fed by <see cref="ObjectSeenEvent"/>, <see cref="GatherEvent"/>,
/// <see cref="ObjectGoneEvent"/>, <see cref="ZoneChangedEvent"/> and the local player's <see cref="PositionEvent"/>.
/// Saved as one JSON file per map in <c>directory</c> (atomic write like the other stores).
/// </summary>
public sealed class NodeStore
{
    private readonly string directory;

    public NodeStore(string directory)
    {
        this.directory = directory;
        // The map the player was in when the meter last ran: no zone-load packet comes until the next loading screen.
        try
        {
            var last = Path.Combine(directory, "last-map.txt");
            if (File.Exists(last) && int.TryParse(File.ReadAllText(last).Trim(), out var map)) SetMapLocked(map);
        }
        catch (IOException)
        {
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly Dictionary<int, Dictionary<(int Code, int Gx, int Gy), NodeSpot>> _maps = new();
    private readonly Dictionary<uint, NodeSpot> _live = new();   // entity id → spot, current map only
    private readonly object _gate = new();
    private readonly HashSet<int> _dirty = new();
    private readonly Dictionary<uint, long> _selfGatheredUnknown = new();
    private readonly (long At, float X, float Y)[] _recent = new (long, float, float)[64];
    private int _recentAt;
    private float _emaX, _emaY; private int _emaN;

    /// <summary>Centroid of the positions seen in the last <paramref name="windowMs"/>: a rough (view-radius) stand-in for the player.</summary>
    public (float X, float Y, int Samples) Estimate(long nowMs, long windowMs = 15_000)
    {
        lock (_gate)
        {
            double sx = 0, sy = 0; var n = 0;
            foreach (var (at, x, y) in _recent)
                if (at > 0 && nowMs - at <= windowMs) { sx += x; sy += y; n++; }
            return n == 0 ? (0, 0, 0) : (_emaX, _emaY, n);
        }
    }
    private int _savedMap;
    private uint _hintId;

    /// <summary>The local player started gathering a spot (raised on the capture thread).</summary>
    public event Action<NodeSpot>? SelfGathering;

    public int MapId { get; private set; }
    public uint SelfId { get; private set; }
    public float SelfX { get; private set; }
    public float SelfY { get; private set; }
    public float SelfZ { get; private set; }
    public long SelfAtMs { get; private set; }
    /// <summary>Diagnostics: position events received, and those that were the local player's.</summary>
    public long PositionsSeen { get; private set; }
    public long SelfPositions { get; private set; }
    /// <summary>Newest event time seen (position, gather, object): the stream's clock. A replay runs on the recording's clock, so ages
    /// measured against UtcNow would all be hours old (A2M-48 `position` message).</summary>
    public long LastEventMs { get; private set; }

    public static (int Code, int Gx, int Gy) KeyOf(int code, float x, float y) => (code, (int)MathF.Round(x / 100f), (int)MathF.Round(y / 100f));

    public void OnEvent(GameEvent e)
    {
        lock (_gate)
        {
            if (e.TimeMs > LastEventMs && e is PositionEvent or GatherEvent or ObjectSeenEvent or ObjectGoneEvent) LastEventMs = e.TimeMs;
            switch (e)
            {
                case ZoneChangedEvent z:
                    SetMapLocked(z.MapId);
                    break;
                case SelfIdentifiedEvent s:
                    SelfId = s.ActorId; _hintId = 0;
                    break;
                case LocalPlayerHintEvent h:
                    // Two agreeing hints replace whatever the cache said (ids change on every zone load).
                    if (h.ActorId == SelfId) break;
                    if (_hintId == h.ActorId) { SelfId = h.ActorId; _hintId = 0; }
                    else _hintId = h.ActorId;
                    break;
                case PositionEvent p:
                    PositionsSeen++;
                    if (p.ActorId == SelfId && SelfId != 0) { SelfX = p.X; SelfY = p.Y; SelfZ = p.Z; SelfAtMs = p.TimeMs; SelfPositions++; SweepMissing(p.X, p.Y, p.TimeMs); }
                    // Everything in view reports positions (~5/s): their recent centroid stands in for the player while
                    // the server says nothing about the player's own walking.
                    _recent[_recentAt] = (p.TimeMs, p.X, p.Y); _recentAt = (_recentAt + 1) % _recent.Length;
                    // Smoothed centroid (EMA, ~4 s at 5 samples/s): the raw centroid jumps as entities enter and leave view.
                    if (_emaN == 0) { _emaX = p.X; _emaY = p.Y; }
                    else { _emaX += 0.05f * (p.X - _emaX); _emaY += 0.05f * (p.Y - _emaY); }
                    _emaN++;
                    break;
                case ObjectSeenEvent o when MapId != 0:
                    OnSeen(o);
                    break;
                case GatherEvent g when g.Started && g.PlayerId == SelfId && SelfId != 0:
                    var target = _live.TryGetValue(g.ObjectId, out var mine) ? mine : BindByPosition(g.ObjectId, g.TimeMs);
                    if (target is null) { _selfGatheredUnknown[g.ObjectId] = g.TimeMs; break; } // asked when it is seen again
                    // Gathering happens at the node: the node's position is the player's, to the centimetre (Diego's idea).
                    SelfX = target.X; SelfY = target.Y; SelfZ = target.Z; SelfAtMs = g.TimeMs; SelfPositions++;
                    SweepMissing(target.X, target.Y, g.TimeMs);
                    SelfGathering?.Invoke(target);
                    break;
                case GatherEvent g when !g.Started && _live.TryGetValue(g.ObjectId, out var spot):
                    if (g.Count > spot.Taken) spot.Taken = g.Count;
                    spot.LastSeenMs = g.TimeMs;
                    _dirty.Add(MapId);
                    break;
                case ObjectGoneEvent x when _live.TryGetValue(x.ActorId, out var spot):
                    spot.InView = false;
                    spot.LiveId = 0; // a return into view is announced again (34 36 with the same id and spawn time)
                    _live.Remove(x.ActorId);
                    if (x.Exhausted)
                    {
                        spot.Alive = false; spot.Taken = NodeSpot.MaxUnits; spot.ExhaustedAtMs = x.TimeMs; spot.GoneUnseen = false;
                        _dirty.Add(MapId);
                    }
                    break;
            }
        }
    }

    /// <summary>The map the player is already in when the meter starts (no zone-load packet will come until the next
    /// loading screen): from the name cache or any other source that knows it.</summary>
    public void SetMap(int mapId)
    {
        lock (_gate) SetMapLocked(mapId);
    }

    /// <summary>The local player's entity id when already known at start (name cache): positions are keyed by it.</summary>
    public void SetSelf(uint id)
    {
        lock (_gate) if (id != 0 && SelfId == 0) SelfId = id;
    }

    private void SetMapLocked(int mapId)
    {
        if (mapId <= 0 || mapId == MapId) return;
        MapId = mapId; _live.Clear(); Load(mapId);
    }

    /// <summary>
    /// A node in view before the meter started has a remembered spot but no entity id. Gathering happens at the node,
    /// so the player's position names it: the nearest alive spot within 12 m of an exact position (a recent cast or
    /// action), or within 40 m of the smoothed centroid. Mislabelling a neighbour self-corrects when nodes are seen again.
    /// </summary>
    private NodeSpot? BindByPosition(uint objectId, long nowMs)
    {
        if (!_maps.TryGetValue(MapId, out var m)) return null;
        float px, py, max;
        if (SelfAtMs > 0 && nowMs - SelfAtMs < 30_000) { px = SelfX; py = SelfY; max = 1_200; }
        else if (_emaN > 0) { px = _emaX; py = _emaY; max = 4_000; }
        else return null;
        NodeSpot? best = null; var bestD = max * max;
        foreach (var s in m.Values)
        {
            if (!s.Alive || s.LiveId != 0 || !s.IsNode) continue;
            var d = (s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py);
            if (d < bestD) { bestD = d; best = s; }
        }
        if (best is null) return null;
        best.LiveId = objectId; best.InView = true; best.LastSeenMs = nowMs;
        _live[objectId] = best;
        return best;
    }

    private void OnSeen(ObjectSeenEvent o)
    {
        var map = _maps.TryGetValue(MapId, out var m) ? m : _maps[MapId] = new();
        var key = KeyOf(o.ObjectCode, o.X, o.Y);
        if (!map.TryGetValue(key, out var spot))
        {
            map[key] = spot = new NodeSpot { Code = o.ObjectCode, Kind = o.Kind, X = o.X, Y = o.Y, Z = o.Z, FirstSeenMs = o.TimeMs };
            DropSeedsNear(map, o.X, o.Y); // the real node replaces the map-data one (shugo positions match ours within 1.3 m, 2026-10-08)
        }
        var newSpawn = spot.LiveId != o.ActorId && (o.SpawnedAtMs == 0 || o.SpawnedAtMs != spot.SpawnedAtMs);
        if (newSpawn)
        {
            // A later spawn after a known exhaustion teaches the respawn interval (whole minutes); not when the exhaustion time is a guess.
            if (spot.ExhaustedAtMs > 0 && !spot.GoneUnseen && o.SpawnedAtMs > spot.ExhaustedAtMs)
            {
                var minutes = (int)Math.Round((o.SpawnedAtMs - spot.ExhaustedAtMs) / 60_000.0);
                if (minutes is >= 1 and <= 24 * 60) spot.RespawnMinutes = minutes;
            }
            spot.Spawns++;
            spot.SpawnedAtMs = o.SpawnedAtMs;
            spot.Taken = o.Taken;
            spot.ExhaustedAtMs = 0;
            spot.GoneUnseen = false;
        }
        else if (o.Taken > spot.Taken) spot.Taken = o.Taken;
        spot.Alive = true; spot.InView = true; spot.LiveId = o.ActorId; spot.LastSeenMs = o.TimeMs;
        _live[o.ActorId] = spot;
        _dirty.Add(MapId);
        if (_selfGatheredUnknown.Remove(o.ActorId, out var at) && o.TimeMs - at < 30 * 60_000) SelfGathering?.Invoke(spot);
    }

    /// <summary>
    /// The server announces every object within ~100 m (node spawns arrived 95–105 m from the player's own fix, 2026-10-08), so a
    /// remembered node within 30 m of an exact fix that has not been announced for 8 s is not there: someone emptied it while it was
    /// out of view. Marked gone with the time of the visit (GoneUnseen), which keeps the respawn learning honest.
    /// </summary>
    private const float SweepCm = 3000;

    private void SweepMissing(float x, float y, long nowMs)
    {
        if (!_maps.TryGetValue(MapId, out var m)) return;
        foreach (var s in m.Values)
        {
            if (!s.Alive || s.LiveId != 0 || !s.IsNode || nowMs - s.LastSeenMs < 8_000) continue;
            var dx = s.X - x; var dy = s.Y - y;
            if (dx * dx + dy * dy > SweepCm * SweepCm) continue;
            s.Alive = false; s.Taken = NodeSpot.MaxUnits; s.ExhaustedAtMs = nowMs; s.GoneUnseen = true;
            _dirty.Add(MapId);
        }
    }

    private static void DropSeedsNear(Dictionary<(int Code, int Gx, int Gy), NodeSpot> map, float x, float y)
    {
        List<(int Code, int Gx, int Gy)>? gone = null;
        foreach (var (k, s) in map)
            if (s.Seeded && Math.Abs(s.X - x) < 400 && Math.Abs(s.Y - y) < 400 && (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) < 300f * 300f) (gone ??= new()).Add(k);
        if (gone is not null) foreach (var k in gone) map.Remove(k);
    }

    /// <summary>
    /// Every gathering node of a zone from the map data (<c>data/atlas/&lt;map&gt;/nodes.json</c>: name, x, y in world cm, fitted
    /// on the named bosses), added as seeded spots unless a seen spot already sits within 3 m. Call once after construction.
    /// </summary>
    public int LoadSeeds(string atlasDataDir)
    {
        var n = 0;
        if (!Directory.Exists(atlasDataDir)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(atlasDataDir))
        {
            var path = Path.Combine(dir, "nodes.json");
            if (!int.TryParse(Path.GetFileName(dir), out var mapId) || !File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("nodes", out var nodes)) continue;
                lock (_gate)
                {
                    var map = _maps.TryGetValue(mapId, out var m) ? m : _maps[mapId] = new();
                    var real = map.Values.Where(s => !s.Seeded).ToList();
                    foreach (var e in nodes.EnumerateArray())
                    {
                        var x = e.GetProperty("x").GetSingle(); var y = e.GetProperty("y").GetSingle();
                        var name = e.GetProperty("name").GetString() ?? "";
                        var kind = name.Split(' ', 2)[0]; // "Yggdrasil Log" → "Yggdrasil", matching the catalogue icons and objects.json
                        var key = KeyOf(0, x, y); // code 0 = seeded, so it never collides with a seen spot
                        if (map.ContainsKey(key)) continue;
                        var taken = false;
                        foreach (var s in real) if ((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) < 300f * 300f) { taken = true; break; }
                        if (taken) continue;
                        map[key] = new NodeSpot { Code = 0, X = x, Y = y, Name = kind, Seeded = true };
                        n++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        return n;
    }

    /// <summary>The spot nearest to a world point within <paramref name="maxCm"/>, or null.</summary>
    public NodeSpot? Nearest(int mapId, float x, float y, float maxCm)
    {
        lock (_gate)
        {
            if (!_maps.TryGetValue(mapId, out var m)) return null;
            NodeSpot? best = null; var bestD = maxCm * maxCm;
            foreach (var s in m.Values)
            {
                var d = (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y);
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }
    }

    /// <summary>Every gathering spot of a map (a copy, safe to read from the UI thread); scenery objects are left out.</summary>
    public List<NodeSpot> Spots(int mapId)
    {
        lock (_gate) return _maps.TryGetValue(mapId, out var m) ? m.Values.Where(s => s.IsNode).ToList() : [];
    }

    public int Count(int mapId) { lock (_gate) return _maps.TryGetValue(mapId, out var m) ? m.Count : 0; }

    /// <summary>A2M-48: the spots of the current map in view right now (map, code, position, units taken), appended to <paramref name="into"/>.</summary>
    public int LiveSpots(List<LiveSpot> into)
    {
        lock (_gate)
        {
            foreach (var s in _live.Values) if (s.IsNode) into.Add(new LiveSpot(s.Code, s.X, s.Y, s.Taken));
            return MapId;
        }
    }

    // viewer side (the Linux UI): which spots the last MergeLive marked in view, and the file stamps it loaded
    private readonly HashSet<(int Code, int Gx, int Gy)> _merged = new();
    private readonly Dictionary<int, DateTime> _stamps = new();

    /// <summary>
    /// A2M-48 viewer: the daemon's in-view spots (`position` message) onto this snapshot — those spots alive and in view with their units,
    /// a spot new to this process added, and the ones the previous call had in view that are gone from the list marked out of view.
    /// Exhaustion and respawn learning stay the daemon's: they arrive with its next save of the map file (<see cref="ReloadIfChanged"/>).
    /// </summary>
    public void MergeLive(int mapId, IReadOnlyList<LiveSpot> live, long nowMs)
    {
        lock (_gate)
        {
            if (mapId > 0 && mapId != MapId) { MapId = mapId; _live.Clear(); Load(mapId); _merged.Clear(); }
            if (!_maps.TryGetValue(MapId, out var map)) return;
            var seen = new HashSet<(int, int, int)>();
            for (var i = 0; i < live.Count; i++)
            {
                var l = live[i];
                var key = KeyOf(l.Code, l.X, l.Y);
                if (!map.TryGetValue(key, out var spot))
                {
                    map[key] = spot = new NodeSpot { Code = l.Code, X = l.X, Y = l.Y, FirstSeenMs = nowMs };
                    DropSeedsNear(map, l.X, l.Y);
                }
                spot.Alive = true; spot.InView = true; spot.Taken = l.Taken; spot.ExhaustedAtMs = 0; spot.LastSeenMs = nowMs;
                seen.Add(key);
            }
            foreach (var key in _merged)
                if (!seen.Contains(key) && map.TryGetValue(key, out var s)) s.InView = false;
            _merged.Clear();
            _merged.UnionWith(seen);
        }
    }

    /// <summary>
    /// A2M-48 viewer: the daemon rewrote atlas/&lt;map&gt;.json (every 30 s when something changed): take its spots, keep the seeded ones
    /// no real spot covers and the in-view marks of the last <see cref="MergeLive"/>. False when the file is the one already loaded.
    /// </summary>
    public bool ReloadIfChanged(int mapId)
    {
        var path = PathOf(mapId);
        DateTime stamp;
        try { if (!File.Exists(path)) return false; stamp = File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        lock (_gate) if (_stamps.TryGetValue(mapId, out var known) && known == stamp) return false;
        List<NodeSpot>? spots;
        try { spots = JsonSerializer.Deserialize<List<NodeSpot>>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
        lock (_gate)
        {
            _stamps[mapId] = stamp;
            if (spots is null) return false;
            var map = _maps.TryGetValue(mapId, out var m) ? m : _maps[mapId] = new();
            var seeds = new List<KeyValuePair<(int, int, int), NodeSpot>>();
            foreach (var kv in map) if (kv.Value.Seeded) seeds.Add(kv);
            var inView = mapId == MapId ? new HashSet<(int, int, int)>(_merged) : null;
            map.Clear();
            foreach (var s in spots)
            {
                var key = KeyOf(s.Code, s.X, s.Y);
                s.Alive = s.Alive && s.ExhaustedAtMs == 0;
                if (inView is not null && inView.Contains(key)) { s.InView = true; s.Alive = true; }
                map[key] = s;
            }
            foreach (var (key, seed) in seeds)
            {
                var covered = false;
                foreach (var s in spots)
                    if (Math.Abs(s.X - seed.X) < 300 && Math.Abs(s.Y - seed.Y) < 300 && (s.X - seed.X) * (s.X - seed.X) + (s.Y - seed.Y) * (s.Y - seed.Y) < 300f * 300f) { covered = true; break; }
                if (!covered) map[key] = seed;
            }
            return true;
        }
    }

    private string PathOf(int mapId) => Path.Combine(directory, mapId + ".json");

    private void Load(int mapId)
    {
        if (_maps.ContainsKey(mapId)) return;
        var map = _maps[mapId] = new();
        try
        {
            var path = PathOf(mapId);
            if (!File.Exists(path)) return;
            _stamps[mapId] = File.GetLastWriteTimeUtc(path);
            var spots = JsonSerializer.Deserialize<List<NodeSpot>>(File.ReadAllText(path), Json);
            if (spots is null) return;
            foreach (var s in spots) { s.Alive = s.Alive && s.ExhaustedAtMs == 0; map[KeyOf(s.Code, s.X, s.Y)] = s; }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
    }

    /// <summary>Writes every map changed since the last save. Call from a timer and on exit.</summary>
    public void Save()
    {
        List<(int Map, string Text)> work;
        int current;
        lock (_gate)
        {
            work = _dirty.Select(m => (m, JsonSerializer.Serialize(_maps[m].Values.Where(v => !v.Seeded), Json))).ToList();
            _dirty.Clear();
            current = MapId;
        }
        try
        {
            Directory.CreateDirectory(directory);
            if (current > 0 && (_savedMap != current)) { File.WriteAllText(Path.Combine(directory, "last-map.txt"), current.ToString()); _savedMap = current; }
            if (work.Count == 0) return;
            foreach (var (map, text) in work)
            {
                var path = PathOf(map); var tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
