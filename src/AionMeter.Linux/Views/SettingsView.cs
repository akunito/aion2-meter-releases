using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Core.Builds;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Game;
using AionMeter.Linux.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux.Views;

/// <summary>
/// Settings (WPF SettingsView, A2M-46) on Linux: every section, built from settings.json as it is now; Save writes it (atomic) and sends the
/// daemon `reload-settings`; Back / Cancel / Esc discard. Windows-only parts are notes: Hotkeys (Sway bindings), Updates (deploy.sh),
/// "start with AION 2" (the daemon is a system service). Adapters come from libpcap when it loads in this process, else /sys/class/net.
/// </summary>
public sealed class SettingsView : HostedView
{
    private static UiText T => UiText.Current;
    private readonly List<(string? Name, string Label)> _devices = new();
    private readonly List<int> _serverIds = new();
    private CheckBox _barsRelative = null!, _showBoss = null!, _showGear = null!, _autoShow = null!, _showOnStart = null!, _allDesktops = null!;
    private CheckBox _saveHistory = null!, _record = null!, _icons = null!, _abyss = null!, _checkUpdates = null!, _autoInstall = null!, _checkUpstream = null!;
    private CheckBox _shareUsage = null!, _shareBuilds = null!, _shareBackup = null!, _repSummary = null!, _repTop = null!, _repInv = null!;
    private TextBox _autoHide = null!, _idle = null!, _bossIdle = null!, _historyMin = null!;
    private ComboBox _language = null!, _maxRows = null!, _mode = null!, _historyMode = null!, _device = null!, _region = null!, _server = null!, _channel = null!;
    private Slider _opacity = null!, _rowSize = null!, _uiScale = null!;
    private TextBlock _rowSizeText = null!, _backupWarn = null!, _backupOff = null!, _copyState = null!, _serverNow = null!;
    private TextBox _backupCode = null!;
    private bool _loadingServer;
    private static readonly int[] RowChoices = [5, 8, 10, 12, 15, 20];

    public SettingsView(AppShell shell) : base(shell, "settings", UiText.Current.SettingsTitle)
    {
        Build();
    }

    public override void Shown()
    {
        // a fresh editor per visit, as WPF: what the daemon saved meanwhile (set-measure, telemetry marks) is what it starts from
        Shell.RefreshSettings();
        Build();
        Focus();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.SettingsTitle;
        Build();
    }

    /// <summary>The window takes this editor with its unsaved edits (WPF OpenSettingsWindow); the card goes back.</summary>
    public override IHostedView PopOut() => this;

