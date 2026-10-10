using System.Text.Json;

namespace AionMeter.Core.GameDb;

public sealed record Recipe(int Output, string Name, string Profession, int Level, string Grade, int Yield, (int Item, int Qty)[] Materials);

/// <summary>One recipe against what the player holds: Times = how many crafts the stock allows; Missing = materials short (item, need, have).</summary>
public sealed record CraftOption(Recipe Recipe, int Times, (int Item, int Need, int Have)[] Missing);

/// <summary>Recipes from data/gamedb/recipes.json (aion2hub, 1,684 global recipes) matched against the inventory counts.</summary>
public sealed class RecipeBook
{
    public static readonly RecipeBook Empty = new(Array.Empty<Recipe>());
    private readonly Recipe[] _recipes;
    private RecipeBook(Recipe[] recipes) { _recipes = recipes; }

    public IReadOnlyList<Recipe> All => _recipes;
    public int Count => _recipes.Length;

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
