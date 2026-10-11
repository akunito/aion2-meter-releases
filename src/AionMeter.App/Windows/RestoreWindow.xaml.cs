using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using AionMeter.App.Services;
using AionMeter.Core.Builds;

namespace AionMeter.App.Windows;

/// <summary>A2M-30 "Restore from code": the server copy of a backup code comes back to this PC; never automatic.</summary>
public partial class RestoreWindow : Window
{
    private readonly MeterService _meter;
    private static UiText T => UiText.Current;

    public RestoreWindow(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        Loaded += (_, _) => CodeBox.Focus();
    }

    /// <summary>Render tool: the dialog as it looks after a restore.</summary>
    public void ShowSample(string code, string status)
    {
        CodeBox.Text = code;
        StatusText.Text = status;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Green");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CodeBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Restore_Click(sender, e); }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!RestoreButton.IsEnabled) return;
        if (BackupCode.Parse(CodeBox.Text) is not { } code) { Show(T.RestoreBadCode, "Red"); return; }
        if (_meter.Settings.Transient) return;
        RestoreButton.IsEnabled = false;
        Show(T.RestoreWorking, "TextDim");
        try
        {
            var (status, body, error) = await Telemetry.FetchRestoreAsync(code);
            switch (status)
            {
                case 0: Show(string.Format(T.RestoreFailed, error), "Red"); return;
                case 404: Show(T.RestoreNotFound, "Red"); return;
                case 429: Show(T.RestoreLimited, "Red"); return;
                case < 200 or >= 300: Show(string.Format(T.RestoreRefused, status), "Red"); return;
            }
            RestorePayload payload;
            try { payload = await Task.Run(() => BuildTelemetryJson.ParseRestore(body)); }
            catch (JsonException ex) { Log.Info($"Restore: unreadable answer: {ex.Message}"); Show(string.Format(T.RestoreBadAnswer, ex.Message), "Red"); return; }
            MeterService.RestoreSummary r;
            try { r = _meter.ApplyRestore(code, payload); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Log.Error("Restore: files not written", ex); Show(ex.Message, "Red"); return; }
            var text = string.Format(T.RestoreDone, r.Builds, r.Placed, r.Waiting);
            if (r.KeptLocal > 0) text += "; " + string.Format(T.RestoreKeptNewer, r.KeptLocal);
            text += "; " + (!r.StateFound ? T.RestoreNoState : r.WatchRestored ? T.RestoreWatchRestored : T.RestoreWatchKept);
            if (r.FavouritesRestored) text += "; " + T.RestoreFavRestored;
            text += ".";
            if (!_meter.Settings.Telemetry) text += " " + T.RestoreNoTelemetry;
            Show(text, "Green");
        }
        finally { RestoreButton.IsEnabled = true; }
    }

    private void Show(string text, string brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(brush);
    }
}
