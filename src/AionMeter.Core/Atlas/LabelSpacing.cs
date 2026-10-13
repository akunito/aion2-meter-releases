namespace AionMeter.Core.Atlas;

/// <summary>
/// Screen-space label anchors placed in one atlas frame. At low zoom neighbouring nodes overprint each other's counts, so a
/// label is refused when another anchor sits closer than <see cref="Min"/> px (A2M-8; the icon still draws, the count comes
/// back when zooming in). First come, first served: bosses are placed before nodes, so they keep their labels.
/// A hash grid of Min-sized cells: each test looks at 3x3 cells instead of every label (hundreds per frame zoomed out).
/// </summary>
public sealed class LabelSpacing(double min = 14)
{
    public double Min { get; } = min;
    private readonly Dictionary<long, List<(double X, double Y)>> _cells = new();
    private readonly Stack<List<(double X, double Y)>> _spare = new();

    public int Count { get; private set; }

    public void Clear()
    {
        foreach (var list in _cells.Values) { list.Clear(); _spare.Push(list); }
        _cells.Clear();
        Count = 0;
    }

    private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    /// <summary>True (and the anchor is kept) when no placed anchor is closer than Min px.</summary>
    public bool TryPlace(double x, double y)
    {
        var cx = (int)Math.Floor(x / Min); var cy = (int)Math.Floor(y / Min);
        var min2 = Min * Min;
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!_cells.TryGetValue(Key(cx + dx, cy + dy), out var list)) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    var ex = list[i].X - x; var ey = list[i].Y - y;
                    if (ex * ex + ey * ey < min2) return false;
                }
            }
        var key = Key(cx, cy);
        if (!_cells.TryGetValue(key, out var own)) _cells[key] = own = _spare.Count > 0 ? _spare.Pop() : new List<(double, double)>(2);
        own.Add((x, y));
        Count++;
        return true;
    }
}
