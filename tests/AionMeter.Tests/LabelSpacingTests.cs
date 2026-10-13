using AionMeter.Core.Atlas;

namespace AionMeter.Tests;

public class LabelSpacingTests
{
    [Fact]
    public void A_label_closer_than_14_px_to_a_placed_one_is_refused()
    {
        var s = new LabelSpacing();
        Assert.True(s.TryPlace(100, 100));
        Assert.False(s.TryPlace(110, 105));    // 11.2 px
        Assert.False(s.TryPlace(100, 113.9));
        Assert.True(s.TryPlace(114, 100));     // exactly 14 px: room
        Assert.True(s.TryPlace(100, 80));      // 20 px above the first
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void Neighbours_across_grid_cells_and_negative_coordinates_are_seen()
    {
        var s = new LabelSpacing();
        Assert.True(s.TryPlace(-0.5, -0.5));   // cell (-1,-1)
        Assert.False(s.TryPlace(0.5, 0.5));    // cell (0,0), 1.4 px away
        Assert.True(s.TryPlace(27.9, 0));      // cell (1,0)
        Assert.False(s.TryPlace(28.1, 13));    // cell (2,0), 13 px from the last
        Assert.True(s.TryPlace(-200, 300));
    }

    [Fact]
    public void Refused_labels_leave_no_anchor_and_clear_starts_a_new_frame()
    {
        var s = new LabelSpacing();
        Assert.True(s.TryPlace(0, 0));
        Assert.False(s.TryPlace(10, 0));       // refused: does not block what is near it but far from the first
        Assert.True(s.TryPlace(20, 0));
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.True(s.TryPlace(5, 0));
        Assert.True(s.TryPlace(0, 50));
    }

    [Fact]
    public void A_dense_cluster_keeps_one_label_per_14_px()
    {
        // 400 icons on a 4 px lattice (an 80x80 px blob, Verteron seeds zoomed out): every kept pair is >= 14 px apart
        var s = new LabelSpacing();
        var kept = new List<(double X, double Y)>();
        for (var i = 0; i < 20; i++)
            for (var j = 0; j < 20; j++)
                if (s.TryPlace(i * 4, j * 4)) kept.Add((i * 4, j * 4));
        for (var a = 0; a < kept.Count; a++)
            for (var b = a + 1; b < kept.Count; b++)
            {
                var dx = kept[a].X - kept[b].X; var dy = kept[a].Y - kept[b].Y;
                Assert.True(dx * dx + dy * dy >= 14 * 14);
            }
        Assert.Equal(35, kept.Count);          // greedy in column order (same count from a brute-force check of the loop)
    }
}
