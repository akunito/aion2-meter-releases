using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using AionMeter.Linux.Shell;

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
    private static OverlayWindow? _window;

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
            var client = new DaemonClient(SocketPath);
            var shell = new AppShell(client, Ui);
            _window = new OverlayWindow(shell);
            desktop.MainWindow = _window;
            desktop.ShutdownRequested += (_, _) => { client.Dispose(); shell.Dispose(); };
            client.Start();
            shell.StartEngine();
            if (StartTab is { } tab) Dispatcher.UIThread.Post(() => _window.SelectTab(tab), DispatcherPriority.Background);
            if (StartFight is { } fight) DispatcherTimer.RunOnce(() => shell.OpenFight(fight), TimeSpan.FromSeconds(1.5));
            if (StartPopOut) DispatcherTimer.RunOnce(() => _window.PopOut(), TimeSpan.FromSeconds(StartFight is null ? 1 : 3));
            if (StartHelp is { } help) Dispatcher.UIThread.Post(() => shell.ShowHelp(help), DispatcherPriority.Background);
            if (StartWindow is { } win) DispatcherTimer.RunOnce(() => shell.ShowWindow(win), TimeSpan.FromSeconds(1));
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ToggleClickThroughFromSignal() => Dispatcher.UIThread.Post(() => _window?.ToggleClickThrough());
}

/// <summary>The UI thread for Service code that edits UI-owned state (WPF installs a Dispatcher host the same way).</summary>
internal sealed class AvaloniaUiHost : AionMeter.Service.IUiHost
{
    public Task Invoke(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
    public Task<T> Invoke<T>(Func<T> func) => Dispatcher.UIThread.InvokeAsync(func).GetTask();
}
