using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Market;
using AionMeter.Core.Protocol;

namespace AionMeter.Tests;

/// <summary>Two Black Cloud shop pages captured on 2026-10-08 (pets, skins); first read as broker pages, see docs/27.</summary>
public class MarketTests
{
    private static byte[] Fixture(string name) => Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "market", name)).Trim());

    private static List<GameEvent> Parse(ushort op, byte[] body)
    {
        var events = new List<GameEvent>();
        var parser = new PacketParser(GameData.Empty, events.Add) { TimeMs = 1_000 };
        parser.Handle(op, body);
        return events;
    }

    [Fact]
    public void Shop_page_gives_ten_offers_with_both_prices()
    {
        var p = Assert.IsType<ShopPageEvent>(Assert.Single(Parse(Opcodes.ShopPage, Fixture("shop-9156-pets.hex"))));
        Assert.Equal(2664, p.Page);
        Assert.Equal(10, p.Listings.Count);
        var coradon = p.Listings[0];
        Assert.Equal((534760056, 1, 0, 100_000L, 90_000L), (coradon.Item, coradon.Quantity, coradon.Level, coradon.Price, coradon.Current));
        var slime = p.Listings.Single(l => l.Item == 534760061);
        Assert.Equal((3, 100_000L, 50_000L), (slime.Level, slime.Price, slime.Current));

        var s = Assert.IsType<ShopPageEvent>(Assert.Single(Parse(Opcodes.ShopPage, Fixture("shop-9156-skins.hex"))));
        Assert.Equal(10, s.Listings.Count);
        Assert.Equal(316_800L, s.Listings[0].Price);

        var store = new ShopStore(null);
        store.OnEvent(p); store.OnEvent(p); store.OnEvent(s);
        Assert.Equal(20, store.Count); // the repeated page is not counted twice
        Assert.Equal(100_000L, store.Lowest(534760056, 1_000));
    }
}
