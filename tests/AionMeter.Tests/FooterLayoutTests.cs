using AionMeter.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-50 footer (Diego 2026-10-14): the DPS status at the end of the last pill line, right-aligned, or on a line of its own when
/// that line has no room; pills keep the PillWrap lines laid out from the requested width − 72 (no display).</summary>
public class FooterLayoutTests
{
    // pill widths like the replay's DPS card at scale 1 (timers + 7 events), as PillWrapTests
    private static readonly int[] Eight = [112, 80, 116, 72, 76, 96, 82, 90];

    [Fact]
    public void Status_EndsTheLastPillLineWhenItFits()
    {
        Span<int> line = stackalloc int[1];
        // the replay card: only the timers pill (140 px) on a 479 px window → 407 px of line, 267 left
        PillWrap.Lines([140], 479 - 72, line, 3);
        Assert.Equal(0, PillWrap.StatusLine([140], line, 479 - 72, Card.StatusMin));
    }

    [Fact]
    public void Status_GetsItsOwnLineWhenTheLastIsFull()
    {
        var nat = Eight.AsSpan();
        Span<int> line = stackalloc int[8];
        // a 488 px line: 5 pills, then 96+82+90 = 268 with 220 left → the status ends line 1
        PillWrap.Lines(nat, 560 - 72, line, 3);
        Assert.Equal([0, 0, 0, 0, 0, 1, 1, 1], line.ToArray());
        Assert.Equal(1, PillWrap.StatusLine(nat, line, 560 - 72, Card.StatusMin));
        // a 300 px line (372 px window): three lines, the last 268 wide with 32 left → a line of its own unless the status is that short
        PillWrap.Lines(nat, 300, line, 3);
        Assert.Equal([0, 0, 1, 1, 1, 2, 2, 2], line.ToArray());
        Assert.Equal(3, PillWrap.StatusLine(nat, line, 300, Card.StatusMin));
        Assert.Equal(2, PillWrap.StatusLine(nat, line, 300, 30));
    }

    [Fact]
    public void Status_CanUseTheFourthLine()
    {
        Span<int> line = stackalloc int[5];
        PillWrap.Lines([100, 100, 100, 100, 100], 150, line, 3);   // third line holds three
        Assert.Equal(3, PillWrap.StatusLine([100, 100, 100, 100, 100], line, 150, 50));
        Assert.Equal(0, PillWrap.StatusLine([], [], 300, 50));
    }

    [Fact]
    public void Status_DoesNotChangeThePillFloor()
    {
        // the status ellipsizes in its own clip: the narrowest card is still the pills' 3-line fit (the right-drag floor, the window min)
        var m = PillWrap.MinWidth(Eight, 3);
        Span<int> line = stackalloc int[8];
        PillWrap.Lines(Eight, m, line, 3);
        Assert.InRange(PillWrap.StatusLine(Eight, line, m, Card.StatusMin), 2, 3);
    }
}
