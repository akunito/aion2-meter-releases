using AionMeter.Core.GameDb;

namespace AionMeter.Tests;

public class TitleBookTests
{
    private static TitleBook Sample()
    {
        var path = Path.Combine(Path.GetTempPath(), "titles-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """[{"i":11010001,"n":"Draped in Sky","c":"Quest","g":"Common","f":"l","d":"Quest","e":[["FixingDamage","5"]],"k":[["Defense","10"]]},{"i":11010003,"n":"Illusion Illuminator","c":"Quest","g":"Rare","f":"l","d":"","e":[["Defense","180"],["Restoration","1.9%"]],"k":[["DecreaseDamage","0.4%"]]},{"i":21020064,"n":"A Change of Perspective","c":"Quest","g":"Common","f":"d","d":"Wesley","e":[["RestHPRegen","64"]],"k":[["Defense","10"]]}]""");
        var b = TitleBook.Load(path); File.Delete(path); return b;
    }

    [Fact]
    public void LoadsNamesFactionsAndBonuses()
    {
        var b = Sample();
        Assert.Equal(3, b.Count);
        var t = b.Get(11010003)!;
        Assert.Equal("Illusion Illuminator", t.Name);
        Assert.Equal('l', t.Faction);
        Assert.Equal(("Restoration", "1.9%"), t.Equipped[1]);
        Assert.Null(b.Get(1));
        Assert.Equal(2, TitleBook.GradeRank("Heroic"));
    }

    [Fact]
    public void CollectionTotalsAndFactionFollowTheOwnedTitles()
    {
        var b = Sample();
        var owned = new[] { 11010001, 11010003, 99 };
        var totals = b.CollectionTotals(owned);
        Assert.Equal(10, totals["Defense"]);
        Assert.Equal(0.4, totals["DecreaseDamage%"], 3);
        Assert.Equal('l', b.FactionOf(owned));
        Assert.Equal('d', b.FactionOf(new[] { 21020064 }));
        Assert.Equal('?', b.FactionOf(Array.Empty<int>()));
    }
}
