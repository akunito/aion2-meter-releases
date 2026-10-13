using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>The display-free maths of the fight charts (smoothing, axis scale, ticks, rotation tile layout, skill colours), tested in FightTabTests.</summary>
internal static class ChartMath
{
    /// <summary>Moving average over the last <paramref name="window"/> samples (fewer at the start) into <paramref name="into"/> (grown when short).</summary>
    public static int Smooth(IReadOnlyList<long> data, int window, ref double[] into)
    {
        var n = data.Count;
        if (into.Length < n) into = new double[Math.Max(n, into.Length * 2)];
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            sum += data[i];
            if (i >= window) sum -= data[i - window];
            into[i] = sum / Math.Min(window, i + 1);
        }
        return n;
    }

    /// <summary>The axis top: the smallest of 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 × 10^n at or above <paramref name="v"/> (values ≤ 1 give 1).</summary>
    public static double NiceCeiling(double v)
    {
        if (!(v > 1)) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        ReadOnlySpan<double> steps = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10];
        foreach (var m in steps)
            if (m * exp >= v) return m * exp;
        return 10 * exp;
    }

    /// <summary>Seconds between time labels for a fight of <paramref name="seconds"/>.</summary>
    public static int TimeStep(int seconds) => seconds switch { <= 30 => 5, <= 90 => 15, <= 300 => 30, <= 900 => 60, _ => 300 };

    /// <summary>X of sample <paramref name="i"/> of a <paramref name="seconds"/>-long axis (≥ 2) over [left, left+width].</summary>
    public static double X(int i, int seconds, double left, double width) => left + width * i / (seconds - 1.0);

    /// <summary>Y of value <paramref name="v"/> on a 0..<paramref name="max"/> axis, clamped to the top.</summary>
    public static double Y(double v, double max, double bottom, double height) => bottom - height * Math.Min(1, v / max);

    /// <summary>Rotation tile side: the smallest tile plus the square root of the hit against the biggest one.</summary>
    public static double TileSize(long damage, long max, double min, double maxSize) => min + (maxSize - min) * Math.Sqrt((double)Math.Max(0, damage) / Math.Max(1, max));

    /// <summary>
    /// Flows tiles left to right into rows of <paramref name="rowStep"/>, bottom-aligned on a <paramref name="maxTile"/> line; each advances by
    /// max(size, <paramref name="minAdvance"/>) + gap (room for the damage label). Writes x, y (top of the tile) and row; returns the row count.
    /// </summary>
    public static int Layout(ReadOnlySpan<double> sizes, double width, double maxTile, double minAdvance, double gap, double rowStep, Span<double> xs, Span<double> ys, Span<int> rows)
    {
        double x = 0;
        var row = 0;
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            if (x + size > width && x > 0) { x = 0; row++; }
            xs[i] = x;
            ys[i] = row * rowStep + (maxTile - size);
            rows[i] = row;
            x += Math.Max(size, minAdvance) + gap;
        }
        return sizes.Length == 0 ? 0 : row + 1;
    }

    /// <summary>Two letters for a skill tile without an icon: "Flame Strike" → "FS", "Bolt" → "Bo".</summary>
    public static string Initials(string name)
    {
        var parts = name.Split([' ', ':', '-', '\''], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Length >= 2 ? parts[0][..2] : parts[0];
        return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
    }

    /// <summary>Stable, well-spread hue per skill code (WPF RotationStrip.SkillColor), 0xRRGGBB.</summary>
    public static uint SkillColor(int code)
    {
        var hue = (uint)(code * 2654435761u) % 360;
        return FromHsl(hue, 0.55, 0.45);
    }

    public static uint FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return ((uint)(byte)((r + m) * 255) << 16) | ((uint)(byte)((g + m) * 255) << 8) | (byte)((b + m) * 255);
    }

    /// <summary>Linear mix of two RGB colours (Visuals.Mix).</summary>
    public static uint Mix(uint a, uint b, double t)
    {
        uint Ch(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
        return (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }
}

/// <summary>
/// WPF TimelineChart on cairo: per-second damage smoothed over 3 s; the selected player as a filled area, the party as thin lines behind it.
/// Fill it with <see cref="Begin"/>, <see cref="Add"/>…, <see cref="End"/> (buffers reused between fights; one redraw per update).
/// </summary>
internal sealed class TimelineChart
{
    private sealed class Line { public double[] V = new double[64]; public int N; public uint Rgb; public bool Primary; }

    private readonly List<Line> _lines = new();
    private int _count, _seconds = 2;
    private double _max = 1;
    private static readonly Dictionary<uint, Cairo.LinearGradient> Fills = new();
    public Gtk.DrawingArea Widget { get; }

    public TimelineChart(double height)
    {
        Widget = Gtk.DrawingArea.New();
        Widget.SetSizeRequest(-1, UiKit.Px(height));
        Widget.Hexpand = true;
        Widget.SetDrawFunc(Draw);
    }

    public void Begin() { _count = 0; _seconds = 2; _max = 1; }

    public void Add(IReadOnlyList<long> perSecond, uint rgb, bool primary)
    {
        if (_count == _lines.Count) _lines.Add(new Line());
        var l = _lines[_count++];
        l.N = ChartMath.Smooth(perSecond, 3, ref l.V);
        l.Rgb = rgb;
        l.Primary = primary;
        if (l.N > _seconds) _seconds = l.N;
        for (var i = 0; i < l.N; i++) if (l.V[i] > _max) _max = l.V[i];
    }

    public void End()
    {
        _max = ChartMath.NiceCeiling(_max);
        Widget.QueueDraw();
    }

    private void Draw(Gtk.DrawingArea area, Cairo.Context cr, int w, int h)
    {
        var k = Icons.Scale;
        if (w < 80 * k || h < 50 * k) return;
        double left = 44 * k, top = 10 * k, right = w - 8 * k, bottom = h - 20 * k;
        double pw = right - left, ph = bottom - top;
        var layout = FightKit.Layout(cr);
        var font = FightKit.Font(10 * k);
        cr.LineWidth = 1;
        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(bottom - ph * i / 4) + 0.5;
            Paint.Argb(cr, 0x22FFFFFF);
            cr.MoveTo(left, y);
            cr.LineTo(right, y);
            cr.Stroke();
            var (tw, th) = FightKit.Measure(layout, Format.Compact(_max * i / 4), font);
            FightKit.Show(cr, layout, left - tw - 6 * k, y - th / 2.0, Palette.TextMute);
        }
        var step = ChartMath.TimeStep(_seconds);
        for (var s = 0; s <= _seconds; s += step)
        {
            var x = ChartMath.X(s, _seconds, left, pw);
            if (x > right + 0.5) break;
            var (tw, _) = FightKit.Measure(layout, Format.Clock(s * 1000L), font);
            FightKit.Show(cr, layout, Math.Min(x - tw / 2.0, right - tw), bottom + 4 * k, Palette.TextMute);
        }
        // the party first, the selected player last (on top)
        for (var pass = 0; pass < 2; pass++)
            for (var li = 0; li < _count; li++)
            {
                var l = _lines[li];
                if (l.N == 0 || l.Primary != (pass == 1)) continue;
                if (l.Primary)
                {
                    Path(cr, l, left, pw, bottom, ph);
                    cr.LineTo(ChartMath.X(l.N - 1, _seconds, left, pw), bottom);
                    cr.LineTo(left, bottom);
                    cr.ClosePath();
                    Paint.FillWith(cr, FillOf(l.Rgb), left, top, pw, ph);
                    Path(cr, l, left, pw, bottom, ph);
                    Paint.Rgb(cr, l.Rgb);
                    cr.LineWidth = 2 * k;
                    cr.LineJoin = Cairo.LineJoin.Round;
                    cr.Stroke();
                }
                else
                {
                    Path(cr, l, left, pw, bottom, ph);
                    Paint.Rgb(cr, l.Rgb, 0x80 / 255.0);
                    cr.LineWidth = 1.2 * k;
                    cr.Stroke();
                }
            }
    }

    private void Path(Cairo.Context cr, Line l, double left, double pw, double bottom, double ph)
    {
        cr.NewPath();
        cr.MoveTo(ChartMath.X(0, _seconds, left, pw), ChartMath.Y(l.V[0], _max, bottom, ph));
        for (var i = 1; i < l.N; i++) cr.LineTo(ChartMath.X(i, _seconds, left, pw), ChartMath.Y(l.V[i], _max, bottom, ph));
    }

    private static Cairo.LinearGradient FillOf(uint rgb)
    {
        if (Fills.TryGetValue(rgb, out var g)) return g;
        g = Paint.Vertical((0x70000000 | rgb, 0), (0x05000000 | rgb, 1));
        Fills[rgb] = g;
        return g;
    }
}

