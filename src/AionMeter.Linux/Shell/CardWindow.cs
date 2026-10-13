using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>
/// A stand-alone meter window on Linux (pop-out tab, help): the overlay's card frame (dark fill, gold hairline, gold top line), borderless,
/// draggable from any empty spot, resizable from the corner, WM_CLASS "aionmeter" like the card (Sway floats it on DP-2). Size and place
/// per <c>key</c> in ui.json.
/// </summary>
public class CardWindow : Window
{
    private readonly string _key;
    private readonly AppShell _shell;
    private readonly Border _host;

    public CardWindow(AppShell shell, string key, double width, double height)
    {
        _shell = shell;
        _key = key;
        var place = shell.Ui.Windows.TryGetValue(key, out var p) && p.Length == 4 ? p : null;
        Width = place?[2] ?? width;
        Height = place?[3] ?? height;
        MinWidth = 360;
        MinHeight = 260;
        if (place is not null) { WindowStartupLocation = WindowStartupLocation.Manual; Position = new PixelPoint((int)place[0], (int)place[1]); }
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = true;
        FontFamily = Visuals.Ui;
        Title = AppInfo.Name;

        var root = new Grid { Margin = new Thickness(8) };
        root.Children.Add(new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = Visuals.CardFrame, BoxShadow = BoxShadows.Parse("0 0 14 0 #99000000") });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(1), Background = new SolidColorBrush(Color.FromUInt32(0xFF0D111A), 0.97), IsHitTestVisible = false });
        root.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(60, 0), Background = Visuals.GoldTopLine, IsHitTestVisible = false });
        _host = new Border { Margin = new Thickness(8) };
        root.Children.Add(_host);
        var grip = new Border { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 3), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
        grip.Child = new Avalonia.Controls.Shapes.Path { Data = Visuals.IconGrip, Stroke = Visuals.GoldDim, StrokeThickness = 1.3 };
        grip.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { e.Handled = true; BeginResizeDrag(WindowEdge.SouthEast, e); } };
        root.Children.Add(grip);
        Content = root;
        // Unhandled presses on empty parts of the card move it (the views mark their own clicks handled).
        PointerPressed += (_, e) => { if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not TextBox) BeginMoveDrag(e); };
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Sway floats an Xwayland window at its mapped size, not the one asked for (OverlayWindow, 2026-10-13): ask again once mapped.
            var (w, h) = (Width, Height);
            Width = w + 1; Height = h + 1; Width = w; Height = h;
            if (place is not null) Position = new PixelPoint((int)place[0], (int)place[1]);
        }, DispatcherPriority.Background);
    }

    protected Control? Body { get => _host.Child; set => _host.Child = value; }

    protected override void OnClosed(EventArgs e)
    {
        _shell.Ui.Windows[_key] = [Position.X, Position.Y, Bounds.Width, Bounds.Height];
        _shell.Ui.Save();
        base.OnClosed(e);
    }
}

/// <summary>The pop-out of a tab: the view in its own card window with an X (WPF PopOut / ShowBreakdownWindow / OpenSettingsWindow).</summary>
public sealed class PopOutWindow : CardWindow
{
    private readonly IHostedView _view;

    public PopOutWindow(AppShell shell, IHostedView view, double width, double height) : base(shell, "popout-" + view.Key, width, height)
    {
        _view = view;
        view.Hosted = false;
        view.CloseRequested += Close;
        shell.LanguageChanged += view.LanguageChanged;
        Body = view.View;
        Opened += (_, _) => view.Shown();
        Closed += (_, _) => { view.Hidden(); view.CloseRequested -= Close; shell.LanguageChanged -= view.LanguageChanged; };
    }
}
