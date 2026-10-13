using System.Net;
using AionMeter.Core.GameDb;

namespace AionMeter.Tests;

public class ItemNameResolverTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "questlog-items", name));

    private static ItemNames Catalogue()
    {
        var path = Path.Combine(Path.GetTempPath(), "names-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"610511013":["Wrathful Mind","Perversión de ira"]}""");
        var n = ItemNames.Load(path);
        File.Delete(path);
        return n;
    }

    /// <summary>Answers getItem from the fixtures: 503700031 in en/es, every other id the 500 questlog sends for an unknown id.</summary>
    private sealed class FakeQuestlog : HttpMessageHandler
    {
        public readonly List<string> Asked = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var input = Uri.UnescapeDataString(request.RequestUri!.Query);
            lock (Asked) Asked.Add(input);
            var lang = input.Contains("\"language\":\"es\"") ? "es" : "en";
            var body = input.Contains("\"503700031\"") ? Fixture("503700031." + lang + ".json") : Fixture("unknown.json");
            var code = body.Contains("\"error\"") ? HttpStatusCode.InternalServerError : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void ParsesGetItemInBothLanguages()
    {
        var en = ItemNameResolver.Parse(Fixture("503700031.en.json"));
        var es = ItemNameResolver.Parse(Fixture("503700031.es.json"));
        Assert.NotNull(en); Assert.NotNull(es);
        Assert.Equal("Odyle Energy (Bound)", en!.Name);
        Assert.Equal("Energía de Od (vinculado)", es!.Name);
        Assert.Equal(71, en.GradeCode);
        Assert.True(en.Bound);   // exchangeType nonexchangeable
        Assert.Equal("https://cdn.questlog.gg/aion-2/assets/Game/UI/Resource/Texture/Item/ETC/Icon_Item_Odenergy_A_001.webp", ItemNameResolver.IconUrl(en.Icon!));
        Assert.Null(ItemNameResolver.Parse(Fixture("unknown.json")));
        Assert.Null(ItemNameResolver.Parse("""{"result":{"data":null}}"""));
        Assert.Null(ItemNameResolver.Parse("not json"));
    }

    [Fact]
    public void GradeCodesMapToTheLadder()
    {
        Assert.Equal(Grade.Common, ItemNameResolver.GradeOf(11));
        Assert.Equal(Grade.Rare, ItemNameResolver.GradeOf(21));
        Assert.Equal(Grade.Legendary, ItemNameResolver.GradeOf(31));
        Assert.Equal(Grade.Unique, ItemNameResolver.GradeOf(41));
        Assert.Equal(Grade.Special, ItemNameResolver.GradeOf(71));
        Assert.Equal(Grade.Unknown, ItemNameResolver.GradeOf(51));
    }

    [Fact]
    public void ExtrasMergeIntoNamesAndGrades()
    {
        var n = Catalogue();
        var grades = ItemGrades.Parse("""{"610511013":"legendary"}""");
        n.AttachGrades(grades);
        var changed = 0;
        n.Changed += () => changed++;
        n.AddExtras(new[]
        {
            new ExtraItem(600590001, "Balaur's Essence", "Esencia de Balaur", Grade.Legendary, false, "/assets/Game/UI/Resource/Texture/Item/ETC/Icon_X.Icon_X"),
            new ExtraItem(610511013, "Overwrite attempt", "", Grade.Common, false, null),   // catalogue ids keep their name
        });
        Assert.Equal(1, changed);
        Assert.Equal("Balaur's Essence", n.Name(600590001));
        Assert.Equal("Esencia de Balaur", n.Name(600590001, "es"));
        Assert.Equal("Balaur's Essence", n.Label(600590001));
        Assert.Equal(("Balaur's Essence", "Esencia de Balaur"), n.Both(600590001));
        Assert.Equal(Grade.Legendary, n.GradeOf(600590001));
        Assert.Equal(Grade.Legendary, grades.Of(600590001));   // ItemGrades falls back to the extras
        Assert.False(grades.Bound(600590001));
        Assert.Equal("Wrathful Mind", n.Name(610511013));
        Assert.True(n.Known(600590001));
        Assert.False(n.Known(42));
        Assert.Equal("https://cdn.questlog.gg/aion-2/assets/Game/UI/Resource/Texture/Item/ETC/Icon_X.webp", n.ExtraIconUrl(600590001));
        var hits = n.Search("esencia", "es");
        Assert.Single(hits);
        Assert.Equal(600590001, hits[0].Id);
        Assert.Equal(2, n.Search("legendary").Count);   // grade word finds the catalogue item and the extra
    }

    [Fact]
    public async Task ResolvesOnceAndNeverReasksFailedIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "resolver-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "item-names-extra.json");
        try
        {
            var n = Catalogue();
            var api = new FakeQuestlog();
            using (var r = new ItemNameResolver(n, path, api) { PaceMs = 0 })
            {
                r.Want(503700031); r.Want(503700031); r.Want(999999999); r.Want(610511013);   // known id: no request
                await r.IdleAsync();
                r.Want(999999999);   // failed this session: not asked again
                await r.IdleAsync();
                Assert.Equal(3, api.Asked.Count);   // 503700031 en + es, 999999999 en (an unknown id is not asked in es)
            }
            Assert.Equal("Odyle Energy (Bound)", n.Name(503700031));
            Assert.Equal("Energía de Od (vinculado)", n.Name(503700031, "es"));
            Assert.Equal(Grade.Special, n.GradeOf(503700031));
            Assert.Null(n.Name(999999999));

            // round trip: a new session loads the file and asks nothing
            var saved = ItemNameResolver.Load(path);
            var x = Assert.Single(saved);
            Assert.Equal(new ExtraItem(503700031, "Odyle Energy (Bound)", "Energía de Od (vinculado)", Grade.Special, true, "/assets/Game/UI/Resource/Texture/Item/ETC/Icon_Item_Odenergy_A_001.Icon_Item_Odenergy_A_001"), x);
            Assert.Contains("\"Energía de Od (vinculado)\"", File.ReadAllText(path));
            var n2 = Catalogue();
            var api2 = new FakeQuestlog();
            using var r2 = new ItemNameResolver(n2, path, api2) { PaceMs = 0 };
            r2.Want(503700031);
            await r2.IdleAsync();
            Assert.Empty(api2.Asked);
            Assert.Equal("Odyle Energy (Bound)", n2.Name(503700031));
            Assert.True(n2.ExtraBound(503700031));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void DisabledResolverAsksNothing()
    {
        var api = new FakeQuestlog();
        using var r = new ItemNameResolver(Catalogue(), null, api) { Enabled = false };
        r.Want(503700031);
        Assert.Empty(api.Asked);
    }
}
