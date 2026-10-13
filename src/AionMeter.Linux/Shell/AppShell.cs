using Avalonia.Threading;
using AionMeter.Core.Armory;
using AionMeter.Linux.Views;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux.Shell;

/// <summary>
/// What every hosted view gets (A2M-48 phase 3b). The UI process is unprivileged and never captures:
/// <list type="bullet">
/// <item><see cref="Daemon"/>: live data and commands over the daemon's socket (overlay snapshots, fights, `fight-detail`, `reload-settings`).</item>
/// <item><see cref="Engine"/>: a viewer-mode MeterEngine over the same data dir (~/.local/share/AionMeter): settings, fight history, character
/// files, inventories, builds, price watch, atlas, boss timers, top-dps.json, names and catalogues — read-only, built in the background;
/// <see cref="WhenEngine"/> runs code once it exists.</item>
/// <item><see cref="Icons"/>: item / skill icons as bitmaps (disk cache + downloads per the "Download icons" setting).</item>
/// <item><see cref="SaveSettings"/>: the only way a view writes settings.json (atomic) — it also tells the daemon to reload.</item>
/// </list>
/// Everything here is UI-thread only.
/// </summary>
public sealed class AppShell : IDisposable
{
    private readonly List<Action<MeterEngine>> _waiting = new();

    public AppShell(DaemonClient daemon, UiSettings ui)
    {
        Daemon = daemon;
        Ui = ui;
        Settings = AppSettings.Load();   // read only: never re-saved on start (the daemon owns the migration save)
        UiText.Use(Settings.Language);
        Icons = new IconProvider(MeterEngine.DataDirectory, Settings.DownloadIcons);
        daemon.Overlay += m => LastOverlay = m;
        ui.ScaleChangedOnDisk += () => UiScaleChanged?.Invoke(Ui.UiScale);
        // the layer-shell card writes "UiScale" into ui.json (wheel on its corner): a 2 s mtime poll
        _uiPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _uiPoll.Tick += (_, _) => Ui.ReloadScaleIfChanged();
        _uiPoll.Start();
    }

    private readonly DispatcherTimer _uiPoll;

    /// <summary>A2M-48 UX: the zoom of every Avalonia window (and, through ui.json, the layer-shell card) changed; argument = the new factor.</summary>
    public event Action<double>? UiScaleChanged;

    /// <summary>Settings → Overlay "UI scale", Ctrl+wheel: clamped to 80–200 %, saved in ui.json (the card picks it up within 2 s), applied.</summary>
    public void SetUiScale(double scale)
    {
        Ui.ReloadScaleIfChanged();
        scale = UiScale.Clamp(scale);
        if (Math.Abs(scale - Ui.UiScale) < 0.001) return;
        Ui.UiScale = scale;
        Ui.Save();
        UiScaleChanged?.Invoke(scale);
    }

    public DaemonClient Daemon { get; }
    public UiSettings Ui { get; }
    public IconProvider Icons { get; }
    /// <summary>settings.json as this process knows it (the engine's own instance once it is loaded). Re-read with <see cref="RefreshSettings"/>.</summary>
    public AppSettings Settings { get; }
    /// <summary>The viewer engine; null for the first second or so after start (and if it failed to load: see the log).</summary>
    public MeterEngine? Engine { get; private set; }
    public event Action? EngineReady;
    /// <summary>The newest `overlay` snapshot (null without a daemon).</summary>
    public OverlayModel? LastOverlay { get; private set; }
    /// <summary>Settings saved with another language: views rebuild their texts (the host calls <see cref="IHostedView.LanguageChanged"/>).</summary>
    public event Action? LanguageChanged;

    /// <summary>The card host: selects a tab ("meter" = DPS rows), opens the breakdown, shows help. Set by the overlay window.</summary>
    public Action<string>? SelectTabHandler { get; set; }
    /// <summary>Sets up the card's "fight" view (the callback runs before it is shown) and brings it to front; returns it.</summary>
    public Func<Action<FightView>, FightView?>? FightHandler { get; set; }

    public void SelectTab(string key) => SelectTabHandler?.Invoke(key);

    /// <summary>The fight breakdown tab for an overlay fight id ("live", "s:&lt;guid&gt;", "h:&lt;file&gt;") on <paramref name="actor"/> (0 = you, else the top),
    /// opened on <paramref name="tab"/> ("dps", "profile", …) when given.</summary>
    public FightView? OpenFight(string fightId, uint actor = 0, string? tab = null) => FightHandler?.Invoke(v => v.SetFight(fightId, actor, tab));

