using AionMeter.Core.Inventory;
using AionMeter.Overlay.Views;

namespace AionMeter.Tests;

/// <summary>A2M-50: the GTK Items tab's display-free logic (ItemsLogic): filter, purse, location strings, layout maths. GTK is never loaded.</summary>
public class GtkItemsTests
{
    private static ItemStack S(int item, int count, byte slot = 0, byte container = Containers.Bag) => new(0, item, count, container, slot, 0);

    [Fact]
    public void Visible_DropsCurrenciesSumsKinaAndAppliesTheShowPill()
    {
        var stacks = new List<ItemStack>
        {
            S(InventoryStore.Kinah, 2_000_000), S(InventoryStore.Kinah, 170_503, 1), S(InventoryStore.AbyssPoints, 900, 2),
            S(110_000_001, 1, 3), S(610_000_001, 50, 4), S(910_000_001, 3, 5),
        };
        var into = new List<ItemStack>();
        Assert.Equal(2_170_503, ItemsLogic.Visible(stacks, "all", into));
        Assert.Equal([110_000_001, 610_000_001, 910_000_001], into.Select(s => s.Item));
        ItemsLogic.Visible(stacks, "eq", into);
        Assert.Equal([110_000_001], into.Select(s => s.Item));
        ItemsLogic.Visible(stacks, "mat", into);
        Assert.Equal([610_000_001], into.Select(s => s.Item));
        Assert.Equal(2_170_503, ItemsLogic.Visible(stacks, "use", into));   // the purse is named whatever the pill
        Assert.Equal([910_000_001], into.Select(s => s.Item));
        Assert.Equal(0, ItemsLogic.Visible([S(910_000_001, 3)], "all", into));
    }

    [Fact]
    public void HeadRight_NamesTheKinaOnlyWhenTheCardHoldsSome()
    {
        Assert.Equal("28 stacks · 2,213,167 Kina", ItemsLogic.HeadRight("28 stacks", 2_213_167, "{0} Kina"));
        Assert.Equal("28 stacks", ItemsLogic.HeadRight("28 stacks", 0, "{0} Kina"));
    }

    [Fact]
    public void Warehouse_ChipStateFollowsContainerTimesAndTheOldTenStackRule()
    {
        Assert.Equal(ItemsLogic.WhState.Never, ItemsLogic.Warehouse(0, 0, 0));
        Assert.Equal(ItemsLogic.WhState.Full, ItemsLogic.Warehouse(3, 1000, 900));    // a full list was seen
        Assert.Equal(ItemsLogic.WhState.Partial, ItemsLogic.Warehouse(3, 1000, 0));   // only single slots since
        Assert.Equal(ItemsLogic.WhState.Full, ItemsLogic.Warehouse(12, 0, 0));        // file of an older build: 10+ stacks = the window's list
        Assert.Equal(ItemsLogic.WhState.Partial, ItemsLogic.Warehouse(4, 0, 0));
    }

    [Fact]
    public void Elsewhere_SkipsEquippedAndTheRowsOwnPlace()
    {
        var holds = new List<Holding>
        {
            new("1303-Akunita", Place.Bag, 4, 1), new("1303-Akunita", Place.Equipped, 1, 1), new("", Place.AccountWarehouse, 191, 2), new("1303-Arielka", Place.Bag, 1234, 1),
        };
        static string Label(Holding h) => h.Place == Place.AccountWarehouse ? "acct" : h.Who[(h.Who.IndexOf('-') + 1)..] + " bag";
        Assert.Equal("acct 191 · Arielka bag 1,234", ItemsLogic.Elsewhere(holds, "1303-Akunita", Place.Bag, Label));
        Assert.Equal("Akunita bag 4 · Arielka bag 1,234", ItemsLogic.Elsewhere(holds, "", Place.AccountWarehouse, Label));
        Assert.Equal("", ItemsLogic.Elsewhere([holds[0]], "1303-Akunita", Place.Bag, Label));
    }

    [Fact]
    public void SortHoldings_ViewedCharacterFirstAccountLastThenByCount()
    {
        var holds = new List<Holding>
        {
            new("", Place.AccountWarehouse, 900, 1), new("1303-Arielka", Place.Bag, 5, 1), new("1303-Akunita", Place.CharacterWarehouse, 2, 1),
            new("1303-Arielka", Place.CharacterWarehouse, 50, 1), new("1303-Akunita", Place.Bag, 20, 1),
        };
        ItemsLogic.SortHoldings(holds, "1303-Akunita");
        Assert.Equal([("1303-Akunita", 20L), ("1303-Akunita", 2L), ("1303-Arielka", 50L), ("1303-Arielka", 5L), ("", 900L)], holds.Select(h => (h.Who, h.Count)));
    }

