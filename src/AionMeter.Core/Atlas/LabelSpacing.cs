namespace AionMeter.Core.Atlas;

/// <summary>
/// Screen-space labels placed in one atlas frame. A label is refused when its anchor (the icon centre) is closer than
/// <see cref="Min"/> px to a placed anchor, or when its text box overlaps a placed box (A2M-8: the 14 px anchor rule alone let
/// "13:59" and "×21" boxes, 25-40 px wide, overprint at 15-30 px spacing — render of 2026-10-13). The icon still draws; the
/// label comes back when zooming in. First come, first served: bosses are placed before nodes and keep their labels.
/// Hash grid of 32 px cells: a test looks at the few cells the box covers instead of every label (hundreds zoomed out).
/// </summary>
public sealed class LabelSpacing(double min = 14)
{
    private const double Cell = 32;
    public double Min { get; } = min;
    private readonly Dictionary<long, List<int>> _cells = new();
    private readonly Stack<List<int>> _spare = new();
    private readonly List<(double X, double Y, double L, double T, double R, double B)> _placed = new();

    public int Count => _placed.Count;

    public void Clear()
    {
        foreach (var list in _cells.Values) { list.Clear(); _spare.Push(list); }
        _cells.Clear();
        _placed.Clear();
    }

    private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;
    private static int C(double v) => (int)Math.Floor(v / Cell);

    /// <summary>Anchor only (a zero-size box at the anchor).</summary>
    public bool TryPlace(double x, double y) => TryPlace(x, y, x, y, 0, 0);

    /// <summary>True (and the label is kept) when no placed anchor is closer than Min px and no placed box overlaps this one.</summary>
    public bool TryPlace(double x, double y, double left, double top, double width, double height)
    {
        double r = left + width, b = top + height, min2 = Min * Min;
        // the cells to search: those the box covers, widened by Min around the anchor
        int c0 = C(Math.Min(left, x - Min)), c1 = C(Math.Max(r, x + Min)), r0 = C(Math.Min(top, y - Min)), r1 = C(Math.Max(b, y + Min));
        for (var cx = c0; cx <= c1; cx++)
            for (var cy = r0; cy <= r1; cy++)
            {
                if (!_cells.TryGetValue(Key(cx, cy), out var list)) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    var p = _placed[list[i]];
                    var ex = p.X - x; var ey = p.Y - y;
                    if (ex * ex + ey * ey < min2) return false;
                    if (width > 0 && height > 0 && left < p.R && r > p.L && top < p.B && b > p.T) return false;
                }
            }
        var idx = _placed.Count;
        _placed.Add((x, y, left, top, r, b));
        // registered in the cells of its box and of its anchor's Min-disc, so both tests above find it
        int d0 = C(Math.Min(left, x - Min)), d1 = C(Math.Max(r, x + Min)), e0 = C(Math.Min(top, y - Min)), e1 = C(Math.Max(b, y + Min));
        for (var cx = d0; cx <= d1; cx++)
            for (var cy = e0; cy <= e1; cy++)
            {
                var key = Key(cx, cy);
                if (!_cells.TryGetValue(key, out var own)) _cells[key] = own = _spare.Count > 0 ? _spare.Pop() : new List<int>(4);
                own.Add(idx);
            }
        return true;
    }
}
