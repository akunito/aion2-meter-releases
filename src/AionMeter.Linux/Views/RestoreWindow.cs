using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// A2M-30 "Restore from code" on Linux (WPF Windows/RestoreWindow, opened from Settings → Backup): the server copy of a backup code comes
/// back to this PC through <see cref="RestoreFlow"/>; never automatic. The restore writes the UI's own files (builds, the watch list,
/// settings.json) through the viewer engine and tells the daemon to reload.
/// </summary>
internal sealed class RestoreWindow : DialogCard
{
    private static UiText T => UiText.Current;
    private readonly TextBox _code;
    private readonly Button _go;
    private readonly TextBlock _state;

    public RestoreWindow(AppShell shell) : base(shell, "restore", UiText.Current.RestoreTitle, 560, 280)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        stack.Children.Add(new TextBlock { Text = T.RestoreIntro, TextWrapping = TextWrapping.Wrap, Foreground = Visuals.TextDim, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 12) });
        _code = UiKit.Input("", double.NaN);
        _code.FontFamily = new FontFamily("DejaVu Sans Mono, Liberation Mono, monospace");
        _code.FontSize = 13.5;
        _code.Watermark = "XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX";
        _code.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = RestoreAsync(); } };
        _go = UiKit.Primary(T.RestoreGo, () => _ = RestoreAsync());
        _go.Margin = new Thickness(8, 0, 0, 0);
        stack.Children.Add(UiKit.Row("*,Auto", _code, _go));
        _state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Visuals.TextDim, MinHeight = 18, FontSize = 12.5 };
        stack.Children.Add(_state);
        SetContent(stack);
        Opened += (_, _) => _code.Focus();
    }

    private async Task RestoreAsync()
    {
        if (!_go.IsEnabled) return;
        if (Shell.Engine is not { } engine) { Show("…", Visuals.TextDim); return; }
        if (engine.Settings.Transient) return;
        _go.IsEnabled = false;
        Show(T.RestoreWorking, Visuals.TextDim);
        try
        {
            var r = await RestoreFlow.RunAsync(_code.Text ?? "", engine);
            Show(r.Text, r.Outcome == RestoreFlow.Outcome.Restored ? Visuals.Green : Visuals.Red);
            // the restore saved settings.json (install id, telemetry marks, favourites) and the watch list: the daemon takes them now, not in 2 s
            if (r.Outcome == RestoreFlow.Outcome.Restored) Shell.Daemon.Send("reload-settings");
        }
        finally { _go.IsEnabled = true; }
    }

    private void Show(string text, IBrush brush) { _state.Text = text; _state.Foreground = brush; }
}
