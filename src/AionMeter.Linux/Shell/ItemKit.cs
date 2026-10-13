using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AionMeter.Core.GameDb;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>
/// One item of the Items tab (WPF Controls/ItemTile, A2M-29) in a single Render: the 48 px tile with the grade-coloured frame, count badge
/// bottom-right (k/M), "+N" enchant top-left, blue dot top-right for an account-warehouse slot newer than the last full list, grey initials
/// without an icon. The icon is asked for on the first render only, so tiles the virtualised list never realises cost nothing.
/// </summary>
public sealed class ItemTile : Control
{
    private static readonly IBrush Back = Visuals.Solid(0xFF151515), BadgeBack = Visuals.Solid(0xD1000000), EnchBack = Visuals.Solid(0xCC000000), White = Visuals.Solid(0xFFFFFFFF),
        Num = Visuals.Solid(0xFFFCC78B), Initials = Visuals.Solid(0xFF666666), Blue = Visuals.Solid(0xFF65B0FC);
    private static readonly IPen[] GradePens = MakePens();
    private static readonly IPen HitPen = new ImmutablePen((IImmutableBrush)Blue, 2), DotPen = new ImmutablePen(Visuals.Solid(0xFF000000), 1.5);
    private static readonly Typeface Bold = new(Visuals.Ui, FontStyle.Normal, FontWeight.Bold), Semi = new(Visuals.Ui, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly RoundedRect TileClip = new(new Rect(0, 0, 48, 48), 6);

    /// <summary>Raised by a view (coalesced) when icons landed: tiles still without one ask again.</summary>
    public static event Action? IconsArrived;
    public static void NotifyIcons() => IconsArrived?.Invoke();

    public int Item { get; private set; }
    public long Count { get; private set; }
    private IconProvider? _icons;
    private Avalonia.Media.Imaging.Bitmap? _icon;
    private bool _asked, _dot, _hit, _listening;
    private Grade _grade;
    private int _enchant;
    private string _name = "";
    private FormattedText? _countText, _enchText, _initText;

    public ItemTile() { Width = 48; Height = 48; }

    public void Set(int item, long count, int enchant, Grade grade, string name, IconProvider? icons, bool dot = false, bool dim = false, bool hit = false)
    {
        if (item != Item || !ReferenceEquals(icons, _icons)) { _icon = null; _asked = false; _initText = null; }
        Item = item; _icons = icons; _grade = grade; _dot = dot; _hit = hit; _name = name;
        if (Count != count) { Count = count; _countText = null; }
        if (_enchant != enchant) { _enchant = enchant; _enchText = null; }
        Opacity = dim ? 0.16 : 1;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_listening) { IconsArrived += OnIcons; _listening = true; }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_listening) { IconsArrived -= OnIcons; _listening = false; }
    }

    private void OnIcons()
    {
        if (_icon is not null || !_asked || _icons is null) return;
        if (_icons.Item(Item) is { } img) { _icon = img; InvalidateVisual(); }
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        if (!_asked) { _asked = true; _icon = _icons?.Item(Item); }
        var s = w / 48;
        using var scale = dc.PushTransform(Matrix.CreateScale(s, s));
        using (dc.PushClip(TileClip))
        {
            dc.FillRectangle(Back, new Rect(0, 0, 48, 48));
            if (_icon is { } icon) dc.DrawImage(icon, new Rect(0, 0, 48, 48));
            else
            {
                _initText ??= new FormattedText(InitialsOf(_name), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Semi, 12, Initials);
                dc.DrawText(_initText, new Point((48 - _initText.Width) / 2, (48 - _initText.Height) / 2));
            }
        }
        dc.DrawRectangle(null, GradePens[(int)_grade < GradePens.Length ? (int)_grade : 0], new RoundedRect(new Rect(0.75, 0.75, 46.5, 46.5), 5.5));
        if (Count > 1)
        {
            _countText ??= new FormattedText(ItemKit.Short(Count), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 11, White);
            var bw = Math.Max(16, _countText.Width + 6);
            dc.DrawRectangle(BadgeBack, null, new RoundedRect(new Rect(49 - bw, 34, bw, 15), 4));
            dc.DrawText(_countText, new Point(49 - bw + (bw - _countText.Width) / 2, 34 + (15 - _countText.Height) / 2));
        }
        if (_enchant > 0)
        {
            _enchText ??= new FormattedText("+" + _enchant.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, Num);
            var bw = _enchText.Width + 6;
            dc.DrawRectangle(EnchBack, null, new RoundedRect(new Rect(-1, -1, bw, 14), 4));
            dc.DrawText(_enchText, new Point(2, -1 + (14 - _enchText.Height) / 2));
        }
        if (_dot) dc.DrawEllipse(Blue, DotPen, new Point(42.5, 5.5), 3.5, 3.5);
        if (_hit) dc.DrawRectangle(null, HitPen, new RoundedRect(new Rect(-2, -2, 52, 52), 7));
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

    private static IPen[] MakePens()
    {
        var pens = new IPen[7];
        for (var g = 0; g < pens.Length; g++) pens[g] = new ImmutablePen((IImmutableBrush)Visuals.GradeLine((Grade)g), 1.5);
        return pens;
    }
}

