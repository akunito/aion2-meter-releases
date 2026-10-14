using AionMeter.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-50: right-button drag resize of the layer-shell card — which edges the grab point picks and the size and margins that keep the
/// opposite edges still, for every anchor (no display).</summary>
public class EdgeResizeTests
{
    private const int OutW = 2560, OutH = 1440, W = 560, H = 540, MinW = OverlaySettings.MinWidth, MinH = OverlaySettings.MinHeight;
    private static readonly Anchor[] Anchors = [Anchor.TopRight, Anchor.TopLeft, Anchor.BottomRight, Anchor.BottomLeft];

    [Theory]
    [InlineData(10, 10, -1, -1)]
    [InlineData(300, 10, 0, -1)]
    [InlineData(590, 10, 1, -1)]
    [InlineData(10, 150, -1, 0)]
    [InlineData(590, 150, 1, 0)]
    [InlineData(10, 290, -1, 1)]
    [InlineData(300, 290, 0, 1)]
    [InlineData(590, 290, 1, 1)]
    [InlineData(199, 99, -1, -1)]    // just inside the left and top thirds
    [InlineData(200, 100, -1, -1)]   // centre cell, upper-left quadrant: Sway's corner
    [InlineData(300, 150, 1, 1)]     // the exact centre counts as lower-right (Sway: dx > width / 2 is right, here ≥)
    [InlineData(250, 180, -1, 1)]
    [InlineData(350, 120, 1, -1)]
    [InlineData(400, 200, 1, 1)]     // first px of the right and bottom thirds
    public void EdgeAt_ThirdsAndCorners(double x, double y, int ex, int ey) => Assert.Equal((ex, ey), Placement.EdgeAt(x, y, 600, 300));

    /// <summary>The card's rectangle on the output from anchor, margins and the px size.</summary>
    private static (int L, int T, int R, int B) Rect(Anchor a, int mh, int mv, int w, int h)
    {
        var l = Placement.IsRight(a) ? OutW - mh - w : mh;
        var t = Placement.IsBottom(a) ? OutH - mv - h : mv;
        return (l, t, l + w, t + h);
    }

    private static (int H, int V) Margins(Anchor a, int l, int t, int w, int h) =>
        (Placement.IsRight(a) ? OutW - l - w : l, Placement.IsBottom(a) ? OutH - t - h : t);

    [Fact]
    public void EveryEdgeAndCorner_MovesOnlyThePickedEdges_ForEveryAnchor()
    {
        foreach (var a in Anchors)
            for (var ex = -1; ex <= 1; ex++)
                for (var ey = -1; ey <= 1; ey++)
                {
                    if (ex == 0 && ey == 0) continue;
                    // the card in the middle of the output: nothing clamps
                    var (mh, mv) = Margins(a, 1000, 400, W, H);
                    var before = Rect(a, mh, mv, W, H);
                    // grow outwards by 70 / 50 on the moving edges
                    double dx = ex * 70, dy = ey * 50;
                    var (w, h, nh, nv) = Placement.EdgeResize(a, ex, ey, W, H, mh, mv, dx, dy, MinW, MinH, OutW, OutH, 1);
                    var after = Rect(a, nh, nv, w, h);
                    Assert.Equal(before.L + (ex < 0 ? -70 : 0), after.L);
                    Assert.Equal(before.R + (ex > 0 ? 70 : 0), after.R);
                    Assert.Equal(before.T + (ey < 0 ? -50 : 0), after.T);
                    Assert.Equal(before.B + (ey > 0 ? 50 : 0), after.B);
                }
    }

    [Theory]
    // anchor, edge, margins at the press, offset → width, height, margins (px = unscaled at scale 1)
    [InlineData(Anchor.TopRight, -1, 0, 24, 140, -100, 0, 660, 540, 24, 140)]     // left edge out: the right margin stays
    [InlineData(Anchor.TopRight, 1, 0, 24, 140, -100, 0, 460, 540, 124, 140)]     // right edge in: the right margin grows
    [InlineData(Anchor.TopRight, 0, -1, 24, 140, 0, -40, 560, 580, 24, 100)]      // top edge up: the top margin shrinks
    [InlineData(Anchor.TopLeft, -1, -1, 100, 200, -50, 30, 610, 510, 50, 230)]    // top-left corner
    [InlineData(Anchor.TopLeft, 1, 1, 100, 200, 40, 60, 600, 600, 100, 200)]      // bottom-right corner = the grip's resize
    [InlineData(Anchor.BottomRight, 0, -1, 24, 40, 0, -100, 560, 640, 24, 40)]    // top edge up: the bottom margin stays
    [InlineData(Anchor.BottomRight, 0, 1, 24, 40, 0, 20, 560, 560, 24, 20)]       // bottom edge down: the bottom margin shrinks
    [InlineData(Anchor.BottomLeft, 1, -1, 300, 60, 25, -15, 585, 555, 300, 60)]   // top-right corner: left and bottom margins stay
    [InlineData(Anchor.BottomLeft, -1, 1, 300, 60, 20, 30, 540, 570, 320, 30)]    // bottom-left corner
    public void Concrete(Anchor a, int ex, int ey, int mh, int mv, double dx, double dy, int w, int h, int nh, int nv) =>
        Assert.Equal((w, h, nh, nv), Placement.EdgeResize(a, ex, ey, W, H, mh, mv, dx, dy, MinW, MinH, OutW, OutH, 1));

