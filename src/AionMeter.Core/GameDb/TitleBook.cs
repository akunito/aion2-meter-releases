using System.Globalization;
using System.Text.Json;

namespace AionMeter.Core.GameDb;

/// <summary>Faction is 'l' (Elyos/light) or 'd' (Asmodian/dark); Equipped applies while worn, Collection is permanent once owned.</summary>
public sealed record TitleInfo(int Id, string Name, string Category, string Grade, char Faction, string Description, (string Stat, string Value)[] Equipped, (string Stat, string Value)[] Collection);

/// <summary>Titles from data/gamedb/titles.json (aion2hub, tools/titles.py): names for the ids of the login title list and the bonuses they carry.</summary>
public sealed class TitleBook
{
    public static readonly TitleBook Empty = new(Array.Empty<TitleInfo>());
    private readonly TitleInfo[] _all;
    private readonly Dictionary<int, TitleInfo> _byId;
    private TitleBook(TitleInfo[] all) { _all = all; _byId = new Dictionary<int, TitleInfo>(all.Length); foreach (var t in all) _byId[t.Id] = t; }
    public IReadOnlyList<TitleInfo> All => _all;
    public int Count => _all.Length;
    public TitleInfo? Get(int id) => _byId.GetValueOrDefault(id);

    /// <summary>Special > Unique > Heroic > Epic > Rare > Common, for ordering what is worth chasing first.</summary>
    public static int GradeRank(string grade) => grade switch { "Special" => 0, "Unique" => 1, "Heroic" => 2, "Epic" => 3, "Rare" => 4, _ => 5 };

    public static TitleBook Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var list = new List<TitleInfo>(1500);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var f = r.TryGetProperty("f", out var fe) ? fe.GetString() : null;
                list.Add(new TitleInfo(r.GetProperty("i").GetInt32(), r.GetProperty("n").GetString() ?? "", r.GetProperty("c").GetString() ?? "", r.GetProperty("g").GetString() ?? "",
                                       string.IsNullOrEmpty(f) ? '?' : f[0], r.GetProperty("d").GetString() ?? "", Pairs(r, "e"), Pairs(r, "k")));
            }
            return new TitleBook(list.ToArray());
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { return Empty; }
    }

    private static (string, string)[] Pairs(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<(string, string)>();
        var list = new List<(string, string)>(2);
        foreach (var p in arr.EnumerateArray()) list.Add((p[0].GetString() ?? "", p[1].GetString() ?? ""));
        return list.ToArray();
    }

    /// <summary>Sum of the permanent collection bonuses of the owned titles, per stat; percent values summed under "Stat%".</summary>
    public Dictionary<string, double> CollectionTotals(IReadOnlyCollection<int> owned)
    {
        var totals = new Dictionary<string, double>();
        foreach (var id in owned)
        {
            if (!_byId.TryGetValue(id, out var t)) continue;
            foreach (var (stat, value) in t.Collection)
            {
                var pct = value.EndsWith('%');
                if (!double.TryParse(pct ? value[..^1] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                var key = pct ? stat + "%" : stat;
                totals[key] = totals.GetValueOrDefault(key) + v;
            }
        }
        return totals;
    }

    /// <summary>The faction most of the owned titles belong to ('?' when nothing is owned or known).</summary>
    public char FactionOf(IReadOnlyCollection<int> owned)
    {
        int l = 0, d = 0;
        foreach (var id in owned) if (_byId.TryGetValue(id, out var t)) { if (t.Faction == 'l') l++; else if (t.Faction == 'd') d++; }
        return l == 0 && d == 0 ? '?' : l >= d ? 'l' : 'd';
    }
}
