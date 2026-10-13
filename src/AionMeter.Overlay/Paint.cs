using AionMeter.Core.Events;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>Cairo helpers and the bar fills of the Avalonia card (Visuals.cs), as gradients in unit space reused across draws.</summary>
internal static class Paint
{
    public static void Rgb(Cairo.Context cr, uint rgb, double a = 1) =>
        cr.SetSourceRgba(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0, a);

    public static void Argb(Cairo.Context cr, uint argb) =>
        cr.SetSourceRgba(((argb >> 16) & 0xFF) / 255.0, ((argb >> 8) & 0xFF) / 255.0, (argb & 0xFF) / 255.0, (argb >> 24) / 255.0);

    /// <summary>A rounded rectangle path (radii per corner: top-left, top-right, bottom-right, bottom-left), clamped to the box.</summary>
    public static void RoundRect(Cairo.Context cr, double x, double y, double w, double h, double tl, double tr, double br, double bl)
    {
        var m = Math.Min(w, h) / 2;
        tl = Math.Min(tl, m); tr = Math.Min(tr, m); br = Math.Min(br, m); bl = Math.Min(bl, m);
        cr.NewPath();
        cr.MoveTo(x + tl, y);
        cr.LineTo(x + w - tr, y);
        if (tr > 0) cr.Arc(x + w - tr, y + tr, tr, -Math.PI / 2, 0);
        cr.LineTo(x + w, y + h - br);
        if (br > 0) cr.Arc(x + w - br, y + h - br, br, 0, Math.PI / 2);
        cr.LineTo(x + bl, y + h);
        if (bl > 0) cr.Arc(x + bl, y + h - bl, bl, Math.PI / 2, Math.PI);
        cr.LineTo(x, y + tl);
        if (tl > 0) cr.Arc(x + tl, y + tl, tl, Math.PI, 1.5 * Math.PI);
        cr.ClosePath();
    }

    public static void RoundRect(Cairo.Context cr, double x, double y, double w, double h, double r) => RoundRect(cr, x, y, w, h, r, r, r, r);

    /// <summary>Fills the current path with a unit-space gradient stretched over (x, y, w, h) (cairo locks a source to the user space at set time).</summary>
    public static void FillWith(Cairo.Context cr, Cairo.Pattern unit, double x, double y, double w, double h)
    {
        cr.Save();
        cr.Translate(x, y);
        cr.Scale(Math.Max(w, 0.001), Math.Max(h, 0.001));
        cr.SetSource(unit);
        cr.Fill();
        cr.Restore();
    }

    public static Cairo.LinearGradient Vertical(params (uint Argb, double Offset)[] stops) => Stops(new Cairo.LinearGradient(0, 0, 0, 1), stops);

    public static Cairo.LinearGradient Horizontal(params (uint Argb, double Offset)[] stops) => Stops(new Cairo.LinearGradient(0, 0, 1, 0), stops);

    private static Cairo.LinearGradient Stops(Cairo.LinearGradient g, (uint Argb, double Offset)[] stops)
    {
        foreach (var (c, o) in stops)
            g.AddColorStopRgba(o, ((c >> 16) & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, (c & 0xFF) / 255.0, (c >> 24) / 255.0);
        return g;
    }

    public static readonly Cairo.LinearGradient RowTrack = Vertical((0xFF2A3046, 0), (0xFF20253A, 1));
    public static readonly Cairo.LinearGradient RowGloss = Vertical((0x22FFFFFF, 0), (0x05FFFFFF, 1));
    public static readonly Cairo.LinearGradient HpFill = Vertical((0xFFF67A6C, 0), (0xFFE0473F, 0.5), (0xFFB8202A, 1));

    private static readonly Cairo.LinearGradient?[] Fills = new Cairo.LinearGradient?[256];

    /// <summary>Full-height row fill: saturated class colour easing off towards the end of the bar (Visuals.FillOf).</summary>
    public static Cairo.LinearGradient FillOf(GameClass c)
    {
        if (Fills[(byte)c] is { } g) return g;
        var v = OverlayPalette.BarColor(c);
        uint r = (v >> 16) & 0xFF, gr = (v >> 8) & 0xFF, b = v & 0xFF;
        uint Mix(uint ch) => (uint)(ch + (255 - ch) * 0.08);
        var start = 0xFF000000 | (Mix(r) << 16) | (Mix(gr) << 8) | Mix(b);
        var end = 0xC8000000 | ((uint)(r * 0.72) << 16) | ((uint)(gr * 0.72) << 8) | (uint)(b * 0.72);
        g = Horizontal((start, 0), (0xFF000000 | v, 0.55), (end, 1));
        Fills[(byte)c] = g;
        return g;
    }
}
