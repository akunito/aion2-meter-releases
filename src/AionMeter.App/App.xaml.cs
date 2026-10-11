using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.App.Windows;
using AionMeter.Core.Capture;

namespace AionMeter.App;

public static class AppHost
{
    public static App Current => (App)Application.Current;

    /// <summary>Clipboard writes fail while another process holds the clipboard; retry briefly. False if it never let go.</summary>
    public static bool CopyText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }
}

public partial class App : Application
{
    private const string ShowRequestName = "AionMeter.ShowOverlay"; // set by a second start of the app
    private Mutex? _singleInstance;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showWait;
    private MeterService _meter = null!;
    private OverlayWindow _overlay = null!;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private DispatcherTimer? _timer;
    private HistoryWindow? _history;
    private SettingsWindow? _settingsWindow;
    private BossTimersWindow? _timersWindow;
    private readonly Dictionary<object, BreakdownWindow> _breakdowns = new();
    private int _ticks;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ArgValue(e.Args, "--render-sample") is { } samplePath)
        {
            Log.Enabled = false;
            // --render-sample <out.png | folder with --animate> [--lang ru] [--window …] [--width N] [--height N] [--tab …]
            // [--backdrop game|dark|none] [--scale 1.5] [--row-size 50..100] [--animate <seconds> [--fps 10]]
            var options = new SampleRenderer.Options(
                ArgValue(e.Args, "--window") ?? "overlay",
                int.TryParse(ArgValue(e.Args, "--width"), out var width) ? width : null,
                int.TryParse(ArgValue(e.Args, "--height"), out var height) ? height : null,
                ArgValue(e.Args, "--tab"),
                ArgValue(e.Args, "--backdrop") ?? "game",
                double.TryParse(ArgValue(e.Args, "--scale"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var scale) ? scale : 1.5,
                int.TryParse(ArgValue(e.Args, "--row-size"), out var rowSize) ? rowSize : 100);
            var language = ArgValue(e.Args, "--lang") ?? "en";
            if (double.TryParse(ArgValue(e.Args, "--animate"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                SampleRenderer.Animate(samplePath, language, options, seconds, int.TryParse(ArgValue(e.Args, "--fps"), out var fps) ? fps : 10);
            else
                SampleRenderer.Render(samplePath, language, options);
            Shutdown();
            return;
        }
        _singleInstance = new Mutex(true, "AionMeter.SingleInstance", out var first);
        if (!first)
        {
            // Started again (the shortcut clicked twice): the running meter shows its overlay, this copy leaves quietly.
            if (EventWaitHandle.TryOpenExisting(ShowRequestName, out var running))
                using (running) running.Set();
            else // a version from before 0.2.2 is running
                MessageBox.Show($"{AppInfo.Name} is already running — look for the gold \"A\" in the notification area.", AppInfo.Name);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("UI exception", ex.Exception);
            ex.Handled = true;
            // A2M-19: keep running (as upstream does) but leave a crash file and offer the report once per run
            var crash = BugReport.WriteCrash("UI exception", ex.Exception);
            if (crash is not null && !_crashOffered)
            {
                _crashOffered = true;
                _tray?.ShowBalloon(AppInfo.Name, UiText.Current.CrashBalloon, () => ShowBugReport(string.Format(UiText.Current.ReportCrashNote, Path.GetFileName(crash))));
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log.Error("Fatal exception", ex.ExceptionObject as Exception); BugReport.WriteCrash("Fatal exception", ex.ExceptionObject as Exception); };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        var settings = AppSettings.Load();
        settings.Save(); // persist any migration done while loading
        UiText.Use(settings.Language);
        Core.Net.CloudflareHttp.SolverUrl = settings.CloudflareSolverUrl; // scrapers fall back to the solver when set
        Func<Core.Game.GameData, IEventSource> factory = data => new LiveCapture(data, new LiveCaptureOptions
        {
            DeviceName = settings.CaptureDevice,
            RecordDirectory = settings.RecordPackets ? Path.Combine(AppSettings.AppDataDir, "captures") : null,
        });

        _meter = new MeterService(settings, factory);
        // The naming prompt opens over the atlas (or the overlay): on the monitor the player keeps the meter, not on the game's.
        _meter.NodeNameWanted += code => Dispatcher.BeginInvoke(() => ShowNameNode(code, fromGather: true));
        Log.Info($"Started. Data: {_meter.Data.Skills.Count} skills, {_meter.Data.Npcs.Count} npcs.");
        _meter.StartCapture();

        _overlay = new OverlayWindow(_meter);
        if (settings.OverlayVisible || e.Args.Contains("--show")) _overlay.Show(); // --show: this run only, not saved
        else
        {
            new WindowInteropHelper(_overlay).EnsureHandle();
            // Hidden by choice: still up for a moment, so it is plain that the meter started.
            if (settings.ShowOnStart) ShowByItself();
        }

        _hotkeys = new HotkeyManager(new WindowInteropHelper(_overlay).Handle);
        RegisterHotkeys();

        _tray = new TrayIcon(new TrayActions(
            ToggleOverlay: () => SetOverlayVisible(!_overlay.IsVisible),
            ToggleClickThrough: ToggleClickThrough,
            Reset: RestartMeter,
            History: ShowHistory,
            BossTimers: ShowBossTimers,
            Atlas: ShowAtlas,
            Top: ShowTop,
            Settings: ShowSettings,
            Demo: () => _meter.StartDemo(),
            Replay: PickReplay,
            ToggleLanguage: ToggleLanguage,
            Update: () => ShowUpdate(),
            CheckUpdates: () => _ = CheckUpdatesFromTrayAsync(),
            Report: () => ShowBugReport(),
            Exit: () =>
            {
                Log.Info("Exit chosen in the tray menu");
                Shutdown();
            }));
        SessionEnding += (_, end) => Log.Info($"Windows {end.ReasonSessionEnding}: closing");

        // A new player's first start: without Npcap nothing can be measured — say so and offer the download.
        if (!LiveCapture.IsNpcapInstalled())
            Dispatcher.BeginInvoke(ShowNpcapHelp, DispatcherPriority.ApplicationIdle);

        // Another start of the app asks this one to show the overlay.
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequest,
            (_, _) => Dispatcher.BeginInvoke(() => SetOverlayVisible(true)), null, Timeout.Infinite, executeOnlyOnce: false);

        // New versions on GitHub: tray item, the overlay's update button and one notification per version.
        _meter.Updates.Changed += OnUpdatesChanged;
        _meter.CharacterChanged += () => Dispatcher.BeginInvoke(_overlay.SyncCharBox);   // selector follows the login / the manual pick
        Dispatcher.BeginInvoke(_overlay.SyncCharBox, DispatcherPriority.ApplicationIdle);
        _meter.Updates.Start();
        Updater.CleanDownloads();
        if (_meter.Settings.Telemetry && !_meter.Settings.TelemetryNoticeShown)
        {
            _meter.Settings.TelemetryNoticeShown = true; _meter.Settings.Save();
            Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(AppInfo.Name, UiText.Current.TelemetryNotice, () => ShowSettings()), DispatcherPriority.ApplicationIdle);
        }
        var telemetry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
        telemetry.Tick += (_, _) => { telemetry.Interval = TimeSpan.FromMinutes(15); _ = Telemetry.SendIfDueAsync(_meter); };
        telemetry.Start();
        if (e.Args.Contains("--updated")) // started by the installer after an in-app update
            Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(AppInfo.Name,
                string.Format(UiText.Current.UpdatedBalloon, _meter.Updates.Current.ToString(3))), DispatcherPriority.ApplicationIdle);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        // Developer switches: --demo, --replay <file.pcap> (instant), --open-last (breakdown of the newest fight).
        if (e.Args.Contains("--demo")) _meter.StartDemo();
        if (ArgValue(e.Args, "--replay") is { } replay) _meter.Replay(replay);
        if (e.Args.Contains("--open-last"))
        {
            var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            once.Tick += (_, _) =>
            {
                once.Stop();
                var seg = _meter.Tracker.Segments().OrderByDescending(s => s.TotalDamage).FirstOrDefault();
                if (seg is null || _meter.Tracker.Snapshot(seg.Id, 0) is not { } snap || snap.Combatants.Count == 0) return;
                var who = snap.Combatants.FirstOrDefault(c => c.IsSelf) ?? snap.Combatants[0];
                ShowBreakdown(seg.Id, who.ActorId);
                if (ArgValue(e.Args, "--tab") is { } tab && _breakdowns.TryGetValue(seg.Id, out var w)) w.ShowTab(tab);
            };
            once.Start();
        }
    }

    private static void ShowNpcapHelp()
    {
        var answer = MessageBox.Show(UiText.Current.NpcapNeeded, AppInfo.Name, MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error("Could not open the Npcap page", ex);
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnTick()
    {
        _meter.Tick();
        UpdateAutoVisibility();
        if (_overlay.IsVisible) _overlay.Refresh();
        for (var i = _extras.Count - 1; i >= 0; i--) if (_extras[i].IsVisible) _extras[i].Refresh();
        if (++_ticks % 5 == 0)
        {
            _tray?.Update(_overlay.IsVisible, _meter.Settings.ClickThrough, _meter.DemoRunning ? "demo" : _meter.CaptureStatus.Message);
            CheckBossAlerts();
            CheckEventAlerts();
            CheckPriceAlerts();
            CheckGameStart();
            CheckPendingUpdate();
        }
    }

    // ------------------------------------------------------------ automatic show / hide

    private bool _autoShown;           // the overlay is up because a fight brought it up, not the user
    private Guid? _suppressedFight;    // hidden by hand during this fight: do not pop it back until the next one
    private long _lastEngagedTick;

    /// <summary>
    /// A boss being fought nearby, or any fight you take part in, brings the overlay up. One that came up by itself
    /// goes away again after <see cref="AppSettings.AutoHideSeconds"/> out of combat (never while the mouse is on it).
    /// </summary>
    private void UpdateAutoVisibility()
    {
        var s = _meter.Settings;
        var fight = _meter.Tracker.LiveFight();
        var engaged = fight is { } f && (f.Boss || f.Self);
        var now = Environment.TickCount64;
        if (engaged) _lastEngagedTick = now;
        if (!s.AutoShow) return;

        if (engaged && !_overlay.IsVisible && fight!.Value.Id != _suppressedFight)
        {
            _overlay.Show();
            _autoShown = true;
        }
        else if (!engaged && _autoShown && _overlay.IsVisible && s.AutoHideSeconds > 0 && !_overlay.TabActive &&
                 now - _lastEngagedTick > s.AutoHideSeconds * 1000L && !_overlay.IsMouseOver)
        {
            _overlay.Hide();
            _autoShown = false;
        }
    }

    /// <summary>Up without the user asking: goes away again after the usual idle time (Settings → Overlay).</summary>
    private void ShowByItself()
    {
        if (_overlay.IsVisible) return;
        _overlay.Show();
        _autoShown = true;
        _lastEngagedTick = Environment.TickCount64;
    }

    private CaptureState _lastCaptureState = CaptureState.Stopped;
    private int[] _gameShownFor = [];

    /// <summary>
    /// The overlay comes up when the meter starts seeing the game's traffic, once per launch of the game: a reconnect
    /// after a loading screen is the same game process and does not count.
    /// </summary>
    private void CheckGameStart()
    {
        var state = _meter.CaptureStatus.State;
        var connected = state == CaptureState.Capturing && _lastCaptureState != CaptureState.Capturing;
        _lastCaptureState = state;
        if (!connected || !_meter.Settings.ShowOnStart || _meter.DemoRunning) return;
        var game = GameProcessLocator.FindGameProcessIds().Order().ToArray();
        if (game.Length == 0 || game.SequenceEqual(_gameShownFor)) return;
        _gameShownFor = game;
        ShowByItself();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        var s = _meter.Settings;
        _hotkeys.Register(s.HotkeyToggleOverlay, () => SetOverlayVisible(!_overlay.IsVisible));
        _hotkeys.Register(s.HotkeyReset, RestartMeter);
        _overlay.ApplyHotkeyTips();
        _hotkeys.Register(s.HotkeyClickThrough, ToggleClickThrough);
        _hotkeys.Register(s.HotkeyTimers, ToggleBossTimers);
        _hotkeys.Register(s.HotkeyAtlas, ToggleAtlas);
    }

    /// <summary>
    /// The overlay's restart button, the tray item and its hotkey: when the numbers look wrong, start over without
    /// closing the app — the fight so far is saved, capture restarts, and the players and bosses the meter knows stay
    /// known (a restart of the whole app mid-fight could not learn them again until the next loading screen).
    /// </summary>
    public void RestartMeter()
    {
        Log.Info("Meter restarted by the user");
        _meter.Restart();
        _overlay.ShowLive();
    }

    // ------------------------------------------------------------ window management (used by windows via AppHost)

    public void SetOverlayVisible(bool visible)
    {
        _autoShown = false; // the user decided: automatic hiding no longer applies
        if (visible)
        {
            _overlay.Show();
            _overlay.Refresh();
        }
        else
        {
            _overlay.Hide();
            _suppressedFight = _meter.Tracker.LiveFight()?.Id;
        }
        _meter.Settings.OverlayVisible = visible;
        _meter.Settings.Save();
    }

    private void ToggleClickThrough()
    {
        var on = !_meter.Settings.ClickThrough;
        _overlay.SetClickThrough(on);
        _tray?.ShowBalloon(AppInfo.Name, on
            ? string.Format(UiText.Current.ClickThroughOn, _meter.Settings.HotkeyClickThrough)
            : UiText.Current.ClickThroughOff);
    }

    public void ShowBreakdown(Guid segmentId, uint actorId) =>
        ShowBreakdown(segmentId, new LiveFightView(_meter.Tracker, segmentId), actorId);

    public void ShowBreakdown(IFightView view, uint actorId) => ShowBreakdown(view, view, actorId);

    private void ShowBreakdown(object key, IFightView view, uint actorId)
    {
        if (_breakdowns.TryGetValue(key, out var existing))
        {
            existing.Select(actorId);
            Activate(existing);
            return;
        }
        var w = new BreakdownWindow(view, actorId, _meter.Data, _meter.Icons, _meter.Portraits);
        _breakdowns[key] = w;
        w.Closed += (_, _) => _breakdowns.Remove(key);
        w.Show();
    }

    /// <summary>English ⇄ Russian, live: names reload, the overlay re-binds, other windows reopen in the new language.</summary>
    public void SetLanguage(string language)
    {
        _meter.SetLanguage(language);
        foreach (var w in _breakdowns.Values.ToList()) w.Close();
        _history?.Close();
        _timersWindow?.Close();
        _updateWindow?.Close();
        _tray?.ApplyTexts();
        _overlay.OnLanguageChanged();
        foreach (var w in _extras) w.OnLanguageChanged();
    }

    public void ToggleLanguage() => SetLanguage(UiText.Current.Code == "ru" ? "en" : "ru");

    public void ShowSavedInOverlay(Core.Storage.HistoryEntry entry)
    {
        if (!_overlay.IsVisible) SetOverlayVisible(true);
        _overlay.ShowSaved(entry);
    }

    public void ShowHistory()
    {
        if (_history is { IsLoaded: true })
        {
            _history.Reload();
            Activate(_history);
            return;
        }
        _history = new HistoryWindow(_meter);
        _history.Closed += (_, _) => _history = null;
        _history.Show();
    }

    /// <summary>Hotkey: open the boss timers, or close them when they are already up.</summary>
    private void ToggleBossTimers()
    {
        if (_timersWindow is { IsLoaded: true }) _timersWindow.Close();
        else ShowBossTimers();
    }

    public void ShowBossTimers()
    {
        if (_timersWindow is { IsLoaded: true })
        {
            Activate(_timersWindow);
            return;
        }
        _timersWindow = new BossTimersWindow(_meter);
        _timersWindow.Closed += (_, _) => _timersWindow = null;
        _timersWindow.Show();
    }

    public void ShowTop() => ShowTab("top");

    public void ShowPrices() => ShowTab("prices");

    /// <summary>Notification and chime for every watched item at or under its maximum, once per hourly poll.</summary>
    private void CheckPriceAlerts()
    {
        foreach (var w in _meter.TakePriceHits())
        {
            _tray?.ShowBalloon(UiText.Current.PricesTitle, string.Format(w.Sell ? UiText.Current.PriceHitSell : UiText.Current.PriceHit, w.Name, w.LastPrice ?? 0, w.MaxPrice, w.LastListings ?? 0), ShowPrices);
            System.Media.SystemSounds.Asterisk.Play();
        }
    }

    private NameNodeWindow? _nameWindow;

    public void ShowNameNode(int code, bool fromGather = false)
    {
        // One prompt at a time: a newer gather replaces an older unanswered prompt.
        if (_nameWindow is { IsLoaded: true } open) { if (open.Code == code) { open.Activate(); return; } open.Close(); }
        Window owner = _overlay;
        var w = new NameNodeWindow(_meter, code, fromGather);
        _nameWindow = w;
        w.Closed += (_, _) => { if (_nameWindow == w) _nameWindow = null; };
        if (owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        w.Show();
        w.Activate();
    }

    private void ToggleAtlas() => ShowTab(_overlay.Tab == "atlas" ? "meter" : "atlas");

    public void ShowAtlas() => ShowTab("atlas");

    private readonly List<OverlayWindow> _extras = new();

    /// <summary>One more card showing <paramref name="tab"/>, the same size as the one it came from; closed with its own X.</summary>
    public void PopOut(string tab, double width, double height)
    {
        var w = new OverlayWindow(_meter, secondary: true) { Width = width, Height = height };
        w.Closed += (_, _) => _extras.Remove(w);
        _extras.Add(w);
        w.Show();
        w.SelectTab(tab);
    }

    /// <summary>Atlas, Top and Prices are tabs of the overlay card (2026-10-10): show the card and switch; "meter" switches back.</summary>
    private void ShowTab(string tab)
    {
        if (!_overlay.IsVisible) SetOverlayVisible(true);
        _overlay.SelectTab(tab);
    }

    private readonly HashSet<(string, DateTimeOffset, int)> _eventAlerted = new();

    /// <summary>Windows notification and/or sound 10 and/or 5 minutes before each recurring event, as chosen on the pill's right-click menu.</summary>
    private void CheckEventAlerts()
    {
        var now = DateTimeOffset.Now;
        foreach (var ev in _meter.Events)
        {
            var (start, _, running) = ev.Next(now, _meter.Settings.EventAnchors.TryGetValue(ev.Key, out var anchor) ? anchor : null, _meter.Settings.EventPeriods.TryGetValue(ev.Key, out var pm) ? pm : null);
            if (running) continue;
            var a = _meter.Settings.AlertFor(ev.Key);
            if (!a.Notify && !a.Sound) continue;
            var left = start - now;
            foreach (var minutes in (int[])[10, 5])
            {
                if (minutes == 10 ? !a.At10 : !a.At5) continue;
                if (left > TimeSpan.FromMinutes(minutes) || left < TimeSpan.FromMinutes(minutes - 1) || !_eventAlerted.Add((ev.Key, start, minutes))) continue;
                if (a.Notify) _tray?.ShowBalloon(ev.Name, string.Format(UiText.Current.EventStartsAt, ev.Name, start.ToString("HH:mm"), (int)Math.Ceiling(left.TotalMinutes)));
                if (a.Sound) System.Media.SystemSounds.Asterisk.Play();
            }
        }
    }

    /// <summary>Tray notification (and a chime) shortly before a tracked boss respawns.</summary>
    private void CheckBossAlerts()
    {
        var t = UiText.Current;
        foreach (var timer in _meter.Timers.TakeDueAlerts(DateTimeOffset.Now, _meter.Settings.BossAlertMinutes))
        {
            var next = timer.NextSpawn!.Value;
            var minutes = (int)Math.Ceiling((next - DateTimeOffset.Now).TotalMinutes);
            var name = _meter.Data.NpcName(timer.NpcCode);
            var text = minutes > 0
                ? string.Format(t.AlertSoon, name, minutes, next.ToString("HH:mm"))
                : string.Format(t.AlertNow, name, next.ToString("HH:mm"));
            if (_meter.Settings.BossAlertNotify) _tray?.ShowBalloon(t.TimersTitle, text);
            if (_meter.Settings.BossAlertSound) System.Media.SystemSounds.Asterisk.Play();
        }
    }

    private bool _crashOffered;
    private BugReportWindow? _reportWindow;
    /// <summary>"Report a problem" (tray, settings, crash balloon): one window, the crash file name pre-filled when it comes from a crash.</summary>
    public void ShowBugReport(string? prefill = null)
    {
        if (_reportWindow is { IsLoaded: true } open) { Activate(open); return; }
        var w = new BugReportWindow(_meter.Settings, prefill);
        w.Closed += (_, _) => { if (_reportWindow == w) _reportWindow = null; };
        _reportWindow = w;
        w.Show();
    }

    private HelpWindow? _helpWindow;
    /// <summary>The "?" of a tab: one help window, re-pointed at the tab.</summary>
    public void ShowHelp(string tab)
    {
        if (_helpWindow is not { IsLoaded: true })
        {
            _helpWindow = new HelpWindow(_meter.Settings);
            _helpWindow.Closed += (_, _) => _helpWindow = null;
        }
        _helpWindow.ShowTab(tab);
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            Activate(_settingsWindow);
            return;
        }
        _settingsWindow = new SettingsWindow(_meter);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // ------------------------------------------------------------ updates

    private UpdateWindow? _updateWindow;
    private Version? _announcedUpdate;
    private bool _checkingByHand;

    /// <summary>A check finished or a version was skipped: the tray item follows; a new version is announced once.</summary>
    private void OnUpdatesChanged()
    {
        var updates = _meter.Updates;
        var available = updates.Available;
        _tray?.SetUpdate(available?.Version.ToString(3));
        if (updates.UpstreamAvailable is { } up && up.Version != _announcedUpstream)
        {
            _announcedUpstream = up.Version;   // Diego only (Settings.CheckUpstream): the public project moved, time to re-audit and rebase
            _tray?.ShowBalloon(UiText.Current.UpdateTitle, string.Format(UiText.Current.UpstreamBalloon, up.Tag), () => Updater.OpenReleasePage(up));
        }
        if (available is null || available.Version == _announcedUpdate) return;
        var t = UiText.Current;
        if (updates.AutoInstall && !updates.DownloadFailed)
        {
            // It installs itself: one notice once it is downloaded, nothing to click.
            if (updates.Downloaded?.Version != available.Version) return; // still downloading
            _announcedUpdate = available.Version;
            if (!_checkingByHand)
                _tray?.ShowBalloon(t.UpdateTitle, string.Format(t.UpdateReadyBalloon, available.Version.ToString(3)), InstallUpdateNow);
            return;
        }
        _announcedUpdate = available.Version;
        if (_checkingByHand) return; // the one who asked gets the update window instead
        _tray?.ShowBalloon(t.UpdateTitle, string.Format(t.UpdateBalloon, available.Version.ToString(3)), () => ShowUpdate());
    }

    private readonly long _startedAt = Environment.TickCount64;
    private Version? _announcedUpstream;
    private bool _installing;
    private Version? _installFailed; // not retried by itself: the tray item and the update window remain

    /// <summary>
    /// A downloaded update goes in at a quiet moment: when the game is not running, at once (but not in the meter's first
    /// minute); while it runs, after ten minutes without a fight. The installer has no window; the meter closes and the
    /// new version starts by itself.
    /// </summary>
    private void CheckPendingUpdate()
    {
        var updates = _meter.Updates;
        if (_installing || !updates.AutoInstall || updates.Downloaded is not { } ready || updates.Available?.Version != ready.Version ||
            ready.Version == _installFailed || _meter.Tracker.LiveFight() is not null) return; // turned off, skipped, failed, or a fight is on
        var sinceStart = Environment.TickCount64 - _startedAt;
        var quiet = Environment.TickCount64 - Math.Max(_lastEngagedTick, _startedAt);
        // Diego 2026-10-10: a downloaded update goes in right at start (first 3 minutes, no fight on); later only at a quiet moment.
        if (sinceStart > 3 * 60_000)
        {
            if (quiet < 60_000) return;
            if (quiet < 10 * 60_000 && GameProcessLocator.FindGameProcessIds().Length > 0) return;
        }
        InstallUpdateNow();
    }

    /// <summary>Installs the downloaded update right away (also the overlay banner's and the update window's choice).</summary>
    public void InstallUpdateNow()
    {
        if (_installing || _meter.Updates.Downloaded is not { } update) return;
        _installing = true;
        try
        {
            Log.Info($"Installing update {update.Tag}");
            _meter.Updates.InstallDownloaded();
            Shutdown(); // the installer replaces our files and starts the new version
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            _installing = false;
            _installFailed = update.Version;
            Log.Error("Update could not start", ex);
        }
    }

    /// <summary>The update window for <paramref name="release"/>, by default the available update.</summary>
    public void ShowUpdate(Core.Updates.ReleaseInfo? release = null)
    {
        release ??= _meter.Updates.Available;
        if (release is null) return;
        if (_updateWindow is { IsLoaded: true } open && open.Release.Version == release.Version)
        {
            Activate(open);
            return;
        }
        _updateWindow?.Close();
        var w = new UpdateWindow(_meter.Updates, release, !_meter.Updates.IsDevBuild);
        w.Closed += (_, _) =>
        {
            if (_updateWindow == w) _updateWindow = null;
        };
        _updateWindow = w;
        w.Show();
    }

    /// <summary>"Check for updates" by hand (tray or settings): asks GitHub now; a newer version opens the update window.</summary>
    public async Task<UpdateCheckResult> CheckUpdatesNowAsync()
    {
        UpdateCheckResult result;
        _checkingByHand = true;
        try
        {
            result = await _meter.Updates.CheckAsync();
        }
        finally
        {
            _checkingByHand = false;
        }
        if (result == UpdateCheckResult.Available) ShowUpdate(_meter.Updates.Latest);
        return result;
    }

    private async Task CheckUpdatesFromTrayAsync()
    {
        var t = UiText.Current;
        switch (await CheckUpdatesNowAsync())
        {
            case UpdateCheckResult.UpToDate:
                _tray?.ShowBalloon(AppInfo.Name, t.UpToDate);
                break;
            case UpdateCheckResult.Failed:
                _tray?.ShowBalloon(AppInfo.Name, string.Format(t.UpdateCheckFailed, _meter.Updates.LastError));
                break;
        }
    }

    public void OnSettingsChanged()
    {
        RegisterHotkeys();
        _overlay.ApplyAppearance();
        _overlay.Refresh();
    }

    /// <summary>The Settings window's row size slider, shown on the overlay before it is saved.</summary>
    public void PreviewRowSize(int percent) => _overlay?.ApplyRowSize(percent);

    private void PickReplay()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Replay an AION 2 capture",
            Filter = "Packet captures (*.pcap;*.pcapng)|*.pcap;*.pcapng|All files|*.*",
            InitialDirectory = Path.Combine(AppSettings.AppDataDir, "captures"),
        };
        if (dialog.ShowDialog() == true) _meter.Replay(dialog.FileName);
    }

    private static void Activate(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_meter is not null) Log.Info("Exiting");
        _timer?.Stop();
        _showWait?.Unregister(null);
        _showRequest?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_meter is not null)
        {
            _meter.Settings.Save();
            _meter.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
