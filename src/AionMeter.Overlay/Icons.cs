using System.Globalization;

namespace AionMeter.Overlay;

/// <summary>
/// The Avalonia card's stroked 16×16 icons (stand-ins for Segoe Fluent), drawn with cairo so they stay sharp at any output scale
/// (a rasterised SVG would need librsvg's gdk-pixbuf loader on the host). Absolute M/L/C/A/Z paths only, parsed once.
/// </summary>
internal static class Icons
{
    public static readonly Icon Meter = new("M3 14 V9 M8 14 V4 M13 14 V1.5");
    public static readonly Icon Atlas = new("M1.5 3.5 L5.5 1.5 L10.5 3.5 L14.5 1.5 L14.5 12.5 L10.5 14.5 L5.5 12.5 L1.5 14.5 Z M5.5 1.5 L5.5 12.5 M10.5 3.5 L10.5 14.5");
    public static readonly Icon Top = new("M4.5 1.5 L11.5 1.5 L11.5 5 C11.5 9 4.5 9 4.5 5 Z M8 8.5 L8 12 M5 14 L11 14 M4.5 3 L1.5 3 C1.5 5.5 3 6.5 4.8 6.5 M11.5 3 L14.5 3 C14.5 5.5 13 6.5 11.2 6.5");
    public static readonly Icon Prices = new("M1.5 1.5 L7.5 1.5 L14.5 8.5 L8.5 14.5 L1.5 7.5 Z M4.5 4.2 L5 4.2");
    public static readonly Icon Craft = new("M2 14 L9 7 M7 3.5 L10 1.5 L14.5 6 L12.5 9 Z");
    public static readonly Icon Build = new("M14.5 1.5 L6.5 9.5 M4 7.5 L8.5 12 M2 14 L5 11 M14.5 1.5 L14.5 4.5 M14.5 1.5 L11.5 1.5");
    public static readonly Icon Items = new("M2.5 5.5 L13.5 5.5 L12.5 14.5 L3.5 14.5 Z M5.5 5.5 L5.5 4 C5.5 0.8 10.5 0.8 10.5 4 L10.5 5.5");
    public static readonly Icon Clock = new("M8 1.5 A6.5 6.5 0 1 1 7.99 1.5 Z M8 4.5 L8 8 L10.5 10");
    public static readonly Icon Stopwatch = new("M8 3.5 A5.5 5.5 0 1 1 7.99 3.5 Z M8 6 L8 9.2 M6 1 L10 1");
    public static readonly Icon Reset = new("M13.5 8 A5.5 5.5 0 1 1 11.9 4.1 M13.5 1.5 L13.5 5 L10 5");
    public static readonly Icon Pointer = new("M3.5 2 L12.5 7 L8.5 8 L7 12 Z M1.5 14.5 L14.5 1.5");
    public static readonly Icon Hide = new("M1.5 8 C4 3.5 12 3.5 14.5 8 C12 12.5 4 12.5 1.5 8 Z M8 6 A2 2 0 1 1 7.99 6 Z M2 14 L14 2");
    public static readonly Icon Chevron = new("M3 6 L8 11 L13 6");
    public static readonly Icon Download = new("M8 2 L8 11 M4 7 L8 11 L12 7 M2.5 14 L13.5 14");
    public static readonly Icon Grip = new("M12 2 L2 12 M12 6 L6 12 M12 10 L10 12");
    // A2M-50: the tab host and the kit (Avalonia Visuals.Icon*)
    public static readonly Icon Gear = new("M8 5.5 A2.5 2.5 0 1 1 7.99 5.5 Z M8 1 L8 3 M8 13 L8 15 M1 8 L3 8 M13 8 L15 8 M3 3 L4.4 4.4 M11.6 11.6 L13 13 M3 13 L4.4 11.6 M11.6 4.4 L13 3");
    public static readonly Icon Help = new("M8 1.5 A6.5 6.5 0 1 1 7.99 1.5 Z M6 6.2 C6 3.8 10 3.8 10 6.2 C10 7.8 8 7.8 8 9.6 M8 11.8 L8 12.2");
    public static readonly Icon Close = new("M3.5 3.5 L12.5 12.5 M12.5 3.5 L3.5 12.5");
    public static readonly Icon Back = new("M10 3 L5 8 L10 13");
    public static readonly Icon Fight = new("M1.5 14.5 L1.5 1.5 M1.5 14.5 L14.5 14.5 M4 11 L7 7 L9.5 9 L14 3.5");
    public static readonly Icon PopOut = new("M9 1.5 L14.5 1.5 L14.5 7 M14.5 1.5 L7.5 8.5 M12 10 L12 14.5 L1.5 14.5 L1.5 4 L6 4");
    public static readonly Icon Unknown = new("M8 1.5 A6.5 6.5 0 1 1 7.99 1.5 Z M6.3 6.3 C6.3 4.3 9.7 4.3 9.7 6.3 C9.7 7.7 8 7.7 8 9.4 M8 11.4 L8 11.7");

