using System.Diagnostics;
using AionMeter.Core.Builds;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Game;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Settings (WPF SettingsView, A2M-46) on the layer-shell card (A2M-50, port of the Avalonia SettingsView, behaviour unchanged): every section,
/// built from settings.json as it is now; Save writes it (atomic) and sends the daemon `reload-settings`; Back / Cancel / Esc discard.
/// Windows-only parts are notes: Hotkeys (Sway bindings), Updates (deploy.sh), "start with AION 2" (the daemon is a system service). Adapters
/// come from libpcap when it loads in this process, else /sys/class/net. The opacity slider is the card's (overlay.json); "UI scale" rebuilds
/// the card after the save. A2M-50: Daemon (the retired Avalonia tray's entries): live status, Restart the daemon, Quit the card.
/// </summary>
internal sealed class SettingsView : HostedView
{
    private static UiText T => UiText.Current;
    private readonly List<(string? Name, string Label)> _devices = new();
    private readonly List<int> _serverIds = new();
    private Gtk.CheckButton _barsRelative = null!, _showBoss = null!, _showGear = null!, _autoShow = null!, _showOnStart = null!, _allDesktops = null!;
    private Gtk.CheckButton _saveHistory = null!, _record = null!, _icons = null!, _abyss = null!, _checkUpdates = null!, _autoInstall = null!, _checkUpstream = null!;
    private Gtk.CheckButton _shareUsage = null!, _shareBuilds = null!, _shareBackup = null!, _repSummary = null!, _repTop = null!, _repInv = null!;
    private Gtk.Entry _autoHide = null!, _idle = null!, _bossIdle = null!, _historyMin = null!, _backupCode = null!;
    private Gtk.DropDown _language = null!, _maxRows = null!, _mode = null!, _historyMode = null!, _device = null!, _region = null!, _server = null!, _channel = null!;
    private Gtk.Scale _opacity = null!, _rowSize = null!, _uiScale = null!;
    private Gtk.Label _backupWarn = null!, _backupOff = null!, _copyState = null!, _serverNow = null!, _daemonLine = null!, _restartState = null!;
    private Gtk.Button _restart = null!;
    private string _daemonText = "", _restartText = "";
    private Fg _restartFg = Fg.Dim;
    private bool _restarting;
    private bool _loadingServer;
    private int _built;
    private static readonly int[] RowChoices = [5, 8, 10, 12, 15, 20];

    public SettingsView(AppShell shell) : base(shell, "settings", UiText.Current.SettingsTitle)
    {
        _daemonText = shell.Daemon.Connected ? DaemonLine(shell.LastOverlay?.CaptureMessage ?? "") : T.TrayDaemonOff;
        Listen(() => { Shell.Daemon.Status += OnStatus; Shell.Daemon.ConnectionChanged += OnConnection; },
            () => { Shell.Daemon.Status -= OnStatus; Shell.Daemon.ConnectionChanged -= OnConnection; });
        Build();
    }

    private static string DaemonLine(string msg) => string.Format(T.TrayDaemon, T.CaptureStatusText(msg));

    private void OnStatus(OverlayStatus st) => SetDaemonLine(DaemonLine(st.Message.Length > 0 ? st.Message : st.State.ToString()));

    private void OnConnection(bool on) { if (!on) SetDaemonLine(T.TrayDaemonOff); }

    private void SetDaemonLine(string text)
    {
        if (text == _daemonText) return;
        _daemonText = text;
        _daemonLine.SetText(text);
    }

    /// <summary>systemctl (polkit), then sudo -A with the askpass; on failure the command to run goes to the line under the button and to a notification.</summary>
    private void RestartDaemon()
    {
        if (_restarting) return;
        _restarting = true;
        _restart.Sensitive = false;
        SetRestartState(T.RestartRunning, Fg.Dim);
        AppShell.Run(DaemonControl.Restart, ok =>
        {
            _restarting = false;
            _restart.Sensitive = true;
            if (ok) { SetRestartState(T.RestartDone, Fg.Green); return; }
            var failed = DaemonControl.FailedText();
            SetRestartState(failed, Fg.Red);
            DaemonControl.Notify(failed);
        }, "daemon restart");
    }

