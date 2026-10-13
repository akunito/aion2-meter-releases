using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// "Report a problem" on Linux (WPF Windows/BugReportWindow, A2M-19 + A2M-45): the player's note and the three Privacy ticks (character
/// summary, Top DPS rows, inventory; saved as the next defaults), "Create zip" (reports/report_&lt;ts&gt;.zip, the folder opens) and "Send to
/// Diego" (the zip to the n8n report webhook through <see cref="BugReport.SendAsync"/>, under the client limits of <see cref="BugReport.SendBlock"/>,
/// whose countdown the button shows). What goes in the zip is decided by Core/Diagnostics/BugReportFiles from the ticks.
/// </summary>
internal sealed class ReportWindow : DialogCard
{
    private static UiText T => UiText.Current;
    private readonly TextBox _note;
    private readonly CheckBox _summary, _top, _inventory;
    private readonly TextBlock _state;
    private readonly Button _create, _send;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _sending;

    public ReportWindow(AppShell shell, string? prefill = null) : base(shell, "report", UiText.Current.ReportTitle, 560, 520)
    {
        var s = shell.Settings;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(12, 0, 12, 12) };
        root.Place(new TextBlock { Text = T.ReportHint, TextWrapping = TextWrapping.Wrap, Foreground = Visuals.TextDim, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 10) }, 0, 0);
        _note = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = prefill ?? "", Watermark = T.ReportDescribe, MinHeight = 120, FontSize = 12.5,
            Background = Visuals.Bg2, Foreground = Visuals.Text, BorderBrush = Visuals.Line, CaretBrush = Visuals.Gold, Margin = new Thickness(0, 0, 0, 10),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        root.Place(_note, 1, 0);
        var ticks = new StackPanel();
        ticks.Children.Add(new TextBlock { Text = T.ReportAlways, FontSize = 11.5, Foreground = Visuals.TextMute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
        ticks.Children.Add(_summary = UiKit.Check(T.ShareReportSummary, s.ReportSummary, 2));
        ticks.Children.Add(_top = UiKit.Check(T.ShareReportTopDps, s.ReportTopDps, 2));
        ticks.Children.Add(_inventory = UiKit.Check(T.ShareReportInventory, s.ReportInventory, 2));
        ticks.Children.Add(_state = new TextBlock { FontSize = 11.5, Foreground = Visuals.TextDim, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, IsVisible = false });
        root.Place(ticks, 2, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(UiKit.Ghost(T.UpdateLater, Close));
        buttons.Children.Add(_create = UiKit.Ghost(T.ReportCreate, Create));
        buttons.Children.Add(_send = UiKit.Primary(T.ReportSend, () => _ = SendAsync()));
        _send.Margin = new Thickness(0);
        root.Place(buttons, 3, 0);
        SetContent(root);
        _tick.Tick += (_, _) => Tick();
        Opened += (_, _) => { _note.Focus(); _tick.Start(); Tick(); };
        Closed += (_, _) => _tick.Stop();
    }

    // The send button shows the remaining wait (gap/minute/hour/day limits) and stays disabled until it ends
    private void Tick()
    {
        if (_sending) return;
        var block = BugReport.SendBlock(Shell.Settings, DateTimeOffset.UtcNow);
        if (block is null) { _send.IsEnabled = true; _send.Content = T.ReportSend; return; }
        var (wait, reason) = block.Value;
        _send.IsEnabled = false;
        var span = wait.TotalHours >= 1 ? $"{(int)wait.TotalHours}h{wait.Minutes:00}" : $"{(int)wait.TotalMinutes}:{wait.Seconds:00}";
        _send.Content = $"{T.ReportSend} ({span})";
        if (reason != "gap") State(string.Format(T.ReportLimit, span));
    }

    private void State(string text) { _state.IsVisible = true; _state.Text = text; }

    /// <summary>The ticks become the next report's defaults (the same switches as Settings → Privacy); settings.json is the UI's to write.</summary>
    private Core.Diagnostics.SharePolicy Policy()
    {
        Shell.RefreshSettings();
        var s = Shell.Settings;
        s.ReportSummary = _summary.IsChecked == true;
        s.ReportTopDps = _top.IsChecked == true;
        s.ReportInventory = _inventory.IsChecked == true;
        Shell.SaveSettings();
        return s.CurrentSharePolicy();
    }

    private void Create()
    {
        _create.IsEnabled = false;
        try
        {
            var path = BugReport.Create(_note.Text ?? "", Policy());
            State(string.Format(T.ReportCreated, path));
            Log.Info($"Bug report created: {path}");
            BugReport.Reveal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            State(string.Format(T.ReportFailed, ex.Message));
            Log.Error("Bug report failed", ex);
        }
        _create.IsEnabled = true;
    }

    /// <summary>Create the zip and send it to Diego; on a network failure the zip stays and the reports folder opens (Discord fallback).</summary>
    private async Task SendAsync()
    {
        if (BugReport.SendBlock(Shell.Settings, DateTimeOffset.UtcNow) is not null) { Tick(); return; }
        _sending = true;
        _create.IsEnabled = _send.IsEnabled = false;
        var policy = Policy();
        State(T.ReportSending);
        try
        {
            var (zip, id) = await BugReport.SendAsync(Shell.Settings, _note.Text ?? "", policy);
            Shell.Daemon.Send("reload-settings");   // ReportSends went into settings.json
            State(string.Format(T.ReportSent, id));
            Log.Info($"Bug report sent: {zip} → {id}");
        }
        catch (BugReport.SendFailure ex)
        {
            Log.Error("Bug report send failed", ex);
            State(string.Format(T.ReportSendFailed, ex.Message));
            BugReport.Reveal(ex.Zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log.Error("Bug report send failed", ex);
            State(string.Format(T.ReportSendFailed, ex.Message));
        }
        _sending = false;
        _create.IsEnabled = true;
        Tick();
    }
}
