using System.Text.Json;

namespace AionMeter.Core.GameDb;

public sealed record Recipe(int Output, string Name, string Profession, int Level, string Grade, int Yield, (int Item, int Qty)[] Materials);

/// <summary>One recipe against what the player holds: Times = how many crafts the stock allows; Missing = materials short (item, need, have).</summary>
public sealed record CraftOption(Recipe Recipe, int Times, (int Item, int Need, int Have)[] Missing);

/// <summary>Value = cheapest listing × yield after the broker fee; Cost = cheapest listings of the materials; UnpricedMaterials = materials the snapshot does not list (cost counted as 0: a gathered or quest item, or a gap).</summary>
public sealed record CraftProfit(Recipe Recipe, long OutUnit, long Value, long Cost, long Profit, int UnpricedMaterials, int Times);

/// <summary>Recipes from data/gamedb/recipes.json (aion2hub, 1,684 global recipes) matched against the inventory counts.</summary>
public sealed class RecipeBook
{
    public static readonly RecipeBook Empty = new(Array.Empty<Recipe>());
    private readonly Recipe[] _recipes;
    private RecipeBook(Recipe[] recipes) { _recipes = recipes; }

    public IReadOnlyList<Recipe> All => _recipes;
    public int Count => _recipes.Length;

    private Dictionary<int, Recipe[]>? _usedIn;
    private static readonly Recipe[] None = [];

    /// <summary>Recipes that take <paramref name="item"/> as a material (index built on first use).</summary>
    public Recipe[] UsedIn(int item)
    {
        var idx = _usedIn;
        if (idx is null)
        {
            var tmp = new Dictionary<int, List<Recipe>>(2_000);
            foreach (var r in _recipes)
                foreach (var (m, _) in r.Materials)
                {
                    if (!tmp.TryGetValue(m, out var l)) tmp[m] = l = new List<Recipe>(4);
                    if (l.Count == 0 || !ReferenceEquals(l[^1], r)) l.Add(r);
                }
            idx = new Dictionary<int, Recipe[]>(tmp.Count);
            foreach (var (k, v) in tmp) idx[k] = v.ToArray();
            _usedIn = idx;
        }
        return idx.TryGetValue(item, out var a) ? a : None;
    }

    public static RecipeBook Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var list = new List<Recipe>(1800);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var mats = new List<(int, int)>();
                foreach (var m in r.GetProperty("m").EnumerateArray()) mats.Add((m[0].GetInt32(), m[1].GetInt32()));
                list.Add(new Recipe(r.GetProperty("o").GetInt32(), r.GetProperty("n").GetString() ?? "", r.GetProperty("p").GetString() ?? "", r.GetProperty("l").GetInt32(), r.GetProperty("g").GetString() ?? "", r.GetProperty("y").GetInt32(), mats.ToArray()));
            }
            return new RecipeBook(list.ToArray());
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return Empty; }
    }

    /// <summary>Every recipe the stock allows at least once (Times ≥ 1), plus those short by at most <paramref name="maxMissingKinds"/> materials; sorted craftable first, then by level descending.</summary>
    /// <summary>Every recipe whose output has a price, by profit (one craft). <paramref name="count"/> = stock, for "×N now".</summary>
    public List<CraftProfit> Profit(Func<int, long?> price, double feePercent, Func<int, int> count, string? profession = null)
    {
        var list = new List<CraftProfit>(_recipes.Length / 4);
        var keep = 1 - Math.Clamp(feePercent, 0, 50) / 100.0;
        foreach (var r in _recipes)
        {
            if (profession is not null && !string.Equals(r.Profession, profession, StringComparison.OrdinalIgnoreCase)) continue;
            if (price(r.Output) is not { } unit) continue;
            long cost = 0; var unpriced = 0; var times = int.MaxValue;
            foreach (var (item, qty) in r.Materials)
            {
                if (price(item) is { } p) cost += p * qty; else unpriced++;
                var have = count(item); times = Math.Min(times, qty > 0 ? have / qty : times);
            }
            if (r.Materials.Length == 0) times = 0;
            var value = (long)(unit * Math.Max(1, r.Yield) * keep);
            list.Add(new CraftProfit(r, unit, value, cost, value - cost, unpriced, times == int.MaxValue ? 0 : times));
        }
        list.Sort((a, b) => b.Profit.CompareTo(a.Profit));
        return list;
    }

    public List<CraftOption> Evaluate(Func<int, int> count, int maxMissingKinds = 2, string? profession = null)
    {
        var result = new List<CraftOption>();
        var missing = new List<(int, int, int)>(4);
        foreach (var r in _recipes)
        {
            if (profession is not null && r.Profession != profession) continue;
            var times = int.MaxValue; missing.Clear();
            foreach (var (item, qty) in r.Materials)
            {
                var have = count(item);
                if (have < qty) missing.Add((item, qty, have));
                else times = Math.Min(times, have / qty);
            }
            if (missing.Count == 0) result.Add(new CraftOption(r, times == int.MaxValue ? 0 : times, Array.Empty<(int, int, int)>()));
            else if (missing.Count <= maxMissingKinds && r.Materials.Length > missing.Count) result.Add(new CraftOption(r, 0, missing.ToArray()));
        }
        result.Sort((a, b) => a.Times > 0 != b.Times > 0 ? (a.Times > 0 ? -1 : 1) : a.Missing.Length != b.Missing.Length ? a.Missing.Length.CompareTo(b.Missing.Length) : b.Recipe.Level.CompareTo(a.Recipe.Level));
        return result;
    }
}