    /// <summary>A2M-44 player card without a fight (Top DPS ranking rows): header, CP / item level / rank / seen tiles and the Profile / Skills /
    /// Daevanion tabs the breakdown renders; <paramref name="status"/> shows while there is no profile yet ("Loading…", "not found"). Update it
    /// with <see cref="FightView.UpdateCard"/> on the returned view.</summary>
    public FightView? OpenCard(PlayerCard card, string? status) => FightHandler?.Invoke(v => v.SetCard(card, status));

    private HelpWindow? _help;

    /// <summary>The help page of a tab ("meter", "settings", "atlas", …) in its own window, EN/ES per Settings.HelpLanguage.</summary>
    public void ShowHelp(string key)
    {
        if (_help is null || !_help.IsVisible) { _help = new HelpWindow(this); _help.Closed += (_, _) => _help = null; }
        _help.ShowTab(key);
    }

    /// <summary>"Report a problem" (Settings → Privacy, --window report): one window, <paramref name="prefill"/> as the note (a crash file name).</summary>
    public void ShowReport(string? prefill = null) => DialogCard.ShowSingle(() => new Views.ReportWindow(this, prefill));

    /// <summary>"Restore from code" (Settings → Backup, --window restore).</summary>
    public void ShowRestore() => DialogCard.ShowSingle(() => new Views.RestoreWindow(this));

    /// <summary>The Build tab's "Import from questlog.gg" (--window import): the tab opens and raises it, so the picked build lands in that tab.</summary>
    public event Action? ImportRequested;

    /// <summary>--window KEY at start: report, restore, import.</summary>
    public void ShowWindow(string key)
    {
        switch (key)
        {
            case "report": ShowReport(); break;
            case "restore": ShowRestore(); break;
            case "import": SelectTab("build"); WhenEngine(_ => ImportRequested?.Invoke()); break;
        }
    }

    public void StartEngine()
    {
        var settings = Settings;
        _ = Task.Run(() =>
        {
            try
            {
                var started = Environment.TickCount64;
                var engine = MeterEngine.OpenViewer(settings);
                Console.Error.WriteLine($"viewer engine ready in {Environment.TickCount64 - started} ms");
                Log.Info($"UI viewer engine ready in {Environment.TickCount64 - started} ms ({engine.Data.Skills.Count} skills, {engine.History.List().Count} saved fights)");
                Dispatcher.UIThread.Post(() =>
                {
                    Engine = engine;
                    Icons.ExtraUrl = engine.Names.ExtraIconUrl;
                    foreach (var a in _waiting) a(engine);
                    _waiting.Clear();
                    EngineReady?.Invoke();
                });
            }
            catch (Exception e) { Console.Error.WriteLine("viewer engine failed: " + e); Log.Error("UI viewer engine failed", e); }
        });
    }

    /// <summary>Runs <paramref name="action"/> with the engine now, or on the UI thread once it has loaded.</summary>
    public void WhenEngine(Action<MeterEngine> action)
    {
        if (Engine is { } e) action(e);
        else _waiting.Add(action);
    }

    /// <summary>settings.json changed on disk since this process read or wrote it (the daemon's own saves, `set-measure`): take it in place.</summary>
    public void RefreshSettings()
    {
        if (Settings.ChangedOnDisk()) Settings.CopyFrom(AppSettings.Load());
    }

    /// <summary>
    /// Writes settings.json (aside + rename) and sends `reload-settings`; the daemon also notices the new mtime within 2 s if the socket is
    /// down. Applies what the UI itself follows: icon downloads, language (UiText, the engine's names, every view's texts).
    /// </summary>
    public void SaveSettings(string? previousLanguage = null)
    {
        try { Settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("settings.json not saved", e); return; }
        Daemon.Send("reload-settings");
        Icons.Enabled = Settings.DownloadIcons;
        Engine?.ApplySettingsInPlace();
        if (previousLanguage is not null && UiText.Normalize(previousLanguage) != UiText.Normalize(Settings.Language))
        {
            UiText.Use(Settings.Language);
            Engine?.Data.ReloadNames(MeterEngine.DataDirectory, Settings.Language);
            LanguageChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _uiPoll.Stop();
        Icons.Dispose();
        Engine?.Dispose();
    }
}