    private void SetRestartState(string text, Fg fg)
    {
        (_restartText, _restartFg) = (text, fg);
        _restartState.SetText(text);
        UiKit.SetFg(_restartState, fg);
        _restartState.Visible = text.Length > 0;
    }

    public override void Shown()
    {
        // a fresh editor per visit, as WPF: what the daemon saved meanwhile (set-measure, telemetry marks) is what it starts from
        Shell.RefreshSettings();
        Build();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.SettingsTitle;
        Build();
    }

    private void Build()
    {
        var built = ++_built;   // WhenEngine callbacks of an older build must not touch this one's widgets
        var s = Shell.Settings;
        var engine = Shell.Engine;
        var stack = UiKit.VBox();
        stack.MarginBottom = UiKit.Px(8);

        // Language
        var lang = UiKit.VBox();
        _language = UiKit.Combo(["English", "Español"], UiText.Normalize(s.Language) == "es" ? 1 : 0);
        lang.Append(UiKit.Field("Language / Idioma", _language, 0));
        lang.Append(UiKit.Hint(T.LanguageHint, 0));
        stack.Append(UiKit.Section(lang, "LANGUAGE / IDIOMA"));

        // Overlay
        var ov = UiKit.VBox();
        ov.Append(_barsRelative = UiKit.Check(T.BarsRelative, s.BarsRelativeToTop));
        ov.Append(UiKit.Hint(T.BarsRelativeHint));
        ov.Append(_showBoss = UiKit.Check(T.ShowBossPanel, s.ShowBossPanel));
        ov.Append(_showGear = UiKit.Check(T.ShowGear, s.ShowGear, 6));
        ov.Append(UiKit.Hint(T.ShowGearHint));
        ov.Append(_autoShow = UiKit.Check(T.AutoShow, s.AutoShow, 6));
        ov.Append(UiKit.Hint(T.AutoShowHint));
        var autoHide = UiKit.Field(T.AutoHide, _autoHide = UiKit.Input(s.AutoHideSeconds.ToString(), 70), 0, 250);
        UiKit.Margin(autoHide, 24, 0, 0, 4);
        ov.Append(autoHide);
        ov.Append(_showOnStart = UiKit.Check(T.ShowOnStart, s.ShowOnStart, 6));
        ov.Append(_allDesktops = UiKit.Check(T.AllDesktops, s.AllDesktops, 6));
        ov.Append(UiKit.Hint(T.AllDesktopsHint));
        var rowIdx = 0;
        for (var i = 0; i < RowChoices.Length; i++) if (Math.Abs(RowChoices[i] - s.MaxRows) < Math.Abs(RowChoices[rowIdx] - s.MaxRows)) rowIdx = i;
        var rowLabels = new string[RowChoices.Length];
        for (var i = 0; i < RowChoices.Length; i++) rowLabels[i] = RowChoices[i].ToString();
        ov.Append(UiKit.Field(T.RowsShown, _maxRows = UiKit.Combo(rowLabels, rowIdx, 80)));
        // the layer-shell card's opacity (overlay.json); WPF's 0.15 floor, the card clamps to 0.2
        _opacity = UiKit.Slider(0.15, 1, 0, Shell.CardOpacity);
        ov.Append(UiKit.Field(T.BackgroundOpacity, _opacity));
        // A2M-48 UX: zoom of the Linux windows and the layer-shell card (ui.json "UiScale")
        var us = UiKit.HBox();
        _uiScale = UiKit.Slider(UiScale.Min * 100, UiScale.Max * 100, 5, Shell.UiScale * 100);
        us.Append(_uiScale);
        var usText = UiKit.Text($"{Shell.UiScale * 100:0}%");
        usText.Hexpand = false;
        usText.MarginStart = UiKit.Px(10);
        us.Append(usText);
        _uiScale.OnValueChanged += (_, _) => usText.SetText($"{(int)Math.Round(_uiScale.GetValue())}%");
        ov.Append(UiKit.Field(T.UiScaleLabel, us));
        ov.Append(UiKit.Hint(T.UiScaleHint, 190));
        var rs = UiKit.HBox();
        _rowSize = UiKit.Slider(50, 100, 5, s.RowSize);
        rs.Append(_rowSize);
        var rowSizeText = UiKit.Text($"{s.RowSize}%");
        rowSizeText.Hexpand = false;
        rowSizeText.MarginStart = UiKit.Px(10);
        rs.Append(rowSizeText);
        _rowSize.OnValueChanged += (_, _) => rowSizeText.SetText($"{(int)Math.Round(_rowSize.GetValue())}%");
        ov.Append(UiKit.Field(T.RowSize, rs));
        ov.Append(UiKit.Hint(T.RowSizeHint, 190));
        stack.Append(UiKit.Section(ov, T.SecOverlay));

        // Meter
        var me = UiKit.VBox();
        me.Append(UiKit.Field(T.Measure, _mode = UiKit.Combo([T.MeasureBoss, T.MeasureAll], s.TargetMode == TargetMode.BossOnly ? 0 : 1, 220), 0));
        me.Append(UiKit.Field(T.IdleEnd, _idle = UiKit.Input(s.IdleTimeoutSec.ToString(), 60)));
        me.Append(UiKit.Field(T.IdleBoss, _bossIdle = UiKit.Input(s.BossIdleTimeoutSec.ToString(), 60)));
        stack.Append(UiKit.Section(me, T.SecMeter));

        // History
        var hi = UiKit.VBox();
        hi.Append(_saveHistory = UiKit.Check(T.SaveAuto, s.SaveHistory));
        hi.Append(UiKit.Field(T.WhichFights, _historyMode = UiKit.Combo([T.HistBosses, T.HistBossesLong, T.HistAll], (int)s.HistoryMode, 220)));
        hi.Append(UiKit.Field(T.LongerThan, _historyMin = UiKit.Input(s.HistoryMinSeconds.ToString(), 60)));
        hi.Append(UiKit.Hint(T.HistoryHint, 0));
        var info = UiKit.Hint(engine is null ? "…" : string.Format(T.HistoryInfo, engine.History.List().Count, engine.History.Directory), 0, Fg.Dim);
        hi.Append(info);
        if (engine is null) Shell.WhenEngine(e => { if (built == _built) info.SetText(string.Format(T.HistoryInfo, e.History.List().Count, e.History.Directory)); });
        stack.Append(UiKit.Section(hi, T.SecHistory));

        // Capture (the daemon captures; the adapter list is what this machine has)
        var ca = UiKit.VBox();
        _devices.Clear();
        _devices.Add((null, T.AdapterAuto));
        foreach (var d in ListAdapters()) _devices.Add(d);
        var devIdx = Math.Max(0, _devices.FindIndex(d => d.Name == s.CaptureDevice));
        if (s.CaptureDevice is { Length: > 0 } forced && devIdx == 0) { _devices.Add((forced, forced)); devIdx = _devices.Count - 1; }
        var devLabels = new string[_devices.Count];
        for (var i = 0; i < _devices.Count; i++) devLabels[i] = _devices[i].Label;
        // 190 + 250: with 260 the row was 450 px, 6 over a section's inside at 560, and widened the card to 542 visible (measured 2026-10-13)
        ca.Append(UiKit.Field(T.NetworkAdapter, _device = UiKit.Combo(devLabels, devIdx, 250), 0));
        ca.Append(UiKit.Hint(T.AdapterHint, 0));
        ca.Append(_record = UiKit.Check(T.RecordPackets, s.RecordPackets, 6));
        ca.Append(UiKit.Hint(T.RecordHint));
        ca.Append(UiKit.Hint(T.LinuxCaptureNote, 0, Fg.Dim));
        var ov2 = Shell.LastOverlay;
        ca.Append(UiKit.Hint(ov2 is null ? string.Format(T.LinuxNoDaemon, Shell.Daemon.Path)
            : T.StatusPrefix + T.CaptureStatusText(ov2.CaptureMessage) + (ov2.Status.Length > 0 && ov2.Status != ov2.CaptureMessage ? "\n" + ov2.Status : ""), 0, Fg.Dim));
        stack.Append(UiKit.Section(ca, T.SecCapture));

        // Daemon (A2M-50: the Avalonia tray's Restart / Quit, now that the card is the only Linux UI)
        var dm = UiKit.VBox();
        dm.Append(_daemonLine = UiKit.Text(_daemonText));
        dm.Append(UiKit.Hint(T.DaemonHint, 0));
        var dmButtons = UiKit.HBox();
        dmButtons.MarginTop = UiKit.Px(8);
        dmButtons.Append(_restart = UiKit.Ghost(T.TrayRestartDaemon, RestartDaemon));
        _restart.Sensitive = !_restarting;
        dmButtons.Append(UiKit.Ghost(T.TrayQuit, Shell.Host.Quit));
        dm.Append(dmButtons);
        dm.Append(_restartState = UiKit.Hint(_restartText, 0, _restartFg));
        _restartState.Visible = _restartText.Length > 0;
        stack.Append(UiKit.Section(dm, T.SecDaemon));

        // Hotkeys: Sway's
        stack.Append(UiKit.Section(UiKit.Hint(T.LinuxHotkeysNote, 0, Fg.Dim), T.SecHotkeys));

        // Game
        var ga = UiKit.VBox();
        _region = UiKit.Combo([], -1, 220);
        _server = UiKit.Combo([], -1, 220);
        UiKit.OnSelected(_region, () =>
        {
            var r = UiKit.Selected(_region);
            if (_loadingServer || r < 0) return;
            var sv = UiKit.Selected(_server);
            var keep = sv > 0 && sv <= _serverIds.Count ? _serverIds[sv - 1] : 0;
            FillServers(ServerContext.Regions[r], keep);
        });
        ga.Append(UiKit.Field(T.GameRegion, _region, 0));
        ga.Append(UiKit.Field(T.GameServer, _server));
        ga.Append(_serverNow = UiKit.Hint("", 190, Fg.Dim));
        _serverNow.MarginTop = UiKit.Px(4);
        ga.Append(UiKit.Hint(T.GameServerHint, 0));
        ga.Append(UiKit.Hint(T.LinuxGameNote, 0, Fg.Dim));
        if (engine is not null) LoadServer(engine);
        else Shell.WhenEngine(e => { if (built == _built) LoadServer(e); });
        stack.Append(UiKit.Section(ga, T.SecGame));

        // Updates: deploy.sh on Linux; the switches stay (they are the Windows build's settings in the same file)
        var up = UiKit.VBox();
        up.Append(UiKit.Hint(T.LinuxUpdatesNote, 0, Fg.Dim));
        up.Append(UiKit.Hint(BuildInfo(), 0));
        up.Append(_checkUpdates = UiKit.Check(T.CheckUpdatesAuto, s.CheckUpdates, 4));
        up.Append(_autoInstall = UiKit.Check(T.AutoInstallUpdates, s.AutoInstallUpdates, 4));
        up.Append(UiKit.Field(T.UpdateChannelLabel, _channel = UiKit.Combo([T.UpdateChannelStable, T.UpdateChannelDev], s.UpdateChannel == "dev" ? 1 : 0, 160)));
        up.Append(_checkUpstream = UiKit.Check(T.CheckUpstream, s.CheckUpstream, 6));
        stack.Append(UiKit.Section(up, T.SecUpdates));

        // Privacy
        var pr = UiKit.VBox();
        pr.Append(UiKit.Hint(T.PrivacyIntro, 0));
        pr.Append(_shareUsage = UiKit.Check(T.ShareUsage, s.ShareUsage));
        pr.Append(UiKit.Hint(T.ShareUsageHint));
        pr.Append(_shareBuilds = UiKit.Check(T.ShareBuilds, s.ShareBuilds, 4));
        pr.Append(UiKit.Hint(T.ShareBuildsHint));
        pr.Append(_shareBackup = UiKit.Check(T.ShareBackup, s.ShareBackup, 4));
        pr.Append(UiKit.Hint(T.ShareBackupHint));
        pr.Append(_backupWarn = UiKit.Hint("", 24, Fg.Red));
        pr.Append(UiKit.Text(T.ShareReports));
        pr.Append(UiKit.Hint(T.ShareReportsHint, 0));
        pr.Append(_repSummary = UiKit.Check(T.ShareReportSummary, s.ReportSummary, 2));
        pr.Append(_repTop = UiKit.Check(T.ShareReportTopDps, s.ReportTopDps, 2));
        pr.Append(_repInv = UiKit.Check(T.ShareReportInventory, s.ReportInventory, 2));
        foreach (var c in (Gtk.CheckButton[])[_repSummary, _repTop, _repInv]) c.MarginStart = UiKit.Px(24);
        var report = UiKit.Ghost(T.TrayReport, () => Shell.ShowReport());
        UiKit.Margin(report, 24, 8, 0, 0);
        report.Halign = Gtk.Align.Start;
        pr.Append(report);
        _shareBuilds.OnToggled += (_, _) => ShowShareWarnings();
        _shareBackup.OnToggled += (_, _) => ShowShareWarnings();
        stack.Append(UiKit.Section(pr, T.SecPrivacy));

        // Backup
        var bk = UiKit.VBox();
        // Avalonia wraps [label][code][Copy] in a WrapPanel; at the card's 560 px a section has ~450 px inside, so the label goes above
        var codeLabel = UiKit.Text(T.BackupCodeLabel, 12.5, Fg.Dim);
        codeLabel.MarginBottom = UiKit.Px(4);
        bk.Append(codeLabel);
        var codeRow = UiKit.HBox();
        _backupCode = UiKit.Input(BackupCode.Format(s.EnsureInstallId()), 200);
        _backupCode.Editable = false;
        _backupCode.AddCssClass("k-mono");
        _backupCode.Hexpand = true;
        codeRow.Append(_backupCode);
        var copy = UiKit.Ghost(T.BackupCopy, CopyCode);
        copy.MarginStart = UiKit.Px(8);
        copy.MarginEnd = 0;
        codeRow.Append(copy);
        bk.Append(codeRow);
        bk.Append(_copyState = UiKit.Text("", 12.5, Fg.Green));
        _copyState.Visible = false;
        bk.Append(UiKit.Hint(T.BackupHint, 0));
        bk.Append(_backupOff = UiKit.Hint(T.BackupOff, 0, Fg.Red));
        // WPF: the button opens the Restore from code dialog (A2M-30); the dialog runs Service/RestoreFlow
        var restore = UiKit.Ghost(T.RestoreTitle + "…", () => Shell.ShowRestore());
        restore.MarginTop = UiKit.Px(10);
        restore.Halign = Gtk.Align.Start;
        bk.Append(restore);
        stack.Append(UiKit.Section(bk, T.SecBackup));
        ShowShareWarnings();

        // Data
        var da = UiKit.VBox();
        da.Append(_icons = UiKit.Check(T.DownloadIcons, s.DownloadIcons));
        da.Append(UiKit.Hint(T.IconsHint));
        da.Append(_abyss = UiKit.Check(T.AbyssLogsSetting, s.AbyssLogsPercentile, 8));
        da.Append(UiKit.Hint(T.AbyssLogsHint));
        var dataInfo = UiKit.Hint("", 0, Fg.Dim);
        void Info(MeterEngine e)
        {
            if (built != _built) return;
            var d = e.Data;
            var bosses = 0;
            foreach (var n in d.Npcs.Values) if (n.IsBoss) bosses++;
            dataInfo.SetText(string.Format(T.DataInfo, d.Skills.Count, d.Npcs.Count, bosses, d.DotSkills.Count, d.Maps.Count));
        }
        if (engine is not null) Info(engine); else Shell.WhenEngine(Info);
        da.Append(dataInfo);
        // two lines: the three buttons on one are ~470 px in Spanish and widened the card to 555 visible instead of 540 (measured
        // 2026-10-13 in a nested sway)
        var folders = UiKit.HBox();
        folders.MarginTop = UiKit.Px(10);
        folders.Append(UiKit.Ghost(T.OpenDataFolder, () => Open(MeterEngine.DataDirectory)));
        folders.Append(UiKit.Ghost(T.OpenAppFolder, () => Open(AppSettings.AppDataDir)));
        da.Append(folders);
        var logs = UiKit.Ghost(T.Logs, () => Open(Log.Directory));
        logs.MarginTop = UiKit.Px(6);
        logs.Halign = Gtk.Align.Start;
        da.Append(logs);
        stack.Append(UiKit.Section(da, T.SecData));
        stack.Append(UiKit.Hint(T.Disclaimer, 4));

        SetBody(UiKit.Scroll(stack));
        var buttons = UiKit.HBox();
        buttons.Halign = Gtk.Align.End;
        buttons.Append(UiKit.Ghost(T.Cancel, RaiseClose));
        var save = UiKit.Primary(T.Save, Save);
        save.MarginEnd = 0;
        buttons.Append(save);
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
            var names = new List<string>();
            foreach (var d in Directory.EnumerateDirectories("/sys/class/net"))
                if (Path.GetFileName(d) is { Length: > 0 } n && n != "lo") names.Add(n);
            names.Sort(StringComparer.Ordinal);
            foreach (var n in names) list.Add((n, n));
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
        var regions = new string[ServerContext.Regions.Length];
        for (var i = 0; i < regions.Length; i++) regions[i] = T.RegionName(ServerContext.Regions[i]);
        var region = s.ServerId > 0 && ServerContext.RegionOfServer(s.ServerId) is { Length: > 0 } pr ? pr : s.ServerId == 0 ? now.Region : ServerContext.NormalizeRegion(s.Region);
        UiKit.SetItems(_region, regions, Math.Max(0, Array.IndexOf(ServerContext.Regions, region)));
        _loadingServer = false;
        FillServers(region, s.ServerId);
        _serverNow.SetText(string.Format(T.GameServerNow, now.ServerName, now.ServerId, T.ServerSourceName(now.Source)));
    }

