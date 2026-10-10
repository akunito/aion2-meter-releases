using System.IO;
using System.Windows;
using AionMeter.App.Services;

namespace AionMeter.App.Windows;

/// <summary>"Report a problem": a note from the player + the diagnostic bundle (BugReport.Create) saved as a zip and shown in Explorer.</summary>
public partial class BugReportWindow : Window
{
    public BugReportWindow(string? prefill = null)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        if (prefill is not null) NoteBox.Text = prefill;
        Loaded += (_, _) => NoteBox.Focus();
    }

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
        CreateButton.IsEnabled = SendButton.IsEnabled = false;
        State.Visibility = Visibility.Visible;
        string? path = null;
        try
        {
            path = BugReport.Create(NoteBox.Text, IncludeState.IsChecked == true);
            State.Text = UiText.Current.ReportSending;
            var id = await BugReport.UploadAsync(path, NoteBox.Text, CancellationToken.None);
            State.Text = string.Format(UiText.Current.ReportSent, id);
            Log.Info($"Bug report sent: {path} → {id}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Log.Error("Bug report send failed", ex);
            State.Text = string.Format(UiText.Current.ReportSendFailed, ex is TaskCanceledException ? "timeout" : ex.Message);
            if (path is not null) BugReport.Reveal(path);
        }
        CreateButton.IsEnabled = SendButton.IsEnabled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
