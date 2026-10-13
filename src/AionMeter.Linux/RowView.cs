using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Core.Events;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux;

/// <summary>One player (WPF RowTemplate): a full-height class-coloured bar with place medal, emblem, name, GS/CP, then DPS, damage and share.
/// Built once and updated in place each snapshot.</summary>
internal sealed class RowView : Grid
{
    public const double RowHeight = 42;
    private readonly Border _fill, _self, _hover;
    private readonly ScaleTransform _scale = new(0, 1);
    private readonly Ellipse _rankDisc;
    private readonly TextBlock _rank, _name, _gs, _cp, _dps, _damage, _share;
    private readonly Rectangle _rule;
    private readonly ClassGlyph _glyph;
    private GameClass _class = (GameClass)255;
    private int _rankValue = -1;
    private bool _isSelf;

    private static readonly TextDecorationCollection SelfUnderline =
    [
        new TextDecoration
        {
            Location = TextDecorationLocation.Underline, Stroke = Visuals.GoldBright, StrokeThickness = 1.6,
            StrokeThicknessUnit = TextDecorationUnit.Pixel, StrokeOffset = 3, StrokeOffsetUnit = TextDecorationUnit.Pixel,
        },
    ];

    public uint ActorId { get; private set; }

    public RowView()
    {
        Height = RowHeight;
        Margin = new Thickness(0, 0, 0, 6);
        Background = Brushes.Transparent;
        Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = Visuals.RowTrack });
        Children.Add(_fill = new Border { CornerRadius = new CornerRadius(7), RenderTransform = _scale, RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative) });
        Children.Add(new Border { CornerRadius = new CornerRadius(7, 7, 0, 0), Height = 19, VerticalAlignment = VerticalAlignment.Top, Background = Visuals.RowGloss, IsHitTestVisible = false });
        Children.Add(_hover = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent });
        Children.Add(_self = new Border
        {
            CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1.6), BorderBrush = Visuals.GoldBright, IsVisible = false,
            BoxShadow = BoxShadows.Parse("0 0 8 0 #CCE7C67F"),
        });

        var g = new Grid { Margin = new Thickness(8, 0, 14, 0), Effect = Visuals.TextShadow() };
        OverlayWindow.AddColumns(g);
        var rank = new Grid { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        rank.Children.Add(_rankDisc = new Ellipse { StrokeThickness = 1.2 });
        rank.Children.Add(_rank = new TextBlock
        {
            FontFamily = Visuals.Number, FontSize = 13, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 1),
        });
        g.Children.Add(rank);
        _glyph = new ClassGlyph { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(_glyph, 1);
        g.Children.Add(_glyph);
        _name = new TextBlock
        {
            FontSize = 16.5, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 1), MaxWidth = 220,
        };
        SetColumn(_name, 2);
        g.Children.Add(_name);
        g.Children.Add(_gs = Number(3, 14, Visuals.GearText));
        g.Children.Add(_cp = Number(4, 14, Visuals.GearText));
        _rule = new Rectangle { Width = 1.2, Fill = Visuals.Solid(0x8CFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) };
        SetColumn(_rule, 6);
        g.Children.Add(_rule);
        g.Children.Add(_dps = Number(7, 16, Brushes.White));
        g.Children.Add(_damage = Number(8, 16, Brushes.White));
        g.Children.Add(_share = Number(9, 16, Brushes.White));
        Children.Add(g);

        PointerEntered += (_, _) => _hover.BorderBrush = Visuals.Solid(0x66FFFFFF);
        PointerExited += (_, _) => _hover.BorderBrush = Brushes.Transparent;
    }

    private static TextBlock Number(int column, double size, IBrush fg)
    {
        var t = new TextBlock
        {
            FontFamily = Visuals.Number, FontSize = size, FontWeight = FontWeight.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 0, 0),
        };
        SetColumn(t, column);
        return t;
    }

    public void SetNameMax(double max) => _name.MaxWidth = max;

    public void Update(OverlayRow r, bool hasGear)
    {
        ActorId = r.ActorId;
        if (_rankValue != r.Rank)
        {
            _rankValue = r.Rank;
            var (bg, fg, border) = Visuals.Rank(r.Rank);
            _rankDisc.Fill = bg;
            _rankDisc.Stroke = border;
            _rank.Foreground = fg;
            _rank.Text = r.Rank > 0 ? r.Rank.ToString() : "·";   // pets of unknown owners have no place
        }
        if (_class != r.Class)
        {
            _class = r.Class;
            _fill.Background = Visuals.FillOf(r.Class);
            _glyph.Class = r.Class;
        }
        if (_isSelf != r.IsSelf)
        {
            _isSelf = r.IsSelf;
            _self.IsVisible = r.IsSelf;
            _name.TextDecorations = r.IsSelf ? SelfUnderline : null;
        }
        _name.Text = r.Name;
        _gs.Text = r.GearScore;
        _gs.IsVisible = r.GearScore.Length > 0;
        _cp.Text = r.CombatPower;
        _cp.IsVisible = r.CombatPower.Length > 0;
        _rule.IsVisible = hasGear;
        _dps.Text = r.Dps;
        _damage.Text = r.Damage;
        _share.Text = r.Share;
        if (_scale.ScaleX != r.Fill) _scale.ScaleX = r.Fill;
        if (!ReferenceEquals(ToolTip.GetTip(this), r.Tooltip) && (ToolTip.GetTip(this) as string) != r.Tooltip) ToolTip.SetTip(this, r.Tooltip);
    }
}
