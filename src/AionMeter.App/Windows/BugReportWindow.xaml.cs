using System.IO;
using System.Windows;
using AionMeter.App.Services;

namespace AionMeter.App.Windows;

/// <summary>"Report a problem": a note from the player + the diagnostic bundle (BugReport.Create) saved as a zip and shown in Explorer, for the player to attach to a GitHub issue.</summary>
public partial class BugReportWindow : Window
{
    private readonly AppSettings _settings;

    public BugReportWindow(AppSettings settings, string? prefill = null)
    {
        _settings = settings;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        if (prefill is not null) NoteBox.Text = prefill;
        IncSummary.IsChecked = settings.ReportSummary;
        IncTopDps.IsChecked = settings.ReportTopDps;
        IncInventory.IsChecked = settings.ReportInventory;
        Loaded += (_, _) => NoteBox.Focus();
    }

    /// <summary>The ticks become the next report's defaults (the same switches as Settings → Privacy → Bug reports include).</summary>
    private Core.Diagnostics.SharePolicy Policy()
    {
        _settings.ReportSummary = IncSummary.IsChecked == true;
        _settings.ReportTopDps = IncTopDps.IsChecked == true;
        _settings.ReportInventory = IncInventory.IsChecked == true;
        try { _settings.Save(); } catch (IOException ex) { Log.Info($"Report choices not saved: {ex.Message}"); }
        return _settings.CurrentSharePolicy();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        CreateButton.IsEnabled = false;
        State.Visibility = Visibility.Visible;
        try
        {
            var path = BugReport.Create(NoteBox.Text, Policy());
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

    private void Issue_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.IssuesUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("Open " + AppInfo.IssuesUrl, ex); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