/// <summary>
/// WPF RotationStrip on cairo: every cast in order as tiles that wrap; size follows the hit's damage against the biggest one, a gold ring
/// marks crits; the tooltip follows the tile under the pointer. Rows are split over several DrawingAreas of <see cref="RowsPerArea"/> rows:
/// one area per strip made a cairo surface as tall as the whole fight (1500 tiles ≈ 9000 px, past the GL texture limit at scale 2).
/// </summary>
internal sealed class RotationStrip
{
    private const double MinTile = 22, MaxTile = 40, Gap = 6, LabelHeight = 13, MinAdvance = 34;
    private const int MaxTiles = 1500, RowsPerArea = 12;
    private readonly IconProvider _icons;
    private readonly List<CastRecord> _casts = new();
    private double[] _size = new double[64], _x = new double[64], _y = new double[64];
    private int[] _row = new int[64];
    private readonly List<Gtk.DrawingArea> _areas = new();
    private readonly Dictionary<int, string> _initials = new();
    private Func<int, string> _name = c => c.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private long _max = 1;
    private int _rows, _laidWidth = -1, _width;
    private uint _posted;
    public Gtk.Box Widget { get; }

    public RotationStrip(IconProvider icons)
    {
        _icons = icons;
        Widget = UiKit.VBox();
        Widget.Hexpand = true;
        // width source while there are no rows yet
        var probe = Gtk.DrawingArea.New();
        probe.Hexpand = true;
        probe.SetSizeRequest(-1, 0);
        probe.OnResize += (_, e) => Resized(e.Width);
        Widget.Append(probe);
    }