    /// <summary>The strip pill's icon for a UiTabs.Strip key (and the "fight" / "settings" pills of the tab host).</summary>
    public static Icon ForTab(string key) => key switch
    {
        "meter" => Meter, "atlas" => Atlas, "top" => Top, "prices" => Prices, "craft" => Craft, "build" => Build, "items" => Items, "timers" => Clock,
        "fight" => Fight, "settings" => Gear, _ => Unknown,
    };

    /// <summary>The card's scale (UI scale / --scale): fixed pixel sizes go through <see cref="Px"/>; the CSS is scaled by CardText.ScaleCss.</summary>
    public static double Scale { get; set; } = 1;

    public static int Px(double v) => (int)Math.Round(v * Scale);

    /// <summary>A DrawingArea of <paramref name="size"/> px stroking <paramref name="icon"/> in the colour <see cref="IconView.Rgb"/> holds.</summary>
    public static IconView View(Icon icon, uint rgb, int size, double thickness = 1.5) => new(icon, rgb, size, thickness);
}

internal sealed class Icon
{
    // op: 0 move, 1 line, 2 curve (6 values), 3 close; arcs are converted to curves at parse time
    private readonly byte[] _ops;
    private readonly double[] _v;

    public Icon(string d)
    {
        var ops = new List<byte>();
        var v = new List<double>();
        var t = System.Text.RegularExpressions.Regex.Matches(d, @"[A-Za-z]|-?(?:\d+\.?\d*|\.\d+)").Select(m => m.Value).ToArray();
        double cx = 0, cy = 0, sx = 0, sy = 0;
        var i = 0;
        double N() => double.Parse(t[i++], CultureInfo.InvariantCulture);
        while (i < t.Length)
        {
            var c = t[i++][0];
            switch (c)
            {
                case 'M': cx = N(); cy = N(); sx = cx; sy = cy; ops.Add(0); v.Add(cx); v.Add(cy); break;
                case 'L': cx = N(); cy = N(); ops.Add(1); v.Add(cx); v.Add(cy); break;
                case 'H': cx = N(); ops.Add(1); v.Add(cx); v.Add(cy); break;
                case 'V': cy = N(); ops.Add(1); v.Add(cx); v.Add(cy); break;
                case 'C':
                    for (var k = 0; k < 6; k++) v.Add(N());
                    ops.Add(2); cx = v[^2]; cy = v[^1]; break;
                case 'A':
                    {
                        double rx = N(), ry = N(), rot = N(), large = N(), sweep = N(), x = N(), y = N();
                        ArcToCurves(ops, v, cx, cy, rx, ry, rot, large != 0, sweep != 0, x, y);
                        cx = x; cy = y; break;
                    }
                case 'Z': ops.Add(3); cx = sx; cy = sy; break;
                default: throw new FormatException($"icon path: unsupported command {c}");
            }
        }
        _ops = ops.ToArray();
        _v = v.ToArray();
    }

