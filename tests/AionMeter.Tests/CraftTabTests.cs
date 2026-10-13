using AionMeter.Core.GameDb;
using AionMeter.Overlay.Shell;
using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>A2M-50: the GTK Craft tab's list rules (CraftRows, the Avalonia CraftView's Reload/ReloadProfit) and its stylesheet, without a display.</summary>
public class CraftTabTests
{
    private static readonly UiText En = new();

    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Iron Sword", [2] = "Iron Sword", [3] = "Healing Potion", [4] = "Refining Stone", [5] = "Refining Stone", [6] = "Silk Robe",
        [100] = "Iron Ore", [101] = "Herb", [102] = "Silk", [103] = "Gem",
    };

    private static readonly Dictionary<int, Grade> Grades = new() { [1] = Grade.Rare, [2] = Grade.Rare, [3] = Grade.Common, [4] = Grade.Rare, [5] = Grade.Legendary, [6] = Grade.Epic };

    private static CraftNames Lookups() => new(
        id => Names.GetValueOrDefault(id, "Item " + id),
        id => Names.GetValueOrDefault(id, "Item " + id),
        id => (Names.GetValueOrDefault(id), id == 3 ? "Poción curativa" : null),
        id => id == 103 ? "drop Boss" : null,
        id => Grades.GetValueOrDefault(id));

    private static Recipe R(int output, string prof, int level, params (int, int)[] mats) => new(output, Names[output], prof, level, "rare", 1, mats);

    // book order as RecipeBook.Evaluate returns it: craftable first
    private static List<CraftOption> Options() =>
    [
        new(R(3, "alchemy", 10, (101, 2)), 4, []),
        new(R(1, "blacksmithing", 20, (100, 5)), 0, [(100, 5, 1)]),                       // Iron Sword twin, short of ore
        new(R(2, "blacksmithing", 30, (100, 5)), 1, []),                                  // Iron Sword twin, craftable → kept
        new(R(4, "alchemy", 5, (103, 1)), 0, [(103, 1, 0)]),                              // Refining Stone rare
        new(R(5, "alchemy", 40, (103, 3)), 0, [(103, 3, 0)]),                             // Refining Stone legendary: another recipe, not a twin
        new(R(6, "tailoring", 50, (102, 2), (103, 1), (100, 1)), 0, [(102, 2, 0), (103, 1, 0), (100, 1, 0)]),   // short of 3 kinds
    ];

    [Fact]
    public void Scope_KeysRoundTripAndUnknownIsNow()
    {
        for (var i = 0; i < 3; i++) Assert.Equal(i, CraftRows.ScopeIndex(CraftRows.ScopeKey(i)));
        Assert.Equal(0, CraftRows.ScopeIndex("bogus"));
        Assert.Equal(0, CraftRows.ScopeIndex(null));
        Assert.Equal("now", CraftRows.ScopeKey(-1));
        Assert.Equal("all", CraftRows.EffectiveScope(ItemQuery.Parse("sword"), "now"));   // typing searches the whole book
        Assert.Equal("near", CraftRows.EffectiveScope(ItemQuery.Parse(""), "near"));
        Assert.Equal(6, CraftRows.ProfKeys.Length);
    }

    [Fact]
    public void Twins_KeepTheCraftableOneAndCountVariants_GradeSplits()
    {
        var opts = Options();
        var names = new string[opts.Count];
        var (keep, variants) = CraftRows.PickTwins(opts, id => Names[id], id => Grades.GetValueOrDefault(id), names);
        Assert.Contains(2, keep);
        Assert.DoesNotContain(1, keep);
        Assert.Equal(2, variants[2]);
        Assert.Contains(4, keep);           // same name, different grade: both stay
        Assert.Contains(5, keep);
        Assert.Equal("Iron Sword", names[1]);
    }

    [Fact]
    public void Book_ScopesFilterFavouritesLeadAndBypass()
    {
        var n = Lookups();
        var now = CraftRows.Book(Options(), n, ItemQuery.Empty, "now", [], En, out var craftable);
        Assert.Equal([3, 2], now.Select(r => r.Output));
        Assert.Equal(2, craftable);
        Assert.Equal("×4", now[0].Times);
        Assert.Equal(CraftTone.Ok, now[0].Tone);
        Assert.True(now[0].OkFrame);

        var near = CraftRows.Book(Options(), n, ItemQuery.Empty, "near", [], En, out _);
        Assert.Equal([3, 2, 4, 5], near.Select(r => r.Output));   // Silk Robe misses 3 kinds
        var all = CraftRows.Book(Options(), n, ItemQuery.Empty, "all", [], En, out _);
        Assert.Equal([3, 2, 4, 5, 6], all.Select(r => r.Output));

        // a favourite outside the scope comes first anyway, starred
        var fav = CraftRows.Book(Options(), n, ItemQuery.Empty, "now", [6], En, out craftable);
        Assert.Equal([6, 3, 2], fav.Select(r => r.Output));
        Assert.True(fav[0].Fav);
        Assert.False(fav[1].Fav);
        Assert.Equal(2, craftable);
    }

    [Fact]
    public void Twins_FullTieKeepsTheFavouriteThenTheLowerId_WhateverTheOrder()
    {
        // identical twins (Truth Orb 110660023/110660024): the unstable book sort hands them over in either order
        CraftOption a = new(R(4, "alchemy", 5, (103, 1)), 0, [(103, 1, 0)]), b = new(new Recipe(7, "Refining Stone", "alchemy", 5, "rare", 1, [(103, 1)]), 0, [(103, 1, 0)]);
        Grade G(int id) => id == 7 ? Grade.Rare : Grades.GetValueOrDefault(id);
        string N(int id) => id == 7 ? "Refining Stone" : Names[id];
        Assert.Contains(4, CraftRows.PickTwins([a, b], N, G).Keep);
        Assert.Contains(4, CraftRows.PickTwins([b, a], N, G).Keep);
        Assert.Contains(7, CraftRows.PickTwins([a, b], N, G, null, [7]).Keep);
        Assert.Contains(7, CraftRows.PickTwins([b, a], N, G, null, [7]).Keep);
    }

    [Fact]
    public void Book_QuerySearchesEveryScopeInBothLanguages()
    {
        var n = Lookups();
        var hits = CraftRows.Book(Options(), n, ItemQuery.Parse("refining"), "now", [], En, out var craftable);
        Assert.Equal([4, 5], hits.Select(r => r.Output));
        Assert.Equal(0, craftable);
        Assert.Equal([3], CraftRows.Book(Options(), n, ItemQuery.Parse("poción"), "now", [], En, out _).Select(r => r.Output));
        Assert.Empty(CraftRows.Book(Options(), n, ItemQuery.Parse("zzz"), "all", [], En, out _));
    }

    [Fact]
    public void Book_DetailLineCarriesVariantsMaterialsAndWhatIsMissing()
    {
        var rows = CraftRows.Book(Options(), Lookups(), ItemQuery.Empty, "all", [], En, out _);
        var sword = rows.Single(r => r.Output == 2);
        Assert.Equal("blacksmithing 30 · rare · +1 " + En.CraftVariant + " · Iron Ore ×5", sword.Sub);
        var stone = rows.Single(r => r.Output == 4);
        Assert.Equal("alchemy 5 · rare · Gem ×1  —  " + En.CraftMissing + ": Gem 0/1 (drop Boss)", stone.Sub);
        Assert.Equal("", stone.Times);
        Assert.Equal(CraftTone.Near, stone.Tone);
        Assert.False(stone.OkFrame);
        Assert.Equal(Grade.Rare, stone.Grade);
    }

    [Fact]
    public void Profit_FavouritesFirstTwinsOnceAndFigures()
    {
        var prices = new Dictionary<int, long> { [100] = 1_000, [101] = 50 };
        long? Price(int id) => prices.TryGetValue(id, out var p) ? p : null;
        List<CraftProfit> all =
        [
            new(R(1, "blacksmithing", 20, (100, 5)), 12_000, 10_800, 5_000, 5_800, 0, 2),
            new(R(2, "blacksmithing", 30, (100, 5)), 12_000, 10_800, 5_000, 5_800, 0, 0),    // twin of 1: dropped
            new(R(3, "alchemy", 10, (101, 2), (103, 1)), 300, 270, 100, 170, 1, 0),
            new(R(6, "tailoring", 50, (102, 2)), 1_000, 900, 2_000, -1_100, 0, 3),
        ];
        var rows = CraftRows.Profit(all, Lookups(), Price, ItemQuery.Empty, [6], En);
        Assert.Equal([6, 1, 3], rows.Select(r => r.Output));
        Assert.Equal("-1100 (-55%)", rows[0].Times);   // ItemFormat.Kinah leaves negatives unshortened (as Avalonia)
        Assert.Equal(CraftTone.Bad, rows[0].Tone);
        Assert.False(rows[0].OkFrame);   // losing even if craftable
        Assert.Equal("+5.8k (+116%)", rows[1].Times);
        Assert.True(rows[1].OkFrame);    // profitable and craftable now
        Assert.False(rows[2].OkFrame);   // profitable, not craftable
        Assert.Contains(" · ×2 " + En.CraftNow + "\nIron Ore ×5 @1k", rows[1].Sub);
        Assert.Contains("(1 " + En.CraftUnpriced + ")", rows[2].Sub);
        Assert.EndsWith("Herb ×2 @50, Gem ×1 @?", rows[2].Sub);
        Assert.Equal([3], CraftRows.Profit(all, Lookups(), Price, ItemQuery.Parse("healing"), [], En).Select(r => r.Output));
    }

    [Fact]
    public void Profit_CapsAt150Rows()
    {
        var all = new List<CraftProfit>(200);
        for (var i = 0; i < 200; i++) all.Add(new CraftProfit(new Recipe(10_000 + i, "r" + i, "cooking", 1, "common", 1, [(100, 1)]), 10, 9, 1, 8, 0, 0));
        var n = new CraftNames(id => "Dish " + id, id => "Item " + id, _ => (null, null), _ => null, _ => Grade.Common);
        var rows = CraftRows.Profit(all, n, _ => 1, ItemQuery.Empty, [10_199], En);
        Assert.Equal(CraftRows.ProfitMax, rows.Count);
        Assert.Equal(10_199, rows[0].Output);
    }

    [Fact]
    public void NewestExport_PicksTheLatestMatchingFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "craft-dl-" + Guid.NewGuid().ToString("N"));
        Assert.Null(CraftRows.NewestExport(dir));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(CraftRows.NewestExport(dir));
            var old = Path.Combine(dir, "aion2exchange-old.json");
            var nu = Path.Combine(dir, "aion2exchange-new.json");
            var other = Path.Combine(dir, "other.json");
            File.WriteAllText(old, "{}"); File.WriteAllText(nu, "{}"); File.WriteAllText(other, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(nu, DateTime.UtcNow.AddHours(-1));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow);
            Assert.Equal(nu, CraftRows.NewestExport(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FileDialog_DismissalIsNotAFailure()
    {
        Assert.True(CraftRows.IsDismissal("Dismissed by user"));
        Assert.True(CraftRows.IsDismissal("Cancelled by application"));
        Assert.True(CraftRows.IsDismissal("Descartado por el usuario", "Descartado por el usuario", ""));
        Assert.False(CraftRows.IsDismissal("Failed to talk to portal", "Descartado por el usuario", ""));
        Assert.False(CraftRows.IsDismissal("Operation failed"));
    }

    [Fact]
    public void Css_EveryRuleIsCraftScopedUnderTheKitRoot()
    {
        var sheet = CraftCss.Sheet();
        var rules = 0;
        foreach (var raw in sheet.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            rules++;
            Assert.StartsWith(".k-root ", line);
            Assert.Contains("k-craft-", line[..line.IndexOf('{')]);
        }
        Assert.True(rules >= 15);
        Assert.Contains("#8EE08A", sheet);   // Avalonia's Ok brush
        Assert.Contains("12.6px", AionMeter.Overlay.CardText.ScaleCss(sheet, 1.05));   // scales with the card
    }
}
