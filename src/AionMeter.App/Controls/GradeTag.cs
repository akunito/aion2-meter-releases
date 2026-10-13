using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AionMeter.Core.GameDb;

namespace AionMeter.App.Controls;

/// <summary>
/// The grade chip after an item name ("Artisan's Refining Stone [legendary]"): several items share a name and differ only by grade, so the
/// colour alone is not enough (Diego could not tell which refining stone he was adding to Prices, 2026-10-12). Colours from <see cref="ItemTile"/>.
/// Bound to XAML rows through the implicit DataTemplate in each view; built in code through <see cref="Inline"/>.
/// </summary>
public sealed class GradeTag
{
    public string Text { get; }
    public Brush Fore { get; }
    public Brush Line { get; }
    public Brush Back { get; }
    private GradeTag(string text, Brush fore, Brush line, Brush back) { Text = text; Fore = fore; Line = line; Back = back; }

    // 7 grades × 2 languages, made once: rows of a 1,684-recipe list share them
    private static readonly GradeTag?[] Cache = new GradeTag?[16];

    /// <summary>Null for an unknown grade (nothing to show).</summary>
    public static GradeTag? Of(Grade g, string lang)
    {
        if (g == Grade.Unknown) return null;
        var ix = (int)g * 2 + (lang == "es" ? 1 : 0);
        if (Cache[ix] is { } t) return t;
        var line = ItemTile.LineOf(g);
        var back = line is SolidColorBrush sb ? SkillTile.Frozen(sb.Color.R, sb.Color.G, sb.Color.B, 0x2E) : Brushes.Transparent;
        return Cache[ix] = new GradeTag(ItemGrades.Word(g, lang), ItemTile.TextOf(g), line, back);
    }

    public FrameworkElement Chip(double size = 10.5) => new Border
    {
        Background = Back, BorderBrush = Line, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1),
        Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = Text, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = Fore },
    };

    /// <summary>The chip as an inline for a TextBlock that already holds the name.</summary>
    public Inline Inline(double size = 10.5) => new InlineUIContainer(Chip(size)) { BaselineAlignment = BaselineAlignment.Center };
}