/// <summary>Shared bits of the Items, Craft and Prices tabs: grade chips (WPF GradeTag), kinah formats, the price parser.</summary>
public static class ItemKit
{
    private static readonly IBrush?[] ChipBacks = new IBrush?[8];

    public static string Short(long n) => ItemFormat.Short(n);

    public static string Kinah(long v) => ItemFormat.Kinah(v);

    public static string N(long v) => ItemFormat.N(v);

    /// <summary>The grade chip after an item name ("Artisan's Refining Stone [legendary]"); null for an unknown grade.</summary>
    public static Border? GradeChip(Grade g, string lang, double size = 10.5)
    {
        if (g == Grade.Unknown) return null;
        var line = Visuals.GradeLine(g);
        var ix = (int)g < ChipBacks.Length ? (int)g : 0;
        var back = ChipBacks[ix] ??= line is ISolidColorBrush sb ? new ImmutableSolidColorBrush(Color.FromArgb(0x2E, sb.Color.R, sb.Color.G, sb.Color.B)) : Brushes.Transparent;
        return new Border
        {
            Background = back, BorderBrush = line, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1),
            Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = ItemGrades.Word(g, lang), FontSize = size, FontWeight = FontWeight.SemiBold, Foreground = Visuals.GradeText(g) },
        };
    }

    /// <summary>The chip as an inline of a TextBlock that already holds the name.</summary>
    public static void AddChip(InlineCollection inlines, Grade g, string lang, double size = 10.5)
    {
        if (GradeChip(g, lang, size) is { } chip) inlines.Add(new InlineUIContainer { Child = chip, BaselineAlignment = BaselineAlignment.Center });
    }

    public static bool TryParsePrice(string? text, out long value) => ItemFormat.TryParsePrice(text, out value);

    /// <summary>Search box with a hint text and a clear ✕ (WPF SearchField / Field + ClearSearch): Esc clears too.</summary>
    public static (Grid Host, TextBox Box) SearchBox(string hint, double width, Action changed)
    {
        var box = UiKit.Input("", width);
        box.Watermark = hint;
        box.Padding = new Thickness(6, 3, 24, 3);
        box.Height = 28;
        var clear = UiKit.IconButton(Visuals.IconClose, UiText.Current.CraftClear, () => { box.Text = ""; box.Focus(); }, 18);
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.Margin = new Thickness(0, 0, 4, 0);
        clear.IsVisible = false;
        var host = new Grid { Width = width, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        host.Children.Add(box);
        host.Children.Add(clear);
        box.TextChanged += (_, _) => { clear.IsVisible = !string.IsNullOrEmpty(box.Text); changed(); };
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && !string.IsNullOrEmpty(box.Text)) { box.Text = ""; e.Handled = true; } };
        return (host, box);
    }
}
