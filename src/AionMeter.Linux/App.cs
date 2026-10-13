using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using AionMeter.Linux.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux;

public sealed class App : Application
{
    public static string SocketPath { get; set; } = "";
    public static UiSettings Ui { get; set; } = new();
    /// <summary>--tab / --fight / --help-page: what to open once the card is up (tests, screenshots).</summary>
    public static string? StartTab { get; set; }
    public static string? StartFight { get; set; }
    public static string? StartHelp { get; set; }
    public static bool StartPopOut { get; set; }
    /// <summary>--view key=value (repeatable): start state for the opened tab (search, open cards, expanded row…), the WPF render tool's
    /// Sample* hooks; screenshots need it because neither xdotool nor wlrctl clicks reach Avalonia under Xwayland (measured 2026-10-13).</summary>
    public static Dictionary<string, string> ViewArgs { get; } = new();
    public static string? StartWindow { get; set; }
    /// <summary>A2M-48 UX: the single tools window (not a --data-dir test instance): command socket, tray, hide on close.</summary>
    public static bool Primary { get; set; }
    public static string UiSocket { get; set; } = "";
    public static bool StartHidden { get; set; }
    /// <summary>The tray icon is up: closing the window hides it (the process keeps the tray).</summary>
    public static bool HideOnClose { get; private set; }
    public static bool Quitting { get; private set; }
    private static OverlayWindow? _window;
    private static IClassicDesktopStyleApplicationLifetime? _desktop;
    private static UiCommandServer? _commands;
    private static Tray? _tray;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        // Fluent's accent in the WPF gold, so check boxes, sliders and focus rings match the card
        var theme = new FluentTheme();
        theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources
        {
            Accent = Color.Parse("#C9A45C"), AltHigh = Color.Parse("#0B0F17"), RegionColor = Color.Parse("#111726"), ChromeMediumLow = Color.Parse("#19203A"),
            BaseHigh = Color.Parse("#F1E9D8"),
        };
        Styles.Add(theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // settings dictionaries the build telemetry records into (Telemetry.SendBuildAsync) are edited on the UI thread, as in WPF
            AionMeter.Service.UiHost.Current = new AvaloniaUiHost();
            _desktop = desktop;
            var client = new DaemonClient(SocketPath);
            var shell = new AppShell(client, Ui);
            _window = new OverlayWindow(shell);
            if (Primary)
            {
                // the process outlives its window (tray); Quit in the tray menu, `aionmeter-ui --quit` or {"cmd":"quit"} end it
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _commands = UiCommandServer.Start(UiSocket, Handle);
                try { _tray = new Tray(this, shell, () => _window.IsVisible, ShowTab, ToggleTools, Quit); }
                catch (Exception e) { Log.Error("tray icon", e); Console.Error.WriteLine("tray icon failed: " + e.Message); }
                HideOnClose = _tray is not null;
                _window.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) _tray?.SyncTools(_window.IsVisible); };
            }
            if (!StartHidden) desktop.MainWindow = _window;
            // Exit, not ShutdownRequested: an explicit Shutdown() (tray Quit, {"cmd":"quit"}) does not raise the latter (measured: the socket file stayed)
            desktop.Exit += (_, _) => { Quitting = true; _commands?.Dispose(); _tray?.Dispose(); client.Dispose(); shell.Dispose(); };
            client.Start();
            shell.StartEngine();
            // A2M-48 UX: the layer-shell card is the DPS card over the game; this window opens on a tool tab (the last one used, Items first)
            var tab = StartTab ?? (StartFight is null ? UiTabs.StartTab(Ui.LastTab) : null);
            if (tab is not null) Dispatcher.UIThread.Post(() => _window.SelectTab(tab), DispatcherPriority.Background);
            if (StartFight is { } fight) DispatcherTimer.RunOnce(() => shell.OpenFight(fight), TimeSpan.FromSeconds(1.5));
            if (StartPopOut) DispatcherTimer.RunOnce(() => _window.PopOut(), TimeSpan.FromSeconds(StartFight is null ? 1 : 3));
            if (StartHelp is { } help) Dispatcher.UIThread.Post(() => shell.ShowHelp(help), DispatcherPriority.Background);
            if (StartWindow is { } win) DispatcherTimer.RunOnce(() => shell.ShowWindow(win), TimeSpan.FromSeconds(1));
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ToggleClickThroughFromSignal() => Dispatcher.UIThread.Post(() => _window?.ToggleClickThrough());

    /// <summary>A command from the UI socket (UI thread).</summary>
    private static void Handle(UiCommand cmd)
    {
        switch (cmd.Kind)
        {
            case UiCommandKind.Show: ShowTab(cmd.Tab); break;
            case UiCommandKind.Raise: ShowTab(null); break;
            case UiCommandKind.Hide: _window?.HideToTray(); break;
            case UiCommandKind.Toggle: ToggleTools(); break;
            case UiCommandKind.Quit: Quit(); break;
        }
    }

    /// <summary>Shows (maps, places) and raises the window, on <paramref name="tab"/> when given.</summary>
    private static void ShowTab(string? tab)
    {
        if (_window is not { } w) return;
        if (!w.IsVisible) w.Show();   // Opened runs Place on every show
        if (tab is not null) w.SelectTab(tab);
        // Sway has focus_on_window_activation urgent on DESK: _NET_ACTIVE_WINDOW only marks it; focus it by its X11 id
        w.Activate();
        Dispatcher.UIThread.Post(() => SwayIpc.OnWindow(w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, "focus"), DispatcherPriority.Background);
    }

    private static void ToggleTools()
    {
        if (_window is null) return;
        if (_window.IsVisible) _window.HideToTray();
        else ShowTab(null);
    }

    private static void Quit()
    {
        Quitting = true;
        _desktop?.Shutdown();
    }
}

/// <summary>The UI thread for Service code that edits UI-owned state (WPF installs a Dispatcher host the same way).</summary>
internal sealed class AvaloniaUiHost : AionMeter.Service.IUiHost
{
    public Task Invoke(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    public Task<T> Invoke<T>(Func<T> func) => Dispatcher.UIThread.InvokeAsync(func).GetTask();
}
