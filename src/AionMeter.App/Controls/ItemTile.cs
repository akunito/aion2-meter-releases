using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.Core.GameDb;

namespace AionMeter.App.Controls;

/// <summary>
/// One item of the Items tab (A2M-29), drawn in a single OnRender like <see cref="SkillTile"/>: the approved mock's 48 px tile with the
/// grade-coloured frame, count badge bottom-right (k/M), "+N" enchant top-left, blue dot top-right for an account-warehouse slot newer than
/// the last full list, grey initials when there is no icon. The icon is asked for on the first render only, so tiles in rows the
/// virtualised list never realises cost nothing. Laid out for 48 px and scaled to any size.
/// </summary>
public sealed class ItemTile : FrameworkElement
{
    // Grade colours as Diego set them on 2026-10-11 (the mock had rare blue and legendary purple: wrong for the game)
    public static readonly Brush CommonLine = SkillTile.Frozen(0x8C, 0x8C, 0x8C), RareLine = SkillTile.Frozen(0x4C, 0xB0, 0x5A), LegendaryLine = SkillTile.Frozen(0x4F, 0x8F, 0xE0),
        UniqueLine = SkillTile.Frozen(0xD3, 0xAD, 0x0F), EpicLine = SkillTile.Frozen(0xE0, 0x66, 0x3A), SpecialLine = SkillTile.Frozen(0x3F, 0xB6, 0xB0), UnknownLine = SkillTile.Frozen(0x37, 0x37, 0x37);
    public static readonly Brush CommonText = SkillTile.Frozen(0xCF, 0xCF, 0xCF), RareText = SkillTile.Frozen(0x7E, 0xD3, 0x8A), LegendaryText = SkillTile.Frozen(0x7F, 0xB0, 0xF0),
        UniqueText = SkillTile.Frozen(0xE7, 0xC7, 0x4A), EpicText = SkillTile.Frozen(0xF0, 0x8A, 0x5D), SpecialText = SkillTile.Frozen(0x6F, 0xD3, 0xCD), UnknownText = SkillTile.Frozen(0xFA, 0xFA, 0xFA);

    public static Brush LineOf(Grade g) => g switch { Grade.Common => CommonLine, Grade.Rare => RareLine, Grade.Legendary => LegendaryLine, Grade.Unique => UniqueLine, Grade.Epic => EpicLine, Grade.Special => SpecialLine, _ => UnknownLine };
    public static Brush TextOf(Grade g) => g switch { Grade.Common => CommonText, Grade.Rare => RareText, Grade.Legendary => LegendaryText, Grade.Unique => UniqueText, Grade.Epic => EpicText, Grade.Special => SpecialText, _ => UnknownText };

    private static readonly Brush Back = SkillTile.Frozen(0x15, 0x15, 0x15), BadgeBack = SkillTile.Frozen(0, 0, 0, 0xD1), EnchBack = SkillTile.Frozen(0, 0, 0, 0xCC), White = SkillTile.Frozen(0xFF, 0xFF, 0xFF),
        Num = SkillTile.Frozen(0xFC, 0xC7, 0x8B), Initials = SkillTile.Frozen(0x66, 0x66, 0x66), Blue = SkillTile.Frozen(0x65, 0xB0, 0xFC), Black = SkillTile.Frozen(0, 0, 0);
    private static readonly Pen[] GradePens = MakePens();
    private static readonly Pen HitPen = MakePen(Blue, 2), DotPen = MakePen(Black, 1.5);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface Semi = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Geometry Clip = Freeze(new RectangleGeometry(new Rect(0, 0, 48, 48), 6, 6));

    /// <summary>Raised by the view (coalesced) when icons landed: tiles still without one ask again.</summary>
    public static event Action? IconsArrived;
    public static void NotifyIcons() => IconsArrived?.Invoke();

    public int Item { get; private set; }
    public long Count { get; private set; }
    private IconCache? _icons;
    private ImageSource? _icon;
    private bool _asked, _dot, _hit;
    private Grade _grade;
    private int _enchant;
    private string _name = "";
    private FormattedText? _countText, _enchText, _initText;

