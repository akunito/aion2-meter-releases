using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;

namespace AionMeter.App.Controls;

/// <summary>
/// Every cast in order as tiles that wrap onto new lines. Tile size follows the hit's damage relative to the
/// biggest hit; a gold ring marks critical hits. Drawn directly for speed — a long fight has thousands of casts.
/// </summary>
public sealed class RotationStrip : FrameworkElement
{
    private const double MinTile = 22, MaxTile = 40, Gap = 6, LabelHeight = 13;
    private static readonly Typeface Face = new("Bahnschrift");
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private IReadOnlyList<CastRecord> _casts = [];
    private Func<int, string> _skillName = c => c.ToString();
    private Func<int, ImageSource?> _icon = _ => null;
    private readonly List<(Rect Rect, int Index)> _layout = new();
    private long _maxDamage = 1;
    private readonly ToolTip _tip = new();

    public RotationStrip()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
    }

    public int MaxTiles { get; set; } = 1500;

    public void SetData(IReadOnlyList<CastRecord> casts, Func<int, string> skillName, Func<int, ImageSource?>? icon = null)
    {
        _casts = casts.Where(c => (c.Flags & HitFlags.Dot) == 0).Take(MaxTiles).ToList();
        _skillName = skillName;
        _icon = icon ?? (_ => null);
        _maxDamage = Math.Max(1, _casts.Count == 0 ? 1 : _casts.Max(c => c.Damage));
        InvalidateMeasure();
        InvalidateVisual();
    }

    public static Color SkillColor(int code)
    {
        // Stable, well-spread hue per skill code.
        var hue = (uint)(code * 2654435761u) % 360;
        return FromHsl(hue, 0.55, 0.45);
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 600 : available.Width;
        Layout(width);
        var height = _layout.Count == 0 ? 40 : _layout.Max(l => l.Rect.Bottom) + LabelHeight + 4;
        return new Size(width, height);
    }

    private void Layout(double width)
    {
        _layout.Clear();
        double x = 0, y = 0;
        for (var i = 0; i < _casts.Count; i++)
        {
            var size = TileSize(_casts[i].Damage);
            if (x + size > width && x > 0)
            {
                x = 0;
                y += MaxTile + LabelHeight + Gap;
            }
            // Bottom-align tiles of different sizes on the same line.
            _layout.Add((new Rect(x, y + (MaxTile - size), size, size), i));
            x += Math.Max(size, 34) + Gap;
        }
    }

    private double TileSize(long damage) => MinTile + (MaxTile - MinTile) * Math.Sqrt((double)damage / _maxDamage);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gold = new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xCB, 0x82)), 2);
        var label = new SolidColorBrush(Color.FromRgb(0xA3, 0xAC, 0xBF));
        var white = Brushes.White;

        foreach (var (rect, index) in _layout)
        {
            var cast = _casts[index];
            var color = SkillColor(cast.SkillCode);
            var brush = new LinearGradientBrush(Lighten(color, 0.25), color, 90);
            var crit = (cast.Flags & HitFlags.Critical) != 0;
            var icon = _icon(cast.SkillCode);
            if (icon is not null)
            {
                dc.PushClip(new RectangleGeometry(rect, 6, 6));
                dc.DrawImage(icon, rect);
                dc.Pop();
                dc.DrawRoundedRectangle(null, crit ? gold : new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1), rect, 6, 6);
            }
            else
            {
                dc.DrawRoundedRectangle(brush, crit ? gold : new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 1), rect, 6, 6);
                var initials = Initials(_skillName(cast.SkillCode));
                var t = new FormattedText(initials, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, FaceBold, rect.Width * 0.36, white, dpi);
                dc.DrawText(t, new Point(rect.X + (rect.Width - t.Width) / 2, rect.Y + (rect.Height - t.Height) / 2));
            }

            var dmg = new FormattedText(Format.Compact(cast.Damage), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, label, dpi);
            dc.DrawText(dmg, new Point(rect.X + (rect.Width - dmg.Width) / 2, rect.Bottom + 2));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        foreach (var (rect, index) in _layout)
        {
            if (!rect.Contains(p)) continue;
            var c = _casts[index];
            var flags = c.Flags == HitFlags.None ? "" : " · " + UiText.Current.FlagsText(c.Flags);
            _tip.Content = $"{Format.ClockPrecise(c.OffsetMs)}  {_skillName(c.SkillCode)}\n{Format.Grouped(c.Damage)}{flags}";
            _tip.IsOpen = true;
            return;
        }
        _tip.IsOpen = false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.IsOpen = false;
    }

    private static string Initials(string name)
    {
        var parts = name.Split([' ', ':', '-', '\''], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0].Length >= 2 ? parts[0][..2] : parts[0];
        return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
    }

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
