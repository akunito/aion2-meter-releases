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
    /// <summary>Minutes from exhausted to the next spawn at this spot, learned from two consecutive spawns; 0 = unknown.</summary>
    public int RespawnMinutes { get; set; }
    public int Spawns { get; set; }
    [JsonIgnore] public uint LiveId { get; set; }
    [JsonIgnore] public bool InView { get; set; }
    public string? Name { get; set; }

    public const int MaxUnits = 3;
    public int UnitsLeft => Alive ? Math.Max(0, MaxUnits - Taken) : 0;
    public long NextSpawnMs => !Alive && ExhaustedAtMs > 0 && RespawnMinutes > 0 ? ExhaustedAtMs + RespawnMinutes * 60_000L : 0;
}

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
    private int _savedMap;

    public int MapId { get; private set; }
    public uint SelfId { get; private set; }
    public float SelfX { get; private set; }
    public float SelfY { get; private set; }
    public float SelfZ { get; private set; }
    public long SelfAtMs { get; private set; }

    public static (int Code, int Gx, int Gy) KeyOf(int code, float x, float y) => (code, (int)MathF.Round(x / 100f), (int)MathF.Round(y / 100f));

    public void OnEvent(GameEvent e)
    {
        lock (_gate)
        {
            switch (e)
            {
                case ZoneChangedEvent z:
                    SetMapLocked(z.MapId);
                    break;
                case SelfIdentifiedEvent s:
                    SelfId = s.ActorId;
                    break;
                case PositionEvent p when p.ActorId == SelfId && SelfId != 0:
                    SelfX = p.X; SelfY = p.Y; SelfZ = p.Z; SelfAtMs = p.TimeMs;
                    break;
                case ObjectSeenEvent o when MapId != 0:
                    OnSeen(o);
                    break;
                case GatherEvent g when !g.Started && _live.TryGetValue(g.ObjectId, out var spot):
                    if (g.Count > spot.Taken) spot.Taken = g.Count;
                    spot.LastSeenMs = g.TimeMs;
                    _dirty.Add(MapId);
                    break;
                case ObjectGoneEvent x when _live.TryGetValue(x.ActorId, out var spot):
                    spot.InView = false;
                    if (x.Exhausted)
                    {
                        spot.Alive = false; spot.Taken = NodeSpot.MaxUnits; spot.ExhaustedAtMs = x.TimeMs; spot.LiveId = 0;
                        _live.Remove(x.ActorId);
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

    private void OnSeen(ObjectSeenEvent o)
    {
        var map = _maps.TryGetValue(MapId, out var m) ? m : _maps[MapId] = new();
        var key = KeyOf(o.ObjectCode, o.X, o.Y);
        if (!map.TryGetValue(key, out var spot))
        {
            map[key] = spot = new NodeSpot { Code = o.ObjectCode, Kind = o.Kind, X = o.X, Y = o.Y, Z = o.Z, FirstSeenMs = o.TimeMs };
        }
        var newSpawn = spot.LiveId != o.ActorId && (o.SpawnedAtMs == 0 || o.SpawnedAtMs != spot.SpawnedAtMs);
        if (newSpawn)
        {
            // A later spawn after a known exhaustion teaches the respawn interval (whole minutes).
            if (spot.ExhaustedAtMs > 0 && o.SpawnedAtMs > spot.ExhaustedAtMs)
            {
                var minutes = (int)Math.Round((o.SpawnedAtMs - spot.ExhaustedAtMs) / 60_000.0);
                if (minutes is >= 1 and <= 24 * 60) spot.RespawnMinutes = minutes;
            }
            spot.Spawns++;
            spot.SpawnedAtMs = o.SpawnedAtMs;
            spot.Taken = o.Taken;
            spot.ExhaustedAtMs = 0;
        }
        else if (o.Taken > spot.Taken) spot.Taken = o.Taken;
        spot.Alive = true; spot.InView = true; spot.LiveId = o.ActorId; spot.LastSeenMs = o.TimeMs;
        _live[o.ActorId] = spot;
        _dirty.Add(MapId);
    }

    /// <summary>Every spot of a map (a copy, safe to read from the UI thread).</summary>
    public List<NodeSpot> Spots(int mapId)
    {
        lock (_gate) return _maps.TryGetValue(mapId, out var m) ? m.Values.ToList() : [];
    }

    public int Count(int mapId) { lock (_gate) return _maps.TryGetValue(mapId, out var m) ? m.Count : 0; }

    private string PathOf(int mapId) => Path.Combine(directory, mapId + ".json");

    private void Load(int mapId)
    {
        if (_maps.ContainsKey(mapId)) return;
        var map = _maps[mapId] = new();
        try
        {
            var path = PathOf(mapId);
            if (!File.Exists(path)) return;
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
            work = _dirty.Select(m => (m, JsonSerializer.Serialize(_maps[m].Values, Json))).ToList();
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
