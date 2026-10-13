using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>
/// A tab of the Linux card (A2M-48 phase 3b), the WPF OverlayWindow tab host contract: the card shows <see cref="View"/> in place of the DPS
/// rows, the `?` opens data/help/&lt;<see cref="HelpKey"/>&gt;.&lt;lang&gt;.md, the pop-out button asks <see cref="PopOut"/> for the view of a
/// stand-alone window. Register a factory in <see cref="ViewRegistry"/> (one line) to put a view in the tab strip.
/// </summary>
public interface IHostedView
{
    /// <summary>Registry key: "settings", "fight", "atlas", "top", "prices", "craft", "build", "items", "timers".</summary>
    string Key { get; }
    Control View { get; }
    /// <summary>data/help/&lt;HelpKey&gt;.&lt;lang&gt;.md ("meter" for the fight breakdown).</summary>
    string HelpKey { get; }
    /// <summary>In the card: a Back chip (and Esc) raise <see cref="CloseRequested"/>; in a pop-out window: an X does.</summary>
    bool Hosted { get; set; }
    /// <summary>Brought to front (card tab selected, window shown): start timers, refresh from the engine / socket.</summary>
    void Shown();
    /// <summary>Another tab in front, or the window closed: stop timers. A hidden view keeps its state and is reused.</summary>
    void Hidden();
    /// <summary>The language changed (Settings save): rebuild the texts.</summary>
    void LanguageChanged();
    event Action? CloseRequested;
    /// <summary>The view the pop-out window shows: a fresh one (default), a copy on the same data, or this one moved out (the card then drops it).</summary>
    IHostedView PopOut();
}

/// <summary>
/// Base for hosted views: the WPF view frame (Bg0 card with a GoldDim hairline), a 52 px header with Back chip, serif title, `?` and the
/// view's own chips on the right (<see cref="HeaderRight"/>), the body (<see cref="SetBody"/>) and an optional footer bar (<see cref="SetFooter"/>).
/// </summary>
public abstract class HostedView : UserControl, IHostedView
{
    protected readonly AppShell Shell;
    private readonly Border _back, _close;
    private readonly TextBlock _title;
    private readonly Grid _root;
    private Control? _footer;
    protected readonly StackPanel HeaderRight;

    protected HostedView(AppShell shell, string key, string title, string? helpKey = null)
    {
        Shell = shell;
        Key = key;
        HelpKey = helpKey ?? key;
        Focusable = true;
        _root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = new Grid { Margin = new Thickness(14, 8, 8, 8), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _back = UiKit.Chip(UiText.Current.Back, () => CloseRequested?.Invoke(), null, Visuals.IconBack);
        _back.Margin = new Thickness(0, 0, 10, 0);
        left.Children.Add(_back);
        _title = new TextBlock { Text = title, FontFamily = Visuals.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        left.Children.Add(_title);
        var help = UiKit.IconButton(Visuals.IconHelp, UiText.Current.TipHelp, () => Shell.ShowHelp(HelpKey));
        help.Margin = new Thickness(8, 1, 0, 0);
        left.Children.Add(help);
        header.Children.Add(left);
        HeaderRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(HeaderRight, 1);
        header.Children.Add(HeaderRight);
        _close = UiKit.IconButton(Visuals.IconClose, UiText.Current.Cancel, () => CloseRequested?.Invoke());
        _close.Margin = new Thickness(6, 0, 0, 0);
        _close.IsVisible = false;
        Grid.SetColumn(_close, 2);
        header.Children.Add(_close);
        _root.Children.Add(header);
        Content = new Border { Background = Visuals.Bg0, BorderBrush = Visuals.GoldDim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _root, ClipToBounds = true };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && Hosted && !e.Handled) { e.Handled = true; CloseRequested?.Invoke(); } };
        PointerPressed += (_, _) => { if (Hosted && !IsKeyboardFocusWithin) Focus(); };
    }

    public string Key { get; }
    public Control View => this;
    public string HelpKey { get; protected set; }
    public event Action? CloseRequested;

    public bool Hosted
    {
        get => _back.IsVisible;
        set { _back.IsVisible = value; _close.IsVisible = !value; }
    }

    protected string Title { get => _title.Text ?? ""; set => _title.Text = value; }

    protected void SetBody(Control body)
    {
        if (_root.Children.Count > 1 && Grid.GetRow(_root.Children[1]) == 1) _root.Children.RemoveAt(1);
        Grid.SetRow(body, 1);
        _root.Children.Insert(1, body);
    }

    protected void SetFooter(Control? footer)
    {
        if (_footer is not null) _root.Children.Remove(_footer);
        _footer = footer;
        if (footer is null) return;
        var bar = new Border { BorderBrush = Visuals.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10), Child = footer };
        Grid.SetRow(bar, 2);
        _footer = bar;
        _root.Children.Add(bar);
    }

    protected void RaiseClose() => CloseRequested?.Invoke();

    public virtual void Shown() { }
    public virtual void Hidden() { }
    public virtual void LanguageChanged()
    {
        if (_back.Child is StackPanel { Children: [.., TextBlock label] }) label.Text = UiText.Current.Back;
    }
    public virtual IHostedView PopOut() => ViewRegistry.Create(Key, Shell) ?? this;
}
