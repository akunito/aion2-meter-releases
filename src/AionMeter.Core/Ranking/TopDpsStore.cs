using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Storage;

namespace AionMeter.Core.Ranking;

/// <summary>One cast of the kept sequence: offset from the fight start (ms) and the skill code. Hits of one cast collapse to one entry.</summary>
public readonly record struct TopCast(int T, int S);

public sealed record TopSkill(int Code, string Name, int Hits, long Damage, double Share);

/// <summary>A player's best fight for the per-class ranking, with enough of the fight to study the rotation.</summary>
public sealed record TopEntry(
    string Name,
    GameClass Class,
    int ServerId,
    bool IsSelf,
    double Dps,
    long Damage,
    long CombatMs,
    string Title,
    string? Zone,
    int BossCode,
    DateTimeOffset StartedAt,
    string Fight,
    IReadOnlyList<TopSkill> Skills,
    IReadOnlyList<TopCast> Casts);

/// <summary>
/// The ten best DPS per class seen by this meter (one entry per player, their best fight), kept with the skill table and the
/// collapsed cast sequence so the rotation can be read later. Fed from finished fights; rebuilt from the history files when empty.
/// </summary>
public sealed class TopDpsStore
{
    public const int Keep = 10;
    public const long MinCombatMs = 30_000;
    private const int MaxCasts = 4000;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Dictionary<GameClass, List<TopEntry>> _byClass = new();
    private readonly string? _path;
    private bool _dirty;

    public TopDpsStore(string? path)
    {
        _path = path;
        Load();
    }

    public event Action? Changed;

    public int Count { get { lock (_gate) return _byClass.Values.Sum(l => l.Count); } }

    public IReadOnlyList<GameClass> Classes { get { lock (_gate) return _byClass.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(c => c).ToList(); } }

    public IReadOnlyList<TopEntry> For(GameClass c) { lock (_gate) return _byClass.TryGetValue(c, out var l) ? l.ToList() : []; }

    /// <summary>Offers every combatant of a finished fight; returns how many entered or improved the ranking.</summary>
    public int Consider(FightRecord record, string fightFile, GameData? data = null)
    {
        var s = record.Summary;
        if (s.CombatMs < MinCombatMs) return 0;
        var changed = 0;
        lock (_gate)
        {
            foreach (var d in record.Details)
            {
                if (d.Class == GameClass.Unknown || d.ActorId == Combatant.UnknownSummonsId || d.Dps <= 0 || d.Damage <= 0) continue;
                if (string.IsNullOrEmpty(d.Name) || d.Name[0] == '#') continue; // never identified (out of view)
                if (!_byClass.TryGetValue(d.Class, out var list)) _byClass[d.Class] = list = new List<TopEntry>(Keep + 1);
                var i = list.FindIndex(e => e.ServerId == d.ServerId && string.Equals(e.Name, d.Name, StringComparison.Ordinal));
                if (i >= 0)
                {
                    if (list[i].Dps >= d.Dps) continue;
                    list.RemoveAt(i);
                }
                else if (list.Count >= Keep && list[^1].Dps >= d.Dps) continue;
                list.Add(Build(d, s, fightFile, data));
                list.Sort((a, b) => b.Dps.CompareTo(a.Dps));
                if (list.Count > Keep) list.RemoveRange(Keep, list.Count - Keep);
                changed++;
            }
            if (changed > 0) _dirty = true;
        }
        if (changed > 0) Changed?.Invoke();
        return changed;
    }

    private static TopEntry Build(CombatantDetail d, EncounterSnapshot s, string fightFile, GameData? data)
    {
        var skills = new List<TopSkill>(d.Skills.Count);
        foreach (var r in d.Skills) skills.Add(new TopSkill(r.SkillCode, r.Name, r.Hits, r.Damage, r.Share));
        // One entry per cast: the damage opcode repeats the skill for every hit of a multi-hit cast (same offset), DoT ticks are not casts.
        var casts = new List<TopCast>(Math.Min(d.Casts.Count, MaxCasts));
        var lastT = int.MinValue; var lastS = 0;
        foreach (var c in d.Casts)
        {
            if ((c.Flags & HitFlags.Dot) != 0) continue;
            if (c.OffsetMs == lastT && c.SkillCode == lastS) continue;
            lastT = c.OffsetMs; lastS = c.SkillCode;
            casts.Add(new TopCast(c.OffsetMs, c.SkillCode));
            if (casts.Count >= MaxCasts) break;
        }
        return new TopEntry(d.Name, d.Class, d.ServerId, d.IsSelf, d.Dps, d.Damage, s.CombatMs, s.Title, s.Zone, s.Boss?.NpcCode ?? 0,
            s.StartedAt, fightFile, skills, casts);
    }

