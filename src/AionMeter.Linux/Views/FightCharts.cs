using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

public sealed record ChartSeries(string Name, IReadOnlyList<long> PerSecond, Color Color, bool Primary);

/// <summary>WPF TimelineChart: per-second damage smoothed over 3 s; the selected player as a filled area, the party as thin lines behind it.</summary>
public sealed class TimelineChart : Control
{
    private const double PadLeft = 44, PadRight = 8, PadTop = 10, PadBottom = 20;
    private static readonly Typeface Face = new(Visuals.Number);
    private static readonly IBrush Label = Visuals.Solid(0xFF6B7590);
    private static readonly IPen Grid = new ImmutablePen(Visuals.Solid(0x22FFFFFF), 1);
    private IReadOnlyList<ChartSeries> _series = [];

    public void SetData(IReadOnlyList<ChartSeries> series)
    {
        _series = series;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 80 || h < 50) return;
        var plot = new Rect(PadLeft, PadTop, w - PadLeft - PadRight, h - PadTop - PadBottom);
        var smoothed = new List<(ChartSeries S, double[] V)>(_series.Count);
        var seconds = 2;
        double max = 1;
        foreach (var s in _series)
        {
            var v = Smooth(s.PerSecond, 3);
            smoothed.Add((s, v));
            seconds = Math.Max(seconds, v.Length);
            for (var i = 0; i < v.Length; i++) if (v[i] > max) max = v[i];
        }
        max = NiceCeiling(max);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(Grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = new FormattedText(Format.Compact(max * i / 4), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Label);
            dc.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }
        var step = seconds switch { <= 30 => 5, <= 90 => 15, <= 300 => 30, <= 900 => 60, _ => 300 };
        for (var s = 0; s <= seconds; s += step)
        {
            var x = plot.Left + plot.Width * s / (seconds - 1.0);
            if (x > plot.Right + 0.5) break;
            var t = new FormattedText(Format.Clock(s * 1000L), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, Label);
            dc.DrawText(t, new Point(Math.Min(x - t.Width / 2, plot.Right - t.Width), plot.Bottom + 4));
        }
        smoothed.Sort((a, b) => a.S.Primary.CompareTo(b.S.Primary));   // the selected player last, on top
        foreach (var (s, v) in smoothed)
        {
            if (v.Length == 0) continue;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(Pt(0, v[0]), s.Primary);
                for (var i = 1; i < v.Length; i++) ctx.LineTo(Pt(i, v[i]));
                if (s.Primary)
                {
                    ctx.LineTo(new Point(Pt(v.Length - 1, 0).X, plot.Bottom));
                    ctx.LineTo(new Point(plot.Left, plot.Bottom));
                }
                ctx.EndFigure(s.Primary);
            }
            var c = s.Color;
            if (s.Primary)
            {
                var fill = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0x70, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0x05, c.R, c.G, c.B), 1) },
                };
                dc.DrawGeometry(fill, new Pen(new SolidColorBrush(c), 2, lineJoin: PenLineJoin.Round), geo);
            }
            else dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B)), 1.2), geo);
        }

        Point Pt(int i, double v) => new(plot.Left + plot.Width * i / (seconds - 1.0), plot.Bottom - plot.Height * Math.Min(1, v / max));
    }

    private static double[] Smooth(IReadOnlyList<long> data, int window)
    {
        var result = new double[data.Count];
        double sum = 0;
        for (var i = 0; i < data.Count; i++)
        {
            sum += data[i];
            if (i >= window) sum -= data[i - window];
            result[i] = sum / Math.Min(window, i + 1);
        }
        return result;
    }

    private static double NiceCeiling(double v)
    {
        var exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in (ReadOnlySpan<double>)[1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10])
            if (m * exp >= v) return m * exp;
        return 10 * exp;
    }
}

/// <summary>
/// WPF RotationStrip: every cast in order as tiles that wrap; size follows the hit's damage against the biggest one, a gold ring marks
/// crits. Drawn directly (thousands of casts in a long fight); the tooltip follows the tile under the pointer.
/// </summary>
public sealed class RotationStrip : Control
{
    private const double MinTile = 22, MaxTile = 40, Gap = 6, LabelHeight = 13;
    private const int MaxTiles = 1500;
    private static readonly Typeface Face = new(Visuals.Number);
    private static readonly Typeface Bold = new(Visuals.Ui, FontStyle.Normal, FontWeight.Bold);
    private static readonly IPen GoldRing = new ImmutablePen(Visuals.Solid(0xFFECCB82), 2);
    private static readonly IPen Rim = new ImmutablePen(Visuals.Solid(0x50000000), 1);
    private static readonly IBrush Label = Visuals.Solid(0xFFA3ACBF);
    private readonly List<CastRecord> _casts = new();
    private readonly List<Rect> _layout = new();
    private Func<int, string> _name = c => c.ToString(CultureInfo.InvariantCulture);
    private Func<int, Bitmap?> _icon = _ => null;
    private long _max = 1;
    private double _laidFor = -1;
    private int _tipIndex = -1;

