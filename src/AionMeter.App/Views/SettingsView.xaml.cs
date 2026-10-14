using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AionMeter.App.Services;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Game;

namespace AionMeter.App.Views;

/// <summary>
/// Settings, shown as the overlay's "settings" tab (<see cref="Hosted"/>: Back + Esc return to the previous tab) and as the content of
/// the stand-alone <see cref="Windows.SettingsWindow"/> (A2M-46). Cancel, Back, the X and leaving the tab all discard; Save applies.
/// The host calls <see cref="Discard"/> when it drops the view (row size preview back to the saved value).
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly MeterService _meter;
    private readonly List<(string? Name, string Label)> _devices = new();

    /// <summary>Save (after applying), Cancel, Back, the X or Esc in the overlay.</summary>
    public event Action? CloseRequested;

    public SettingsView(MeterService meter, bool hosted)
    {
        _meter = meter;
        InitializeComponent();
        Hosted = hosted;
        Load();
    }

    /// <summary>In the overlay: a Back chip instead of the X. The pop-out moves a hosted view into the window and clears it.</summary>
    public bool Hosted
    {
        get => BackButton.Visibility == Visibility.Visible;
        set
        {
            BackButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The row size slider previews on the overlay; dropping the view without saving puts the saved size back.</summary>
    public void Discard() => AppHost.Current.PreviewRowSize(_meter.Settings.RowSize);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // an open ComboBox handles its own Esc first (e.Handled), so this only fires with no dropdown open
        if (e.Key == Key.Escape && !e.Handled && Hosted) { e.Handled = true; CloseRequested?.Invoke(); }
    }

    private void Load()
    {
        var s = _meter.Settings;
        BarsRelative.IsChecked = s.BarsRelativeToTop;
        ShowBoss.IsChecked = s.ShowBossPanel;
        ShowGear.IsChecked = s.ShowGear;
        AutoShow.IsChecked = s.AutoShow;
        AutoHideSeconds.Text = s.AutoHideSeconds.ToString();
        ShowOnStart.IsChecked = s.ShowOnStart;
        AllDesktops.IsChecked = s.AllDesktops;
        foreach (var n in new[] { 5, 8, 10, 12, 15, 20 }) MaxRows.Items.Add(n);
        MaxRows.SelectedItem = MaxRows.Items.Cast<int>().OrderBy(n => Math.Abs(n - s.MaxRows)).First();
        OpacityValue.Value = s.BackgroundOpacity;
        RowSizeValue.Value = s.RowSize;
        Mode.SelectedIndex = s.TargetMode == TargetMode.BossOnly ? 0 : 1;
        Idle.Text = s.IdleTimeoutSec.ToString();
        BossIdle.Text = s.BossIdleTimeoutSec.ToString();
        SaveHistory.IsChecked = s.SaveHistory;
        HistoryModeBox.SelectedIndex = (int)s.HistoryMode;
        HistoryMin.Text = s.HistoryMinSeconds.ToString();
        HistoryInfo.Text = string.Format(T.HistoryInfo, _meter.History.List().Count, _meter.History.Directory);
        Record.IsChecked = s.RecordPackets;
        HkToggle.Text = s.HotkeyToggleOverlay;
        HkReset.Text = s.HotkeyReset;
        HkClick.Text = s.HotkeyClickThrough;
        HkTimers.Text = s.HotkeyTimers;
        HkAtlas.Text = s.HotkeyAtlas;

        // A2M-27: without Npcap the adapter list is never asked for (SharpPcap would throw) and the section says what that costs
        var availability = _meter.Availability;
        var present = availability.Present;
        _devices.Add((null, T.AdapterAuto));
        if (present) foreach (var (name, description) in LiveCapture.ListDevices(() => availability.Present)) _devices.Add((name, description));
        else _devices.Add((null, T.AdapterNoNpcap));
        foreach (var d in _devices) Device.Items.Add(d.Label);
        var idx = _devices.FindIndex(d => d.Name == s.CaptureDevice);
        Device.SelectedIndex = present ? Math.Max(0, idx) : 1;
        Device.IsEnabled = present;
        Record.IsEnabled = present;
        RecordNpcapHint.Visibility = present ? Visibility.Collapsed : Visibility.Visible;
        NoCaptureBox.Visibility = availability.Live ? Visibility.Collapsed : Visibility.Visible;
        var forwarded = CaptureEndpoint.StreamEndpoint(s.CaptureSource);
        ReceiveForward.IsChecked = forwarded is not null;
        ForwardAddress.Text = forwarded ?? "";
        NoCaptureInfo.Text = availability.SettingsText(T.CaptureFeatureLabels, T.NoCaptureHeadline, T.NoCaptureLost, T.NoCaptureKept);

        LanguageBox.SelectedIndex = UiText.Normalize(s.Language) == "es" ? 1 : 0;
        Icons.IsChecked = s.DownloadIcons;
        AbyssLogs.IsChecked = s.AbyssLogsPercentile;
        CheckUpdates.IsChecked = s.CheckUpdates;
        AutoInstallUpdates.IsChecked = s.AutoInstallUpdates;
        AutoInstallUpdates.IsEnabled = !_meter.Updates.IsDevBuild; // a hand-deployed developer build never replaces itself
        VersionText.Text = _meter.Updates.IsDevBuild ? string.Format(T.VersionDev, _meter.Updates.BuildSha ?? "?")
                                                     : string.Format(T.VersionRelease, _meter.Updates.Current.ToString(3), _meter.Updates.ReleaseTag);
        if (s.UpdateChannel == "dev") ChannelDev.IsChecked = true; else ChannelStable.IsChecked = true;
        CheckUpstreamBox.IsChecked = s.CheckUpstream;
        ShareUsageBox.IsChecked = s.ShareUsage;
        ShareBuildsBox.IsChecked = s.ShareBuilds;
        ShareBackupBox.IsChecked = s.ShareBackup;
        ShareAtlasBox.IsChecked = s.ShareAtlasNames;
        ReportSummaryBox.IsChecked = s.ReportSummary;
        ReportTopDpsBox.IsChecked = s.ReportTopDps;
        ReportInventoryBox.IsChecked = s.ReportInventory;
        ShowBackupCode();
        LoadServer();
        StartWithGameBox.IsChecked = s.StartWithGame;
        CloseWithGameBox.IsChecked = s.CloseWithGame;

        CaptureState.Text = T.StatusPrefix + T.CaptureStatusText(_meter.CaptureStatus.Message) + "\n" + T.CaptureHealthPrefix + _meter.CaptureHealthText();
        var data = _meter.Data;
        DataInfo.Text = string.Format(T.DataInfo, data.Skills.Count, data.Npcs.Count, data.Npcs.Values.Count(n => n.IsBoss),
            data.DotSkills.Count, data.Maps.Count);
    }

    private static UiText T => UiText.Current;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _meter.Settings;
        var captureChanged = false;
        string? source = null;
        if (ReceiveForward.IsChecked == true)
        {
            if (!CaptureEndpoint.TryParse(ForwardAddress.Text, out var host, out var port))
            {
                MessageBox.Show(T.ReceiveForwardInvalid, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            source = CaptureEndpoint.Scheme + CaptureEndpoint.Format(host, port);
        }
        if (source != s.CaptureSource) captureChanged = true;
        s.CaptureSource = source;

        s.BarsRelativeToTop = BarsRelative.IsChecked == true;
        s.ShowBossPanel = ShowBoss.IsChecked == true;
        s.ShowGear = ShowGear.IsChecked == true;
        s.AutoShow = AutoShow.IsChecked == true;
        if (int.TryParse(AutoHideSeconds.Text, out var autoHide)) s.AutoHideSeconds = Math.Clamp(autoHide, 0, 3600);
        s.ShowOnStart = ShowOnStart.IsChecked == true;
        s.AllDesktops = AllDesktops.IsChecked == true;
        s.MaxRows = MaxRows.SelectedItem is int rows ? rows : 10;
        s.BackgroundOpacity = Math.Round(OpacityValue.Value, 2);
        s.RowSize = (int)Math.Round(RowSizeValue.Value);
        s.TargetMode = Mode.SelectedIndex == 1 ? TargetMode.All : TargetMode.BossOnly;
        if (int.TryParse(Idle.Text, out var idle)) s.IdleTimeoutSec = Math.Clamp(idle, 3, 120);
        if (int.TryParse(BossIdle.Text, out var bossIdle)) s.BossIdleTimeoutSec = Math.Clamp(bossIdle, 5, 300);
        s.SaveHistory = SaveHistory.IsChecked == true;
        s.HistoryMode = (HistoryMode)Math.Max(0, HistoryModeBox.SelectedIndex);
        if (int.TryParse(HistoryMin.Text, out var minSec)) s.HistoryMinSeconds = Math.Clamp(minSec, 5, 600);

        // the list is disabled without Npcap (its "no adapters" row is no choice): a forced adapter stays saved for when it is back
        var device = Device.IsEnabled ? _devices[Math.Max(0, Device.SelectedIndex)].Name : s.CaptureDevice;
        if (device != s.CaptureDevice || (Record.IsChecked == true) != s.RecordPackets) captureChanged = true;
        s.CaptureDevice = device;
        s.RecordPackets = Record.IsChecked == true;

        var language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
        s.DownloadIcons = Icons.IsChecked == true;
        s.AbyssLogsPercentile = AbyssLogs.IsChecked == true;
        s.CheckUpdates = CheckUpdates.IsChecked == true;
        s.AutoInstallUpdates = AutoInstallUpdates.IsChecked == true;
        s.UpdateChannel = ChannelDev.IsChecked == true ? "dev" : "stable";
        s.CheckUpstream = CheckUpstreamBox.IsChecked == true;
        s.ShareUsage = ShareUsageBox.IsChecked == true;
        s.ShareBuilds = ShareBuildsBox.IsChecked == true;
        s.ShareBackup = ShareBackupBox.IsChecked == true;
        s.ShareAtlasNames = ShareAtlasBox.IsChecked == true;
        if (s.ShareAtlasNames) Telemetry.QueueAtlas(_meter);   // switched on: the names answered so far go as one batch (nothing when all are sent)
        s.ReportSummary = ReportSummaryBox.IsChecked == true;
        s.ReportTopDps = ReportTopDpsBox.IsChecked == true;
        s.ReportInventory = ReportInventoryBox.IsChecked == true;
        s.CloseWithGame = CloseWithGameBox.IsChecked == true;
        s.Region = RegionBox.SelectedIndex >= 0 ? ServerContext.Regions[RegionBox.SelectedIndex] : ServerContext.DefaultRegion;
        s.ServerId = ServerBox.SelectedIndex > 0 && ServerBox.SelectedIndex <= _serverIds.Count ? _serverIds[ServerBox.SelectedIndex - 1] : 0;
        var wantHook = StartWithGameBox.IsChecked == true;
        if (wantHook != s.StartWithGame)
        {
            var err = GameHook.Set(wantHook);
            if (err is null) s.StartWithGame = wantHook;
            else { StartWithGameState.Visibility = Visibility.Visible; StartWithGameState.Text = string.Format(T.StartWithGameFailed, err); }
        }
        s.HotkeyToggleOverlay = HkToggle.Text.Trim();
        s.HotkeyReset = HkReset.Text.Trim();
        s.HotkeyClickThrough = HkClick.Text.Trim();
        s.HotkeyTimers = HkTimers.Text.Trim();
        s.HotkeyAtlas = HkAtlas.Text.Trim();

        _meter.ApplySettings();
        if (captureChanged) _meter.RestartCapture();
        CloseRequested?.Invoke();
        AppHost.Current.OnSettingsChanged();
        if (language != UiText.Normalize(s.Language)) AppHost.Current.SetLanguage(language);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    // ------------------------------------------------------------------ A2M-40: region + server
    private readonly List<int> _serverIds = new();
    private bool _loadingServer;

    private void LoadServer()
    {
        var s = _meter.Settings;
        var now = _meter.Server;
        _loadingServer = true;
        foreach (var r in ServerContext.Regions) RegionBox.Items.Add(T.RegionName(r));
        // a picked server names its shard; Auto shows the shard of the server in use
        var region = s.ServerId > 0 && ServerContext.RegionOfServer(s.ServerId) is { Length: > 0 } pr ? pr : s.ServerId == 0 ? now.Region : ServerContext.NormalizeRegion(s.Region);
        RegionBox.SelectedIndex = Math.Max(0, Array.IndexOf(ServerContext.Regions, region));
        _loadingServer = false;
        FillServers(region, s.ServerId);
        ServerNowText.Text = string.Format(T.GameServerNow, now.ServerName, now.ServerId, T.ServerSourceName(now.Source));
    }

    private void FillServers(string region, int selected)
    {
        ServerBox.Items.Clear();
        _serverIds.Clear();
        var now = _meter.Server;
        var detected = now.Source == ServerSource.Settings ? (_meter.Tracker.SelfServerId is > 0 and var l ? _meter.Data.ServerName(l) : "") : now.ServerName;
        ServerBox.Items.Add(detected.Length > 0 ? string.Format(T.GameServerAuto, detected) : T.GameServerAutoUnknown);
        var data = _meter.Data;
        if (data.ServersByRegion.TryGetValue(region, out var ids))
            foreach (var id in ids) { _serverIds.Add(id); ServerBox.Items.Add($"{data.ServerName(id)} · {(id / 1000 == 1 ? T.RaceElyos : T.RaceAsmodian)}"); }
        var idx = selected > 0 ? _serverIds.IndexOf(selected) : -1;
        ServerBox.SelectedIndex = idx >= 0 ? idx + 1 : 0;
    }

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingServer || RegionBox.SelectedIndex < 0) return;
        var keep = ServerBox.SelectedIndex > 0 && ServerBox.SelectedIndex <= _serverIds.Count ? _serverIds[ServerBox.SelectedIndex - 1] : 0;
        FillServers(ServerContext.Regions[RegionBox.SelectedIndex], keep);
    }

    private void ShowBackupCode()
    {
        // the render tool's throw-away settings have no id: a fixed sample keeps the picture stable
        var id = _meter.Settings.Transient ? (_meter.Settings.InstallId.Length > 0 ? _meter.Settings.InstallId : "3f9c0a12b7e44d85a6c1e09f2b7d5e31") : _meter.Settings.EnsureInstallId();
        BackupCodeBox.Text = Core.Builds.BackupCode.Format(id);
        ShowShareWarnings();
    }

    /// <summary>Backup copy off: restore stops working for this PC; builds off: restore brings back only the watch list and favourites.</summary>
    private void ShowShareWarnings()
    {
        var backup = ShareBackupBox.IsChecked == true;
        var builds = ShareBuildsBox.IsChecked == true;
        BackupOffText.Visibility = backup ? Visibility.Collapsed : Visibility.Visible;
        ShareBackupWarn.Text = !backup ? T.ShareBackupOff : !builds ? T.ShareBuildsOffBackup : "";
        ShareBackupWarn.Visibility = backup && builds ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Share_Click(object sender, RoutedEventArgs e) => ShowShareWarnings();

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(BackupCodeBox.Text); CopyState.Text = T.BackupCopied; }
        catch (System.Runtime.InteropServices.COMException ex) { CopyState.Text = ex.Message; }   // clipboard held by another app
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        new Windows.RestoreWindow(_meter) { Owner = Window.GetWindow(this) }.ShowDialog();
        ShowBackupCode();
    }

    /// <summary>The overlay follows the slider at once, so the size can be judged on the real rows.</summary>
    private void RowSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RowSizeText is null) return; // the slider sets its first value before the rest of the window exists
        var percent = (int)Math.Round(e.NewValue);
        RowSizeText.Text = $"{percent}%";
        AppHost.Current.PreviewRowSize(percent);
    }

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp("settings");

    private void NpcapHelp_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowNpcapHelp();

    /// <summary>Asks GitHub right away; a newer version also opens the update window.</summary>
    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateState.Visibility = Visibility.Visible;
        UpdateState.Text = T.UpdateChecking;
        var result = await AppHost.Current.CheckUpdatesNowAsync();
        UpdateState.Text = result switch
        {
            UpdateCheckResult.Available => string.Format(T.UpdateFound, _meter.Updates.Latest!.Version.ToString(3)),
            UpdateCheckResult.UpToDate => T.UpToDate,
            _ => string.Format(T.UpdateCheckFailed, _meter.Updates.LastError),
        };
        CheckNowButton.IsEnabled = true;
    }

    private static void Open(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Open(Path.Combine(AppContext.BaseDirectory, "data"));

    private void OpenAppData_Click(object sender, RoutedEventArgs e) => Open(AppSettings.AppDataDir);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Open(Log.Directory);
}