    /// <summary>Feeds every saved fight; used once when the store is empty (and by the CLI).</summary>
    public int Rebuild(HistoryStore history, GameData? data = null)
    {
        var n = 0;
        foreach (var e in history.List())
        {
            var rec = history.Load(e.Path);
            if (rec is not null) n += Consider(rec, e.FileName, data);
        }
        return n;
    }

    // ------------------------------------------------------------------ rotation analysis (for the window and the CLI)

    /// <summary>The first <paramref name="n"/> casts (the opener).</summary>
    public static IEnumerable<int> Opening(TopEntry e, int n) => e.Casts.Take(n).Select(c => c.S);

    /// <summary>Most repeated sequences of <paramref name="len"/> consecutive casts (the loop), with counts; same-skill repeats are kept.</summary>
    public static List<(int[] Skills, int Count)> Frequent(TopEntry e, int len, int top)
    {
        var counts = new Dictionary<string, (int[] Skills, int Count)>();
        var cs = e.Casts;
        var key = new int[len];
        for (var i = 0; i + len <= cs.Count; i++)
        {
            for (var j = 0; j < len; j++) key[j] = cs[i + j].S;
            var k = string.Join(',', key);
            if (counts.TryGetValue(k, out var v)) counts[k] = (v.Skills, v.Count + 1);
            else counts[k] = ((int[])key.Clone(), 1);
        }
        return counts.Values.Where(v => v.Count > 1).OrderByDescending(v => v.Count).Take(top).ToList();
    }

    /// <summary>Casts per minute and mean gap between casts of each skill (how close to the cooldown it is used).</summary>
    public static List<(int Skill, int Casts, double PerMinute, double MeanGapS)> Cadence(TopEntry e)
    {
        var last = new Dictionary<int, int>();
        var n = new Dictionary<int, int>();
        var gap = new Dictionary<int, long>();
        foreach (var c in e.Casts)
        {
            n[c.S] = n.GetValueOrDefault(c.S) + 1;
            if (last.TryGetValue(c.S, out var t)) gap[c.S] = gap.GetValueOrDefault(c.S) + (c.T - t);
            last[c.S] = c.T;
        }
        var minutes = Math.Max(e.CombatMs, 1) / 60_000.0;
        return n.Select(kv => (kv.Key, kv.Value, kv.Value / minutes, kv.Value > 1 ? gap.GetValueOrDefault(kv.Key) / 1000.0 / (kv.Value - 1) : 0))
            .OrderByDescending(x => x.Item2).ToList();
    }

    // ------------------------------------------------------------------ persistence

    private DateTime _stamp;

    /// <summary>A2M-48 Linux UI: the daemon rewrote top-dps.json after a fight: take the file's ranking. False when unchanged.</summary>
    public bool ReloadIfChanged()
    {
        if (_path is null) return false;
        try { if (!File.Exists(_path) || File.GetLastWriteTimeUtc(_path) == _stamp) return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        lock (_gate) { _byClass.Clear(); Load(); }
        Changed?.Invoke();
        return true;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            _stamp = File.GetLastWriteTimeUtc(_path);
            var all = JsonSerializer.Deserialize<List<TopEntry>>(File.ReadAllText(_path), Json);
            if (all is null) return;
            foreach (var e in all)
            {
                if (!_byClass.TryGetValue(e.Class, out var l)) _byClass[e.Class] = l = new List<TopEntry>(Keep + 1);
                l.Add(e);
            }
            foreach (var l in _byClass.Values) l.Sort((a, b) => b.Dps.CompareTo(a.Dps));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    public void Save()
    {
        if (_path is null) return;
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            text = JsonSerializer.Serialize(_byClass.Values.SelectMany(l => l), Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _path, overwrite: true);
            _stamp = File.GetLastWriteTimeUtc(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_gate) _dirty = true; }
    }
}
