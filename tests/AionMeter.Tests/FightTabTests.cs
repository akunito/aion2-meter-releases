using System.Text.Json;
using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Overlay.Shell;
using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>A2M-50: the GTK fight tab's display-free parts (chart maths, rotation layout, tab and player rules, table texts, its stylesheet).</summary>
[Collection(ProcessStaticsCollection.Name)]
public class FightTabTests
{
    // ---- timeline chart
    [Fact]
    public void Smooth_IsATrailingThreeSecondAverage()
    {
        var into = Array.Empty<double>();
        var n = ChartMath.Smooth([3, 6, 9, 0, 0], 3, ref into);
        Assert.Equal(5, n);
        Assert.Equal([3, 4.5, 6, 5, 3], into[..5]);
    }

    [Fact]
    public void Smooth_ReusesTheBufferWhenLongEnough()
    {
        var into = new double[16];
        var same = into;
        ChartMath.Smooth([1, 2], 3, ref into);
        Assert.Same(same, into);
        ChartMath.Smooth(new long[40], 3, ref into);
        Assert.True(into.Length >= 40);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(1.2, 1.5)]
    [InlineData(7, 8)]
    [InlineData(23_000, 25_000)]
    [InlineData(41_000, 50_000)]
    [InlineData(81_000, 100_000)]
    [InlineData(100_000, 100_000)]
    public void NiceCeiling_RoundsUpToAReadableAxisTop(double v, double expected) => Assert.Equal(expected, ChartMath.NiceCeiling(v), 6);

    [Theory]
    [InlineData(20, 5)]
    [InlineData(31, 15)]
    [InlineData(200, 30)]
    [InlineData(600, 60)]
    [InlineData(3600, 300)]
    public void TimeStep_FollowsTheFightLength(int seconds, int step) => Assert.Equal(step, ChartMath.TimeStep(seconds));

    [Fact]
    public void Axis_MapsFirstAndLastSampleToTheEdgesAndClampsTheTop()
    {
        Assert.Equal(44, ChartMath.X(0, 10, 44, 300));
        Assert.Equal(344, ChartMath.X(9, 10, 44, 300));
        Assert.Equal(140, ChartMath.Y(0, 100, 140, 130));
        Assert.Equal(10, ChartMath.Y(100, 100, 140, 130));
        Assert.Equal(10, ChartMath.Y(250, 100, 140, 130));   // a smoothed peak never draws above the plot
    }

    // ---- rotation strip
    [Fact]
    public void TileSize_GrowsWithTheSquareRootOfTheHit()
    {
        Assert.Equal(22, ChartMath.TileSize(0, 1000, 22, 40));
        Assert.Equal(40, ChartMath.TileSize(1000, 1000, 22, 40));
        Assert.Equal(31, ChartMath.TileSize(250, 1000, 22, 40), 6);
    }

    [Fact]
    public void Layout_WrapsTilesAndBottomAlignsThemOnTheRow()
    {
        // advance = max(size, 34) + 6 → 40 per small tile; a 100 px strip holds two per row
        double[] sizes = [22, 22, 40, 22];
        var xs = new double[4]; var ys = new double[4]; var rows = new int[4];
        var count = ChartMath.Layout(sizes, 100, 40, 34, 6, 59, xs, ys, rows);
        Assert.Equal(2, count);
        Assert.Equal([0, 40, 0, 46], xs);
        Assert.Equal([0, 0, 1, 1], rows);
        Assert.Equal(18, ys[0]);          // 40-px line, 22-px tile at its bottom
        Assert.Equal(59, ys[2]);          // second row, full-size tile
        Assert.Equal(59 + 18, ys[3]);
        Assert.Equal(0, ChartMath.Layout([], 100, 40, 34, 6, 59, xs, ys, rows));
    }

    [Fact]
    public void Layout_NeverLeavesAWideTileAloneOnAnEmptyRowBeforeIt()
    {
        var xs = new double[2]; var ys = new double[2]; var rows = new int[2];
        Assert.Equal(2, ChartMath.Layout([40, 40], 30, 40, 34, 6, 59, xs, ys, rows));   // the first tile stays on row 0 even if wider than the strip
        Assert.Equal([0, 1], rows);
    }

    [Theory]
    [InlineData("Flame Strike", "FS")]
    [InlineData("bolt", "bo")]
    [InlineData("X", "X")]
    [InlineData("Shield-bash of doom", "SB")]
    [InlineData("  ", "?")]
    public void Initials_TwoLetters(string name, string expected) => Assert.Equal(expected, ChartMath.Initials(name));

    [Fact]
    public void SkillColor_IsStableAndHslPure()
    {
        Assert.Equal(ChartMath.SkillColor(11020000), ChartMath.SkillColor(11020000));
        Assert.Equal(0xFF0000u, ChartMath.FromHsl(0, 1, 0.5));
        Assert.Equal(0x00FF00u, ChartMath.FromHsl(120, 1, 0.5));
        Assert.Equal(0x808080u, ChartMath.Mix(0x000000, 0xFFFFFF, 0.5));
        Assert.Equal(0xFFFFFFu, ChartMath.Mix(0x123456, 0xFFFFFF, 1));
    }

