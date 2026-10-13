using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using AionMeter.Core.Capture;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux.Shell;

/// <summary>
/// A2M-48 UX: the tools window's tray icon (Avalonia TrayIcon = StatusNotifierItem + com.canonical.dbusmenu on Linux; waybar's `tray`
/// module is the SNI host on DESK). It exists while the window is hidden, so closing the window only hides it.
/// </summary>
public sealed class Tray : IDisposable
{
    private const string RestartCommand = "systemctl restart aion2-meter-daemon";
    private readonly Application _app;
    private readonly AppShell _shell;
    private readonly Func<bool> _toolsVisible;
    private readonly Action<string?> _show;
    private readonly Action _toggleTools, _quit;
    private readonly TrayIcon _icon;
    private NativeMenuItem _status = null!, _tools = null!;
    private string _statusText = "";

    public Tray(Application app, AppShell shell, Func<bool> toolsVisible, Action<string?> show, Action toggleTools, Action quit)
    {
        _app = app;
        _shell = shell;
        _toolsVisible = toolsVisible;
        _show = show;
        _toggleTools = toggleTools;
        _quit = quit;
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AionMeter.Linux/Assets/app.ico"))),
            ToolTipText = AppInfo.Name,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => _toggleTools();
        _statusText = UiText.Current.TrayDaemonOff;
        Build();
        TrayIcon.SetIcons(app, [_icon]);
        shell.Daemon.Status += OnStatus;
        shell.Daemon.ConnectionChanged += OnConnection;
        shell.LanguageChanged += Build;
    }

    private void Build()
    {
        var t = UiText.Current;
        var menu = new NativeMenu();
        _status = new NativeMenuItem(_statusText) { IsEnabled = false };
        menu.Add(_status);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(t.TrayOverlay, () => { if (!FrontEnds.ToggleOverlay()) Notify(t.TrayNoOverlay); }));
        menu.Add(Item(t.TrayClickThrough, () => { if (!FrontEnds.OverlayClickThrough()) Notify(t.TrayNoOverlay); }));
        menu.Add(new NativeMenuItemSeparator());
        _tools = Item(_toolsVisible() ? t.TrayToolsHide : t.TrayToolsShow, _toggleTools);
        menu.Add(_tools);
        var tabs = new NativeMenu();
        foreach (var spec in ViewRegistry.Strip)
        {
            var key = spec.Key;
            tabs.Add(Item(spec.Tip(t), () => _show(key)));
        }
        menu.Add(new NativeMenuItem(t.TrayTabs) { Menu = tabs });
        menu.Add(Item(t.SettingsTitle, () => _show("settings")));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(t.TrayRestartDaemon, RestartDaemon));
        menu.Add(Item(t.TrayQuit, _quit));
        _icon.Menu = menu;
    }

    private static NativeMenuItem Item(string header, Action click)
    {
        var i = new NativeMenuItem(header);
        i.Click += (_, _) => click();
        return i;
    }

    /// <summary>The window was shown or hidden: the menu item says what it will do.</summary>
    public void SyncTools(bool visible) => _tools.Header = visible ? UiText.Current.TrayToolsHide : UiText.Current.TrayToolsShow;

    private void OnStatus(OverlayStatus s)
    {
        var msg = s.Message.Length > 0 ? s.Message : s.State.ToString();
        SetStatus(string.Format(UiText.Current.TrayDaemon, UiText.Current.CaptureStatusText(msg)));
    }

    private void OnConnection(bool on)
    {
        if (!on) SetStatus(UiText.Current.TrayDaemonOff);
    }

    private void SetStatus(string text)
    {
        if (text == _statusText) return;
        _statusText = text;
        _status.Header = text;
        _icon.ToolTipText = AppInfo.Name + " — " + text;
    }

    /// <summary>systemctl asks polkit (an agent may prompt); on failure a notification carries the command to run in a terminal.</summary>
    private static void RestartDaemon()
    {
        var failed = string.Format(UiText.Current.TrayRestartFailed, "sudo " + RestartCommand);
        _ = Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("systemctl") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in RestartCommand.Split(' ').Skip(1)) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) { Notify(failed); return; }
                var err = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120_000)) { Notify(failed); return; }
                if (p.ExitCode != 0) { Log.Info($"tray: {RestartCommand} exit {p.ExitCode}: {err.Result.Trim()}"); Notify(failed); }
                else Log.Info("tray: daemon restarted");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Notify(failed); }
        });
    }

    public static void Notify(string text)
    {
        try
        {
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(AppInfo.Name);
            psi.ArgumentList.Add(AppInfo.Name);
            psi.ArgumentList.Add(text);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.Error.WriteLine(text); }
    }

    public void Dispose()
    {
        _shell.Daemon.Status -= OnStatus;
        _shell.Daemon.ConnectionChanged -= OnConnection;
        _shell.LanguageChanged -= Build;
        _icon.IsVisible = false;
        _icon.Dispose();
        TrayIcon.SetIcons(_app, []);
    }
}
