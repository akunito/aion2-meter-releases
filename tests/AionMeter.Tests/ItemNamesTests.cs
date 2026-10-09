using AionMeter.Core.GameDb;

namespace AionMeter.Tests;

public class ItemNamesTests
{
    private static ItemNames Sample()
    {
        var path = Path.Combine(Path.GetTempPath(), "names-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"610511013":["Wrathful Mind","Perversión de ira"],"610511014":["Wrathful Will","Voluntad de ira"],"110130002":["Wisdom Greatsword",""],"1":["Mind Stone","Piedra de la mente"]}""");
        var n = ItemNames.Load(path);
        File.Delete(path);
        return n;
    }

    [Fact]
    public void NamesByLanguageWithEnglishFallback()
    {
        var n = Sample();
        Assert.Equal(4, n.Count);
        Assert.Equal(3, n.CountWith("es"));
        Assert.Equal("Perversión de ira", n.Name(610511013, "es"));
        Assert.Equal("Wrathful Mind", n.Name(610511013));
        Assert.Equal("Wisdom Greatsword", n.Name(110130002, "es"));
        Assert.Null(n.Name(42));
    }

    [Fact]
    public void SearchIsAccentInsensitiveAndRanksPrefixFirst()
    {
        var n = Sample();
        var es = n.Search("perversion ira", "es");
        Assert.Single(es);
        Assert.Equal(610511013, es[0].Id);
        var en = n.Search("mind");
        Assert.Equal(2, en.Count);
        Assert.Equal(1, en[0].Id);              // "Mind Stone" starts with the word
        Assert.Equal(610511013, en[1].Id);      // "Wrathful Mind" contains it
        Assert.Empty(n.Search("m"));
        Assert.Equal("perversion de ira", ItemNames.Normalize("Perversión de ira!"));
    }

    [Fact]
    public void MissingFileIsEmpty() => Assert.Equal(0, ItemNames.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))).Count);
}
