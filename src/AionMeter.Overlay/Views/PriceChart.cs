using System.Globalization;
using AionMeter.Core.Market;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Lowest-listing line over the chosen range (expanded Prices row; the Avalonia PriceChart on one cairo DrawingArea, A2M-50): area + line,
/// faint min/median/max grid with labels, the buy/sell threshold dashed, and on hover the nearest sample's time and price. Samples arrive
/// already reduced to ≤ 200 points (PriceHistory.Downsample). Every string is formatted in <see cref="SetData"/> / on a hover change, so a
/// redraw allocates nothing of its own; it redraws only on new data, a resize or a hover that moves to another sample.
/// </summary>
internal sealed class PriceChart
{
    private const double PadLeft = 74, PadRight = 10, PadTop = 12, PadBottom = 20;
    private const uint LineRgb = 0xECCB82, MuteRgb = 0x6B7590, TextRgb = 0xF1E9D8, BuyRgb = 0x8EE08A, SellRgb = 0xF5B544;
    private const uint TipBg = 0xEE111726, Plate = 0x0AFFFFFF, Grid = 0x22FFFFFF, HoverLine = 0x66F1E9D8, TipLine = 0x6E5A33;
    private static readonly double[] Dash = [5, 4];
    private static readonly double[] NoDash = [];
    // the Avalonia area brush: gold 0x55 at the top of the series → 0x04 at the plot bottom, in unit space (Paint.FillWith stretches it)
    private static readonly Cairo.LinearGradient Area = Paint.Vertical((0x55C9A45C, 0), (0x04C9A45C, 1));

    private PriceSample[] _s = [];
    private long _from, _to = 1, _threshold, _min, _median, _max;
    private bool _sell;
    private string _empty = "", _threshLabel = "", _threshAbove = "", _threshBelow = "", _hoverText = "";
    private readonly string[] _markText = new string[3];
    private long[] _ticks = [];
    private string[] _tickText = [];
    private int _tickCount;
    private double[] _xs = [];
    private int _xsCount, _hover = -1;
    private Pango.Layout? _layout;
    private readonly Pango.FontDescription _f9, _f10, _f105, _f11, _f12;

    public Gtk.DrawingArea Widget { get; }

