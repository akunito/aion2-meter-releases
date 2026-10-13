using AionMeter.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-50: dragging the layer-shell card onto another output — which output and which margins after the crossing (no display).</summary>
public class MonitorMoveTests
{
    // DESK-like layout: DP-1 4K at 1.5 (2560×1440 logical) left, a 1.25 output right of it (2560×1440 physical = 2048×1152 logical)
    private static readonly OutputRect[] Desk = [new("DP-1", 0, 0, 2560, 1440, 1.5), new("DP-2", 2560, 0, 2048, 1152, 1.25)];
    private const int W = 560, H = 540;

    [Fact]
    public void Origin_AndMarginsAt_RoundTripForEveryAnchor()
    {
        foreach (var a in (Anchor[])[Anchor.TopRight, Anchor.TopLeft, Anchor.BottomRight, Anchor.BottomLeft])
        {
            var (x, y) = Placement.Origin(a, Desk[1], 100, 60, W, H);
            Assert.Equal((100, 60), Placement.MarginsAt(a, Desk[1], x, y, W, H));
        }
        // the default place on DP-1 (top-right, 24/140): the card's left edge is 2560 − 24 − 560
        Assert.Equal((1976, 140), Placement.Origin(Anchor.TopRight, Desk[0], 24, 140, W, H));
    }

    [Fact]
    public void OutputAt_FindsTheOutputOrNone()
    {
        Assert.Equal(0, Placement.OutputAt(Desk, 0, 0));
        Assert.Equal(0, Placement.OutputAt(Desk, 2559.5, 1439));
        Assert.Equal(1, Placement.OutputAt(Desk, 2560, 0));
        Assert.Equal(-1, Placement.OutputAt(Desk, 3000, 1200));   // below the shorter output
        Assert.Equal(-1, Placement.OutputAt(Desk, -1, 10));
    }

    [Fact]
    public void Cross_StaysNullWhileThePointerIsOnTheCurrentOutput()
    {
        // pressed at (300, 20) inside the card at its default place: the card is clamped at DP-1's right edge, the pointer is not there yet
        Assert.Null(Placement.Cross(Anchor.TopRight, Desk, 0, 1976 + 300, 140 + 20, 283, 0, 300, 20, W, H));
    }

    [Fact]
    public void Cross_ToTheRightOutputKeepsTheGrabPointUnderThePointer()
    {
        // top-left anchor so the margin is the card's x on DP-2: pointer 2276 + 1300 = 3576 → x 1016 − grab 300 = 716
        var c = Placement.Cross(Anchor.TopLeft, Desk, 0, 2276, 160, 1300, 40, 300, 20, W, H);
        Assert.Equal((1, 716, 180), c);
        // right anchor: the margin counts from DP-2's right edge, 2048 − 560 − 716
        Assert.Equal((1, 772, 180), Placement.Cross(Anchor.TopRight, Desk, 0, 2276, 160, 1300, 40, 300, 20, W, H));
    }

    [Fact]
    public void Cross_JustPastTheEdgeClampsTheCardOntoTheNewOutput()
    {
        // the pointer 1 px into DP-2 with a grab 300 px from the card's left: the card would start 299 px left of DP-2 → its left edge
        var c = Placement.Cross(Anchor.TopRight, Desk, 0, 2276, 160, 285, 0, 300, 20, W, H);
        Assert.Equal((1, 2048 - W, 140), c);
        // and back: from DP-2 onto DP-1, the card lands against DP-1's right edge (top-right margin 0)
        Assert.Equal((0, 0, 140), Placement.Cross(Anchor.TopRight, Desk, 1, 2600, 160, -41, 0, 30, 20, W, H));
    }

    [Fact]
    public void Cross_ClampsVerticallyOnAShorterOutput()
    {
        // pressed low on DP-1 (the card's top at 1300 − 20): DP-2 is 1152 tall, the card's bottom must stay on it
        var c = Placement.Cross(Anchor.BottomRight, Desk, 0, 2276, 1100, 400, 0, 300, 20, W, H);
        Assert.NotNull(c);
        Assert.Equal(1, c.Value.Output);
        Assert.Equal(0, c.Value.V);   // bottom anchor: margin 0 = on the bottom edge (1080 + 540 > 1152)
    }

    [Fact]
    public void Cross_NullOverNoOutput()
    {
        OutputRect[] gap = [new("A", 0, 0, 1600, 900), new("B", 2400, 0, 1920, 1080)];
        Assert.Null(Placement.Cross(Anchor.TopRight, gap, 0, 1500, 100, 500, 0, 300, 20, W, H));   // x 2000: between the outputs
        Assert.Equal(1, Placement.Cross(Anchor.TopRight, gap, 0, 1500, 100, 950, 0, 300, 20, W, H)!.Value.Output);
    }
}
