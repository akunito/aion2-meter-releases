using AionMeter.Core.Builds;
using AionMeter.Overlay.Shell;
using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>A2M-50: the GTK Build tab's display-free parts (layout maths of its DrawingAreas, markup, pixels, stylesheet, stats lines).</summary>
public class GtkBuildTabTests
{
    [Fact]
    public void Registry_HasTheBuildTab() => Assert.True(ViewRegistry.Has("build"));

    // ---- tile rows (the Avalonia WrapPanel of 48 px tiles with a 6 px gap)
    [Fact]
    public void Flow_WrapsAtTheAvailableWidth()
    {
        var w = new double[11];
        Array.Fill(w, BuildGeometry.Step);
        var at = new (double X, double Y)[w.Length];
        var h = BuildGeometry.Flow(w, 300, BuildGeometry.Step, at);
        Assert.Equal(3 * BuildGeometry.Step, h);      // 5 per row (5 × 54 = 270 ≤ 300 < 324)
        Assert.Equal((0d, 0d), at[0]);
        Assert.Equal((4 * 54d, 0d), at[4]);
        Assert.Equal((0d, 54d), at[5]);
        Assert.Equal((0d, 108d), at[10]);
        Assert.Equal(0, BuildGeometry.Flow([], 300, 54, []));
    }

    [Fact]
    public void Flow_KeepsAnItemWiderThanTheRowOnItsOwnLine()
    {
        var at = new (double X, double Y)[2];
        Assert.Equal(108, BuildGeometry.Flow([400, 50], 300, 54, at));
        Assert.Equal((0d, 0d), at[0]);
        Assert.Equal((0d, 54d), at[1]);
    }

    // ---- the 12×4 key bar (Viewbox DownOnly: it shrinks, never grows)
    [Fact]
    public void Bar_ScaleShrinksToTheCardOnly()
    {
        Assert.Equal(642, BuildGeometry.BarWidth);
        Assert.Equal(234, BuildGeometry.BarHeight);
        Assert.Equal(460 / 642d, BuildGeometry.BarScale(460), 6);
        Assert.Equal(1, BuildGeometry.BarScale(1000));
        Assert.Equal(1, BuildGeometry.BarScale(0));
    }

    [Fact]
    public void Bar_RowZeroIsTheBottomRow()
    {
        Assert.Equal((0d, 162d), BuildGeometry.Cell(1, 0));
        Assert.Equal((594d, 0d), BuildGeometry.Cell(12, 3));
    }

    [Fact]
    public void Bar_HitsCellsKeysAndGaps()
    {
        var (x, y) = BuildGeometry.Cell(3, 1);
        Assert.Equal(BuildGeometry.BarHit.Cell, BuildGeometry.HitBar(x + 24, y + 24, out var key, out var row));
        Assert.Equal((3, 1), (key, row));
        Assert.Equal(BuildGeometry.BarHit.None, BuildGeometry.HitBar(50, 10, out _, out _));        // the 6 px gap after key 1
        Assert.Equal(BuildGeometry.BarHit.None, BuildGeometry.HitBar(10, 51, out _, out _));        // gap between rows
        Assert.Equal(BuildGeometry.BarHit.Key, BuildGeometry.HitBar(11 * 54 + 5, 210 + 6 + 9, out key, out _));
        Assert.Equal(12, key);
        Assert.Equal(BuildGeometry.BarHit.None, BuildGeometry.HitBar(10, 210 + 3, out _, out _));   // between the grid and the key row
        Assert.Equal(BuildGeometry.BarHit.None, BuildGeometry.HitBar(10, 240, out _, out _));
        Assert.Equal(BuildGeometry.BarHit.None, BuildGeometry.HitBar(650, 10, out _, out _));
        Assert.Equal(BuildGeometry.BarHit.None, BuildGeometry.HitBar(-1, 10, out _, out _));
    }

    // ---- Daevanion board (32 px nodes, 2 px gap, cropped to its nodes)
    [Fact]
    public void Board_SizeAndNodeHits()
    {
        Assert.Equal((508d, 508d), BuildGeometry.Board(1, 15, 1, 15));
        Assert.Equal((32d, 66d), BuildGeometry.Board(4, 4, 2, 3));
        Assert.True(BuildGeometry.HitNode(10, 10, out var col, out var row));
        Assert.Equal((0, 0), (col, row));
        Assert.False(BuildGeometry.HitNode(33, 10, out _, out _));   // the 2 px gap
        Assert.True(BuildGeometry.HitNode(34 * 3 + 1, 34 + 31, out col, out row));
        Assert.Equal((3, 1), (col, row));
        Assert.False(BuildGeometry.HitNode(-3, 5, out _, out _));
    }

