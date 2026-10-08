namespace AionMeter.Core.Atlas;

/// <summary>A dense area of one node kind: its members' convex hull (world cm) and how many spots it holds.</summary>
public sealed record NodeZone(float CenterX, float CenterY, int Count, IReadOnlyList<(float X, float Y)> Hull);

/// <summary>
/// The best areas to farm one kind: spots grouped greedily around the densest spot (all neighbours within <c>radius</c>),
/// densest first, up to <c>top</c> zones. The hull is padded by <c>margin</c> so a zone reads as an area even with 2–3 spots.
/// </summary>
public static class NodeZones
{
    public static List<NodeZone> Compute(IReadOnlyList<NodeSpot> spots, float radiusCm = 15_000, int top = 5, float marginCm = 2_000)
    {
        var zones = new List<NodeZone>(top);
        var left = new List<(float X, float Y)>(spots.Count);
        foreach (var s in spots) left.Add((s.X, s.Y));
        var r2 = radiusCm * radiusCm;
        while (left.Count > 0 && zones.Count < top)
        {
            // densest spot = the one with most neighbours in range
            var best = -1; var bestN = -1;
            for (var i = 0; i < left.Count; i++)
            {
                var n = 0;
                for (var j = 0; j < left.Count; j++)
                    if (Dist2(left[i], left[j]) <= r2) n++;
                if (n > bestN) { bestN = n; best = i; }
            }
            var members = new List<(float X, float Y)>(bestN);
            var centre = left[best];
            for (var j = left.Count - 1; j >= 0; j--)
                if (Dist2(centre, left[j]) <= r2) { members.Add(left[j]); left.RemoveAt(j); }
            float cx = 0, cy = 0;
            foreach (var m in members) { cx += m.X; cy += m.Y; }
            cx /= members.Count; cy /= members.Count;
            zones.Add(new NodeZone(cx, cy, members.Count, Pad(Hull(members), cx, cy, marginCm)));
        }
        return zones;
    }

    private static float Dist2((float X, float Y) a, (float X, float Y) b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

    /// <summary>Andrew's monotone chain; 1–2 points come back as they are (the caller pads them into a shape).</summary>
    public static List<(float X, float Y)> Hull(List<(float X, float Y)> pts)
    {
        if (pts.Count < 3) return new List<(float X, float Y)>(pts);
        var p = pts.OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        var h = new List<(float X, float Y)>(2 * p.Count);
        foreach (var q in p)
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(q);
        }
        var lowerCount = h.Count + 1;
        for (var i = p.Count - 2; i >= 0; i--)
        {
            while (h.Count >= lowerCount && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p[i]);
        }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    private static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>Pushes every hull point away from the centre by <paramref name="margin"/>; 1–2 points become an 8-gon / a capsule-ish ring.</summary>
    private static List<(float X, float Y)> Pad(List<(float X, float Y)> hull, float cx, float cy, float margin)
    {
        if (hull.Count <= 2)
        {
            var reach = margin + (hull.Count == 2 ? MathF.Sqrt(Dist2(hull[0], hull[1])) / 2 : 0);
            var ring = new List<(float X, float Y)>(8);
            for (var k = 0; k < 8; k++) { var a = k * MathF.PI / 4; ring.Add((cx + reach * MathF.Cos(a), cy + reach * MathF.Sin(a))); }
            return ring;
        }
        var outp = new List<(float X, float Y)>(hull.Count);
        foreach (var (x, y) in hull)
        {
            var dx = x - cx; var dy = y - cy; var d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < 1) { outp.Add((x, y)); continue; }
            outp.Add((x + dx / d * margin, y + dy / d * margin));
        }
        return outp;
    }
}