    private void FillServers(string region, int selected)
    {
        if (Shell.Engine is not { } engine) return;
        _serverIds.Clear();
        var now = engine.Server;
        var self = Shell.LastOverlay?.Character is { Length: > 0 } ch && ServerContext.ServerOfKey(ch) is > 0 and var sid ? sid : 0;
        var detected = now.Source == ServerSource.Settings ? (self > 0 ? engine.Data.ServerName(self) : "") : now.ServerName;
        var items = new List<string> { detected.Length > 0 ? string.Format(T.GameServerAuto, detected) : T.GameServerAutoUnknown };
        if (engine.Data.ServersByRegion.TryGetValue(region, out var ids))
            foreach (var id in ids) { _serverIds.Add(id); items.Add($"{engine.Data.ServerName(id)} · {(id / 1000 == 1 ? T.RaceElyos : T.RaceAsmodian)}"); }
        var idx = selected > 0 ? _serverIds.IndexOf(selected) : -1;
        UiKit.SetItems(_server, items, idx >= 0 ? idx + 1 : 0);
    }

    private void ShowShareWarnings()
    {
        var backup = _shareBackup.Active;
        var builds = _shareBuilds.Active;
        _backupOff.Visible = !backup;
        _backupWarn.SetText(!backup ? T.ShareBackupOff : !builds ? T.ShareBuildsOffBackup : "");
        _backupWarn.Visible = !(backup && builds);
    }

