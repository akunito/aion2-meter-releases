using AionMeter.Core.Market;

namespace AionMeter.Tests;

public class PriceWatchTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "market", name));

    [Fact]
    public void ParsesExchangePriceBuckets()
    {
        var pts = PriceWatchStore.ParsePrices(Fixture("exchange-prices-610511013.json"));
        Assert.Equal(43, pts.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero), pts[0].Ts);
        Assert.Equal(765_000, pts[0].Low);
        Assert.Equal(1_799_990, pts[^1].Close);
        Assert.Equal(31, pts[^1].Listings);
    }

    [Fact]
    public void HitAtOrBelowMaximumWithTrend()
    {
        var pts = PriceWatchStore.ParsePrices(Fixture("exchange-prices-610511013.json"));
        var w = new PriceWatch { Item = 610511013, MaxPrice = 1_800_000 };
        PriceWatchStore.Apply(w, pts, DateTimeOffset.Now);
        Assert.Equal(1_799_990, w.LastPrice);
        Assert.True(w.Hit);
        Assert.NotNull(w.DayAgoPrice);
        Assert.True(w.DayAgoPrice < w.LastPrice);   // the 3-day climb 600k → 1.8M
        w.MaxPrice = 1_000_000;
        Assert.False(w.Hit);
        PriceWatchStore.Apply(w, new List<PricePoint>(), DateTimeOffset.Now);
        Assert.Equal("no data", w.LastError);
    }

    [Fact]
    public void StoreStartsEmptyRoundTripsAndPaces()
    {
        var path = Path.Combine(Path.GetTempPath(), "pw-" + Guid.NewGuid().ToString("N"), "price-watch.json");
        using var s = new PriceWatchStore(path);
        Assert.Empty(s.All());                                // nothing seeded: the exe is shared with the clan
        s.Add(610511013, "Wrathful Mind", 1_200_000);
        s.Add(610530010, "Artisan's Ultimate Refining Stone", 500_000);
        s.SetSell(610530010, true);
        Assert.True(s.All().Single(w => w.Item == 610530010).Sell);
        Assert.True(s.Due(10_000));
        s.Add(110130002, "Wisdom Greatsword", 900_000);
        s.SetMax(110130002, 800_000);
        s.Save();
        using var again = new PriceWatchStore(path);
        Assert.Equal(3, again.Count);
        Assert.Equal(800_000, again.All().Single(w => w.Item == 110130002).MaxPrice);
        again.Remove(110130002);
        Assert.Equal(2, again.All().Count);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void SellWatchHitsAtOrAboveTarget()
    {
        var w = new PriceWatch { Item = 610530010, MaxPrice = 500_000, Sell = true, Quantity = 26, PaidEach = 225_000, LastPrice = 499_999 };
        Assert.False(w.Hit);
        w.LastPrice = 500_000;
        Assert.True(w.Hit);
        w.Sell = false;                 // buy mode: 500,000 is at the maximum, so still a hit; one above is not
        Assert.True(w.Hit);
        w.LastPrice = 500_001;
        Assert.False(w.Hit);
    }
}