    // ---- player + tab rules
    private static CombatantSnapshot C(uint id, bool self = false) => new(id, "P" + id, GameClass.Gladiator, self, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void PickActor_AskedThenYouThenTheTop()
    {
        CombatantSnapshot[] party = [C(5), C(7, self: true), C(9)];
        Assert.Equal(9u, FightLogic.PickActor(party, 9));
        Assert.Equal(7u, FightLogic.PickActor(party, 0));
        Assert.Equal(7u, FightLogic.PickActor(party, 42));   // gone from this fight
        Assert.Equal(5u, FightLogic.PickActor([C(5), C(9)], 0));
        Assert.Equal(0u, FightLogic.PickActor([], 3));
    }

    [Fact]
    public void Place_IsOneBasedAndZeroForUnknownSummons()
    {
        CombatantSnapshot[] party = [C(5), C(Combatant.UnknownSummonsId), C(9)];
        Assert.Equal(1, FightLogic.Place(party, 5));
        Assert.Equal(3, FightLogic.Place(party, 9));
        Assert.Equal(0, FightLogic.Place(party, Combatant.UnknownSummonsId));
        Assert.Equal(0, FightLogic.Place(party, 77));
    }

    private static CharacterProfile Teinmeir() =>
        JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "armory", "profile-teinmeir.json")))!;

    [Fact]
    public void FightTab_ProfileTabsWithoutAProfileFallBackToDps()
    {
        var none = CardLayout.Tabs(true, null);
        Assert.Equal("dps", FightLogic.FightTab("profile", none));
        Assert.Equal("dps", FightLogic.FightTab("daevanion", none));
        Assert.Equal("rotation", FightLogic.FightTab("rotation", none));
        var full = CardLayout.Tabs(true, Teinmeir());
        Assert.Equal("skills", FightLogic.FightTab("skills", full));
    }

    [Fact]
    public void CardTab_KeepsTheAskedTabOrTakesTheFirstThereIs()
    {
        var p = Teinmeir();
        var tabs = CardLayout.Tabs(false, p);
        Assert.Equal("daevanion", FightLogic.CardTabFor("daevanion", tabs));
        Assert.Equal("profile", FightLogic.CardTabFor("dps", tabs));
        Assert.Equal("profile", FightLogic.CardTabFor("skills", []));
        var skillsOnly = new List<CardTab> { CardTab.Skills };
        Assert.Equal("skills", FightLogic.CardTabFor("profile", skillsOnly));
    }

    // ---- skills table texts
    private static SkillRow S(int hits, int dots) => new(1, "Strike", hits, dots, 12_345, 678, 0.25, 1000, 10, 5000, 0.5, null, 0.1, 0, 0, 0);

    [Fact]
    public void Cell_HitsShowDotTicksApartAndMissingRatesAsDash()
    {
        Assert.Equal("10+4", FightLogic.Cell(S(10, 4), SkillColumn.Hits));
        Assert.Equal("4", FightLogic.Cell(S(0, 4), SkillColumn.Hits));
        Assert.Equal("10", FightLogic.Cell(S(10, 0), SkillColumn.Hits));
        Assert.Equal(Format.Percent((double?)null), FightLogic.Cell(S(1, 0), SkillColumn.Back));
        Assert.Equal(Format.Compact(12_345), FightLogic.Cell(S(1, 0), SkillColumn.Damage));
        Assert.Equal("", FightLogic.Cell(S(1, 0), SkillColumn.Name));
    }

    [Fact]
    public void HeadText_ArrowAfterTheNameBeforeNumbers()
    {
        var byDamage = SkillOrder.Default;
        Assert.Equal("▾ Damage", FightLogic.HeadText("Damage", SkillColumn.Damage, byDamage));
        Assert.Equal("Hits", FightLogic.HeadText("Hits", SkillColumn.Hits, byDamage));
        var byName = byDamage.Toggle(SkillColumn.Name);
        Assert.EndsWith(byName.Descending ? " ▾" : " ▴", FightLogic.HeadText("Skill", SkillColumn.Name, byName));
    }

    [Fact]
    public void Columns_FitTheCardBudget()
    {
        // a ~484 px scroll body (docs/33) less the 20+8 px icon: the fixed columns leave the skill name at least 120 px
        foreach (var cols in new[] { FightLogic.DpsColumns, FightLogic.AccuracyColumns })
        {
            double fixedPx = 0;
            foreach (var (_, w) in cols) fixedPx += w;
            Assert.True(484 - 28 - fixedPx >= 120, $"fixed columns {fixedPx}px");
            Assert.Equal(SkillColumn.Name, cols[0].C);
        }
    }

    [Fact]
    public void PartyItem_MarksTheSelectedPlayer()
    {
        UiText.Use("en");
        var c = C(5) with { Share = 0.42 };
        var line = FightLogic.PartyItem(c, 1, true, UiText.Current);
        Assert.StartsWith("▸ 1. ", line);
        Assert.EndsWith("42.0%", line);
        Assert.StartsWith("· ", FightLogic.PartyItem(c, 0, false, UiText.Current));
    }

    // ---- profile pages / stylesheet
    [Theory]
    [InlineData("https://cdn.example/icons/Item_Helm-01.png", "Item_Helm-01")]
    [InlineData("a b.c.webp", "a_b_c")]
    [InlineData("plain", "plain")]
    public void KeyOf_IsTheSanitisedFileStem(string url, string key) => Assert.Equal(key, ProfilePages.KeyOf(url));

    [Fact]
    public void FightCss_EveryRuleIsKitScopedAndPrefixed()
    {
        foreach (var raw in FightKit.Sheet().Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            Assert.True(line.StartsWith(".k-", StringComparison.Ordinal), line);
            var selector = line[..line.IndexOf('{')];
            Assert.Contains("k-fight-", selector);
        }
    }
}
