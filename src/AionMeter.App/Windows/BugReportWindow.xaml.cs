using System.IO;
using System.Windows;
using AionMeter.App.Services;

namespace AionMeter.App.Windows;

/// <summary>"Report a problem": a note from the player + the diagnostic bundle (BugReport.Create) saved as a zip and shown in Explorer.</summary>
public partial class BugReportWindow : Window
{
    private readonly AppSettings _settings;
    private readonly System.Windows.Threading.DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public BugReportWindow(AppSettings settings, string? prefill = null)
    {
        _settings = settings;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        if (prefill is not null) NoteBox.Text = prefill;
        Loaded += (_, _) => { NoteBox.Focus(); _tick.Start(); Tick(null, EventArgs.Empty); };
        Unloaded += (_, _) => _tick.Stop();
        _tick.Tick += Tick;
    }

    // The send button shows the remaining wait (gap/minute/hour/day limits) and stays disabled until it ends
    private void Tick(object? sender, EventArgs e)
    {
        if (_sending) return;
        var block = BugReport.SendBlock(_settings, DateTimeOffset.UtcNow);
        if (block is null) { SendButton.IsEnabled = true; SendButton.Content = UiText.Current.ReportSend; return; }
        var (wait, reason) = block.Value;
        SendButton.IsEnabled = false;
        var span = wait.TotalHours >= 1 ? $"{(int)wait.TotalHours}h{wait.Minutes:00}" : $"{(int)wait.TotalMinutes}:{wait.Seconds:00}";
        SendButton.Content = $"{UiText.Current.ReportSend} ({span})";
        if (reason != "gap") { State.Visibility = Visibility.Visible; State.Text = string.Format(UiText.Current.ReportLimit, span); }
    }
    private bool _sending;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        State.Visibility = Visibility.Visible;
        try
        {
            var path = BugReport.Create(NoteBox.Text, IncludeState.IsChecked == true);
            State.Text = string.Format(UiText.Current.ReportCreated, path);
            Log.Info($"Bug report created: {path}");
            BugReport.Reveal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            State.Text = string.Format(UiText.Current.ReportFailed, ex.Message);
            Log.Error("Bug report failed", ex);
        }
        CreateButton.IsEnabled = true;
    }

    /// <summary>Create the zip and send it to Diego; on a network failure the zip stays and Explorer shows it (Discord fallback).</summary>
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (BugReport.SendBlock(_settings, DateTimeOffset.UtcNow) is not null) { Tick(null, EventArgs.Empty); return; }
        _sending = true;
        CreateButton.IsEnabled = SendButton.IsEnabled = false;
        State.Visibility = Visibility.Visible;
        string? path = null;
        try
        {
            path = BugReport.Create(NoteBox.Text, IncludeState.IsChecked == true);
            State.Text = UiText.Current.ReportSending;
            var id = await BugReport.UploadAsync(path, NoteBox.Text, CancellationToken.None);
            _settings.ReportSends.Add(DateTimeOffset.UtcNow); _settings.Save();
            State.Text = string.Format(UiText.Current.ReportSent, id);
            Log.Info($"Bug report sent: {path} → {id}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Log.Error("Bug report send failed", ex);
            State.Text = string.Format(UiText.Current.ReportSendFailed, ex is TaskCanceledException ? "timeout" : ex.Message);
            if (path is not null) BugReport.Reveal(path);
        }
        _sending = false;
        CreateButton.IsEnabled = true;
        Tick(null, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
