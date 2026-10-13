using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AionMeter.App.Services;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Game;

namespace AionMeter.App.Windows;

public partial class SettingsWindow : Window
{
    private readonly MeterService _meter;
    private readonly List<(string? Name, string Label)> _devices = new();

    public SettingsWindow(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        Load();
        // The row size slider previews on the overlay; closing without saving puts the saved size back.
        Closed += (_, _) => AppHost.Current.PreviewRowSize(_meter.Settings.RowSize);
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

        _devices.Add((null, T.AdapterAuto));
        try
        {
            foreach (var (name, description) in LiveCapture.ListDevices()) _devices.Add((name, description));
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            _devices.Add((null, T.NpcapMissing));
        }
        foreach (var d in _devices) Device.Items.Add(d.Label);
        var idx = _devices.FindIndex(d => d.Name == s.CaptureDevice);
        Device.SelectedIndex = Math.Max(0, idx);

        LanguageBox.SelectedIndex = UiText.Normalize(s.Language) == "ru" ? 1 : 0;
        Icons.IsChecked = s.DownloadIcons;
        AbyssLogs.IsChecked = s.AbyssLogsPercentile;
        CheckUpdates.IsChecked = s.CheckUpdates;
        AutoInstallUpdates.IsChecked = s.AutoInstallUpdates;
        AutoInstallUpdates.IsEnabled = !_meter.Updates.IsDevBuild; // a hand-deployed developer build never replaces itself
        VersionText.Text = _meter.Updates.IsDevBuild ? string.Format(T.VersionDev, _meter.Updates.BuildSha ?? "?")
                                                     : string.Format(T.VersionRelease, _meter.Updates.Current.ToString(3), _meter.Updates.ReleaseTag);
        if (s.UpdateChannel == "dev") ChannelDev.IsChecked = true; else ChannelStable.IsChecked = true;
        CheckUpstreamBox.IsChecked = s.CheckUpstream;
        TelemetryBox.IsChecked = s.Telemetry;
        ShowBackupCode();
        LoadServer();
        StartWithGameBox.IsChecked = s.StartWithGame;
        CloseWithGameBox.IsChecked = s.CloseWithGame;

        CaptureState.Text = T.StatusPrefix + _meter.CaptureStatus.Message;
        var data = _meter.Data;
        DataInfo.Text = string.Format(T.DataInfo, data.Skills.Count, data.Npcs.Count, data.Npcs.Values.Count(n => n.IsBoss),
            data.DotSkills.Count, data.Maps.Count);
    }

    private static UiText T => UiText.Current;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _meter.Settings;
        var captureChanged = false;

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

        var device = _devices[Math.Max(0, Device.SelectedIndex)].Name;
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
        s.Telemetry = TelemetryBox.IsChecked == true;
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
        Close();
        AppHost.Current.OnSettingsChanged();
        if (language != UiText.Normalize(s.Language)) AppHost.Current.SetLanguage(language);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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
        BackupOffText.Visibility = TelemetryBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Telemetry_Click(object sender, RoutedEventArgs e) => BackupOffText.Visibility = TelemetryBox.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(BackupCodeBox.Text); CopyState.Text = T.BackupCopied; }
        catch (System.Runtime.InteropServices.COMException ex) { CopyState.Text = ex.Message; }   // clipboard held by another app
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        new RestoreWindow(_meter) { Owner = this }.ShowDialog();
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