    private void Build()
    {
        var s = Shell.Settings;
        var engine = Shell.Engine;
        var stack = new StackPanel { MaxWidth = 760, Margin = new Thickness(0, 0, 0, 8) };

        // Language
        var lang = new StackPanel();
        _language = UiKit.Combo(["English", "Español"], UiText.Normalize(s.Language) == "es" ? 1 : 0);
        lang.Children.Add(UiKit.Field("Language / Idioma", _language, 0));
        lang.Children.Add(UiKit.Hint(T.LanguageHint, 0));
        stack.Children.Add(UiKit.Section(lang, "LANGUAGE / IDIOMA"));

        // Overlay
        var ov = new StackPanel();
        ov.Children.Add(_barsRelative = UiKit.Check(T.BarsRelative, s.BarsRelativeToTop));
        ov.Children.Add(UiKit.Hint(T.BarsRelativeHint));
        ov.Children.Add(_showBoss = UiKit.Check(T.ShowBossPanel, s.ShowBossPanel));
        ov.Children.Add(_showGear = UiKit.Check(T.ShowGear, s.ShowGear, 6));
        ov.Children.Add(UiKit.Hint(T.ShowGearHint));
        ov.Children.Add(_autoShow = UiKit.Check(T.AutoShow, s.AutoShow, 6));
        ov.Children.Add(UiKit.Hint(T.AutoShowHint));
        var autoHide = UiKit.Field(T.AutoHide, _autoHide = UiKit.Input(s.AutoHideSeconds.ToString(), 70), 0);
        autoHide.Margin = new Thickness(24, 0, 0, 4);
        ((TextBlock)autoHide.Children[0]).Width = 250;
        ov.Children.Add(autoHide);
        ov.Children.Add(_showOnStart = UiKit.Check(T.ShowOnStart, s.ShowOnStart, 6));
        ov.Children.Add(_allDesktops = UiKit.Check(T.AllDesktops, s.AllDesktops, 6));
        ov.Children.Add(UiKit.Hint(T.AllDesktopsHint));
        var rowIdx = 0;
        for (var i = 0; i < RowChoices.Length; i++) if (Math.Abs(RowChoices[i] - s.MaxRows) < Math.Abs(RowChoices[rowIdx] - s.MaxRows)) rowIdx = i;
        ov.Children.Add(UiKit.Field(T.RowsShown, _maxRows = UiKit.Combo(RowChoices.Select(n => n.ToString()), rowIdx, 80)));
        _opacity = new Slider { Width = 180, Minimum = 0.15, Maximum = 1, Value = Shell.Ui.Opacity, VerticalAlignment = VerticalAlignment.Center };
        ov.Children.Add(UiKit.Field(T.BackgroundOpacity, _opacity));
        // A2M-48 UX: zoom of the Linux windows and the layer-shell card (ui.json "UiScale")
        var us = new StackPanel { Orientation = Orientation.Horizontal };
        _uiScale = new Slider { Width = 180, Minimum = UiScale.Min * 100, Maximum = UiScale.Max * 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = Shell.Ui.UiScale * 100, VerticalAlignment = VerticalAlignment.Center };
        us.Children.Add(_uiScale);
        var usText = UiKit.Text($"{Shell.Ui.UiScale * 100:0}%");
        usText.Margin = new Thickness(10, 0, 0, 0);
        us.Children.Add(usText);
        _uiScale.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) usText.Text = $"{(int)Math.Round(_uiScale.Value)}%"; };
        Shell.UiScaleChanged += z => { if (Math.Abs(_uiScale.Value - z * 100) > 0.5) _uiScale.Value = z * 100; };
        ov.Children.Add(UiKit.Field(T.UiScaleLabel, us));
        ov.Children.Add(UiKit.Hint(T.UiScaleHint, 190));
        var rs = new StackPanel { Orientation = Orientation.Horizontal };
        _rowSize = new Slider { Width = 180, Minimum = 50, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Value = s.RowSize, VerticalAlignment = VerticalAlignment.Center };
        rs.Children.Add(_rowSize);
        rs.Children.Add(_rowSizeText = UiKit.Text($"{s.RowSize}%"));
        _rowSizeText.Margin = new Thickness(10, 0, 0, 0);
        _rowSize.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) _rowSizeText.Text = $"{(int)Math.Round(_rowSize.Value)}%"; };
        ov.Children.Add(UiKit.Field(T.RowSize, rs));
        ov.Children.Add(UiKit.Hint(T.RowSizeHint, 190));
        stack.Children.Add(UiKit.Section(ov, T.SecOverlay));

        // Meter
        var me = new StackPanel();
        me.Children.Add(UiKit.Field(T.Measure, _mode = UiKit.Combo([T.MeasureBoss, T.MeasureAll], s.TargetMode == TargetMode.BossOnly ? 0 : 1, 220), 0));
        me.Children.Add(UiKit.Field(T.IdleEnd, _idle = UiKit.Input(s.IdleTimeoutSec.ToString(), 60)));
        me.Children.Add(UiKit.Field(T.IdleBoss, _bossIdle = UiKit.Input(s.BossIdleTimeoutSec.ToString(), 60)));
        stack.Children.Add(UiKit.Section(me, T.SecMeter));

        // History
        var hi = new StackPanel();
        hi.Children.Add(_saveHistory = UiKit.Check(T.SaveAuto, s.SaveHistory));
        hi.Children.Add(UiKit.Field(T.WhichFights, _historyMode = UiKit.Combo([T.HistBosses, T.HistBossesLong, T.HistAll], (int)s.HistoryMode, 220)));
        hi.Children.Add(UiKit.Field(T.LongerThan, _historyMin = UiKit.Input(s.HistoryMinSeconds.ToString(), 60)));
        hi.Children.Add(UiKit.Hint(T.HistoryHint, 0));
        var info = UiKit.Hint(engine is null ? "…" : string.Format(T.HistoryInfo, engine.History.List().Count, engine.History.Directory), 0, Visuals.TextDim);
        hi.Children.Add(info);
        if (engine is null) Shell.WhenEngine(e => info.Text = string.Format(T.HistoryInfo, e.History.List().Count, e.History.Directory));
        stack.Children.Add(UiKit.Section(hi, T.SecHistory));

        // Capture (the daemon captures; the adapter list is what this machine has)
        var ca = new StackPanel();
        _devices.Clear();
        _devices.Add((null, T.AdapterAuto));
        foreach (var d in ListAdapters()) _devices.Add(d);
        var devIdx = Math.Max(0, _devices.FindIndex(d => d.Name == s.CaptureDevice));
        if (s.CaptureDevice is { Length: > 0 } forced && devIdx == 0) { _devices.Add((forced, forced)); devIdx = _devices.Count - 1; }
        ca.Children.Add(UiKit.Field(T.NetworkAdapter, _device = UiKit.Combo(_devices.Select(d => d.Label), devIdx, 260), 0));
        ca.Children.Add(UiKit.Hint(T.AdapterHint, 0));
        ca.Children.Add(_record = UiKit.Check(T.RecordPackets, s.RecordPackets, 6));
        ca.Children.Add(UiKit.Hint(T.RecordHint));
        ca.Children.Add(UiKit.Hint(T.LinuxCaptureNote, 0, Visuals.TextDim));
        var ov2 = Shell.LastOverlay;
        ca.Children.Add(UiKit.Hint(ov2 is null ? string.Format(T.LinuxNoDaemon, Shell.Daemon.Path)
            : T.StatusPrefix + T.CaptureStatusText(ov2.CaptureMessage) + (ov2.Status.Length > 0 && ov2.Status != ov2.CaptureMessage ? "\n" + ov2.Status : ""), 0, Visuals.TextDim));
        stack.Children.Add(UiKit.Section(ca, T.SecCapture));

        // Hotkeys: Sway's
        stack.Children.Add(UiKit.Section(UiKit.Hint(T.LinuxHotkeysNote, 0, Visuals.TextDim), T.SecHotkeys));

        // Game
        var ga = new StackPanel();
        _region = UiKit.Combo([], -1, 220);
        _server = UiKit.Combo([], -1, 220);
        _region.SelectionChanged += (_, _) =>
        {
            if (_loadingServer || _region.SelectedIndex < 0) return;
            var keep = _server.SelectedIndex > 0 && _server.SelectedIndex <= _serverIds.Count ? _serverIds[_server.SelectedIndex - 1] : 0;
            FillServers(ServerContext.Regions[_region.SelectedIndex], keep);
        };
        ga.Children.Add(UiKit.Field(T.GameRegion, _region, 0));
        ga.Children.Add(UiKit.Field(T.GameServer, _server));
        ga.Children.Add(_serverNow = UiKit.Hint("", 190, Visuals.TextDim));
        _serverNow.Margin = new Thickness(190, 4, 0, 0);
        ga.Children.Add(UiKit.Hint(T.GameServerHint, 0));
        ga.Children.Add(UiKit.Hint(T.LinuxGameNote, 0, Visuals.TextDim));
        if (engine is not null) LoadServer(engine);
        else Shell.WhenEngine(LoadServer);
        stack.Children.Add(UiKit.Section(ga, T.SecGame));

        // Updates: deploy.sh on Linux; the switches stay (they are the Windows build's settings in the same file)
        var up = new StackPanel();
        up.Children.Add(UiKit.Hint(T.LinuxUpdatesNote, 0, Visuals.TextDim));
        up.Children.Add(UiKit.Hint(BuildInfo(), 0, Visuals.TextMute));
        up.Children.Add(_checkUpdates = UiKit.Check(T.CheckUpdatesAuto, s.CheckUpdates, 4));
        up.Children.Add(_autoInstall = UiKit.Check(T.AutoInstallUpdates, s.AutoInstallUpdates, 4));
        up.Children.Add(UiKit.Field(T.UpdateChannelLabel, _channel = UiKit.Combo([T.UpdateChannelStable, T.UpdateChannelDev], s.UpdateChannel == "dev" ? 1 : 0, 160)));
        up.Children.Add(_checkUpstream = UiKit.Check(T.CheckUpstream, s.CheckUpstream, 6));
        stack.Children.Add(UiKit.Section(up, T.SecUpdates));

        // Privacy
        var pr = new StackPanel();
        pr.Children.Add(UiKit.Hint(T.PrivacyIntro, 0));
        pr.Children.Add(_shareUsage = UiKit.Check(T.ShareUsage, s.ShareUsage));
        pr.Children.Add(UiKit.Hint(T.ShareUsageHint));
        pr.Children.Add(_shareBuilds = UiKit.Check(T.ShareBuilds, s.ShareBuilds, 4));
        pr.Children.Add(UiKit.Hint(T.ShareBuildsHint));
        pr.Children.Add(_shareBackup = UiKit.Check(T.ShareBackup, s.ShareBackup, 4));
        pr.Children.Add(UiKit.Hint(T.ShareBackupHint));
        pr.Children.Add(_backupWarn = UiKit.Hint("", 24, Visuals.Red));
        pr.Children.Add(UiKit.Text(T.ShareReports));
        pr.Children.Add(UiKit.Hint(T.ShareReportsHint, 0));
        pr.Children.Add(_repSummary = UiKit.Check(T.ShareReportSummary, s.ReportSummary, 2));
        pr.Children.Add(_repTop = UiKit.Check(T.ShareReportTopDps, s.ReportTopDps, 2));
        pr.Children.Add(_repInv = UiKit.Check(T.ShareReportInventory, s.ReportInventory, 2));
        foreach (var c in (CheckBox[])[_repSummary, _repTop, _repInv]) c.Margin = new Thickness(24, 2, 0, 0);
        var report = UiKit.Ghost(T.TrayReport, () => Shell.ShowReport());
        report.Margin = new Thickness(24, 8, 0, 0);
        report.HorizontalAlignment = HorizontalAlignment.Left;
        pr.Children.Add(report);
        _shareBuilds.IsCheckedChanged += (_, _) => ShowShareWarnings();
        _shareBackup.IsCheckedChanged += (_, _) => ShowShareWarnings();
        stack.Children.Add(UiKit.Section(pr, T.SecPrivacy));

        // Backup
        var bk = new StackPanel();
        var codeRow = new WrapPanel();
        codeRow.Children.Add(new TextBlock { Text = T.BackupCodeLabel, Foreground = Visuals.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 4) });
        _backupCode = UiKit.Input(BackupCode.Format(s.EnsureInstallId()), 340);
        _backupCode.IsReadOnly = true;
        _backupCode.FontFamily = new FontFamily("DejaVu Sans Mono, Liberation Mono, monospace");
        _backupCode.Foreground = Visuals.Gold;
        codeRow.Children.Add(_backupCode);
        var copy = UiKit.Ghost(T.BackupCopy, CopyCode);
        copy.Margin = new Thickness(8, 0, 0, 0);
        codeRow.Children.Add(copy);
        codeRow.Children.Add(_copyState = new TextBlock { Foreground = Visuals.Green, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        bk.Children.Add(codeRow);
        bk.Children.Add(UiKit.Hint(T.BackupHint, 0));
        bk.Children.Add(_backupOff = UiKit.Hint(T.BackupOff, 0, Visuals.Red));
        // WPF: the button opens the Restore from code dialog (A2M-30); the dialog runs Service/RestoreFlow
        var restore = UiKit.Ghost(T.RestoreTitle + "…", () => Shell.ShowRestore());
        restore.Margin = new Thickness(0, 10, 0, 0);
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        bk.Children.Add(restore);
        stack.Children.Add(UiKit.Section(bk, T.SecBackup));
        ShowShareWarnings();

        // Data
        var da = new StackPanel();
        da.Children.Add(_icons = UiKit.Check(T.DownloadIcons, s.DownloadIcons));
        da.Children.Add(UiKit.Hint(T.IconsHint));
        da.Children.Add(_abyss = UiKit.Check(T.AbyssLogsSetting, s.AbyssLogsPercentile, 8));
        da.Children.Add(UiKit.Hint(T.AbyssLogsHint));
        var dataInfo = UiKit.Hint("", 0, Visuals.TextDim);
        void Info(MeterEngine e)
        {
            var d = e.Data;
            dataInfo.Text = string.Format(T.DataInfo, d.Skills.Count, d.Npcs.Count, d.Npcs.Values.Count(n => n.IsBoss), d.DotSkills.Count, d.Maps.Count);
        }
        if (engine is not null) Info(engine); else Shell.WhenEngine(Info);
        da.Children.Add(dataInfo);
        var folders = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        folders.Children.Add(UiKit.Ghost(T.OpenDataFolder, () => Open(MeterEngine.DataDirectory)));
        folders.Children.Add(UiKit.Ghost(T.OpenAppFolder, () => Open(AppSettings.AppDataDir)));
        folders.Children.Add(UiKit.Ghost(T.Logs, () => Open(Log.Directory)));
        da.Children.Add(folders);
        stack.Children.Add(UiKit.Section(da, T.SecData));
        stack.Children.Add(UiKit.Hint(T.Disclaimer, 4));

        SetBody(UiKit.Scroll(stack));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(UiKit.Ghost(T.Cancel, RaiseClose));
        buttons.Children.Add(UiKit.Primary(T.Save, Save));
        SetFooter(buttons);
    }

    /// <summary>libpcap's list when it loads here (it only enumerates, no capture rights needed), else the kernel's interface names.</summary>
    private static List<(string? Name, string Label)> ListAdapters()
    {
        var list = new List<(string?, string)>();
        try
        {
            foreach (var (name, description) in LiveCapture.ListDevices(() => true)) list.Add((name, description));
            if (list.Count > 0) return list;
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or InvalidOperationException or BadImageFormatException or EntryPointNotFoundException) { }
        try
        {
            foreach (var d in Directory.EnumerateDirectories("/sys/class/net").Select(Path.GetFileName).Order())
                if (d is { Length: > 0 } && d != "lo") list.Add((d, d));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    private static string BuildInfo()
    {
        try
        {
            var f = Path.Combine(AppContext.BaseDirectory, "BUILD-INFO.txt");
            if (File.Exists(f)) return string.Join(" · ", File.ReadAllLines(f).Where(l => l.StartsWith("upstream", StringComparison.Ordinal) || l.StartsWith("built", StringComparison.Ordinal) || l.StartsWith("deployed", StringComparison.Ordinal))
                .Select(l => l.StartsWith("upstream ", StringComparison.Ordinal) && l.Length > 21 ? "upstream " + l[9..21] : l));
        }
        catch (IOException) { }
        return string.Format(T.VersionDev, "?");
    }

    private void LoadServer(MeterEngine engine)
    {
        var s = Shell.Settings;
        var now = engine.Server;
        _loadingServer = true;
        _region.Items.Clear();
        foreach (var r in ServerContext.Regions) _region.Items.Add(new ComboBoxItem { Content = T.RegionName(r) });
        var region = s.ServerId > 0 && ServerContext.RegionOfServer(s.ServerId) is { Length: > 0 } pr ? pr : s.ServerId == 0 ? now.Region : ServerContext.NormalizeRegion(s.Region);
        _region.SelectedIndex = Math.Max(0, Array.IndexOf(ServerContext.Regions, region));
        _loadingServer = false;
        FillServers(region, s.ServerId);
        _serverNow.Text = string.Format(T.GameServerNow, now.ServerName, now.ServerId, T.ServerSourceName(now.Source));
    }

    private void FillServers(string region, int selected)
    {
        if (Shell.Engine is not { } engine) return;
        _server.Items.Clear();
        _serverIds.Clear();
        var now = engine.Server;
        var self = Shell.LastOverlay?.Character is { Length: > 0 } ch && ServerContext.ServerOfKey(ch) is > 0 and var sid ? sid : 0;
        var detected = now.Source == ServerSource.Settings ? (self > 0 ? engine.Data.ServerName(self) : "") : now.ServerName;
        _server.Items.Add(new ComboBoxItem { Content = detected.Length > 0 ? string.Format(T.GameServerAuto, detected) : T.GameServerAutoUnknown });
        if (engine.Data.ServersByRegion.TryGetValue(region, out var ids))
            foreach (var id in ids) { _serverIds.Add(id); _server.Items.Add(new ComboBoxItem { Content = $"{engine.Data.ServerName(id)} · {(id / 1000 == 1 ? T.RaceElyos : T.RaceAsmodian)}" }); }
        var idx = selected > 0 ? _serverIds.IndexOf(selected) : -1;
        _server.SelectedIndex = idx >= 0 ? idx + 1 : 0;
    }

    private void ShowShareWarnings()
    {
        var backup = _shareBackup.IsChecked == true;
        var builds = _shareBuilds.IsChecked == true;
        _backupOff.IsVisible = !backup;
        _backupWarn.Text = !backup ? T.ShareBackupOff : !builds ? T.ShareBuildsOffBackup : "";
        _backupWarn.IsVisible = !(backup && builds);
    }

    private async void CopyCode()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        try { await cb.SetTextAsync(_backupCode.Text ?? ""); _copyState.Text = T.BackupCopied; }
        catch (Exception e) when (e is InvalidOperationException or IOException) { _copyState.Text = e.Message; }
    }


    private static void Open(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("xdg-open", path) { UseShellExecute = false });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { Log.Info($"xdg-open {path}: {e.Message}"); }
    }

    private static int Int(TextBox box, int fallback, int min, int max) => int.TryParse(box.Text, out var v) ? Math.Clamp(v, min, max) : fallback;

    private void Save()
    {
        Shell.RefreshSettings();   // the daemon's own saves since this editor opened stay, then the edits go on top
        var s = Shell.Settings;
        var previousLanguage = s.Language;
        s.BarsRelativeToTop = _barsRelative.IsChecked == true;
        s.ShowBossPanel = _showBoss.IsChecked == true;
        s.ShowGear = _showGear.IsChecked == true;
        s.AutoShow = _autoShow.IsChecked == true;
        s.AutoHideSeconds = Int(_autoHide, s.AutoHideSeconds, 0, 3600);
        s.ShowOnStart = _showOnStart.IsChecked == true;
        s.AllDesktops = _allDesktops.IsChecked == true;
        s.MaxRows = RowChoices[Math.Max(0, _maxRows.SelectedIndex)];
        s.BackgroundOpacity = Math.Round(_opacity.Value, 2);
        s.RowSize = (int)Math.Round(_rowSize.Value);
        s.TargetMode = _mode.SelectedIndex == 1 ? TargetMode.All : TargetMode.BossOnly;
        s.IdleTimeoutSec = Int(_idle, s.IdleTimeoutSec, 3, 120);
        s.BossIdleTimeoutSec = Int(_bossIdle, s.BossIdleTimeoutSec, 5, 300);
        s.SaveHistory = _saveHistory.IsChecked == true;
        s.HistoryMode = (HistoryMode)Math.Max(0, _historyMode.SelectedIndex);
        s.HistoryMinSeconds = Int(_historyMin, s.HistoryMinSeconds, 5, 600);
        s.CaptureDevice = _devices[Math.Max(0, _device.SelectedIndex)].Name;
        s.RecordPackets = _record.IsChecked == true;
        s.Language = _language.SelectedIndex == 1 ? "es" : "en";
        if (UiText.Normalize(s.Language) != UiText.Normalize(previousLanguage))
        {
            // A2M-47 "general language": the help pages, skill catalogue and item search follow the interface (MeterEngine.SetLanguage)
            s.HelpLanguage = s.Language;   // game names follow NamesLanguage (A2M-47)
        }
        s.DownloadIcons = _icons.IsChecked == true;
        s.AbyssLogsPercentile = _abyss.IsChecked == true;
        s.CheckUpdates = _checkUpdates.IsChecked == true;
        s.AutoInstallUpdates = _autoInstall.IsChecked == true;
        s.UpdateChannel = _channel.SelectedIndex == 1 ? "dev" : "stable";
        s.CheckUpstream = _checkUpstream.IsChecked == true;
        s.ShareUsage = _shareUsage.IsChecked == true;
        s.ShareBuilds = _shareBuilds.IsChecked == true;
        s.ShareBackup = _shareBackup.IsChecked == true;
        s.ReportSummary = _repSummary.IsChecked == true;
        s.ReportTopDps = _repTop.IsChecked == true;
        s.ReportInventory = _repInv.IsChecked == true;
        if (_region.SelectedIndex >= 0) s.Region = ServerContext.Regions[_region.SelectedIndex];
        if (_server.ItemCount > 0) s.ServerId = _server.SelectedIndex > 0 && _server.SelectedIndex <= _serverIds.Count ? _serverIds[_server.SelectedIndex - 1] : 0;
        // the Linux card's opacity lives in ui.json (the slider shows it); settings.json keeps the Windows overlay's value in step
        Shell.Ui.Opacity = s.BackgroundOpacity;
        Shell.Ui.Save();
        Shell.SetUiScale(_uiScale.Value / 100);
        Shell.SaveSettings(previousLanguage);
        RaiseClose();
    }
}