    // ---- grey icons (grayscale(1), BT.601 on premultiplied BGRA)
    [Fact]
    public void Greyscale_LumaInEveryColourChannelAlphaKept()
    {
        byte[] px = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0];
        BuildPixels.Greyscale(px);
        Assert.Equal([28, 28, 28, 255], px[..4]);
        Assert.Equal([149, 149, 149, 128], px[4..8]);
        Assert.Equal([76, 76, 76, 0], px[8..12]);
    }

    // ---- markup
    [Fact]
    public void Markup_RunsEscapeTextAndColourNumbers()
    {
        TextSeg[] segs = [new("Deals <x> & ", false), new("120%", true), new(" damage", false)];
        Assert.Equal("Deals &lt;x&gt; &amp; <span foreground=\"#FCC78B\" weight=\"500\">120%</span> damage", BuildMarkup.Runs(segs));
        Assert.Equal("plain", BuildMarkup.Esc("plain"));
    }

    [Fact]
    public void Markup_LinksCarryAnIdNotAnAddress()
    {
        var sb = new System.Text.StringBuilder();
        BuildMarkup.Link(sb, "Check \"questlog\"", "check", "a<b");
        Assert.Equal("<a href=\"check\" title=\"a&lt;b\">Check &quot;questlog&quot;</a>", sb.ToString());
    }

    [Fact]
    public void Markup_RelationGlyphsByDirection()
    {
        Assert.StartsWith("<span foreground=\"#3DDC84\" weight=\"bold\">→ </span>", BuildMarkup.Relation(new SkillRelation(RelationKind.Grants, 1, 2, "gives")));
        Assert.StartsWith("<span foreground=\"#65B0FC\" weight=\"bold\">← </span>", BuildMarkup.Relation(new SkillRelation(RelationKind.Consumes, 1, 2, "takes")));
        Assert.Equal("<span foreground=\"#7D7D7D\" weight=\"bold\">· </span>a &amp; b", BuildMarkup.Relation(new SkillRelation(RelationKind.Priority, 1, 2, "a & b")));
    }

    [Fact]
    public void Markup_LegendSquaresThenTail()
    {
        (uint, string)[] items = [(0x3DDC84, "ok"), (0xFF5C5C, "far")];
        Assert.Equal("<span foreground=\"#3DDC84\">■ </span>ok    <span foreground=\"#FF5C5C\">■ </span>far    grey", BuildMarkup.Legend(items, "grey"));
        Assert.Equal("<span foreground=\"#3DDC84\">■ </span>at\u00A0target", BuildMarkup.Legend([(0x3DDC84u, "at target")], null));   // one item never breaks
    }

    // ---- the tab's own stylesheet: scoped to the kit root, every class prefixed (the card's rules and other views stay untouched)
    [Fact]
    public void Css_EveryRuleUnderKitRootWithBuildPrefix()
    {
        var sheet = BuildColors.Sheet();
        foreach (var raw in sheet.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            Assert.StartsWith(".k-root ", line);
            var selector = line[..line.IndexOf('{')];
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(selector, @"\.([a-z0-9-]+)"))
                Assert.True(m.Groups[1].Value == "k-root" || m.Groups[1].Value.StartsWith("k-build-", StringComparison.Ordinal) || m.Groups[1].Value is "k-w7", m.Groups[1].Value);
        }
        foreach (var c in BuildColors.Classes) Assert.Contains("label." + c + ",", sheet);
        Assert.Contains("#FCC78B", BuildColors.Sheet());
    }

    // ---- Daevanion stats panel
    [Fact]
    public void Stats_GroupHeadersAndTodaysSums()
    {
        List<EffectSum> plan = [new("atk", "Attack", 30, "", DaevGroup.Attack), new("acc", "Accuracy", 12, "", DaevGroup.Attack), new("s1", "Shield Bash", 2, "", DaevGroup.Skill)];
        List<EffectSum> now = [new("acc", "Accuracy", 4, "", DaevGroup.Attack)];
        var lines = DaevStats.Rows(plan, now);
        Assert.Equal(5, lines.Count);
        Assert.True(lines[0].IsGroup && lines[0].Group == DaevGroup.Attack);
        Assert.Equal(("Attack", plan[0].Amount, "—"), (lines[1].Name, lines[1].Plan, lines[1].Now));
        Assert.Equal(now[0].Amount, lines[2].Now);
        Assert.True(lines[3].IsGroup && lines[3].Group == DaevGroup.Skill);
        Assert.Equal("Shield Bash", lines[4].Name);
        Assert.Empty(DaevStats.Rows([], now));
    }
}
