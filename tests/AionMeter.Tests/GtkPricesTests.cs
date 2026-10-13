using System.Globalization;
using AionMeter.Core.GameDb;
using AionMeter.Core.Market;
using AionMeter.Overlay;
using AionMeter.Overlay.Shell;
using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>A2M-50: the GTK Prices tab's chart maths and row rules, display-free (GTK is never loaded here).</summary>
public class GtkPricesTests
{
    private static readonly UiText En = new();

    // ---- chart scaling
    [Fact]
    public void Chart_YRangeAddsAirAndTakesANearThreshold()
    {
        var (lo, hi, thrIn) = PriceChartScale.YRange(1000, 2000, 0);
        Assert.False(thrIn);
        Assert.Equal(920, lo, 6);    // 8 % of the 1000 span below and above
        Assert.Equal(2080, hi, 6);
        // a threshold within 60 % of the span joins the range
        (lo, hi, thrIn) = PriceChartScale.YRange(1000, 2000, 2500);
        Assert.True(thrIn);
        Assert.Equal(2500 + 1500 * 0.08, hi, 6);
        Assert.Equal(1000 - 1500 * 0.08, lo, 6);
        // a far one does not flatten the line: it is drawn as ▲/▼ at the edge instead
        (lo, hi, thrIn) = PriceChartScale.YRange(1000, 2000, 5000);
        Assert.False(thrIn);
        Assert.Equal(2080, hi, 6);
    }

    [Fact]
    public void Chart_FlatSeriesGetsAnHonestRange()
    {
        var (lo, hi, _) = PriceChartScale.YRange(50_000, 50_000, 0);
        Assert.True(hi > 50_000 && lo < 50_000);
        Assert.Equal(50_000 - (hi - 50_000), lo, 6);   // symmetric
        // y maps hi to the top and lo to the bottom
        Assert.Equal(10, PriceChartScale.Y(hi, lo, hi, 10, 110), 6);
        Assert.Equal(110, PriceChartScale.Y(lo, lo, hi, 10, 110), 6);
        Assert.Equal(60, PriceChartScale.Y(50_000, lo, hi, 10, 110), 6);
        // a one-kinah series still has a non-empty range
        (lo, hi, _) = PriceChartScale.YRange(1, 1, 0);
        Assert.True(hi - lo >= 2);
    }