    public PriceChart(double height)
    {
        Widget = Gtk.DrawingArea.New();
        Widget.SetSizeRequest(-1, UiKit.Px(height));
        Widget.Hexpand = true;
        var s = Icons.Scale;
        Pango.FontDescription Font(double px) => Pango.FontDescription.FromString(
            "Inter, Noto Sans, DejaVu Sans " + (px * s).ToString("0.##", CultureInfo.InvariantCulture) + "px");
        _f9 = Font(9); _f10 = Font(10); _f105 = Font(10.5); _f11 = Font(11); _f12 = Font(12);
        Widget.SetDrawFunc((_, cr, w, h) => Draw(cr, w, h));
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) => Hover(e.X);
        motion.OnLeave += (_, _) => { if (_hover >= 0) { _hover = -1; Widget.QueueDraw(); } };
        Widget.AddController(motion);
    }

    public void SetData(PriceSample[] reduced, (long Min, long Median, long Max) stats, long fromTs, long toTs, PriceRange range, long threshold, bool sell, string empty, string buyLabel, string sellLabel)
    {
        _s = reduced; (_min, _median, _max) = stats; _from = fromTs; _to = Math.Max(toTs, fromTs + 1);
        _threshold = threshold; _sell = sell; _empty = empty; _hover = -1; _xsCount = 0;
        _markText[0] = N(_max); _markText[1] = N(_median); _markText[2] = N(_min);
        _threshLabel = threshold > 0 ? (sell ? sellLabel : buyLabel) + " " + N(threshold) : "";
        _threshAbove = "▲ " + _threshLabel;
        _threshBelow = "▼ " + _threshLabel;
        var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(_to)).TotalSeconds;
        var step = PriceChartScale.TickStep(range);
        var n = PriceChartScale.TickCount(_from, _to, step, offset);
        if (_ticks.Length < n) { _ticks = new long[n]; _tickText = new string[n]; }
        var fmt = range == PriceRange.Day ? "HH:mm" : "d MMM";
        var culture = UiText.Current.Culture;
        var ts = PriceChartScale.FirstTick(_from, step, offset);
        for (var i = 0; i < n; i++, ts += step)
        {
            _ticks[i] = ts;
            _tickText[i] = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString(fmt, culture);
        }
        _tickCount = n;
        Widget.QueueDraw();
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private void Hover(double x)
    {
        var best = PriceChartScale.Nearest(_xs, _xsCount, x);
        if (best == _hover) return;
        _hover = best;
        if (best >= 0)
        {
            var s = _s[best];
            var when = DateTimeOffset.FromUnixTimeSeconds(s.Ts).ToLocalTime().ToString("ddd d MMM HH:mm", UiText.Current.Culture);
            _hoverText = $"{when}  ·  {N(s.Price)}" + (s.Listings > 0 ? $"  ·  {s.Listings}×" : "");
        }
        Widget.QueueDraw();
    }

    private void Text(Cairo.Context cr, string text, Pango.FontDescription font, out int tw, out int th)
    {
        _layout ??= Widget.CreatePangoLayout(null);
        _layout.SetFontDescription(font);
        _layout.SetText(text, -1);
        _layout.GetPixelSize(out tw, out th);
    }

    private void Show(Cairo.Context cr, double x, double y, uint rgb)
    {
        Paint.Rgb(cr, rgb);
        cr.MoveTo(x, y);
        PangoCairo.Functions.ShowLayout(cr, _layout!);
    }

    private static void HLine(Cairo.Context cr, double x0, double x1, double y)
    {
        cr.MoveTo(x0, y);
        cr.LineTo(x1, y);
    }

    private void Draw(Cairo.Context cr, int w, int h)
    {
        Paint.RoundRect(cr, 0, 0, w, h, UiKit.Px(6));
        Paint.Argb(cr, Plate);
        cr.Fill();
        if (w < 120 || h < 60) return;
        var k = Icons.Scale;
        double left = PadLeft * k, top = PadTop * k, right = w - PadRight * k, bottom = h - PadBottom * k;
        double pw = right - left, ph = bottom - top;
        cr.LineWidth = 1;

        DrawTimeAxis(cr, left, right, bottom, k);
        if (_s.Length == 0)
        {
            _xsCount = 0;
            _layout ??= Widget.CreatePangoLayout(null);
            _layout.SetWidth((int)(Math.Max(10, pw - 20 * k) * 1024));   // PANGO_SCALE
            _layout.SetWrap(Pango.WrapMode.WordChar);
            _layout.SetAlignment(Pango.Alignment.Center);
            Text(cr, _empty, _f12, out var ew, out var eh);
            Show(cr, left + (pw - Math.Max(10, pw - 20 * k)) / 2, top + (ph - eh) / 2, MuteRgb);
            _layout.SetWidth(-1);
            _layout.SetAlignment(Pango.Alignment.Left);
            return;
        }

        var (lo, hi, thrIn) = PriceChartScale.YRange(_min, _max, _threshold);
        double Y(double v) => PriceChartScale.Y(v, lo, hi, top, bottom);

        // min / median / max: faint lines, full-precision labels on the left (one label when they fall on top of each other)
        var lastLabelY = double.NaN;
        for (var i = 0; i < 3; i++)
        {
            var v = i == 0 ? _max : i == 1 ? _median : _min;
            var y = Y(v);
            HLine(cr, left, right, Math.Round(y) + 0.5);
            Paint.Argb(cr, Grid);
            cr.Stroke();
            if (!double.IsNaN(lastLabelY) && Math.Abs(y - lastLabelY) < 12 * k) continue;
            Text(cr, _markText[i], _f105, out var tw, out var th);
            Show(cr, left - tw - 6 * k, y - th / 2.0, MuteRgb);
            // the tag goes when it would leave the plate (a 7-digit median in Noto Sans, Inter missing on DESK, cut "med" to "ned")
            Text(cr, i == 0 ? "max" : i == 1 ? "med" : "min", _f9, out var gw, out var gh);
            if (left - tw - gw - 10 * k >= 2) Show(cr, left - tw - gw - 10 * k, y - gh / 2.0 + 0.5, MuteRgb);
            lastLabelY = y;
        }

        // line + area
        if (_xs.Length < _s.Length) _xs = new double[_s.Length];
        var span = (double)(_to - _from);
        var dataTop = bottom;
        for (var i = 0; i < _s.Length; i++)
        {
            _xs[i] = left + (_s[i].Ts - _from) / span * pw;
            var y = Y(_s[i].Price);
            if (y < dataTop) dataTop = y;
        }
        _xsCount = _s.Length;
        Paint.Rgb(cr, LineRgb);
        if (_s.Length == 1)
        {
            cr.Arc(_xs[0], Y(_s[0].Price), 3 * k, 0, 2 * Math.PI);
            cr.Fill();
        }
        else
        {
            cr.NewPath();
            cr.MoveTo(_xs[0], bottom);
            for (var i = 0; i < _s.Length; i++) cr.LineTo(_xs[i], Y(_s[i].Price));
            cr.LineTo(_xs[_s.Length - 1], bottom);
            cr.ClosePath();
            Paint.FillWith(cr, Area, left, dataTop, pw, bottom - dataTop);
            cr.MoveTo(_xs[0], Y(_s[0].Price));
            for (var i = 1; i < _s.Length; i++) cr.LineTo(_xs[i], Y(_s[i].Price));
            cr.LineWidth = 1.6 * k;
            cr.LineJoin = Cairo.LineJoin.Round;
            Paint.Rgb(cr, LineRgb);
            cr.Stroke();
            cr.LineWidth = 1;
        }

        // threshold
        if (_threshold > 0)
        {
            var rgb = _sell ? SellRgb : BuyRgb;
            if (thrIn)
            {
                var y = Y(_threshold);
                HLine(cr, left, right, y);
                cr.LineWidth = 1.2 * k;
                cr.SetDash(Dash, 0);
                Paint.Rgb(cr, rgb);
                cr.Stroke();
                cr.SetDash(NoDash, 0);
                cr.LineWidth = 1;
                Text(cr, _threshLabel, _f10, out var tw, out var th);
                double ax = right - tw - 6 * k, ay = y < top + th + 4 * k ? y + 3 * k : y - th - 3 * k;
                Paint.RoundRect(cr, ax - 4 * k, ay - 1, tw + 8 * k, th + 2, 3 * k);   // readable over the line
                Paint.Argb(cr, TipBg);
                cr.Fill();
                Show(cr, ax, ay, rgb);
            }
            else
            {
                var above = _threshold > hi;
                Text(cr, above ? _threshAbove : _threshBelow, _f10, out var tw, out var th);
                Show(cr, right - tw - 4 * k, above ? top : bottom - th - 2 * k, rgb);
            }
        }

        if (_hover >= 0 && _hover < _xsCount)
        {
            double px = _xs[_hover], py = Y(_s[_hover].Price);
            cr.MoveTo(Math.Round(px) + 0.5, top);
            cr.LineTo(Math.Round(px) + 0.5, bottom);
            Paint.Argb(cr, HoverLine);
            cr.Stroke();
            cr.NewPath();
            cr.Arc(px, py, 3.5 * k, 0, 2 * Math.PI);
            Paint.Rgb(cr, LineRgb);
            cr.Fill();
            Text(cr, _hoverText, _f11, out var tw, out var th);
            var bx = px + 10 * k + tw + 12 * k > w ? px - tw - 22 * k : px + 10 * k;
            var by = Math.Max(top, Math.Min(py - th - 14 * k, bottom - th - 8 * k));
            Paint.RoundRect(cr, bx + 0.5, by + 0.5, tw + 12 * k, th + 6 * k, 4 * k);
            Paint.Argb(cr, TipBg);
            cr.FillPreserve();
            Paint.Rgb(cr, TipLine);
            cr.Stroke();
            Show(cr, bx + 6 * k, by + 3 * k, TextRgb);
        }
    }

    private void DrawTimeAxis(Cairo.Context cr, double left, double right, double bottom, double k)
    {
        var lastRight = double.MinValue;
        var pw = right - left;
        var span = (double)(_to - _from);
        for (var i = 0; i < _tickCount; i++)
        {
            var x = Math.Round(left + (_ticks[i] - _from) / span * pw) + 0.5;
            cr.MoveTo(x, bottom);
            cr.LineTo(x, bottom + 3 * k);
            Paint.Argb(cr, Grid);
            cr.Stroke();
            Text(cr, _tickText[i], _f10, out var tw, out _);
            var lx = Math.Min(x - tw / 2.0, right - tw);
            if (lx < lastRight + 6 * k) continue;
            Show(cr, lx, bottom + 4 * k, MuteRgb);
            lastRight = lx + tw;
        }
        HLine(cr, left, right, Math.Round(bottom) + 0.5);
        Paint.Argb(cr, Grid);
        cr.Stroke();
    }
}

