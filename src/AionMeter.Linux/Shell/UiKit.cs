using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AionMeter.Linux.Shell;

/// <summary>
/// The WPF Theme.xaml / view styles as code (A2M-48 phase 3b): section cards, labels, hints, chips, buttons, stat tiles, a grid helper and
/// tooltips. Every hosted view builds from these so the Linux tabs keep one look; colours come from <see cref="Visuals"/>.
/// </summary>
public static class UiKit
{
    /// <summary>Section (SettingsView "Section"): faint card with a rounded hairline, 16×12 padding, 12 below.</summary>
    public static Border Section(Control child, string? label = null)
    {
        Control content = child;
        if (label is not null)
        {
            var stack = new StackPanel();
            stack.Children.Add(SectionLabel(label, new Thickness(0, 0, 0, 8)));
            stack.Children.Add(child);
            content = stack;
        }
        return new Border
        {
            Background = Visuals.SectionFill, BorderBrush = Visuals.SectionLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12), Margin = new Thickness(0, 0, 0, 12), Child = content,
        };
    }

    /// <summary>SectionLabel: gold caps in the number font, 11 px.</summary>
    public static TextBlock SectionLabel(string text, Thickness? margin = null) => new()
    {
        Text = text.ToUpperInvariant(), FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 11, Foreground = Visuals.Gold,
        Margin = margin ?? new Thickness(0, 0, 0, 6), LetterSpacing = 0.6,
    };

    /// <summary>Hint: muted wrapped 11.5 px text, indented under a check box by default.</summary>
    public static TextBlock Hint(string text, double indent = 24, IBrush? brush = null) => new()
    {
        Text = text, FontSize = 11.5, Foreground = brush ?? Visuals.TextMute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 0, 0, 6),
    };

    public static TextBlock Text(string text, double size = 12.5, IBrush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Visuals.Text, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The fixed-width label left of a field (SettingsView "Label": 190 px).</summary>
    public static TextBlock FieldLabel(string text, double width = 190) => new()
    {
        Text = text, Width = width, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Visuals.Text, FontSize = 12.5,
        Margin = new Thickness(0, 0, 8, 0),
    };

    /// <summary>[label][control] on one line, wrapping on a narrow card.</summary>
    public static WrapPanel Field(string label, Control control, double top = 8)
    {
        var w = new WrapPanel { Margin = new Thickness(0, top, 0, 0) };
        w.Children.Add(FieldLabel(label));
        w.Children.Add(control);
        return w;
    }

    public static CheckBox Check(string text, bool value, double top = 0) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 }, IsChecked = value, Margin = new Thickness(0, top, 0, 0),
        Foreground = Visuals.Text,
    };

    public static TextBox Input(string text, double width = 140) => new()
    {
        Text = text, Width = width, Background = Visuals.Bg2, Foreground = Visuals.Text, BorderBrush = Visuals.Line, CaretBrush = Visuals.Gold,
        Padding = new Thickness(6, 3), MinHeight = 26, FontSize = 12.5, VerticalContentAlignment = VerticalAlignment.Center,
    };

    public static ComboBox Combo(IEnumerable<string> items, int selected, double minWidth = 160)
    {
        var c = new ComboBox { MinWidth = minWidth, FontSize = 12.5, MaxDropDownHeight = 360 };
        foreach (var i in items) c.Items.Add(new ComboBoxItem { Content = i });
        c.SelectedIndex = selected;
        return c;
    }

    /// <summary>Chip (Theme "Chip"): gold outline pill in the number font; brighter under the mouse.</summary>
    public static Border Chip(string text, Action click, string? tip = null, Geometry? icon = null)
    {
        var label = new TextBlock { Text = text, FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Foreground = Visuals.Gold, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Shapes.Path? glyph = null;
        if (icon is not null)
        {
            glyph = Visuals.Icon(icon, Visuals.Gold, 10, 1.6);
            glyph.Margin = new Thickness(0, 1, 6, 0);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(glyph);
        }
        row.Children.Add(label);
        var b = new Border
        {
            Height = 24, Padding = new Thickness(9, 2), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            BorderBrush = Visuals.GoldDim, Background = Visuals.Solid(0x1AC9A45C), Cursor = new Cursor(StandardCursorType.Hand), Child = row,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.PointerEntered += (_, _) => { b.BorderBrush = Visuals.Gold; label.Foreground = Visuals.GoldBright; if (glyph is not null) glyph.Stroke = Visuals.GoldBright; };
        b.PointerExited += (_, _) => { b.BorderBrush = Visuals.GoldDim; label.Foreground = Visuals.Gold; if (glyph is not null) glyph.Stroke = Visuals.Gold; };
        b.PointerPressed += (_, e) => { if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return; e.Handled = true; click(); };
        if (tip is not null) Tip(b, tip);
        return b;
    }

    /// <summary>GhostButton: Bg2 fill, hairline, 12×6.</summary>
    public static Button Ghost(string text, Action click) => MakeButton(text, click, Visuals.Bg2, Visuals.Text, Visuals.Line);

    /// <summary>PrimaryButton: gold fill, dark text.</summary>
    public static Button Primary(string text, Action click) => MakeButton(text, click, Visuals.Gold, Visuals.Solid(0xFF1A140A), Visuals.Gold, FontWeight.SemiBold);

    private static Button MakeButton(string text, Action click, IBrush bg, IBrush fg, IBrush border, FontWeight? weight = null)
    {
        var b = new Button
        {
            Content = text, Background = bg, Foreground = fg, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6), FontSize = 12.5, FontWeight = weight ?? FontWeight.Normal, Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 8, 0),
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Icon-only button (ToolIcon): 22×22, the stroke turns gold under the mouse.</summary>
    public static Border IconButton(Geometry icon, string tip, Action click, double size = 22)
    {
        var path = Visuals.Icon(icon, Visuals.TextDim, 12, 1.5);
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = path, VerticalAlignment = VerticalAlignment.Center };
        b.PointerEntered += (_, _) => { b.Background = Visuals.Solid(0x14FFFFFF); path.Stroke = Visuals.GoldBright; };
        b.PointerExited += (_, _) => { b.Background = Brushes.Transparent; path.Stroke = Visuals.TextDim; };
        b.PointerPressed += (_, e) => { if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return; e.Handled = true; click(); };
        Tip(b, tip);
        return b;
    }

    /// <summary>Pill: rounded badge (fill + stroke), e.g. the event pills or a status tag.</summary>
    public static Border Pill(string text, IBrush fill, IBrush stroke, IBrush? fg = null) => new()
    {
        CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 11, 3), BorderThickness = new Thickness(1), Background = fill, BorderBrush = stroke,
        Margin = new Thickness(0, 2, 6, 2),
        Child = new TextBlock { Text = text, FontFamily = Visuals.Number, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = fg ?? Visuals.Solid(0xFFF4F1E8) },
    };

    /// <summary>StatTile (BreakdownView): label caps above a big number, optional caption.</summary>
    public static Border StatTile(string label, string value, string? caption = null, IBrush? valueBrush = null, double size = 17, double minWidth = 80)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 10, Foreground = Visuals.Gold, LetterSpacing = 0.5 });
        stack.Children.Add(new TextBlock { Text = value, FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = size, Foreground = valueBrush ?? Visuals.Text, Margin = new Thickness(0, 2, 0, 0) });
        if (!string.IsNullOrEmpty(caption))
            stack.Children.Add(new TextBlock { Text = caption, FontSize = 10.5, Foreground = Visuals.TextMute, TextTrimming = TextTrimming.CharacterEllipsis });
        return new Border
        {
            Background = Visuals.TileFill, BorderBrush = Visuals.TileLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 6, 6), MinWidth = minWidth, Child = stack,
        };
    }

    /// <summary>A grid from a column spec ("Auto,*,64") with the given children placed by column, row 0.</summary>
    public static Grid Row(string columns, params Control?[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } c) continue;
            Grid.SetColumn(c, i);
            g.Children.Add(c);
        }
        return g;
    }

    /// <summary>Puts <paramref name="c"/> at (row, column) of <paramref name="g"/> and returns it.</summary>
    public static T Place<T>(this Grid g, T c, int row, int column, int columnSpan = 1) where T : Control
    {
        Grid.SetRow(c, row);
        Grid.SetColumn(c, column);
        if (columnSpan > 1) Grid.SetColumnSpan(c, columnSpan);
        g.Children.Add(c);
        return c;
    }

    /// <summary>Tooltip with the short delay the WPF views use (150 ms); a string or any control.</summary>
    public static T Tip<T>(T c, object? tip) where T : Control
    {
        ToolTip.SetTip(c, tip);
        ToolTip.SetShowDelay(c, 150);
        return c;
    }

    /// <summary>A wrapped multi-line tooltip body: title in bold, the rest below.</summary>
    public static Control TipBody(string title, string? body)
    {
        var s = new StackPanel { MaxWidth = 340 };
        s.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(body)) s.Children.Add(new TextBlock { Text = body, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return s;
    }

    /// <summary>The hosted views' scroll body: vertical only, the WPF 20 px gutters.</summary>
    public static ScrollViewer Scroll(Control content, Thickness? padding = null) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = padding ?? new Thickness(16, 0, 16, 0),
    };

    /// <summary>A thin progress bar (gold on a dark track), 0..1.</summary>
    public static Control Bar(double fraction, double height = 4)
    {
        var g = new Grid { Height = height };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Visuals.Solid(0x22FFFFFF) });
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(height / 2), Background = Visuals.Gold, HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = new ScaleTransform(Math.Clamp(fraction, 0, 1), 1), RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            Width = double.NaN,
        });
        ((Border)g.Children[1]).HorizontalAlignment = HorizontalAlignment.Stretch;
        return g;
    }
}
