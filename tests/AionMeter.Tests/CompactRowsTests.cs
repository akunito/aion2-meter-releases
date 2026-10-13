using System.Text.RegularExpressions;
using AionMeter.Overlay;
using AionMeter.Overlay.Shell;
using AionMeter.Overlay.Views;

namespace AionMeter.Tests;

/// <summary>A2M-50 (Diego 2026-10-13): Boss Timers rows 20 % more compact, DPS rows 25 % more compact — the sizing rules, display-free.</summary>
public class CompactRowsTests
{
    // ---- Boss Timers

    [Fact]
    public void Timers_RowCss_IsTheAvaloniaVerticalSizesTimesPointEight()
    {
        Assert.Equal(0.8, BossTimersLogic.RowScale);
        var css = BossTimersLogic.RowCss();
        Assert.Contains(".k-tt-frame.k-tt-trow { padding: 6.4px 10px; margin-bottom: 5.6px; }", css);
        Assert.Contains("min-width: 35.2px; min-height: 35.2px; border-radius: 18.4px;", css);   // portrait 44
        Assert.Contains(".k-tt-trow .k-tt-bell { min-width: 20.8px; min-height: 19.2px; }", css);
        Assert.Contains(".k-tt-trow .k-chip { min-height: 17.6px; }", css);
        Assert.Contains("font-size: 9.2px;", css);   // chip labels 11.5
        Assert.Contains(".k-tt-trow .k-icon { min-width: 17.6px; min-height: 17.6px; }", css);
        // every value printed short (7 * 0.8 is 5.6000000000000005 as a double)
        foreach (Match m in Regex.Matches(css, @"(\d+(?:\.\d+)?)px")) Assert.True(m.Groups[1].Value.Length <= 4, m.Value);
    }

    [Fact]
    public void Timers_RowRules_OnlyHitTimerRows_TopDpsKitRulesUnchanged()
    {
        foreach (var line in BossTimersLogic.RowCss().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains("k-tt-trow", line[..line.IndexOf('{')]);
        var sheet = TopTimersKit.Sheet();
        // the Top DPS view's frames and the shared rules keep the Avalonia sizes
        Assert.Contains(".k-tt-frame { background-color: rgba(255,255,255,0.051); border: 1px solid transparent; border-radius: 9px; padding: 8px 10px; margin-bottom: 7px; }", sheet);
        Assert.Contains(".k-tt-portrait { min-width: 44px; min-height: 44px; border-radius: 23px;", sheet);
        Assert.Contains(".k-tt-bell { all: unset; min-width: 26px; min-height: 24px;", sheet);
        Assert.EndsWith(BossTimersLogic.RowCss(), sheet);
        // the chip label rule targets the chip's own label, never the respawn menu popover parented on the chip
        Assert.Contains(".k-tt-trow .k-chip > box > label", sheet);
    }

    [Fact]
    public void Timers_Fonts_SecondaryPointEight_CountdownPointEightFive()
    {
        Assert.Equal("k-s110", KitCss.SizeClass(BossTimersLogic.Row(14)));      // name 14 → 11.2
        Assert.Equal("k-s90", KitCss.SizeClass(BossTimersLogic.Row(11.5)));     // zone/kill line 11.5 → 9.2
        Assert.Equal("k-s90", KitCss.SizeClass(BossTimersLogic.Row(11)));       // "at 14:05" line 11 → 8.8 (kit floor 9)
        Assert.Equal("k-s160", KitCss.SizeClass(BossTimersLogic.StatusPx(19))); // countdown 19 → 16.15
        Assert.Equal("k-s135", KitCss.SizeClass(BossTimersLogic.StatusPx(16))); // ALIVE
        Assert.Equal("k-s120", KitCss.SizeClass(BossTimersLogic.StatusPx(14))); // SHOULD BE UP
        Assert.True(BossTimersLogic.StatusScale > BossTimersLogic.RowScale);
    }

    [Fact]
    public void Timers_CompactThreshold_Still700_AndTheCardTabIsCompact()
    {
        Assert.Equal(700, BossTimersLogic.CompactBelow);
        Assert.True(TabState.MinTabWidth < BossTimersLogic.CompactBelow);
    }

    // ---- DPS rows (the card's ranking)

    [Fact]
    public void DpsRows_PitchIsThreeQuartersOfAvalonia_SoAThirdMorePlayersFit()
    {
        Assert.Equal(36, RowMetrics.Pitch);
        Assert.True(RowMetrics.Pitch * 4 <= 48 * 3);   // Avalonia: 42 px row + 6 px gap
        var css = Css.Sheet(0.9);
        Assert.Contains($".row {{ margin-bottom: {RowMetrics.Gap}px; min-height: {RowMetrics.Height}px; }}", css);
        Assert.Contains(".cols { margin: 0 14px 2px 8px; }", css);
        Assert.Contains("padding: 4px 18px; }", css);   // .party
        Assert.Contains("min-height: 40px; }", CardText.ScaleCss(css, 1.25));
    }

    [Fact]
    public void DpsRows_ContentFitsTheRow_FontsUnchanged_RuleSlotWidthUnchanged()
    {
        Assert.True(RowMetrics.Coin + 2 <= RowMetrics.Height);   // 1 px border each side
        Assert.True(RowMetrics.Rank + 2 <= RowMetrics.Height);
        Assert.True(RowMetrics.Emblem + 2 * RowMetrics.CoinInset <= RowMetrics.Coin);
        Assert.True(RowMetrics.Unknown + 2 * RowMetrics.CoinInset <= RowMetrics.Coin);
        Assert.True(RowMetrics.Gloss < RowMetrics.Height);
        Assert.True(RowMetrics.Height - 2 * RowMetrics.RuleInset >= 16);
        var css = Css.Sheet(1);
        Assert.Contains(".card label.name { font-size: 16.5px;", css);
        Assert.Contains(".card label.num { font-size: 16px;", css);
        Assert.Contains(".card label.rank { min-width: 22px; min-height: 22px; border-radius: 11px; font-size: 13px;", css);
        // Card.cs sizes the header's rule slot 11 px = 1 px rule + 5 px margins each side
        Assert.Contains($"margin: {RowMetrics.RuleInset}px 5px; }}", css);
        Assert.DoesNotContain("{{", css);
    }
}
