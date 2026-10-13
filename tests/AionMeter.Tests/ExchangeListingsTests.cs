using AionMeter.Core.Market;

namespace AionMeter.Tests;

public class ExchangeListingsTests
{
    // Real /api/items/610610013/depth/ladder?server=11304 response (Odyle, captured by the site 2026-10-06, probed 2026-10-13),
    // trimmed to 6 levels. levels come cheapest-first; each is a price tier, not a single listing.
    private const string Ladder =
        "{\"item_key\":610610013,\"captured_at\":\"2026-10-06T11:52:00.787000Z\",\"complete\":true,\"listings_total\":42,\"units_total\":7320,\"levels\":[" +
        "{\"unit_price\":4088.0,\"listings\":2,\"units\":620,\"cum_units\":620,\"cum_cost\":2534560.0}," +
        "{\"unit_price\":4100.0,\"listings\":1,\"units\":200,\"cum_units\":820,\"cum_cost\":3354560.0}," +
        "{\"unit_price\":4455.0,\"listings\":1,\"units\":913,\"cum_units\":1733,\"cum_cost\":7421975.0}," +
        "{\"unit_price\":4500.0,\"listings\":1,\"units\":1000,\"cum_units\":2733,\"cum_cost\":11921975.0}," +
        "{\"unit_price\":4600.0,\"listings\":1,\"units\":180,\"cum_units\":2913,\"cum_cost\":12749975.0}," +
        "{\"unit_price\":4800.0,\"listings\":2,\"units\":300,\"cum_units\":3213,\"cum_cost\":14189975.0}]}";

    [Fact]
    public void ParsesCheapestFiveLevelsWithCaptureTime()
    {
        var r = ExchangeListingsSource.Parse(Ladder);
        Assert.Null(r.Status);                       // null status + rows => the view renders them
        Assert.Equal(5, r.Cheapest.Count);           // 6 levels in, cheapest 5 kept
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 11, 52, 0, 787, TimeSpan.Zero), r.At);

        var top = r.Cheapest[0];
        Assert.Equal(4088, top.Unit);                // cheapest unit price first
        Assert.Equal(620, top.Quantity);             // units offered at that level
        Assert.Equal(4088L * 620, top.Price);        // stack cost = unit * units
        Assert.Null(top.Seller);                     // ladder is aggregated, no seller

        for (var i = 1; i < r.Cheapest.Count; i++)   // ascending by unit price
            Assert.True(r.Cheapest[i].Unit >= r.Cheapest[i - 1].Unit);
    }

    [Fact]
    public void EmptyLadderIsNoListingsNotAnError()
    {
        // Vaizel (11303) and most servers on 2026-10-13: no capture, empty ladder.
        var r = ExchangeListingsSource.Parse("{\"item_key\":1,\"captured_at\":null,\"listings_total\":0,\"units_total\":0,\"levels\":[]}");
        Assert.Empty(r.Cheapest);
        Assert.Null(r.At);
        Assert.Null(r.Status);                       // null status + empty => "No listings right now", not an error
    }
}
