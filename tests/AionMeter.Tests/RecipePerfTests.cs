using System.Diagnostics;
using AionMeter.Core.GameDb;
using AionMeter.Core.Market;
using Xunit.Abstractions;

namespace AionMeter.Tests;

/// <summary>Data-layer cost of the Craft tab on the real recipe book (build.sh copies data/gamedb before the tests). The UI is virtualised; this guards the part that runs per keystroke.</summary>
public class RecipePerfTests(ITestOutputHelper output)
{
    private static string? Find(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [Fact]
    public void Evaluate_and_profit_over_the_real_book_stay_fast()
    {
        var path = Find("data/gamedb/recipes.json");
        if (path is null) { output.WriteLine("recipes.json not present: skipped"); return; }
        var book = RecipeBook.Load(path);
        var names = ItemNames.Load(Find("data/gamedb/item-names.json") ?? "");
        var stock = new Dictionary<int, int>(); var rnd = new Random(7);
        foreach (var r in book.All) foreach (var (item, _) in r.Materials) if (rnd.Next(4) == 0) stock[item] = rnd.Next(1, 40);
        int Count(int item) => stock.GetValueOrDefault(item);
        var sw = Stopwatch.StartNew();
        List<CraftOption> opts = new();
        for (var i = 0; i < 20; i++) opts = book.Evaluate(Count, 99);
        var evalMs = sw.Elapsed.TotalMilliseconds / 20;
        sw.Restart();
        var n = 0;
        for (var i = 0; i < 20; i++) foreach (var o in opts) { n += (names.Name(o.Recipe.Output, "en") ?? "").Length; foreach (var m in o.Missing) n += (names.Source(m.Item) ?? "").Length; }
        var textMs = sw.Elapsed.TotalMilliseconds / 20;
        sw.Restart();
        var price = new Func<int, long?>(item => item % 3 == 0 ? 1000 + item % 500 : null);
        for (var i = 0; i < 20; i++) book.Profit(price, 10, Count);
        var profitMs = sw.Elapsed.TotalMilliseconds / 20;
        output.WriteLine($"recipes {book.Count}, options {opts.Count}: Evaluate(all) {evalMs:0.0} ms, names+sources {textMs:0.0} ms, Profit {profitMs:0.0} ms (n={n})");
        Assert.True(evalMs < 150, $"Evaluate {evalMs} ms");
        Assert.True(textMs < 150, $"names/sources {textMs} ms");
        Assert.True(profitMs < 150, $"Profit {profitMs} ms");
    }
}