    [Fact]
    public void ClassOf_SpanAndInitials()
    {
        Assert.Equal("Ranger 45", ItemsLogic.ClassOf("Ranger lv45  ·  combat power 1,234"));
        Assert.Equal("Templar 45", ItemsLogic.ClassOf("Templar lv45"));
        Assert.Equal("3 h 05 min", ItemsLogic.Span(TimeSpan.FromMinutes(185)));
        Assert.Equal("42 min", ItemsLogic.Span(TimeSpan.FromMinutes(42.9)));
        Assert.Equal("0 min", ItemsLogic.Span(TimeSpan.FromMinutes(-3)));
        Assert.Equal("RS", ItemsLogic.InitialsOf("Resurrection Spiritstone (Bound)"));
        Assert.Equal("L", ItemsLogic.InitialsOf("Lesser"));
        Assert.Equal("?", ItemsLogic.InitialsOf("123 456"));
    }

    [Fact]
    public void TileColumns_FitTheCardAndNeverZero()
    {
        // 560 px card: the card body inside its frame and padding is ~430 px → 8 tiles of 48 + 6 (as the Avalonia card at 560)
        Assert.Equal(8, ItemsLogic.TileColumns(430 + 4, 52, 2));
        Assert.Equal(1, ItemsLogic.TileColumns(10, 52, 2));
        Assert.Equal(12, ItemsLogic.TileColumns(12 * 54 - 2, 52, 2));
        Assert.Equal(11, ItemsLogic.TileColumns(12 * 54 - 3, 52, 2));
    }

    [Fact]
    public void ColumnWidths_FixedThenWeightedStarsAddUpExactly()
    {
        Span<int> w = stackalloc int[7];
        ItemsLogic.ColumnWidths(ItemsLogic.ListSpec, 460, 1, w);
        Assert.Equal(44, w[0]); Assert.Equal(70, w[2]); Assert.Equal(56, w[5]); Assert.Equal(64, w[6]);
        var sum = 0;
        foreach (var x in w) sum += x;
        Assert.Equal(460, sum);
        Assert.True(w[1] > w[4] && w[4] > w[3]);   // 1.4* > 1.2* > *
        ItemsLogic.ColumnWidths(ItemsLogic.ListSpec, 460, 1.5, w);
        Assert.Equal(66, w[0]);                     // fixed px follow the card scale
        Span<double> foot = stackalloc double[ItemsLogic.FootCols];
        ItemsLogic.FootSpec(true, foot);
        Span<int> fw = stackalloc int[ItemsLogic.FootCols];
        ItemsLogic.ColumnWidths(foot, 484, 1, fw);
        Assert.Equal(0, fw[ItemsLogic.ColBound]);
        Assert.Equal(0, fw[ItemsLogic.ColEvent]);
        sum = 0;
        foreach (var x in fw) sum += x;
        Assert.Equal(484, sum);
        ItemsLogic.FootSpec(false, foot);
        ItemsLogic.ColumnWidths(foot, 1000, 1, fw);
        Assert.True(fw[ItemsLogic.ColBound] > 0 && fw[ItemsLogic.ColEvent] > 0);
        ItemsLogic.ColumnWidths(ItemsLogic.TicketSpec, 200, 1, w[..3]);
        Assert.Equal(0, w[0]);                      // the star column gives way when the fixed ones take everything
    }

    [Fact]
    public void Flow_WrapsLikeAWrapPanel()
    {
        Span<int> line = stackalloc int[6];
        Assert.Equal(2, ItemsLogic.Flow([100, 100, 100, 100], 310, 4, line));
        Assert.Equal([0, 0, 0, 1], line[..4].ToArray());   // 100+4+100+4+100 = 308 fits, the fourth does not
        Assert.Equal(3, ItemsLogic.Flow([50, 400, 50], 300, 4, line));
        Assert.Equal([0, 1, 2], line[..3].ToArray());       // a child wider than a line gets its own
        Assert.Equal(1, ItemsLogic.Flow([400], 300, 4, line));
        Assert.Equal(0, ItemsLogic.Flow([], 300, 4, line));
    }
}
