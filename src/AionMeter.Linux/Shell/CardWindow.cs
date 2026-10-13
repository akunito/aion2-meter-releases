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
        var output = shell.Ui.WindowOutputs.TryGetValue(key, out var o) ? o : "";
        _zoomApplied = shell.Ui.UiScale;
        Width = (place?[2] ?? width) * _zoomApplied;
        Height = (place?[3] ?? height) * _zoomApplied;
        MinWidth = 360 * _zoomApplied;
        MinHeight = 260 * _zoomApplied;
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
        _zoom = new LayoutTransformControl { Child = root, LayoutTransform = new ScaleTransform(_zoomApplied, _zoomApplied) };
        Content = _zoom;
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if ((e.KeyModifiers & KeyModifiers.Control) == 0 || e.Delta.Y == 0) return;
            e.Handled = true;
            shell.SetUiScale(shell.Ui.UiScale + Math.Sign(e.Delta.Y) * AionMeter.Service.Overlay.UiScale.Step);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        shell.UiScaleChanged += ApplyZoom;
        Closed += (_, _) => shell.UiScaleChanged -= ApplyZoom;
        // Unhandled presses on empty parts of the card move it (the views mark their own clicks handled).
        PointerPressed += (_, e) => { if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not TextBox) BeginMoveDrag(e); };
        var (askW, askH) = (Width, Height);
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // Sway floats an Xwayland window at its mapped size, not the one asked for (OverlayWindow.Place, 2026-10-13): ask again once
            // mapped, with the size asked at construction (Width/Height now hold Sway's) and the final value in a separate pass.
            Width = askW + 1; Height = askH + 1;
            Dispatcher.UIThread.Post(() => { Width = askW; Height = askH; }, DispatcherPriority.Background);
            if (place is not null) WindowPlace.Restore(this, output, place[0], place[1]);
        }, DispatcherPriority.Background);
    }

    private readonly LayoutTransformControl _zoom;
    private double _zoomApplied;

    private void ApplyZoom(double z)
    {
        if (Math.Abs(z - _zoomApplied) < 0.001) return;
        var ratio = z / _zoomApplied;
        _zoomApplied = z;
        _zoom.LayoutTransform = new ScaleTransform(z, z);
        MinWidth = 360 * z;
        MinHeight = 260 * z;
        Width = Math.Max(MinWidth, Bounds.Width * ratio);
        Height = Math.Max(MinHeight, Bounds.Height * ratio);
    }

    protected Control? Body { get => _host.Child; set => _host.Child = value; }

    protected override void OnClosed(EventArgs e)
    {
        if (WindowPlace.Capture(this) is var (output, x, y))
        {
            _shell.Ui.Windows[_key] = [x, y, Bounds.Width / _zoomApplied, Bounds.Height / _zoomApplied];
            _shell.Ui.WindowOutputs[_key] = output;
            _shell.Ui.Save();
        }
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
