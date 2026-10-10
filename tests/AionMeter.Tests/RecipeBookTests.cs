using AionMeter.Core.GameDb;

namespace AionMeter.Tests;

public class RecipeBookTests
{
    private static RecipeBook Sample()
    {
        var path = Path.Combine(Path.GetTempPath(), "recipes-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """[{"o":210140015,"n":"Artisan's Orichalcum Breastplate","p":"tailoring","l":30,"g":"legendary","y":1,"m":[[210140014,1],[610530008,3],[610620015,5],[610610015,3]]},{"o":610710001,"n":"Orichalcum Ingot","p":"blacksmithing","l":1,"g":"common","y":1,"m":[[610620013,3]]}]""");
        var b = RecipeBook.Load(path); File.Delete(path); return b;
    }

    [Fact]
    public void CraftableTimesAndNearMisses()
    {
        var book = Sample();
        var stock = new Dictionary<int, int> { [610620013] = 10, [210140014] = 1, [610530008] = 3, [610620015] = 2, [610610015] = 3 };
        var opts = book.Evaluate(i => stock.GetValueOrDefault(i));
        Assert.Equal(2, opts.Count);
        Assert.Equal("Orichalcum Ingot", opts[0].Recipe.Name);
        Assert.Equal(3, opts[0].Times);                                   // 10 ore / 3
        Assert.Equal(0, opts[1].Times);
        Assert.Single(opts[1].Missing);
        Assert.Equal((610620015, 5, 2), opts[1].Missing[0]);              // short of Pure Orichalcum Ore
        Assert.Single(book.Evaluate(i => stock.GetValueOrDefault(i), profession: "blacksmithing"));
        Assert.Empty(book.Evaluate(_ => 0, maxMissingKinds: 0));
    }

    [Fact]
    public void MissingFileIsEmpty() => Assert.Equal(0, RecipeBook.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))).Count);
}
