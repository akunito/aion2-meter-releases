using System.Text.Json;
using AionMeter.Core.Atlas;

namespace AionMeter.Overlay.Views;

/// <summary>
/// The atlas tab's geometry and pixel work without GTK (A2M-50, the Avalonia AtlasView's maths): view transform (screen = calibration px × k + t,
/// k = image/calibration factor × zoom), zoom at a point, fit, follow, hit tests, countdowns, the heading wedge, the 10 s trail, the map pyramid
/// and the icon / edge colours. Everything here is display-free so the tests run it.
/// </summary>
internal static class AtlasMath
{
    public const double MinScale = 0.08, MaxScale = 12, WheelStep = 1.15;
    /// <summary>~420 px per 100 m: nodes a few metres apart stay separate (Diego's pick: closest).</summary>
    public const double FollowScale = 10;
    /// <summary>Zoomed out, icons of one kind pile up: one icon per kind per 28 px cell with the count (Diego 2026-10-10).</summary>
    public const double ClusterBelow = 0.16, ClusterCell = 28;
    /// <summary>Labels only once zoomed in enough to read the map (cluster counts and the player always).</summary>
    public const double LabelsFrom = 0.12;
    public const double PickRadius = 14;

    /// <summary>Wheel zoom keeping the point under the pointer still.</summary>
    public static (double Scale, double Tx, double Ty) ZoomAt(double scale, double tx, double ty, double px, double py, double factor)
    {
        var next = Math.Clamp(scale * factor, MinScale, MaxScale);
        var k = next / scale;
        return (next, px - (px - tx) * k, py - (py - ty) * k);
    }

    /// <summary>The zoom slider (log scale) around the centre of the view.</summary>
    public static (double Scale, double Tx, double Ty) ZoomTo(double scale, double tx, double ty, double w, double h, double logValue)
    {
        var next = Math.Clamp(Math.Exp(logValue), MinScale, MaxScale);
        var cx = w / 2; var cy = h / 2;
        return (next, cx - (cx - tx) * next / scale, cy - (cy - ty) * next / scale);
    }

    /// <summary>The whole image centred in the view (1000² when there is no image; the view at least 100²).</summary>
    public static (double Scale, double Tx, double Ty) Fit(int imgW, int imgH, double vw, double vh)
    {
        double w = imgW > 0 ? imgW : 1000, h = imgH > 0 ? imgH : 1000;
        vw = Math.Max(vw, 100); vh = Math.Max(vh, 100);
        var s = Math.Clamp(Math.Min(vw / w, vh / h), MinScale, MaxScale);
        return (s, (vw - w * s) / 2, (vh - h * s) / 2);
    }

    /// <summary>Translation that puts calibration pixel (px, py) in the middle of a w×h view at factor k.</summary>
    public static (double Tx, double Ty) Centre(double px, double py, double k, double w, double h) => (w / 2 - px * k, h / 2 - py * k);

    /// <summary>Only what is on screen (+ a marker's radius) is drawn.</summary>
    public static bool InView(double x, double y, double w, double h) => x > -30 && y > -30 && x < w + 30 && y < h + 30;

    /// <summary>The marker within <paramref name="radius"/> px nearest to (x, y): its Index, -1 for none.</summary>
    public static int Nearest(List<(double X, double Y, int Index)> markers, double x, double y, double radius = PickRadius)
    {
        var best = -1; var bestD = radius * radius;
        for (var i = 0; i < markers.Count; i++)
        {
            var (mx, my, idx) = markers[i];
            var d = (mx - x) * (mx - x) + (my - y) * (my - y);
            if (d < bestD) { bestD = d; best = idx; }
        }
        return best;
    }

