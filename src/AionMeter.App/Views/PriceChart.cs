using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AionMeter.Core.Market;

namespace AionMeter.App.Views;

/// <summary>
/// Lowest-listing line over the chosen range (expanded Prices row), all in one OnRender: area + line, faint min/median/max grid
/// with labels, the buy/sell threshold dashed, and on hover the nearest sample's time and price. Samples arrive already reduced
/// to ≤ 200 points (PriceHistory.Downsample).
/// </summary>
public sealed class PriceChart : FrameworkElement
{
    private const double PadLeft = 74, PadRight = 10, PadTop = 12, PadBottom = 20;
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush LineBrush = Frozen(Color.FromRgb(0xEC, 0xCB, 0x82)), Mute = Frozen(Color.FromRgb(0x6B, 0x75, 0x90)),
        TextBrush = Frozen(Color.FromRgb(0xF1, 0xE9, 0xD8)), BuyBrush = Frozen(Color.FromRgb(0x8E, 0xE0, 0x8A)), SellBrush = Frozen(Color.FromRgb(0xF5, 0xB5, 0x44)),
        TipBg = Frozen(Color.FromArgb(0xEE, 0x11, 0x17, 0x26)), Plate = Frozen(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)), Area;
    private static readonly Pen LinePen = FrozenPen(LineBrush, 1.6), GridPen = FrozenPen(Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1),
        BuyPen = FrozenPen(BuyBrush, 1.2, true), SellPen = FrozenPen(SellBrush, 1.2, true), HoverPen = FrozenPen(Frozen(Color.FromArgb(0x66, 0xF1, 0xE9, 0xD8)), 1),
        TipPen = FrozenPen(Frozen(Color.FromRgb(0x6E, 0x5A, 0x33)), 1);

    static PriceChart()
    {
        var g = new LinearGradientBrush(Color.FromArgb(0x55, 0xC9, 0xA4, 0x5C), Color.FromArgb(0x04, 0xC9, 0xA4, 0x5C), 90);
        g.Freeze();
        Area = g;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Brush b, double t, bool dashed = false)
    {
        var p = new Pen(b, t);
        if (dashed) p.DashStyle = new DashStyle([5, 4], 0);
        p.Freeze();
        return p;
    }

    private PriceSample[] _s = [];
    private long _from, _to, _threshold, _min, _median, _max;
    private bool _sell;
    private PriceRange _range;
    private string _empty = "", _buyLabel = "", _sellLabel = "";
    private double[] _xs = [];
    private int _hover = -1;

    public void SetData(PriceSample[] reduced, (long Min, long Median, long Max) stats, long fromTs, long toTs, PriceRange range, long threshold, bool sell, string empty, string buyLabel, string sellLabel)
    {
        _s = reduced; (_min, _median, _max) = stats; _from = fromTs; _to = Math.Max(toTs, fromTs + 1); _range = range;
        _threshold = threshold; _sell = sell; _empty = empty; _buyLabel = buyLabel; _sellLabel = sellLabel; _hover = -1;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        var best = -1; var bestD = double.MaxValue;
        for (var i = 0; i < _xs.Length; i++) { var d = Math.Abs(_xs[i] - x); if (d < bestD) { bestD = d; best = i; } }
        if (best != _hover) { _hover = best; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e) { if (_hover >= 0) { _hover = -1; InvalidateVisual(); } }

    private FormattedText Text(string s, double size, Brush b, double dpi) => new(s, UiTextCulture, FlowDirection.LeftToRight, Face, size, b, dpi);
    private static CultureInfo UiTextCulture => UiText.Current.Culture;
    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Plate, null, new Rect(0, 0, w, h), 6, 6);   // also the hit-test surface for hover
        if (w < 120 || h < 60) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plot = new Rect(PadLeft, PadTop, w - PadLeft - PadRight, h - PadTop - PadBottom);

        DrawTimeAxis(dc, plot, dpi);
        if (_s.Length == 0)
        {
            _xs = [];
            var t = Text(_empty, 12, Mute, dpi);
            t.MaxTextWidth = Math.Max(10, plot.Width - 20);
            dc.DrawText(t, new Point(plot.Left + (plot.Width - t.Width) / 2, plot.Top + (plot.Height - t.Height) / 2));
            return;
        }

        // y range: the data, plus the threshold when it is near enough not to flatten the line
        double lo = _min, hi = _max;
        var span = Math.Max(1, hi - lo);
        var thrIn = _threshold > 0 && _threshold >= lo - span * 0.6 && _threshold <= hi + span * 0.6;
        if (thrIn) { lo = Math.Min(lo, _threshold); hi = Math.Max(hi, _threshold); }
        if (hi - lo < 1) { var pad = Math.Max(1, hi * 0.05); lo -= pad; hi += pad; }
        var m = (hi - lo) * 0.08; lo -= m; hi += m;
        double Y(double v) => plot.Bottom - (v - lo) / (hi - lo) * plot.Height;
        double X(long ts) => plot.Left + (double)(ts - _from) / (_to - _from) * plot.Width;

        // min / median / max: faint lines, full-precision labels on the left
        double lastLabelY = double.NaN;
        foreach (var (v, name) in new[] { (_max, "max"), (_median, "med"), (_min, "min") })
        {
            var y = Y(v);
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            if (!double.IsNaN(lastLabelY) && Math.Abs(y - lastLabelY) < 12) continue;   // flat series: one label is enough
            var t = Text(N(v), 10.5, Mute, dpi);
            var tag = Text(name, 9, Mute, dpi);
            dc.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
            dc.DrawText(tag, new Point(plot.Left - t.Width - tag.Width - 10, y - tag.Height / 2 + 0.5));
            lastLabelY = y;
        }

        // line + area
        _xs = new double[_s.Length];
        for (var i = 0; i < _s.Length; i++) _xs[i] = X(_s[i].Ts);
        if (_s.Length == 1)
            dc.DrawEllipse(LineBrush, null, new Point(_xs[0], Y(_s[0].Price)), 3, 3);
        else
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(_xs[0], plot.Bottom), true, true);
                for (var i = 0; i < _s.Length; i++) g.LineTo(new Point(_xs[i], Y(_s[i].Price)), false, false);
                g.LineTo(new Point(_xs[^1], plot.Bottom), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(Area, null, area);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(new Point(_xs[0], Y(_s[0].Price)), false, false);
                for (var i = 1; i < _s.Length; i++) g.LineTo(new Point(_xs[i], Y(_s[i].Price)), true, true);
            }
            line.Freeze();
            dc.DrawGeometry(null, LinePen, line);
        }

        // threshold
        if (_threshold > 0)
        {
            var label = (_sell ? _sellLabel : _buyLabel) + " " + N(_threshold);
            var brush = _sell ? SellBrush : BuyBrush;
            if (thrIn)
            {
                var y = Y(_threshold);
                dc.DrawLine(_sell ? SellPen : BuyPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var t = Text(label, 10, brush, dpi);
                var at = new Point(plot.Right - t.Width - 6, y < plot.Top + t.Height + 4 ? y + 3 : y - t.Height - 3);
                dc.DrawRoundedRectangle(TipBg, null, new Rect(at.X - 4, at.Y - 1, t.Width + 8, t.Height + 2), 3, 3);   // readable over the line
                dc.DrawText(t, at);
            }
            else
            {
                var above = _threshold > hi;
                var t = Text((above ? "▲ " : "▼ ") + label, 10, brush, dpi);
                dc.DrawText(t, new Point(plot.Right - t.Width - 4, above ? plot.Top : plot.Bottom - t.Height - 2));
            }
        }

        if (_hover >= 0 && _hover < _s.Length)
        {
            var s = _s[_hover];
            var p = new Point(_xs[_hover], Y(s.Price));
            dc.DrawLine(HoverPen, new Point(p.X, plot.Top), new Point(p.X, plot.Bottom));
            dc.DrawEllipse(LineBrush, null, p, 3.5, 3.5);
            var when = DateTimeOffset.FromUnixTimeSeconds(s.Ts).ToLocalTime().ToString("ddd d MMM HH:mm", UiTextCulture);
            var t = Text($"{when}  ·  {N(s.Price)}" + (s.Listings > 0 ? $"  ·  {s.Listings}×" : ""), 11, TextBrush, dpi);
            var bx = p.X + 10 + t.Width + 12 > w ? p.X - t.Width - 22 : p.X + 10;
            var by = Math.Max(plot.Top, Math.Min(p.Y - t.Height - 14, plot.Bottom - t.Height - 8));
            dc.DrawRoundedRectangle(TipBg, TipPen, new Rect(bx, by, t.Width + 12, t.Height + 6), 4, 4);
            dc.DrawText(t, new Point(bx + 6, by + 3));
        }
    }

    private void DrawTimeAxis(DrawingContext dc, Rect plot, double dpi)
    {
        long step = _range switch { PriceRange.Day => 6 * 3600, PriceRange.Week => 86_400, _ => 5 * 86_400 };
        var fmt = _range == PriceRange.Day ? "HH:mm" : "d MMM";
        var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.FromUnixTimeSeconds(_to)).TotalSeconds;
        var first = _from + step - ((_from + offset) % step + step) % step;   // local-time boundary (midnight for days)
        var lastRight = double.MinValue;
        for (var ts = first; ts <= _to; ts += step)
        {
            var x = plot.Left + (double)(ts - _from) / (_to - _from) * plot.Width;
            dc.DrawLine(GridPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 3));
            var t = Text(DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString(fmt, UiTextCulture), 10, Mute, dpi);
            var left = Math.Min(x - t.Width / 2, plot.Right - t.Width);
            if (left < lastRight + 6) continue;
            dc.DrawText(t, new Point(left, plot.Bottom + 4));
            lastRight = left + t.Width;
        }
        dc.DrawLine(GridPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
    }
}