/// <summary>The chart's scaling and axis maths, display-free (tests run without GTK).</summary>
internal static class PriceChartScale
{
    /// <summary>The y range: the data, plus the threshold when it is near enough not to flatten the line; never empty; 8 % air above and below.</summary>
    public static (double Lo, double Hi, bool ThresholdIn) YRange(long min, long max, long threshold)
    {
        double lo = min, hi = max;
        var span = Math.Max(1, hi - lo);
        var thrIn = threshold > 0 && threshold >= lo - span * 0.6 && threshold <= hi + span * 0.6;
        if (thrIn) { lo = Math.Min(lo, threshold); hi = Math.Max(hi, threshold); }
        if (hi - lo < 1) { var pad = Math.Max(1, hi * 0.05); lo -= pad; hi += pad; }
        var m = (hi - lo) * 0.08;
        return (lo - m, hi + m, thrIn);
    }

    public static double Y(double v, double lo, double hi, double top, double bottom) => bottom - (v - lo) / (hi - lo) * (bottom - top);

    /// <summary>6 h ticks on 24 h, days on 7 d, 5 days on 30 d.</summary>
    public static long TickStep(PriceRange r) => r switch { PriceRange.Day => 6 * 3600, PriceRange.Week => 86_400, _ => 5 * 86_400 };

    /// <summary>The first local-time boundary after <paramref name="from"/> (midnight for the day steps); <paramref name="offset"/> = local UTC offset in s.</summary>
    public static long FirstTick(long from, long step, long offset) => from + step - ((from + offset) % step + step) % step;

    public static int TickCount(long from, long to, long step, long offset)
    {
        var first = FirstTick(from, step, offset);
        return first > to ? 0 : (int)((to - first) / step) + 1;
    }

    /// <summary>Index of the x nearest <paramref name="x"/> among the first <paramref name="count"/>; -1 when there are none.</summary>
    public static int Nearest(double[] xs, int count, double x)
    {
        var best = -1;
        var bestD = double.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var d = Math.Abs(xs[i] - x);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }
}
