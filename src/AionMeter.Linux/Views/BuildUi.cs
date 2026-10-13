using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>The Build tab's styles as code (WPF BuildView.xaml resources: Card, Band, Lbl, SubTab, Flat, TextBtn, links, tooltip frame).</summary>
internal static class BuildUi
{
    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    public static Border Card(Control child) => new()
    {
        Background = BuildColors.Card, BorderBrush = BuildColors.Line, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(8),
        Margin = new Thickness(0, 0, 0, 10), Child = child,
    };

    public static Border Band(Control child) => new() { Background = BuildColors.Band, CornerRadius = new CornerRadius(8, 8, 0, 0), Padding = new Thickness(12, 8), Child = child };

    public static TextBlock BandTitle(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeight.Medium, Foreground = BuildColors.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public static TextBlock Lbl(string text) => new() { Text = text, FontSize = 11, Foreground = BuildColors.Dim, VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Muted(string text, double size = 12.5, IBrush? brush = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? BuildColors.Mute, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>questlog's flat button: #2b2b2b fill, #373737 hairline, brighter frame under the mouse.</summary>
    public static Button Flat(string text, Action click, double size = 12.5, string? tip = null)
    {
        var b = new Button
        {
            Content = text, Background = BuildColors.Btn, Foreground = BuildColors.Text, BorderBrush = BuildColors.Line2, BorderThickness = new Thickness(0.8),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5), FontSize = size, Cursor = Hand, VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
        b.Click += (_, _) => click();
        if (tip is not null) UiKit.Tip(b, tip);
        return b;
    }

    /// <summary>Text-only button (Clear, + Add macro, the footer's save): muted, white under the mouse.</summary>
    public static TextBlock TextBtn(string text, Action click, double size = 12, IBrush? brush = null, string? tip = null)
    {
        var fg = brush ?? BuildColors.Mute;
        var t = new TextBlock { Text = text, FontSize = size, Foreground = fg, Cursor = Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) };
        t.PointerEntered += (_, _) => t.Foreground = BuildColors.Text;
        t.PointerExited += (_, _) => t.Foreground = fg;
        t.PointerPressed += (_, e) => { if (!e.GetCurrentPoint(t).Properties.IsLeftButtonPressed) return; e.Handled = true; click(); };
        if (tip is not null) UiKit.Tip(t, tip);
        return t;
    }

    /// <summary>A blue inline link (WPF Hyperlink in the source / target lines).</summary>
    public static TextBlock Link(string text, Action click, string? tip = null) => TextBtn(text, click, 12, BuildColors.Blue, tip);

    /// <summary>questlog's sub-tab button: transparent until chosen, then #2b2b2b with a #373737 frame.</summary>
    public static Border SubTab(string text, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 13, Foreground = BuildColors.Dim, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border
        {
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 6, 0), Cursor = Hand, Child = label,
        };
        b.PointerPressed += (_, e) => { if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return; e.Handled = true; click(); };
        return b;
    }

    public static void SetSubTab(Border b, bool on)
    {
        b.Background = on ? BuildColors.Btn : Brushes.Transparent;
        b.BorderBrush = on ? BuildColors.Line2 : Brushes.Transparent;
        if (b.Child is TextBlock t) t.Foreground = on ? BuildColors.Text : BuildColors.Dim;
    }

    /// <summary>The questlog-style tooltip body frame (mock .tip: #141414, #373737 hairline, radius 8, 10 padding).</summary>
    public static Border TipFrame(Control content, double width = 340) => new()
    {
        Background = BuildColors.TipBack, BorderBrush = BuildColors.Line2, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10), Width = width, Child = content,
    };

    public static TextBlock TipSection(string text) => new() { Text = text.ToUpper(UiText.Current.Culture), FontSize = 11, Foreground = BuildColors.Mute, Margin = new Thickness(0, 4, 0, 3) };

    public static Border TagChip(string text) => new()
    {
        Background = BuildColors.TagBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1), Margin = new Thickness(0, 0, 4, 0),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = BuildColors.Dim },
    };

    /// <summary>Runs of a description into a TextBlock (numbers in the questlog peach, medium weight).</summary>
    public static void SetRuns(TextBlock tb, TextSeg[] segs)
    {
        var inl = tb.Inlines ??= new InlineCollection();
        inl.Clear();
        foreach (var seg in segs)
        {
            var r = new Run(seg.Text);
            if (seg.Num) { r.Foreground = BuildColors.Num; r.FontWeight = FontWeight.Medium; }
            inl.Add(r);
        }
    }

    /// <summary>A yes/no question in a flyout under <paramref name="anchor"/> (WPF MessageBox YesNo; Avalonia has no message box).</summary>
    public static void Confirm(Control anchor, string text, Action yes)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var panel = new StackPanel { MaxWidth = 340, Margin = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Visuals.Text, Margin = new Thickness(0, 0, 0, 10) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(UiKit.Primary(UiText.Current.Code == "es" ? "Sí" : "Yes", () => { flyout.Hide(); yes(); }));
        row.Children.Add(UiKit.Ghost(UiText.Current.Cancel, flyout.Hide));
        panel.Children.Add(row);
        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    /// <summary>The default browser through xdg-open (Process.Start with UseShellExecute is Windows-only in spirit).</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Info($"xdg-open {url}: {e.Message}"); }
    }

    public static async Task<bool> CopyText(Control from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is not { } cb) return false;
        try { await cb.SetTextAsync(text); return true; }
        catch (Exception e) when (e is InvalidOperationException or IOException) { Log.Info($"clipboard: {e.Message}"); return false; }
    }
}
