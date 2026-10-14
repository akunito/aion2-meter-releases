namespace AionMeter.Overlay;

/// <summary>The card's footer pills (boss timers + events) in up to three lines by their natural widths: GTK4 has no wrap panel (FlowBox
/// lines its children up in columns). Pure, tested without a display.</summary>
public static class PillWrap
{
    /// <summary>Greedy lines into <paramref name="line"/>; the last line takes what is left. Returns the widest line.</summary>
    public static int Lines(ReadOnlySpan<int> nat, int avail, Span<int> line, int maxLines)
    {
        int x = 0, l = 0, widest = 0;
        for (var i = 0; i < nat.Length; i++)
        {
            if (x > 0 && x + nat[i] > avail && l < maxLines - 1) { l++; x = 0; }
            line[i] = l;
            x += nat[i];
            if (x > widest) widest = x;
        }
        return widest;
    }

    /// <summary>The narrowest width the pills fit into (no line wider than it). Greedy needs no more lines at a larger width, so the fit is
    /// monotone and a binary search finds it.</summary>
    public static int MinWidth(ReadOnlySpan<int> nat, int maxLines)
    {
        int lo = 0, hi = 0;
        foreach (var w in nat) { if (w > lo) lo = w; hi += w; }
        Span<int> line = nat.Length <= 64 ? stackalloc int[nat.Length] : new int[nat.Length];
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines(nat, mid, line, maxLines) <= mid) hi = mid; else lo = mid + 1;
        }
        return lo;
    }
}