    /// <summary>Screen → calibration pixel → world cm. The affine is scale + offset (no rotation in the shipped maps): inverted through two known points, as WPF.</summary>
    public static (float X, float Y) ScreenToWorld(MapCalibration cal, double sx, double sy, double tx, double ty, double k)
    {
        var px = (sx - tx) / k; var py = (sy - ty) / k;
        var (ox, oy) = cal.ToPixel(0, 0); var (ux, uy) = cal.ToPixel(1000, 1000);
        return ((float)((px - ox) / (ux - ox) * 1000), (float)((py - oy) / (uy - oy) * 1000));
    }

    /// <summary>The pick radius in world cm at factor k (14 px).</summary>
    public static float PickCm(MapCalibration cal, double k) => (float)(PickRadius / k / cal.Scale);

    /// <summary>"soon", "m:ss" or "h·mm" until <paramref name="atMs"/>.</summary>
    public static string Countdown(long atMs, long nowMs)
    {
        var left = TimeSpan.FromMilliseconds(atMs - nowMs);
        return left.TotalSeconds <= 0 ? "soon" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}" : $"{left.Minutes}:{left.Seconds:00}";
    }

    /// <summary>The whole seconds a countdown shows (the text only changes with it): a cache key; ≤ 0 = "soon".</summary>
    public static long CountdownKey(long atMs, long nowMs) => atMs - nowMs <= 0 ? 0 : (atMs - nowMs) / 1000 + 1;

    /// <summary>
    /// The pyramid level to sample at <paramref name="pixelScale"/> (image px → device px): level i is the image halved i times; the largest level
    /// that is still minified (≤ 1 device px per level px) so bilinear never shrinks by more than 2 (cairo's GOOD filter on a 4096² source at
    /// the Fit zoom walks every source pixel).
    /// </summary>
    public static int PyramidLevel(double pixelScale, int levels)
    {
        if (levels <= 1 || pixelScale >= 1 || pixelScale <= 0) return 0;
        var i = (int)Math.Floor(-Math.Log2(pixelScale) + 1e-9);
        return Math.Clamp(i, 0, levels - 1);
    }

    /// <summary>The heading wedge of the player at (x, y) pointing to (hx, hy): tip 19 px out, base 9 px out and 6 px each side. False when the two points coincide.</summary>
    public static bool Wedge(double x, double y, double hx, double hy, out (double X, double Y) tip, out (double X, double Y) left, out (double X, double Y) right)
    {
        var dx = hx - x; var dy = hy - y; var len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0.001) { tip = left = right = default; return false; }
        dx /= len; dy /= len;
        tip = (x + dx * 19, y + dy * 19);
        left = (x + dx * 9 - dy * 6, y + dy * 9 + dx * 6);
        right = (x + dx * 9 + dy * 6, y + dy * 9 - dx * 6);
        return true;
    }

    /// <summary>Top-left of the pet card beside the click, kept inside the view (WPF: 14 px right, 10 px up, 4 px from the edges).</summary>
    public static (double X, double Y) PetCardAt(double x, double y, double w, double h, double cardW, double cardH) =>
        (Math.Max(4, Math.Min(x + 14, w - cardW)), Math.Max(4, Math.Min(y - 10, h - cardH)));

    /// <summary>The cluster cell of a screen coordinate.</summary>
    public static int Cell(double v) => (int)Math.Floor(v / ClusterCell);

    /// <summary>Settings key of a kind name.</summary>
    public static string KindKey(string name) => name.Trim().ToLowerInvariant();

    /// <summary>A kind toggle (A2M-3): off when on, else on with at most two (the oldest drops).</summary>
    public static void ToggleKind(List<string> on, string key)
    {
        if (on.Remove(key)) return;
        on.Add(key);
        while (on.Count > 2) on.RemoveAt(0);
    }

    /// <summary>
    /// The icon's dominant colour (0xRRGGBB): the saturation-weighted mean of its opaque pixels lifted 25 % towards white; null without opaque pixels.
    /// <paramref name="px"/> = rows of <paramref name="channels"/>-byte RGB(A) pixels, straight alpha (GdkPixbuf).
    /// </summary>
    public static uint? DominantColour(ReadOnlySpan<byte> px, int w, int h, int stride, int channels)
    {
        double r = 0, g = 0, b = 0, wsum = 0;
        for (var y = 0; y < h; y++)
        {
            var row = px.Slice(y * stride, w * channels);
            for (var i = 0; i < row.Length; i += channels)
            {
                if (channels == 4 && row[i + 3] < 128) continue;
                int rr = row[i], gg = row[i + 1], bb = row[i + 2];
                var max = Math.Max(rr, Math.Max(gg, bb)); var min = Math.Min(rr, Math.Min(gg, bb));
                var sat = max == 0 ? 0 : (max - min) / (double)max;
                var wgt = 0.15 + sat * sat;   // saturated pixels dominate, dark outlines barely count
                r += rr * wgt; g += gg * wgt; b += bb * wgt; wsum += wgt;
            }
        }
        if (wsum <= 0) return null;
        uint Lift(double v) => (uint)(v / wsum + (255 - v / wsum) * 0.25);
        return (Lift(r) << 16) | (Lift(g) << 8) | Lift(b);
    }

    /// <summary>
    /// Mean colour (0xRRGGBB) of four 32 px corner patches and four edge-midpoint patches (the shipped maps are parchment, #DAD1BC at every
    /// corner, measured 2026-10-13): painted outside the map instead of black corners (A2M-8). Null for an image under 64 px.
    /// </summary>
    public static uint? EdgeColour(ReadOnlySpan<byte> px, int w, int h, int stride, int channels)
    {
        const int p = 32;
        int mx = w - p, my = h - p;
        if (mx < p || my < p) return null;
        long r = 0, g = 0, b = 0;
        ReadOnlySpan<(int X, int Y)> at = [(0, 0), (mx, 0), (0, my), (mx, my), (mx / 2, 0), (mx / 2, my), (0, my / 2), (mx, my / 2)];
        foreach (var (x0, y0) in at)
            for (var y = y0; y < y0 + p; y++)
            {
                var row = px.Slice(y * stride + x0 * channels, p * channels);
                for (var i = 0; i < row.Length; i += channels) { r += row[i]; g += row[i + 1]; b += row[i + 2]; }
            }
        long n = at.Length * p * p;
        return (uint)((r / n) << 16 | (g / n) << 8 | (b / n));
    }

    /// <summary>RGB(A) pixbuf rows → cairo RGB24 (native-endian xRGB = B,G,R,x bytes on x86).</summary>
    public static void PackRgb24(ReadOnlySpan<byte> src, int w, int h, int srcStride, int channels, Span<byte> dst, int dstStride)
    {
        for (var y = 0; y < h; y++)
        {
            var s = src.Slice(y * srcStride, w * channels);
            var d = dst.Slice(y * dstStride, w * 4);
            for (int i = 0, o = 0; o < d.Length; i += channels, o += 4)
            {
                d[o] = s[i + 2]; d[o + 1] = s[i + 1]; d[o + 2] = s[i]; d[o + 3] = 255;
            }
        }
    }

    /// <summary>RGBA (straight) pixbuf rows → cairo ARGB32 (premultiplied B,G,R,A bytes), every pixel times <paramref name="alpha"/> (the dimmed icons).</summary>
    public static void PackArgb32(ReadOnlySpan<byte> src, int w, int h, int srcStride, int channels, Span<byte> dst, int dstStride, double alpha = 1)
    {
        for (var y = 0; y < h; y++)
        {
            var s = src.Slice(y * srcStride, w * channels);
            var d = dst.Slice(y * dstStride, w * 4);
            for (int i = 0, o = 0; o < d.Length; i += channels, o += 4)
            {
                var a = (channels == 4 ? s[i + 3] : 255) * alpha;
                var f = a / 255.0;
                d[o] = (byte)(s[i + 2] * f + 0.5); d[o + 1] = (byte)(s[i + 1] * f + 0.5); d[o + 2] = (byte)(s[i] * f + 0.5); d[o + 3] = (byte)(a + 0.5);
            }
        }
    }

    /// <summary>One pyramid step: 2×2 box average of 4-byte pixels (odd last row/column dropped); dst is (w/2)×(h/2).</summary>
    public static void Half(ReadOnlySpan<byte> src, int w, int h, int srcStride, Span<byte> dst, int dstStride)
    {
        int dw = w / 2, dh = h / 2;
        for (var y = 0; y < dh; y++)
        {
            var a = src.Slice(2 * y * srcStride, dw * 8);
            var b = src.Slice((2 * y + 1) * srcStride, dw * 8);
            var d = dst.Slice(y * dstStride, dw * 4);
            for (int o = 0, i = 0; o < d.Length; o += 4, i += 8)
            {
                d[o] = (byte)((a[i] + a[i + 4] + b[i] + b[i + 4] + 2) >> 2);
                d[o + 1] = (byte)((a[i + 1] + a[i + 5] + b[i + 1] + b[i + 5] + 2) >> 2);
                d[o + 2] = (byte)((a[i + 2] + a[i + 6] + b[i + 2] + b[i + 6] + 2) >> 2);
                d[o + 3] = (byte)((a[i + 3] + a[i + 7] + b[i + 3] + b[i + 7] + 2) >> 2);
            }
        }
    }
}