    [Fact]
    public void Chart_TicksFallOnLocalBoundaries()
    {
        Assert.Equal(6 * 3600, PriceChartScale.TickStep(PriceRange.Day));
        Assert.Equal(86_400, PriceChartScale.TickStep(PriceRange.Week));
        Assert.Equal(5 * 86_400, PriceChartScale.TickStep(PriceRange.Month));
        // 2026-10-13 10:17 UTC, UTC+2: the first daily tick is local midnight of the 14th = 22:00 UTC on the 13th
        var from = new DateTimeOffset(2026, 10, 13, 10, 17, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var first = PriceChartScale.FirstTick(from, 86_400, 7200);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), first);
        // 6 h ticks in UTC: 12:00
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), PriceChartScale.FirstTick(from, 6 * 3600, 0));
        // a boundary exactly at `from` is skipped (the axis starts there), the next one counts
        var midnight = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(midnight + 86_400, PriceChartScale.FirstTick(midnight, 86_400, 0));
        // 7 days from a non-boundary: 7 midnights; 24 h: 4 six-hour marks; 30 d at 5-day steps: 6
        Assert.Equal(7, PriceChartScale.TickCount(from, from + 7 * 86_400, 86_400, 0));
        Assert.Equal(4, PriceChartScale.TickCount(from, from + 86_400, 6 * 3600, 0));
        Assert.Equal(6, PriceChartScale.TickCount(from, from + 30 * 86_400, 5 * 86_400, 0));
        Assert.Equal(0, PriceChartScale.TickCount(from, from + 60, 86_400, 0));
    }

    [Fact]
    public void Chart_HoverPicksTheNearestSample()
    {
        double[] xs = [10, 20, 40, 80, 0, 0];
        Assert.Equal(0, PriceChartScale.Nearest(xs, 4, -5));
        Assert.Equal(1, PriceChartScale.Nearest(xs, 4, 24));
        Assert.Equal(2, PriceChartScale.Nearest(xs, 4, 31));
        Assert.Equal(3, PriceChartScale.Nearest(xs, 4, 500));   // the stale slots past the count are never picked
        Assert.Equal(-1, PriceChartScale.Nearest(xs, 0, 10));
    }

    // ---- row texts and rules
    [Fact]
    public void Rows_NamePrefersTheCatalogueAndCutsOldBilingualNames()
    {
        Assert.Equal("Catalogue", PriceRowText.DisplayName("Catalogue", "Stored"));
        Assert.Equal("Refining Stone", PriceRowText.DisplayName(null, "Refining Stone · Piedra de refinado"));
        Assert.Equal("Plain", PriceRowText.DisplayName("", "Plain"));
        Assert.Equal("Stone · legendary", PriceRowText.NameTip("Stone", null, Grade.Legendary));
        Assert.Equal("Stone", PriceRowText.NameTip("Stone", "", Grade.Unknown));
        Assert.StartsWith("Stone · legendary\nPiedra", PriceRowText.NameTip("Stone", "Piedra", Grade.Legendary));
    }

    [Fact]
    public void Rows_TrendAndHoldingColours()
    {
        Assert.Equal(("▲ +10%", PriceTone.Up), PriceRowText.Trend(110, 100));
        Assert.Equal(("▼ -10%", PriceTone.Down), PriceRowText.Trend(90, 100));
        Assert.Equal(("→ +2%", PriceTone.Flat), PriceRowText.Trend(102, 100));
        Assert.Equal(("", PriceTone.Flat), PriceRowText.Trend(null, 100));
        Assert.Equal(("", PriceTone.Flat), PriceRowText.Trend(100, 0));
        // value at the last price, gain over the price paid: up is green (Down tone = the cheaper/green brush), loss red
        Assert.Equal(("3,000  +50%", PriceTone.Down), PriceRowText.Holding(2, 1500, 1000));
        Assert.Equal(("1,000  -50%", PriceTone.Up), PriceRowText.Holding(2, 500, 1000));
        Assert.Equal(("1,000", PriceTone.Flat), PriceRowText.Holding(2, 500, 0));
        Assert.Equal(("", PriceTone.Flat), PriceRowText.Holding(0, 500, 1000));
        Assert.Equal(("", PriceTone.Flat), PriceRowText.Holding(3, null, 1000));
        // price colour: hit green, otherwise red, grey with no price
        Assert.Equal(PriceTone.Down, PriceRowText.PriceToneOf(new PriceWatch { LastPrice = 90, MaxPrice = 100 }));
        Assert.Equal(PriceTone.Up, PriceRowText.PriceToneOf(new PriceWatch { LastPrice = 110, MaxPrice = 100 }));
        Assert.Equal(PriceTone.Down, PriceRowText.PriceToneOf(new PriceWatch { LastPrice = 110, MaxPrice = 100, Sell = true }));
        Assert.Equal(PriceTone.Flat, PriceRowText.PriceToneOf(new PriceWatch { MaxPrice = 100 }));
    }

    [Fact]
    public void Rows_SubLineAndStatusUseTheNewestCheck()
    {
        var check = new DateTimeOffset(2026, 10, 13, 12, 5, 0, TimeSpan.Zero);
        var sample = check.AddMinutes(-40);
        string Hm(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var w = new PriceWatch { Item = 610511013, LastCheck = check, SampledAt = sample };
        Assert.Equal($"#610511013 · site sample {Hm(sample)} · site snapshot {Hm(check)}", PriceRowText.Sub(w, En));
        w.LastError = "HTTP 503";
        Assert.EndsWith(" · HTTP 503", PriceRowText.Sub(w, En));
        Assert.Equal("#5 · not checked yet", PriceRowText.Sub(new PriceWatch { Item = 5 }, En));
        var list = new List<PriceWatch> { new() { LastCheck = check.AddHours(-2) }, new(), new() { LastCheck = check } };
        Assert.Equal(check, PriceRowText.LastCheck(list));
        Assert.Null(PriceRowText.LastCheck([new PriceWatch()]));
        Assert.StartsWith($"last check {Hm(check)} · ", PriceRowText.Status(check, En));
        Assert.Equal("not checked yet", PriceRowText.Status(null, En));
    }

    [Fact]
    public void Rows_SearchCountPickHintAndItemUrl()
    {
        Assert.Equal("0", PriceRowText.SearchCount(0, 40));
        Assert.Equal("7", PriceRowText.SearchCount(7, 40));
        Assert.Equal("40+", PriceRowText.SearchCount(40, 40));
        Assert.Equal("Stone [legendary]  ·  Piedra", PriceRowText.PickHint("Stone", "  ·  Piedra", Grade.Legendary, "en"));
        Assert.Equal("Stone", PriceRowText.PickHint("Stone", "", Grade.Unknown, "en"));
        Assert.Equal(("https://aion2.exchange/#/items/42", true), PriceRowText.ItemUrl("https://aion2.exchange/#/items/{id}", 42, 11303));
        Assert.Equal(("https://x/item/42?server=11303", true), PriceRowText.ItemUrl("https://x/item/{id}?server={server}", 42, 11303));
        // no item route: the caller copies the name for the site's search box
        Assert.False(PriceRowText.ItemUrl("https://aion2.exchange/", 42, 11303).Routed);
    }

    [Fact]
    public void Rows_ItemsScriptGetsTheWatchedIds()
    {
        const string src = "(() => {\n  const ITEMS = [610511013, 610530010]; // the meter pastes its list here\n  run(ITEMS);\n})();";
        var spliced = PriceRowText.SpliceItems(src, [new PriceWatch { Item = 1 }, new PriceWatch { Item = 22 }, new PriceWatch { Item = 333 }]);
        Assert.Contains("const ITEMS = [1, 22, 333]; // the meter", spliced);
        Assert.DoesNotContain("610511013", spliced);
        Assert.Contains("const ITEMS = [];", PriceRowText.SpliceItems(src, []));
        // the real script shipped in data/gamedb carries the line the splice looks for
        var shipped = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "gamedb", "aion2exchange-item-servers.js");
        if (File.Exists(shipped)) Assert.Contains("const ITEMS = [7];", PriceRowText.SpliceItems(File.ReadAllText(shipped), [new PriceWatch { Item = 7 }]));
    }

    [Fact]
    public void Rows_CheckNowCountsTheDaemonsCooldownDown()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(("Checking…", false, (string?)null), PriceRowText.CheckButton(true, DateTimeOffset.MinValue, now, true, En));
        Assert.Equal(("Check now (4:05)", false, (string?)null), PriceRowText.CheckButton(false, now.AddSeconds(245), now, true, En));
        Assert.Equal(("Check now", true, (string?)null), PriceRowText.CheckButton(false, now.AddSeconds(-1), now, true, En));
        var (label, enabled, tip) = PriceRowText.CheckButton(false, DateTimeOffset.MinValue, now, false, En);
        Assert.Equal("Check now", label);
        Assert.False(enabled);
        Assert.Equal(En.PricesNoDaemon, tip);
    }

    [Fact]
    public void Kit_PricesSheetStaysUnderTheViewRoot()
    {
        var css = PricesKit.Sheet();
        foreach (var line in css.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.StartsWith(".k-root ", line.TrimStart());
            // every class it defines is the tab's own
            foreach (var part in line.Split('{')[0].Split([' ', ',', ':', '>'], StringSplitOptions.RemoveEmptyEntries))
                foreach (var cls in part.Split('.', StringSplitOptions.RemoveEmptyEntries).Skip(part.StartsWith('.') ? 0 : 1))
                    Assert.True(cls == "k-root" || cls.StartsWith("k-prices-", StringComparison.Ordinal), line);
        }
        foreach (var t in Enum.GetValues<PriceTone>()) Assert.Contains("label." + PricesKit.ToneClass(t) + " ", css);
        // the card's px scaling applies to it like to the kit
        Assert.Contains("padding: 6px 15px", CardText.ScaleCss(css, 1.5));
    }
}
