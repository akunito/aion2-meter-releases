using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// A2M-30 "Restore from code" on the card's output (A2M-50, port of the Avalonia RestoreWindow; opened from Settings → Backup): the server copy
/// of a backup code comes back to this PC through <see cref="RestoreFlow"/>; never automatic. The restore writes the UI's own files (builds,
/// the watch list, settings.json) through the viewer engine and tells the daemon to reload.
/// </summary>
internal sealed class RestoreWindow : DialogCard
{
    private static UiText T => UiText.Current;
    private readonly Gtk.Entry _code;
    private readonly Gtk.Button _go;
    private readonly Gtk.Label _state;

    public RestoreWindow(AppShell shell) : base(shell, "restore", UiText.Current.RestoreTitle, 560, 280)
    {
        var stack = UiKit.VBox();
        UiKit.Margin(stack, 12, 0, 12, 12);
        var intro = UiKit.Text(T.RestoreIntro, 12.5, Fg.Dim);
        intro.Vexpand = false;
        intro.MarginBottom = UiKit.Px(12);
        stack.Append(intro);
        _code = UiKit.Input("", double.NaN);
        _code.AddCssClass("k-mono13");
        _code.PlaceholderText = "XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX";
        _code.OnActivate += (_, _) => _ = RestoreAsync();
        _go = UiKit.Primary(T.RestoreGo, () => _ = RestoreAsync());
        _go.MarginStart = UiKit.Px(8);
        _go.MarginEnd = 0;
        stack.Append(UiKit.Row("*,Auto", _code, _go));
        _state = UiKit.Text("", 12.5, Fg.Dim);
        _state.MarginTop = UiKit.Px(10);
        stack.Append(_state);
        SetContent(stack);
    }

    protected override void Opened() => _code.GrabFocus();

    private async Task RestoreAsync()
    {
        if (!_go.Sensitive) return;
        if (Shell.Engine is not { } engine) { Show("…", Fg.Dim); return; }
        if (engine.Settings.Transient) return;
        _go.Sensitive = false;
        Show(T.RestoreWorking, Fg.Dim);
        try
        {
            // RestoreFlow awaits HTTP: the continuation comes back on the main loop through GLib's SynchronizationContext (RunWithSynchronizationContext)
            var r = await RestoreFlow.RunAsync(_code.GetText(), engine);
            Show(r.Text, r.Outcome == RestoreFlow.Outcome.Restored ? Fg.Green : Fg.Red);
            // the restore saved settings.json (install id, telemetry marks, favourites) and the watch list: the daemon takes them now, not in 2 s
            if (r.Outcome == RestoreFlow.Outcome.Restored) Shell.Daemon.Send("reload-settings");
        }
        finally { _go.Sensitive = true; }
    }

    private void Show(string text, Fg fg) { _state.SetText(text); UiKit.SetFg(_state, fg); }
}