/// <summary>The player's last 10 s: a point every 50 cm of movement, older ones dropped (WPF trail). No allocation once warm.</summary>
internal sealed class AtlasTrail
{
    public const long KeepMs = 10_000;
    private readonly List<(long Ms, float X, float Y)> _points = new(64);

    public int Count => _points.Count;
    public (long Ms, float X, float Y) this[int i] => _points[i];
    public void Clear() => _points.Clear();

    /// <summary>Adds (x, y) when it is the first point or moved more than 50 cm on an axis, then drops the points older than 10 s.</summary>
    public void Update(long nowMs, float x, float y)
    {
        if (_points.Count == 0 || Math.Abs(_points[^1].X - x) > 50 || Math.Abs(_points[^1].Y - y) > 50) _points.Add((nowMs, x, y));
        var drop = 0;
        while (drop < _points.Count && nowMs - _points[drop].Ms > KeepMs) drop++;
        if (drop > 0) _points.RemoveRange(0, drop);
    }

    /// <summary>Stroke opacity of the segment ending at point i: 0.75 fresh → 0.1 ten seconds old.</summary>
    public double Alpha(int i, long nowMs) => 0.65 * (1 - (nowMs - _points[i].Ms) / (double)KeepMs) + 0.1;
}

/// <summary>One kind of data/atlas/gather-catalog.json (English names: Diego wants English only in the atlas).</summary>
internal sealed record CatalogNode(string Category, string CategoryName, string Key, string Name);

internal static class AtlasCatalog
{
    /// <summary>The kinds by category in file order; empty when the file is missing or malformed.</summary>
    public static List<CatalogNode> Load(string path)
    {
        var list = new List<CatalogNode>(24);
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var cat in doc.RootElement.GetProperty("categories").EnumerateArray())
            {
                var catKey = cat.GetProperty("key").GetString() ?? "";
                var catName = (cat.TryGetProperty("en", out var cen) ? cen.GetString() : null) ?? catKey;
                foreach (var node in cat.GetProperty("nodes").EnumerateArray())
                {
                    var key = node.GetProperty("key").GetString() ?? "";
                    list.Add(new CatalogNode(catKey, catName, key, (node.TryGetProperty("en", out var en) ? en.GetString() : null) ?? key));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AionMeter.Service.Log.Info($"gather-catalog.json ignored: {ex.Message}");
            list.Clear();
        }
        return list;
    }

    /// <summary>The node's current name (objects.json) is this catalogue entry: drawn gold.</summary>
    public static bool IsCurrent(string? current, string name) => current is not null && string.Equals(current.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
