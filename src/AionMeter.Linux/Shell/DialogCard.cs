using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>
/// A WPF dialog window on Linux (A2M-48 phase 3c: Import from questlog, Restore from code, Report a problem): the card frame of
/// <see cref="CardWindow"/> with a serif title, an X and the body below. Not modal: Sway floats it like the card (WM_CLASS aionmeter); the
/// owner talks to it through callbacks. One instance per kind: <see cref="ShowSingle{T}"/>.
/// </summary>
public abstract class DialogCard : CardWindow
{
    private static readonly Dictionary<Type, DialogCard> Open = new();
    private readonly Grid _root;
    protected readonly AppShell Shell;

    protected DialogCard(AppShell shell, string key, string title, double width, double height) : base(shell, key, width, height)
    {
        Shell = shell;
        _root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var head = new Grid { Margin = new Thickness(12, 4, 2, 10), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = title, FontFamily = Visuals.Title, FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Text, VerticalAlignment = VerticalAlignment.Center });
        var close = UiKit.IconButton(Visuals.IconClose, UiText.Current.TipClose, Close);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        _root.Children.Add(head);
        Body = _root;
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); } };
    }

    protected void SetContent(Control content)
    {
        if (_root.Children.Count > 1) _root.Children.RemoveAt(1);
        Grid.SetRow(content, 1);
        _root.Children.Add(content);
    }

    /// <summary>Brings the open dialog of this kind to the front, or makes and shows one.</summary>
    public static T ShowSingle<T>(Func<T> make) where T : DialogCard
    {
        if (Open.TryGetValue(typeof(T), out var w) && w.IsVisible) { w.Activate(); return (T)w; }
        var n = make();
        Open[typeof(T)] = n;
        n.Closed += (_, _) => { if (Open.TryGetValue(typeof(T), out var cur) && ReferenceEquals(cur, n)) Open.Remove(typeof(T)); };
        n.Show();
        return n;
    }
}
