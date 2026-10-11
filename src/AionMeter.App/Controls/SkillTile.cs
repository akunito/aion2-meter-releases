using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AionMeter.App.Controls;

/// <summary>
/// One skill icon of the Build tab, drawn in a single OnRender (no template, no child elements): questlog's 48 px tile with the
/// type-coloured frame, the build level bottom-right, the character's current level bottom-left, one diamond per chosen spec
/// and the selection outline. Any size works: everything is laid out for 48 px and scaled.
/// </summary>
public sealed class SkillTile : FrameworkElement
{
    public enum Look { Normal, Off, Done }

    private static readonly Brush Back = Frozen(0x0D, 0x0D, 0x0D), BadgeBack = Frozen(0, 0, 0, 0xCC), CurBack = Frozen(0, 0, 0, 0xD9), White = Frozen(0xFF, 0xFF, 0xFF),
        Blue = Frozen(0x65, 0xB0, 0xFC);
    private static readonly Pen ActivePen = FrozenPen(0x65, 0xB0, 0xFC, 0xB3), StigmaPen = FrozenPen(0xA9, 0x79, 0xCB, 0xB3), PassivePen = FrozenPen(0xEA, 0x58, 0x4C, 0xB3),
        OffPen = FrozenPen(0x3A, 0x3A, 0x3A, 0xFF), SelectedPen = MakeSelectedPen(), ShadowPen = FrozenPen(0, 0, 0, 0xB0);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Geometry Clip = Freeze(new RectangleGeometry(new Rect(0, 0, 48, 48), 6, 6));
    private static readonly Geometry[] Diamonds = MakeDiamonds();
    private static readonly ConditionalWeakTable<ImageSource, ImageSource> Greys = new();

    public int SkillId { get; private set; }
    private ImageSource? _icon;
    private string _sub = "active";
    private Look _look;
    private int _diamonds;
    private bool _selected, _badges = true;
    private string? _level, _current;
    private Brush? _currentBrush;
    private FormattedText? _levelText, _currentText;
    private Geometry? _levelGeo, _currentGeo;

    public SkillTile() { Width = 48; Height = 48; SnapsToDevicePixels = true; }

    public void Set(int skillId, ImageSource? icon, string sub, Look look, int diamonds, bool selected, string? level, string? current, Brush? currentBrush, bool badges = true)
    {
        SkillId = skillId; _icon = icon; _sub = sub; _look = look; _diamonds = Math.Min(diamonds, Diamonds.Length); _selected = selected; _badges = badges;
        if (_level != level) { _level = level; _levelText = null; _levelGeo = null; }
        if (_current != current || !ReferenceEquals(_currentBrush, currentBrush)) { _current = current; _currentBrush = currentBrush; _currentText = null; _currentGeo = null; }
        Opacity = look == Look.Done ? 0.28 : 1;
        InvalidateVisual();
    }

    public void SetIcon(ImageSource? icon) { if (!ReferenceEquals(icon, _icon)) { _icon = icon; InvalidateVisual(); } }