    public void SetData(IReadOnlyList<CastRecord> casts, Func<int, string> name, Func<int, Bitmap?> icon)
    {
        _casts.Clear();
        foreach (var c in casts)
        {
            if ((c.Flags & HitFlags.Dot) != 0) continue;
            _casts.Add(c);
            if (_casts.Count >= MaxTiles) break;
        }
        _name = name;
        _icon = icon;
        _max = 1;
        foreach (var c in _casts) if (c.Damage > _max) _max = c.Damage;
        _laidFor = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double TileSize(long damage) => MinTile + (MaxTile - MinTile) * Math.Sqrt((double)damage / _max);

    private void Layout(double width)
    {
        if (Math.Abs(width - _laidFor) < 0.5) return;
        _laidFor = width;
        _layout.Clear();
        double x = 0, y = 0;
        foreach (var c in _casts)
        {
            var size = TileSize(c.Damage);
            if (x + size > width && x > 0) { x = 0; y += MaxTile + LabelHeight + Gap; }
            _layout.Add(new Rect(x, y + (MaxTile - size), size, size));
            x += Math.Max(size, 34) + Gap;
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 600 : available.Width;
        Layout(width);
        var bottom = 0.0;
        foreach (var r in _layout) if (r.Bottom > bottom) bottom = r.Bottom;
        return new Size(width, _layout.Count == 0 ? 40 : bottom + LabelHeight + 4);
    }

    public override void Render(DrawingContext dc)
    {
        Layout(Bounds.Width);
        dc.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        for (var i = 0; i < _layout.Count; i++)
        {
            var rect = _layout[i];
            var cast = _casts[i];
            var crit = (cast.Flags & HitFlags.Critical) != 0;
            if (_icon(cast.SkillCode) is { } icon)
            {
                using (dc.PushClip(new RoundedRect(rect, 6))) dc.DrawImage(icon, rect);
                dc.DrawRectangle(null, crit ? GoldRing : Rim, rect, 6, 6);
            }
            else
            {
                var color = SkillColor(cast.SkillCode);
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Visuals.Mix(color, Colors.White, 0.25), 0), new GradientStop(color, 1) },
                };
                dc.DrawRectangle(brush, crit ? GoldRing : Rim, rect, 6, 6);
                var t = new FormattedText(Initials(_name(cast.SkillCode)), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, rect.Width * 0.36, Brushes.White);
                dc.DrawText(t, new Point(rect.X + (rect.Width - t.Width) / 2, rect.Y + (rect.Height - t.Height) / 2));
            }
            var dmg = new FormattedText(Format.Compact(cast.Damage), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, Label);
            dc.DrawText(dmg, new Point(rect.X + (rect.Width - dmg.Width) / 2, rect.Bottom + 2));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var hit = -1;
        for (var i = 0; i < _layout.Count; i++) if (_layout[i].Contains(p)) { hit = i; break; }
        if (hit == _tipIndex) return;
        _tipIndex = hit;
        if (hit < 0) { ToolTip.SetIsOpen(this, false); ToolTip.SetTip(this, null); return; }
        var c = _casts[hit];
        var flags = c.Flags == HitFlags.None ? "" : " · " + UiText.Current.FlagsText(c.Flags);
        ToolTip.SetTip(this, $"{Format.ClockPrecise(c.OffsetMs)}  {_name(c.SkillCode)}\n{Format.Grouped(c.Damage)}{flags}");
        ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _tipIndex = -1;
        ToolTip.SetIsOpen(this, false);
    }

    private static string Initials(string name)
    {
        var parts = name.Split([' ', ':', '-', '\''], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Length >= 2 ? parts[0][..2] : parts[0];
        return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
    }

    /// <summary>Stable, well-spread hue per skill code (WPF RotationStrip.SkillColor).</summary>
    public static Color SkillColor(int code)
    {
        var hue = (uint)(code * 2654435761u) % 360;
        return FromHsl(hue, 0.55, 0.45);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
