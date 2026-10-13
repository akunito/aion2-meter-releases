using AionMeter.Core.Market;

namespace AionMeter.Tests;

public class PriceHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static List<PricePoint> Hourly(DateTimeOffset from, int hours, long basePrice) =>
        Enumerable.Range(0, hours).Select(h => new PricePoint(from.AddHours(h), basePrice + h, basePrice + h + 50, 0, basePrice + h, 10 + h % 5)).ToList();

    [Fact]
    public void MergesTheSiteSeriesAndReplacesARereadBucket()
    {
        var pts = PriceWatchStore.ParsePrices(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "market", "exchange-prices-610511013.json")));
        var h = new PriceHistory(null);
        var at = new DateTimeOffset(2026, 10, 8, 22, 0, 0, TimeSpan.Zero);
        Assert.Equal(43, h.Merge(610511013, pts, at));
        Assert.Equal(0, h.Merge(610511013, pts, at));                       // the same 7-day window read again adds nothing
        var last = pts[^1];
        var reread = new List<PricePoint> { last with { Close = 1_700_000 }, last with { Ts = last.Ts.AddHours(1), Close = 1_650_000 } };
        Assert.Equal(1, h.Merge(610511013, reread, at.AddHours(1)));
        var all = h.Window(610511013, PriceRange.Month, at.AddHours(1));
        Assert.Equal(44, all.Length);
        Assert.Equal(1_700_000, all[^2].Price);                              // the newer read of the same hour wins
        Assert.Equal(1_650_000, all[^1].Price);
        Assert.Equal(pts[0].Ts, h.First(610511013));
    }

    [Fact]
    public void KeepsThirtyDaysAtMostAndPrunesOlder()
    {
        var h = new PriceHistory(null);
        h.Merge(1, Hourly(Now.AddDays(-40), 40 * 24, 1000), Now);
        Assert.Equal(PriceHistory.MaxPoints, h.Count(1));
        Assert.True(h.First(1) >= Now.AddDays(-30));
        h.Merge(1, Hourly(Now.AddHours(1), 24, 5000), Now.AddDays(1));
        Assert.True(h.Count(1) <= PriceHistory.MaxPoints);
        Assert.True(h.First(1) >= Now.AddDays(-29));
        h.Merge(2, Hourly(Now.AddDays(-2), 10, 7), Now);
        h.RetainOnly([1]);
        Assert.Equal(0, h.Count(2));
    }

    [Fact]
    public void RangeWindowsCutAtTwentyFourHoursSevenAndThirtyDays()
    {
        var h = new PriceHistory(null);
        h.Merge(1, Hourly(Now.AddDays(-30), 30 * 24 + 1, 100), Now);
        Assert.Equal(25, h.Window(1, PriceRange.Day, Now).Length);           // both ends inclusive
        Assert.Equal(7 * 24 + 1, h.Window(1, PriceRange.Week, Now).Length);
        Assert.Equal(PriceHistory.MaxPoints, h.Window(1, PriceRange.Month, Now).Length);
        Assert.All(h.Window(1, PriceRange.Day, Now), s => Assert.True(s.Ts >= Now.AddDays(-1).ToUnixTimeSeconds()));
        Assert.Empty(h.Window(99, PriceRange.Day, Now));
        Assert.Equal(PriceRange.Day, PriceHistory.ParseRange("24h"));
        Assert.Equal(PriceRange.Month, PriceHistory.ParseRange("30d"));
        Assert.Equal(PriceRange.Week, PriceHistory.ParseRange(null));
        Assert.Equal("30d", PriceHistory.RangeKey(PriceRange.Month));
    }

    [Fact]
    public void DownsamplesThirtyDaysToTwoHundredPointsKeepingExtremesAndTheLast()
    {
        var rnd = new Random(7);
        var src = Enumerable.Range(0, 720).Select(i => new PriceSample(1_000_000 + i * 3600L, 500_000 + rnd.Next(100_000), 0, 1)).ToArray();
        src[333] = src[333] with { Price = 9_999_999 };
        src[444] = src[444] with { Price = 1 };
        var d = PriceHistory.Downsample(src);
        Assert.True(d.Length <= PriceHistory.ChartPoints);
        Assert.True(d.Length >= 150);
        Assert.Contains(d, s => s.Price == 9_999_999);
        Assert.Contains(d, s => s.Price == 1);
        Assert.Equal(src[^1], d[^1]);
        for (var i = 1; i < d.Length; i++) Assert.True(d[i].Ts > d[i - 1].Ts);
        var small = src.Take(150).ToArray();
        Assert.Same(small, PriceHistory.Downsample(small));
        var (min, med, max) = PriceHistory.Stats([new(0, 30, 0, 0), new(1, 10, 0, 0), new(2, 20, 0, 0), new(3, 40, 0, 0)]);
        Assert.Equal((10L, 25L, 40L), (min, med, max));
    }

    [Fact]
    public void SavesCompactJsonAndLoadsItBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ph-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "price-history.json");
        var h = new PriceHistory(path);
        h.Merge(610511013, Hourly(Now.AddDays(-3), 72, 1_000_000), Now);
        h.Save();
        var text = File.ReadAllText(path);
        Assert.StartsWith("{\"v\":1,\"items\":{\"610511013\":[[", text);
        Assert.True(text.Length < 72 * 40);
        var again = new PriceHistory(path);
        Assert.Equal(72, again.Count(610511013));
        Assert.Equal(h.Window(610511013, PriceRange.Week, Now), again.Window(610511013, PriceRange.Week, Now));
        Directory.Delete(dir, true);
    }
}