    /// <summary>Wayland only lets a client set the selection with a recent input serial of its own: the Copy click is one.</summary>
    private void CopyCode()
    {
        try
        {
            _backupCode.GetClipboard().SetText(_backupCode.GetText());
            _copyState.SetText(T.BackupCopied);
        }
        catch (GLib.GException e) { _copyState.SetText(e.Message); }
        _copyState.Visible = true;
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

    private static int Int(Gtk.Entry box, int fallback, int min, int max) => int.TryParse(box.GetText(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void Save()
    {
        Shell.RefreshSettings();   // the daemon's own saves since this editor opened stay, then the edits go on top
        var s = Shell.Settings;
        var previousLanguage = s.Language;
        s.BarsRelativeToTop = _barsRelative.Active;
        s.ShowBossPanel = _showBoss.Active;
        s.ShowGear = _showGear.Active;
        s.AutoShow = _autoShow.Active;
        s.AutoHideSeconds = Int(_autoHide, s.AutoHideSeconds, 0, 3600);
        s.ShowOnStart = _showOnStart.Active;
        s.AllDesktops = _allDesktops.Active;
        s.MaxRows = RowChoices[Math.Max(0, UiKit.Selected(_maxRows))];
        s.BackgroundOpacity = Math.Round(_opacity.GetValue(), 2);
        s.RowSize = (int)Math.Round(_rowSize.GetValue());
        s.TargetMode = UiKit.Selected(_mode) == 1 ? TargetMode.All : TargetMode.BossOnly;
        s.IdleTimeoutSec = Int(_idle, s.IdleTimeoutSec, 3, 120);
        s.BossIdleTimeoutSec = Int(_bossIdle, s.BossIdleTimeoutSec, 5, 300);
        s.SaveHistory = _saveHistory.Active;
        s.HistoryMode = (HistoryMode)Math.Max(0, UiKit.Selected(_historyMode));
        s.HistoryMinSeconds = Int(_historyMin, s.HistoryMinSeconds, 5, 600);
        s.CaptureDevice = _devices[Math.Max(0, UiKit.Selected(_device))].Name;
        s.RecordPackets = _record.Active;
        s.Language = UiKit.Selected(_language) == 1 ? "es" : "en";
        if (UiText.Normalize(s.Language) != UiText.Normalize(previousLanguage))
        {
            // A2M-47 "general language": the help pages, skill catalogue and item search follow the interface (MeterEngine.SetLanguage)
            s.HelpLanguage = s.SkillLanguage = s.PriceSearchLanguage = s.Language;
        }
        s.DownloadIcons = _icons.Active;
        s.AbyssLogsPercentile = _abyss.Active;
        s.CheckUpdates = _checkUpdates.Active;
        s.AutoInstallUpdates = _autoInstall.Active;
        s.UpdateChannel = UiKit.Selected(_channel) == 1 ? "dev" : "stable";
        s.CheckUpstream = _checkUpstream.Active;
        s.ShareUsage = _shareUsage.Active;
        s.ShareBuilds = _shareBuilds.Active;
        s.ShareBackup = _shareBackup.Active;
        s.ReportSummary = _repSummary.Active;
        s.ReportTopDps = _repTop.Active;
        s.ReportInventory = _repInv.Active;
        if (UiKit.Selected(_region) is var r && r >= 0) s.Region = ServerContext.Regions[r];
        if (UiKit.Count(_server) > 0) s.ServerId = UiKit.Selected(_server) is var sv && sv > 0 && sv <= _serverIds.Count ? _serverIds[sv - 1] : 0;
        // the card's opacity lives in overlay.json (the slider shows it); settings.json keeps the Windows overlay's value in step
        Shell.SetCardOpacity(s.BackgroundOpacity);
        Shell.SaveSettings(previousLanguage);
        RaiseClose();
        // last: the card is rebuilt at the new scale on the next loop turn, with the tab Settings returned to
        Shell.SetUiScale(_uiScale.GetValue() / 100);
    }
}
