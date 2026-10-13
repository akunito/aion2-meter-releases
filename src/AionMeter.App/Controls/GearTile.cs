using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AionMeter.Core.GameDb;

namespace AionMeter.App.Controls;

/// <summary>
/// One equipment slot of the player card's gear screen (A2M-44), one OnRender like <see cref="ItemTile"/> and with its grade colours:
/// icon, grade frame, "+15" top-left, "x2" (exceed) bottom-right, initials while the icon is missing, a dim frame for an empty slot.
/// The icon comes from <see cref="Fetch"/> on the first render and again on <see cref="Retry"/> (the view calls it when icons landed).
/// Laid out for 48 px and scaled to any size.
/// </summary>
public sealed class GearTile : FrameworkElement
{
    private static readonly Brush Back = SkillTile.Frozen(0x15, 0x15, 0x15), EmptyBack = SkillTile.Frozen(0xFF, 0xFF, 0xFF, 0x08), BadgeBack = SkillTile.Frozen(0, 0, 0, 0xCC),
        Num = SkillTile.Frozen(0xFC, 0xC7, 0x8B), Exceed = SkillTile.Frozen(0xEC, 0xCB, 0x82), Initials = SkillTile.Frozen(0x66, 0x66, 0x66);
    private static readonly Pen EmptyPen = MakePen(SkillTile.Frozen(0xFF, 0xFF, 0xFF, 0x22), 1, dashed: true);
    private static readonly Pen[] GradePens = MakePens();
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface Semi = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static new readonly Geometry Clip = Freeze(new RectangleGeometry(new Rect(0, 0, 48, 48), 6, 6));

    private Func<ImageSource?>? _fetch;
    private ImageSource? _icon;
    private bool _asked, _empty = true;
    private Grade _grade;
    private string _name = "";
    private string? _enchant, _exceed;
    private FormattedText? _enchText, _exceedText, _initText;

    public GearTile(double size = 44) { Width = size; Height = size; SnapsToDevicePixels = true; }

    public Func<ImageSource?>? Fetch => _fetch;

    public void Set(string name, Grade grade, string? enchant, string? exceed, Func<ImageSource?>? fetch)
    {
        _empty = false; _name = name; _grade = grade; _enchant = enchant; _exceed = exceed; _fetch = fetch;
        _icon = null; _asked = false; _enchText = _exceedText = _initText = null;
        InvalidateVisual();
    }

    /// <summary>True when the icon arrived (or there is nothing to wait for): the view stops asking.</summary>
    public bool Retry()
    {
        if (_icon is not null || _fetch is null) return true;
        if (!_asked) return false;
        if (_fetch() is { } img) { _icon = img; InvalidateVisual(); return true; }
        return false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0) return;
        var scaled = Math.Abs(w - 48) > 0.01;
        if (scaled) dc.PushTransform(new ScaleTransform(w / 48, w / 48));
        if (_empty)
        {
            dc.DrawRoundedRectangle(EmptyBack, EmptyPen, new Rect(0.5, 0.5, 47, 47), 6, 6);
            if (scaled) dc.Pop();
            return;
        }
        if (!_asked) { _asked = true; _icon = _fetch?.Invoke(); }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.PushClip(Clip);
        dc.DrawRectangle(Back, null, new Rect(0, 0, 48, 48));
        if (_icon is { } icon) dc.DrawImage(icon, new Rect(0, 0, 48, 48));
        else
        {
            _initText ??= new FormattedText(ItemTile.InitialsOf(_name), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Semi, 12, Initials, dpi);
            dc.DrawText(_initText, new Point((48 - _initText.Width) / 2, (48 - _initText.Height) / 2));
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, GradePens[(int)_grade], new Rect(0.75, 0.75, 46.5, 46.5), 5.5, 5.5);
        if (_enchant is not null)
        {
            _enchText ??= new FormattedText(_enchant, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 11, Num, dpi);
            var bw = _enchText.Width + 6;
            dc.DrawRoundedRectangle(BadgeBack, null, new Rect(-1, -1, bw, 15), 4, 4);
            dc.DrawText(_enchText, new Point(2, -1 + (15 - _enchText.Height) / 2));
        }
        if (_exceed is not null)
        {
            _exceedText ??= new FormattedText(_exceed, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, Exceed, dpi);
            var bw = Math.Max(16, _exceedText.Width + 6);
            dc.DrawRoundedRectangle(BadgeBack, null, new Rect(49 - bw, 35, bw, 14), 4, 4);
            dc.DrawText(_exceedText, new Point(49 - bw + (bw - _exceedText.Width) / 2, 35 + (14 - _exceedText.Height) / 2));
        }
        if (scaled) dc.Pop();
    }

    private static Pen[] MakePens()
    {
        var pens = new Pen[7];
        for (var g = 0; g < pens.Length; g++) pens[g] = MakePen(ItemTile.LineOf((Grade)g), 1.5);
        return pens;
    }

    private static Pen MakePen(Brush b, double w, bool dashed = false)
    {
        var p = new Pen(b, w);
        if (dashed) p.DashStyle = new DashStyle([3, 2], 0);
        p.Freeze();
        return p;
    }

    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
}