    [Fact]
    public void MovingEdges_StopAtTheOutput()
    {
        // bottom-right anchored 24/40: the bottom edge stops at the output's bottom (margin 0), the right edge at its right
        Assert.Equal((584, 580, 0, 0), Placement.EdgeResize(Anchor.BottomRight, 1, 1, W, H, 24, 40, 100, 100, MinW, MinH, OutW, OutH, 1));
        // bottom-left anchored 10/10: the left edge stops at 0, the right edge stays at 570
        Assert.Equal((570, 540, 0, 10), Placement.EdgeResize(Anchor.BottomLeft, -1, 0, W, H, 10, 10, -100, 0, MinW, MinH, OutW, OutH, 1));
        // top-right 24/140: the top edge stops at the output's top
        Assert.Equal((560, 680, 24, 0), Placement.EdgeResize(Anchor.TopRight, 0, -1, W, H, 24, 140, 0, -500, MinW, MinH, OutW, OutH, 1));
    }

    [Fact]
    public void MovingEdges_StopAtTheMinimum_TheOppositeEdgeStays()
    {
        foreach (var a in Anchors)
        {
            var (mh, mv) = Margins(a, 1000, 400, W, H);
            var before = Rect(a, mh, mv, W, H);
            // DPS: 340 × 220
            var (w, h, nh, nv) = Placement.EdgeResize(a, -1, -1, W, H, mh, mv, 500, 500, MinW, MinH, OutW, OutH, 1);
            Assert.Equal((MinW, MinH), (w, h));
            var after = Rect(a, nh, nv, w, h);
            Assert.Equal((before.R, before.B), (after.R, after.B));
            // a tab: 560 × 540, the card is already there, so shrinking from the right does nothing
            Assert.Equal((560, 540, mh, mv), Placement.EdgeResize(a, 1, 1, W, H, mh, mv, -200, -200, 560, 540, OutW, OutH, 1));
        }
    }

    [Fact]
    public void Scaled_WidthIsUnscaled_AndTheOppositeEdgeDoesNotShift()
    {
        // UI scale 1.5: 560×540 is 840×810 px. Top-left anchored 100/100: right edge at 940, bottom at 910.
        for (var d = -60; d <= 60; d++)
        {
            var (w, h, mh, mv) = Placement.EdgeResize(Anchor.TopLeft, -1, -1, 840, 810, 100, 100, d, d, MinW, MinH, OutW, OutH, 1.5);
            Assert.Equal(940, mh + (int)Math.Round(w * 1.5));
            Assert.Equal(910, mv + (int)Math.Round(h * 1.5));
            // right-anchored: the right margin stays, the left edge moves
            var r = Placement.EdgeResize(Anchor.TopRight, -1, 0, 840, 810, 24, 100, d, 0, MinW, MinH, OutW, OutH, 1.5);
            Assert.Equal(24, r.MarginH);
            Assert.Equal(840 - d, (int)Math.Round(r.W * 1.5), 1.0);
        }
        // the unscaled width is what overlay.json keeps: +30 px at 1.5 = +20
        Assert.Equal(580, Placement.EdgeResize(Anchor.TopRight, 1, 0, 840, 810, 100, 100, 30, 0, MinW, MinH, OutW, OutH, 1.5).W);
    }

    [Fact]
    public void RightBottomCorner_MatchesTheGripResize()
    {
        foreach (var a in Anchors)
        {
            var (mh, mv) = Margins(a, 800, 300, W, H);
            var grip = Placement.Resize(a, W, H, mh, mv, 90, -30, MinW, MinH, OutW, OutH);
            Assert.Equal(grip, Placement.EdgeResize(a, 1, 1, W, H, mh, mv, 90, -30, MinW, MinH, OutW, OutH, 1));
        }
    }
}

/// <summary>A2M-50: the card's footer pills in up to three lines, and the narrowest card they allow (the right-drag's floor on DPS).</summary>
public class PillWrapTests
{
    // pill widths like the replay's DPS card at scale 1 (timers + 7 events)
    private static readonly int[] Eight = [112, 80, 116, 72, 76, 96, 82, 90];

    [Fact]
    public void Lines_GreedyAndTheLastLineTakesTheRest()
    {
        Span<int> line = stackalloc int[5];
        Assert.Equal(200, PillWrap.Lines([100, 100, 100, 100, 100], 250, line, 3));
        Assert.Equal([0, 0, 1, 1, 2], line.ToArray());
        Assert.Equal(300, PillWrap.Lines([100, 100, 100, 100, 100], 150, line, 3));   // only three lines: the third holds three
        Assert.Equal([0, 1, 2, 2, 2], line.ToArray());
        Assert.Equal(120, PillWrap.Lines([120, 10, 10, 10, 10], 50, line, 3));         // a pill wider than the line still gets one
    }

    [Fact]
    public void MinWidth_IsTheNarrowestFit()
    {
        var nat = Eight.AsSpan();
        var m = PillWrap.MinWidth(nat, 3);
        Span<int> line = stackalloc int[8];
        Assert.True(PillWrap.Lines(nat, m, line, 3) <= m);
        Assert.True(PillWrap.Lines(nat, m - 1, line, 3) > m - 1);
        Assert.Equal(116, PillWrap.MinWidth([116], 3));
        Assert.Equal(0, PillWrap.MinWidth([], 3));
    }

    [Fact]
    public void MinWidth_MatchesALinearScan()
    {
        var rng = new Random(50);
        Span<int> line = stackalloc int[12];
        for (var t = 0; t < 300; t++)
        {
            var n = rng.Next(1, 13);
            var nat = new int[n];
            for (var i = 0; i < n; i++) nat[i] = rng.Next(20, 160);
            var lines = rng.Next(1, 4);
            var scan = nat.Max();
            while (PillWrap.Lines(nat, scan, line, lines) > scan) scan++;
            Assert.Equal(scan, PillWrap.MinWidth(nat, lines));
        }
    }
}