    public ItemTile()
    {
        Width = 48; Height = 48; SnapsToDevicePixels = true;
        Loaded += (_, _) => IconsArrived += OnIcons;
        Unloaded += (_, _) => IconsArrived -= OnIcons;
    }

    public void Set(int item, long count, int enchant, Grade grade, string name, IconCache? icons, bool dot = false, bool dim = false, bool hit = false)
    {
        if (item != Item || !ReferenceEquals(icons, _icons)) { _icon = null; _asked = false; _initText = null; }
        Item = item; _icons = icons; _grade = grade; _dot = dot; _hit = hit; _name = name;
        if (Count != count) { Count = count; _countText = null; }
        if (_enchant != enchant) { _enchant = enchant; _enchText = null; }
        Opacity = dim ? 0.16 : 1;
        InvalidateVisual();
    }

    private void OnIcons()
    {
        if (_icon is not null || !_asked || _icons is null) return;
        if (_icons.Get(Item) is { } img) { _icon = img; InvalidateVisual(); }
    }

    /// <summary>"12,847" → "13k", 1,234,567 → "1.2M" (mock kfmt: thousands from 10,000, one decimal below 10 M).</summary>
    public static string Short(long n) => n >= 1_000_000 ? (n / 1e6).ToString(n >= 10_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M"
        : n >= 10_000 ? Math.Round(n / 1e3).ToString(CultureInfo.InvariantCulture) + "k" : n.ToString("N0", CultureInfo.InvariantCulture);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        if (w <= 0) return;
        if (!_asked) { _asked = true; _icon = _icons?.Get(Item); }
        var scaled = Math.Abs(w - 48) > 0.01;
        if (scaled) dc.PushTransform(new ScaleTransform(w / 48, w / 48));
        dc.PushClip(Clip);
        dc.DrawRectangle(Back, null, new Rect(0, 0, 48, 48));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_icon is { } icon) dc.DrawImage(icon, new Rect(0, 0, 48, 48));
        else
        {
            _initText ??= new FormattedText(InitialsOf(_name), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Semi, 12, Initials, dpi);
            dc.DrawText(_initText, new Point((48 - _initText.Width) / 2, (48 - _initText.Height) / 2));
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, GradePens[(int)_grade], new Rect(0.75, 0.75, 46.5, 46.5), 5.5, 5.5);
        if (Count > 1)
        {
            _countText ??= new FormattedText(Short(Count), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 11, White, dpi);
            var bw = Math.Max(16, _countText.Width + 6);
            dc.DrawRoundedRectangle(BadgeBack, null, new Rect(49 - bw, 34, bw, 15), 4, 4);
            dc.DrawText(_countText, new Point(49 - bw + (bw - _countText.Width) / 2, 34 + (15 - _countText.Height) / 2));
        }
        if (_enchant > 0)
        {
            _enchText ??= new FormattedText("+" + _enchant.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, Num, dpi);
            var bw = _enchText.Width + 6;
            dc.DrawRoundedRectangle(EnchBack, null, new Rect(-1, -1, bw, 14), 4, 4);
            dc.DrawText(_enchText, new Point(-1 + 3, -1 + (14 - _enchText.Height) / 2));
        }
        if (_dot) dc.DrawEllipse(Blue, DotPen, new Point(42.5, 5.5), 3.5, 3.5);
        if (_hit) dc.DrawRoundedRectangle(null, HitPen, new Rect(-2, -2, 52, 52), 7, 7);
        if (scaled) dc.Pop();
    }

    /// <summary>First letters of the first two words (letters only), as the mock does for tiles without an icon.</summary>
    public static string InitialsOf(string name)
    {
        Span<char> buf = stackalloc char[2];
        var n = 0; var start = true;
        foreach (var ch in name)
        {
            if (char.IsLetter(ch)) { if (start && n < 2) buf[n++] = char.ToUpperInvariant(ch); start = false; }
            else if (ch == ' ') start = true;
        }
        return n == 0 ? "?" : new string(buf[..n]);
    }

    private static Pen[] MakePens()
    {
        var pens = new Pen[7];
        for (var g = 0; g < pens.Length; g++) pens[g] = MakePen(LineOf((Grade)g), 1.5);
        return pens;
    }

    private static Pen MakePen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }
    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
}
