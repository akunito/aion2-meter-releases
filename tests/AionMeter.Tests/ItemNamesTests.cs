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

    // the four refining stones as the data has them (2026-10-13): EN shares one name between rare and legendary, ES does not
    private static ItemNames Stones()
    {
        var path = Path.Combine(Path.GetTempPath(), "stones-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"610530005":["Artisan's Refining Stone","Piedra de refinamiento de experto"],"610530008":["Artisan's Refining Stone","Piedra de refinamiento de artesano"],"610530010":["Artisan's Ultimate Refining Stone","Piedra de refinamiento suprema de artesano"],"610530001":["Refining Stone","Piedra de refinamiento"],"630530002":["Artisan's Refining Stone (Bound)",""],"660530002":["Artisan's Refining Stone (Bound)",""]}""");
        var n = ItemNames.Load(path);
        File.Delete(path);
        n.AttachGrades(ItemGrades.Parse("""{"610530005":"rare","610530008":"legendary","610530010":"unique","610530001":"common","630530002":"rare b","660530002":"rare b"}"""));
        return n;
    }

    [Fact]
    public void SameNameDifferentGradeIsAmbiguousAndLabelled()
    {
        var n = Stones();
        Assert.Equal(("Artisan's Refining Stone", Grade.Legendary), n.Display(610530008));
        Assert.True(n.Ambiguous(610530005));
        Assert.True(n.Ambiguous(610530008));
        Assert.False(n.Ambiguous(610530008, "es"));            // the game's es-ES names already differ (experto / artesano)
        Assert.False(n.Ambiguous(610530010));
        Assert.False(n.Ambiguous(630530002));                  // two bound copies of one grade: same item to the player
        Assert.False(n.Ambiguous(660530002, "es"));            // ES falls back to EN, still one grade
        Assert.Equal("Artisan's Refining Stone (legendary)", n.Label(610530008));
        Assert.Equal("Artisan's Refining Stone (rare)", n.Label(610530005));
        Assert.Equal("Piedra de refinamiento de artesano", n.Label(610530008, "es"));
        Assert.Equal("Refining Stone", n.Label(610530001));
        Assert.Null(n.Label(42));
        Assert.Equal(Grade.Unknown, n.GradeOf(42));
        Assert.Equal("legendario", ItemGrades.Word(Grade.Legendary, "es"));
        Assert.Equal("unique", ItemGrades.Word(Grade.Unique, "en"));
    }

    [Fact]
    public void SearchMatchesGradeWordsInBothLanguages()
    {
        var n = Stones();
        Assert.Equal(new[] { 610530008 }, n.Search("legendary stone").Select(h => h.Id));
        Assert.Equal(new[] { 610530008 }, n.Search("artisan refining legendary").Select(h => h.Id));
        Assert.Equal(new[] { 610530008 }, n.Search("piedra legendaria", "es").Select(h => h.Id));
        Assert.Equal(new[] { 610530010 }, n.Search("piedra suprema", "es").Select(h => h.Id));          // in the name
        Assert.Equal(new[] { 610530010 }, n.Search("piedra unica", "es").Select(h => h.Id));            // grade word, accents ignored
        Assert.Equal(new[] { 610530005, 630530002, 660530002 }, n.Search("stone rare").Select(h => h.Id).OrderBy(x => x));
        var plain = n.Search("refining stone");
        Assert.Equal(610530001, plain[0].Id);                                                            // name hits rank first, unchanged
        Assert.Equal(6, plain.Count);
        Assert.Empty(n.Search("leg stone"));                                                             // under 4 letters is no grade word
    }

    [Fact]
    public void GradeWordMaskTakesPrefixesAndPlurals()
    {
        static bool Has(string w, Grade g) => (ItemGrades.WordMask(w) & (1 << (int)g)) != 0;
        Assert.True(Has("legend", Grade.Legendary));
        Assert.True(Has("legendarias", Grade.Legendary));
        Assert.True(Has("unic", Grade.Unique));
        Assert.True(Has("comunes", Grade.Common));
        Assert.True(Has("heroic", Grade.Epic));
        Assert.True(Has("especial", Grade.Special));
        Assert.Equal(0, ItemGrades.WordMask("com"));
        Assert.Equal(0, ItemGrades.WordMask("piedra"));
        var q = ItemQuery.Parse("Piedra Legendaria");
        Assert.True(q.Matches(Grade.Legendary, "piedra de refinamiento de artesano"));
        Assert.False(q.Matches(Grade.Rare, "piedra de refinamiento de experto"));
        Assert.True(q.Matches(Grade.Rare, "x", "piedra legendaria rota"));                             // a name that says it still matches
        Assert.True(ItemQuery.Parse("  ").IsEmpty);
    }

    [Fact]
    public void MissingFileIsEmpty() => Assert.Equal(0, ItemNames.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))).Count);
}
