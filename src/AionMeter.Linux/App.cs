using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace AionMeter.Linux;

public sealed class App : Application
{
    public static string SocketPath { get; set; } = "";
    public static UiSettings Ui { get; set; } = new();
    private static OverlayWindow? _window;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var client = new DaemonClient(SocketPath);
            _window = new OverlayWindow(client, Ui);
            desktop.MainWindow = _window;
            desktop.ShutdownRequested += (_, _) => client.Dispose();
            client.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ToggleClickThroughFromSignal() => Dispatcher.UIThread.Post(() => _window?.ToggleClickThrough());
}