    private double K => Icons.Scale;
    private double RowStep => (MaxTile + LabelHeight + Gap) * K;

    public void SetData(IReadOnlyList<CastRecord> casts, Func<int, string> name)
    {
        _casts.Clear();
        foreach (var c in casts)
        {
            if ((c.Flags & HitFlags.Dot) != 0) continue;
            _casts.Add(c);
            if (_casts.Count >= MaxTiles) break;
        }
        _name = name;
        _initials.Clear();
        _max = 1;
        foreach (var c in _casts) if (c.Damage > _max) _max = c.Damage;
        var n = _casts.Count;
        if (_size.Length < n)
        {
            var len = Math.Max(n, _size.Length * 2);
            _size = new double[len]; _x = new double[len]; _y = new double[len]; _row = new int[len];
        }
        for (var i = 0; i < n; i++) _size[i] = ChartMath.TileSize(_casts[i].Damage, _max, MinTile * K, MaxTile * K);
        _laidWidth = -1;
        Relayout();
    }

    /// <summary>Icons arrived: redraw (tiles without one ask again on draw).</summary>
    public void Redraw() { foreach (var a in _areas) a.QueueDraw(); }

    private void Resized(int width)
    {
        if (width <= 0 || width == _width) return;
        _width = width;
        // never re-layout inside an allocation: next loop turn
        if (_posted == 0) _posted = UiLoop.Delay(0, () => { _posted = 0; Relayout(); });
    }

    private void Relayout()
    {
        var width = _width > 0 ? _width : UiKit.Px(450);
        if (width == _laidWidth) { Redraw(); return; }
        _laidWidth = width;
        var n = _casts.Count;
        _rows = ChartMath.Layout(_size.AsSpan(0, n), width, MaxTile * K, MinAdvance * K, Gap * K, RowStep, _x, _y, _row);
        var areas = Math.Max(1, (_rows + RowsPerArea - 1) / RowsPerArea);
        while (_areas.Count < areas)
        {
            var index = _areas.Count;
            var a = Gtk.DrawingArea.New();
            a.Hexpand = true;
            a.SetDrawFunc((_, cr, w, h) => Draw(cr, index, w));
            a.HasTooltip = true;
            a.OnQueryTooltip += (_, e) => Tooltip(index, e.X, e.Y, e.Tooltip);
            a.OnResize += (_, e) => Resized(e.Width);
            _areas.Add(a);
            Widget.Append(a);
        }
        for (var i = 0; i < _areas.Count; i++)
        {
            var a = _areas[i];
            a.Visible = i < areas;
            if (i >= areas) continue;
            var rowsHere = Math.Min(RowsPerArea, Math.Max(0, _rows - i * RowsPerArea));
            var height = n == 0 ? 40 * K : rowsHere * RowStep - Gap * K + 4 * K;
            a.SetSizeRequest(-1, (int)Math.Ceiling(height));
            a.QueueDraw();
        }
    }