    /// <summary>SVG endpoint arc → centre parametrisation (SVG 1.1 F.6.5) → cubic Béziers of ≤ 90°.</summary>
    private static void ArcToCurves(List<byte> ops, List<double> v, double x1, double y1, double rx, double ry, double rotDeg, bool large, bool sweep, double x2, double y2)
    {
        if (rx == 0 || ry == 0) { ops.Add(1); v.Add(x2); v.Add(y2); return; }
        var phi = rotDeg * Math.PI / 180;
        double cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x1 - x2) / 2, dy = (y1 - y2) / 2;
        double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1) { var s = Math.Sqrt(lambda); rx *= s; ry *= s; }
        var num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        var den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        var coef = (large != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, num / den));
        double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
        double cx = cos * cxp - sin * cyp + (x1 + x2) / 2, cy = sin * cxp + cos * cyp + (y1 + y2) / 2;
        static double Angle(double ux, double uy, double vx, double vy)
        {
            var a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        var t1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var dt = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dt > 0) dt -= 2 * Math.PI;
        else if (sweep && dt < 0) dt += 2 * Math.PI;
        var segs = (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2));
        var step = dt / segs;
        var k = 4.0 / 3 * Math.Tan(step / 4);
        for (var s = 0; s < segs; s++)
        {
            double a1 = t1 + s * step, a2 = a1 + step;
            double c1 = Math.Cos(a1), s1 = Math.Sin(a1), c2 = Math.Cos(a2), s2 = Math.Sin(a2);
            (double X, double Y) P(double px, double py) => (cos * rx * px - sin * ry * py + cx, sin * rx * px + cos * ry * py + cy);
            var p1 = P(c1 - k * s1, s1 + k * c1);
            var p2 = P(c2 + k * s2, s2 - k * c2);
            var p3 = P(c2, s2);
            ops.Add(2); v.Add(p1.X); v.Add(p1.Y); v.Add(p2.X); v.Add(p2.Y); v.Add(p3.X); v.Add(p3.Y);
        }
    }

    /// <summary>Strokes the icon fitted into a <paramref name="size"/> square at (x, y); the line width is in pixels, not icon units.</summary>
    public void Stroke(Cairo.Context cr, double x, double y, double size, double thickness)
    {
        var k = size / 16.0;
        cr.NewPath();
        var j = 0;
        for (var i = 0; i < _ops.Length; i++)
        {
            switch (_ops[i])
            {
                case 0: cr.MoveTo(x + _v[j] * k, y + _v[j + 1] * k); j += 2; break;
                case 1: cr.LineTo(x + _v[j] * k, y + _v[j + 1] * k); j += 2; break;
                case 2: cr.CurveTo(x + _v[j] * k, y + _v[j + 1] * k, x + _v[j + 2] * k, y + _v[j + 3] * k, x + _v[j + 4] * k, y + _v[j + 5] * k); j += 6; break;
                default: cr.ClosePath(); break;
            }
        }
        cr.LineWidth = thickness;
        cr.LineCap = Cairo.LineCap.Round;
        cr.LineJoin = Cairo.LineJoin.Round;
        cr.Stroke();
    }
}

/// <summary>A fixed-size DrawingArea showing one icon; <see cref="Rgb"/> changes the stroke colour (redraws only on change).</summary>
internal sealed class IconView
{
    private readonly Icon _icon;
    private readonly double _thickness;
    private uint _rgb;
    public Gtk.DrawingArea Widget { get; }

    public IconView(Icon icon, uint rgb, int size, double thickness)
    {
        _icon = icon;
        _rgb = rgb;
        _thickness = thickness;
        Widget = Gtk.DrawingArea.New();
        Widget.SetSizeRequest(Icons.Px(size), Icons.Px(size));
        Widget.Valign = Gtk.Align.Center;
        Widget.Halign = Gtk.Align.Center;
        Widget.CanTarget = false;
        Widget.SetDrawFunc((_, cr, w, h) =>
        {
            Paint.Rgb(cr, _rgb);
            var s = Math.Min(w, h);
            _icon.Stroke(cr, (w - s) / 2.0, (h - s) / 2.0, s, _thickness);
        });
    }

    public uint Rgb
    {
        get => _rgb;
        set { if (_rgb == value) return; _rgb = value; Widget.QueueDraw(); }
    }
}
