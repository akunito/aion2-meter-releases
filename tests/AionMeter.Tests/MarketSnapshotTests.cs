using AionMeter.Core.GameDb;
using AionMeter.Core.Market;

namespace AionMeter.Tests;

public class MarketSnapshotTests
{
    private const string Export = """{"exportedAt":"2026-10-08T21:53:22.011Z","server":{"name":"Vaizel","server_id":11303},"items":{"610530010":{"item_key":610530010,"name":"Stone","price":380000,"listings":183,"captured_at":"2026-10-08T21:34:39.129000Z"},"210140015":{"item_key":210140015,"name":"Breastplate","price":5000000,"listings":2,"captured_at":"2026-10-08T21:34:39.129000Z"},"610620015":{"item_key":610620015,"name":"Ore","price":1000,"listings":40,"captured_at":"2026-10-08T21:34:39.129000Z"}},"prices":{},"volume":{}}""";

    [Fact]
    public void ParsesTheBrowserExport()
    {
        var m = MarketSnapshot.Parse(Export);
        Assert.Equal(3, m.Count);
        Assert.Equal("Vaizel", m.Server);
        Assert.Equal(380_000, m.Price(610530010));
        Assert.Null(m.Price(1));
        Assert.Equal(new DateTime(2026, 10, 8, 21, 34, 39, DateTimeKind.Utc).Ticks / TimeSpan.TicksPerSecond, m.CapturedAt!.Value.UtcTicks / TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void ProfitUsesCheapestListingsAndTheFee()
    {
        var m = MarketSnapshot.Parse(Export);
        var path = Path.Combine(Path.GetTempPath(), "recipes-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """[{"o":210140015,"n":"Breastplate","p":"tailoring","l":30,"g":"legendary","y":1,"m":[[610530010,3],[610620015,5],[999,1]]},{"o":424242,"n":"Unsold","p":"cooking","l":1,"g":"common","y":1,"m":[[610620015,1]]}]""");
        var book = RecipeBook.Load(path); File.Delete(path);
        var list = book.Profit(m.Price, 10, item => item == 610620015 ? 10 : 0);
        var p = Assert.Single(list);                                   // the unsold output has no price → not listed
        Assert.Equal(4_500_000, p.Value);                              // 5,000,000 after a 10 % fee
        Assert.Equal(3 * 380_000 + 5 * 1_000, p.Cost);                 // item 999 is unpriced and counted as 0
        Assert.Equal(1, p.UnpricedMaterials);
        Assert.Equal(p.Value - p.Cost, p.Profit);
        Assert.Equal(0, p.Times);                                      // no stones in stock
    }
}