    private (int First, int End) Range(int area)
    {
        int lo = area * RowsPerArea, hi = lo + RowsPerArea;
        int first = -1, end = 0;
        for (var i = 0; i < _casts.Count; i++)
        {
            if (_row[i] < lo) continue;
            if (_row[i] >= hi) break;
            if (first < 0) first = i;
            end = i + 1;
        }
        return (Math.Max(0, first), first < 0 ? 0 : end);
    }

    private void Draw(Cairo.Context cr, int area, int w)
    {
        var k = K;
        var (first, end) = Range(area);
        if (end <= first) return;
        var oy = area * RowsPerArea * RowStep;
        var layout = FightKit.Layout(cr);
        var dmgFont = FightKit.Font(9.5 * k);
        var r = 6 * k;
        for (var i = first; i < end; i++)
        {
            double x = _x[i], y = _y[i] - oy, s = _size[i];
            var cast = _casts[i];
            var crit = (cast.Flags & HitFlags.Critical) != 0;
            if (_icons.Skill(cast.SkillCode) is { } tex) FightKit.DrawTexture(cr, tex, x, y, s, s, r);
            else
            {
                var color = ChartMath.SkillColor(cast.SkillCode);
                Paint.RoundRect(cr, x, y, s, s, r);
                Paint.FillWith(cr, GradientOf(color), x, y, s, s);
                if (!_initials.TryGetValue(cast.SkillCode, out var ini)) _initials[cast.SkillCode] = ini = ChartMath.Initials(_name(cast.SkillCode));
                var (tw, th) = FightKit.Measure(layout, ini, FightKit.Font(s * 0.36, true));
                FightKit.Show(cr, layout, x + (s - tw) / 2.0, y + (s - th) / 2.0, 0xFFFFFF);
            }
            Paint.RoundRect(cr, x + 0.5, y + 0.5, s - 1, s - 1, r);
            if (crit) { Paint.Rgb(cr, Palette.GoldBright); cr.LineWidth = 2 * k; }
            else { Paint.Argb(cr, 0x50000000); cr.LineWidth = 1; }
            cr.Stroke();
            var (dw, _) = FightKit.Measure(layout, Format.Compact(cast.Damage), dmgFont);
            FightKit.Show(cr, layout, x + (s - dw) / 2.0, y + s + 2 * k, Palette.TextDim);
        }
    }

    private static readonly Dictionary<uint, Cairo.LinearGradient> Gradients = new();

    private static Cairo.LinearGradient GradientOf(uint rgb)
    {
        if (Gradients.TryGetValue(rgb, out var g)) return g;
        g = Paint.Vertical((0xFF000000 | ChartMath.Mix(rgb, 0xFFFFFF, 0.25), 0), (0xFF000000 | rgb, 1));
        Gradients[rgb] = g;
        return g;
    }

    private bool Tooltip(int area, double px, double py, Gtk.Tooltip tip)
    {
        var (first, end) = Range(area);
        var oy = area * RowsPerArea * RowStep;
        for (var i = first; i < end; i++)
        {
            double x = _x[i], y = _y[i] - oy, s = _size[i];
            if (px < x || px > x + s || py < y || py > y + s) continue;
            var c = _casts[i];
            var flags = c.Flags == HitFlags.None ? "" : " · " + UiText.Current.FlagsText(c.Flags);
            tip.SetText($"{Format.ClockPrecise(c.OffsetMs)}  {_name(c.SkillCode)}\n{Format.Grouped(c.Damage)}{flags}");
            return true;
        }
        return false;
    }
}
