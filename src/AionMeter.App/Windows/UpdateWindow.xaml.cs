using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.Core.Updates;

namespace AionMeter.App.Windows;

/// <summary>
/// "A new version is out": what changed, then Update / Later / Skip this version. An installed copy downloads the
/// installer here (with progress), starts it and exits; a portable copy opens the release page instead.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly Updater _updater;
    private bool _canInstall;
    private CancellationTokenSource? _download;

    public UpdateWindow(Updater updater, ReleaseInfo release, bool installed)
    {
        _updater = updater;
        Release = release;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);

        var t = UiText.Current;
        var current = updater.Current.ToString(3);
        Headline.Text = string.Format(t.UpdateHeadline, release.Version.ToString(3));
        SubLine.Text = release.PublishedAt is { } at
            ? string.Format(t.UpdateReleased, current, FormatDate(at))
            : string.Format(t.UpdateYouHave, current);
        ShowNotes(release.Notes);
        _canInstall = installed && release.Portable is not null;   // "installed" = a published build that may replace itself
        UpdateButton.Content = _canInstall ? t.UpdateNow : t.UpdateOpenPage;
        Hint.Text = _canInstall ? t.UpdateInstalledHint : t.UpdatePortableHint;
    }

    public ReleaseInfo Release { get; }

    private static string FormatDate(DateTimeOffset at) =>
        at.LocalDateTime.ToString(UiText.Current.Code == "ru" ? "d MMMM yyyy" : "MMMM d, yyyy", UiText.Current.Culture);

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!_canInstall)
        {
            Updater.OpenReleasePage(Release);
            Close();
            return;
        }

        var t = UiText.Current;
        UpdateButton.IsEnabled = SkipButton.IsEnabled = false;
        DownloadBar.Visibility = State.Visibility = Visibility.Visible;
        State.Text = string.Format(t.UpdateDownloading, 0);
        _download = new CancellationTokenSource();
        var progress = new Progress<double>(f =>
        {
            DownloadBar.Value = f;
            State.Text = string.Format(t.UpdateDownloading, f * 100);
        });
        try
        {
            var installer = await _updater.DownloadInstallerAsync(Release, progress, _download.Token);
            State.Text = t.UpdateStarting;
            Log.Info($"Updating to {Release.Tag} with {installer}");
            Updater.LaunchInstaller(installer);
            Application.Current.Shutdown(); // the installer replaces our files and starts the new version
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            // the window was closed while downloading
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException
                                       or OperationCanceledException or Win32Exception)
        {
            Log.Error("Update failed", ex);
            State.Text = string.Format(t.UpdateFailed, ex is OperationCanceledException ? "timeout" : ex.Message);
            DownloadBar.Visibility = Visibility.Collapsed;
            _canInstall = false;
            UpdateButton.Content = t.UpdateOpenPage;
            UpdateButton.IsEnabled = SkipButton.IsEnabled = true;
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _updater.Skip(Release);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _download?.Cancel();
        base.OnClosed(e);
    }

    private void ShowNotes(string markdown)
    {
        Notes.Inlines.Clear();
        var gold = (Brush)FindResource("Gold");
        foreach (var (text, heading) in ReleaseNotes.Lines(markdown, UiText.Current.Code))
        {
            if (Notes.Inlines.Count > 0) Notes.Inlines.Add(new LineBreak());
            Notes.Inlines.Add(heading ? new Run(text) { FontWeight = FontWeights.SemiBold, Foreground = gold } : new Run(text));
        }
    }
}