    public void SetSelected(bool selected) { if (selected != _selected) { _selected = selected; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0) return;
        var scaled = Math.Abs(w - 48) > 0.01;
        if (scaled) dc.PushTransform(new ScaleTransform(w / 48, w / 48));
        dc.PushClip(Clip);
        dc.DrawRectangle(Back, null, new Rect(0, 0, 48, 48));
        if (_icon is { } icon)
        {
            if (_look == Look.Normal) dc.DrawImage(icon, new Rect(0, 0, 48, 48));
            else
            {
                // CSS of the mock: grayscale(1) brightness(.55) for "not in the build", grayscale(.6) at .28 opacity (element) for "done"
                if (_look == Look.Off) dc.PushOpacity(0.55);
                dc.DrawImage(Grey(icon), new Rect(0, 0, 48, 48));
                if (_look == Look.Off) dc.Pop();
            }
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, _look == Look.Off ? OffPen : _sub == "stigma" ? StigmaPen : _sub == "passive" ? PassivePen : ActivePen, new Rect(0.4, 0.4, 47.2, 47.2), 5.6, 5.6);
        for (var i = 0; i < _diamonds; i++) dc.DrawGeometry(Blue, ShadowPen, Diamonds[i]);
        if (_badges && _look != Look.Off)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_level is { Length: > 0 })
            {
                _levelText ??= new FormattedText(_level, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 11, White, dpi);
                var bw = Math.Max(21, _levelText.Width + 6);
                _levelGeo ??= Corner(49 - bw, 33, bw, 16, topLeft: true);
                dc.DrawGeometry(BadgeBack, null, _levelGeo);
                dc.DrawText(_levelText, new Point(49 - bw + (bw - _levelText.Width) / 2, 33 + (16 - _levelText.Height) / 2));
            }
            if (_current is { Length: > 0 } && _currentBrush is not null)
            {
                _currentText ??= new FormattedText(_current, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, _currentBrush, dpi);
                var bw = Math.Max(16, _currentText.Width + 6);
                _currentGeo ??= Corner(-1, 35, bw, 14, topLeft: false);
                dc.DrawGeometry(CurBack, null, _currentGeo);
                dc.DrawText(_currentText, new Point(-1 + (bw - _currentText.Width) / 2, 35 + (14 - _currentText.Height) / 2));
            }
        }
        if (_selected) dc.DrawRoundedRectangle(null, SelectedPen, new Rect(-2, -2, 52, 52), 8, 8);
        if (scaled) dc.Pop();
    }

    /// <summary>Badge with one rounded corner (6 px): top-left for the build level, top-right for the current level.</summary>
    private static Geometry Corner(double x, double y, double w, double h, bool topLeft)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (topLeft)
            {
                c.BeginFigure(new Point(x, y + 6), true, true);
                c.ArcTo(new Point(x + 6, y), new Size(6, 6), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(x + w, y), false, false);
                c.LineTo(new Point(x + w, y + h), false, false);
                c.LineTo(new Point(x, y + h), false, false);
            }
            else
            {
                c.BeginFigure(new Point(x, y), true, true);
                c.LineTo(new Point(x + w - 6, y), false, false);
                c.ArcTo(new Point(x + w, y + 6), new Size(6, 6), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(x + w, y + h), false, false);
                c.LineTo(new Point(x, y + h), false, false);
            }
        }
        g.Freeze();
        return g;
    }

    public static ImageSource Grey(ImageSource icon)
    {
        if (Greys.TryGetValue(icon, out var g)) return g;
        ImageSource grey = icon;
        if (icon is BitmapSource bmp)
        {
            try
            {
                var f = new FormatConvertedBitmap(bmp, PixelFormats.Gray8, null, 0);
                f.Freeze();
                grey = f;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
        }
        Greys.AddOrUpdate(icon, grey);
        return grey;
    }

    // 7 px squares rotated 45° (half diagonal 4.95) at left 3 / top 3, 2 px apart as in the mock's flex row → centres 9 px apart
    private static Geometry[] MakeDiamonds()
    {
        var list = new Geometry[4];
        for (var i = 0; i < list.Length; i++)
        {
            double cx = 6.5 + i * 9, cy = 6.5, r = 4.95;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx, cy - r), true, true);
                c.LineTo(new Point(cx + r, cy), false, false);
                c.LineTo(new Point(cx, cy + r), false, false);
                c.LineTo(new Point(cx - r, cy), false, false);
            }
            g.Freeze();
            list[i] = g;
        }
        return list;
    }

    private static Pen MakeSelectedPen() { var p = new Pen(Frozen(0x65, 0xB0, 0xFC), 2); p.Freeze(); return p; }
    private static Pen FrozenPen(byte r, byte g, byte b, byte a) { var p = new Pen(Frozen(r, g, b, a), 0.8); p.Freeze(); return p; }
    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
    public static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }
}
