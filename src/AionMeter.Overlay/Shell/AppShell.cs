using AionMeter.Core.Armory;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay.Shell;

/// <summary>What the card process (OverlayHost) gives the shell: the GTK application, the output, the card's scale and opacity.</summary>
internal interface ICardHost
{
    Gtk.Application App { get; }
    /// <summary>--layer: the card is a layer-shell surface (dialogs too); otherwise a Sway window and dialogs are transient toplevels of <see cref="Window"/>.</summary>
    bool Layer { get; }
    /// <summary>The card's window (null before it exists).</summary>
    Gtk.Window? Window { get; }
    /// <summary>The GdkMonitor the card is on (extra surfaces go there); IntPtr.Zero = the compositor's choice.</summary>
    IntPtr Monitor { get; }
    /// <summary>The card's UI scale (ui.json "UiScale" or --scale).</summary>
    double Scale { get; }
    /// <summary>Rebuilds the card at <paramref name="scale"/> (views are recreated, the tab in front reopens); saves it to ui.json when asked.</summary>
    void Rescale(double scale, bool save);
    /// <summary>The card's background opacity (overlay.json).</summary>
    double Opacity { get; }
    void SetOpacity(double opacity);
    /// <summary>Ends the card process (Settings → Daemon "Quit the card", the socket's quit, SIGTERM): the pid file and the socket go with it.</summary>
    void Quit();
}

/// <summary>The fight breakdown tab ("fight"): the view the FightView port registers implements this so the shell can drive it.</summary>
internal interface IFightTab
{
    /// <summary>An overlay fight id ("live", "s:&lt;guid&gt;", "h:&lt;file&gt;") on <paramref name="actor"/> (0 = you, else the top), on <paramref name="tab"/> ("dps", "profile", …).</summary>
    void SetFight(string fightId, uint actor, string? tab);
    /// <summary>A2M-44 player card without a fight (Top DPS ranking rows); <paramref name="status"/> while there is no profile yet.</summary>
    void SetCard(PlayerCard card, string? status);
}

/// <summary>
/// What every hosted view of the layer-shell card gets (A2M-50, the Avalonia AppShell on GTK). The process is unprivileged and never captures:
/// <list type="bullet">
/// <item><see cref="Daemon"/>: live data and commands over the daemon's socket (overlay snapshots, fights, `fight-detail`, `reload-settings`, …).</item>
/// <item><see cref="Engine"/>: a viewer-mode MeterEngine over the same data dir (~/.local/share/AionMeter), read-only, built in the background;
/// <see cref="WhenEngine"/> runs code once it exists.</item>
/// <item><see cref="Icons"/>: item / skill icons as GdkTextures (disk cache + downloads per the "Download icons" setting).</item>
/// <item><see cref="SaveSettings"/>: the only way a view writes settings.json (atomic) — it also tells the daemon to reload.</item>
/// </list>
/// The shell outlives the card (a UI-scale change rebuilds the card and its views, not the shell). Everything here is main-loop only; background
/// work goes through <see cref="Run{T}"/> or <see cref="UiLoop.Post"/>.
/// </summary>
internal sealed class AppShell : IDisposable
{
    private readonly List<Action<MeterEngine>> _waiting = new();
    private HelpWindow? _help;

    public AppShell(OverlayClient daemon, ICardHost host, string uiJson)
    {
        Daemon = daemon;
        Host = host;
        UiJsonPath = uiJson;
        Settings = AppSettings.Load();   // read only: never re-saved on start (the daemon owns the migration save)
        UiText.Use(Settings.Language);
        Icons = new IconProvider(MeterEngine.DataDirectory, Settings.DownloadIcons);
        daemon.Overlay += m => LastOverlay = m;
    }

    public OverlayClient Daemon { get; }
    public ICardHost Host { get; }
    public IconProvider Icons { get; }
    /// <summary>ui.json (the card writes single keys through UiJson).</summary>
    public string UiJsonPath { get; }
    /// <summary>settings.json as this process knows it (the engine's own instance once it is loaded). Re-read with <see cref="RefreshSettings"/>.</summary>
    public AppSettings Settings { get; }
    /// <summary>The viewer engine; null for the first ~0.3 s after start (and if it failed to load: see the log).</summary>
    public MeterEngine? Engine { get; private set; }
    public event Action? EngineReady;
    /// <summary>The newest `overlay` snapshot (null without a daemon).</summary>
    public OverlayModel? LastOverlay { get; private set; }
    /// <summary>Settings saved with another language: the card calls every view's LanguageChanged.</summary>
    public event Action? LanguageChanged;

    /// <summary>The card's UI scale (80–200 %).</summary>
    public double UiScale => Host.Scale;
    /// <summary>Raised after the card was rebuilt at a new scale (the views are new instances by then; dialogs listen).</summary>
    public event Action<double>? UiScaleChanged;
    internal void RaiseUiScaleChanged(double scale) => UiScaleChanged?.Invoke(scale);

    /// <summary>Settings → Overlay "UI scale": saved in ui.json, the card is rebuilt at it on the next main-loop turn (after the click that asked).</summary>
    public void SetUiScale(double scale)
    {
        scale = AionMeter.Service.Overlay.UiScale.Clamp(scale);
        if (Math.Abs(scale - Host.Scale) < 0.001) return;
        UiLoop.Post(() => Host.Rescale(scale, save: true));
    }

    /// <summary>The card's background opacity (overlay.json, Settings → Overlay); settings.json keeps the Windows overlay's value in step.</summary>
    public double CardOpacity => Host.Opacity;
    public void SetCardOpacity(double opacity) => Host.SetOpacity(opacity);

    /// <summary>The card: selects a tab ("meter" = DPS rows). Set by the card on every (re)build.</summary>
    public Action<string>? SelectTabHandler { get; set; }
    /// <summary>The card: sets up its "fight" view (the callback runs before it is shown) and brings it to front; null without a FightView registered.</summary>
    public Func<Action<IFightTab>, IFightTab?>? FightHandler { get; set; }
    /// <summary>The tab in front on the card ("meter" while the DPS rows show).</summary>
    public Func<string>? CurrentTab { get; set; }

    public void SelectTab(string key) => SelectTabHandler?.Invoke(key);

    /// <summary>The fight breakdown tab for an overlay fight id ("live", "s:&lt;guid&gt;", "h:&lt;file&gt;") on <paramref name="actor"/> (0 = you, else the top),
    /// opened on <paramref name="tab"/> ("dps", "profile", …) when given. Null until a "fight" view is registered.</summary>
    public IFightTab? OpenFight(string fightId, uint actor = 0, string? tab = null) => FightHandler?.Invoke(v => v.SetFight(fightId, actor, tab));

    /// <summary>A2M-44 player card without a fight (Top DPS ranking rows), in the card's "fight" tab.</summary>
    public IFightTab? OpenCard(PlayerCard card, string? status) => FightHandler?.Invoke(v => v.SetCard(card, status));

    /// <summary>The help page of a tab ("meter", "settings", "atlas", …) on its own layer surface, EN/ES per Settings.HelpLanguage.</summary>
    public void ShowHelp(string key)
    {
        _help ??= new HelpWindow(this);
        _help.ShowTab(key);
    }

    internal void HelpClosed(HelpWindow w) { if (ReferenceEquals(_help, w)) _help = null; }

    /// <summary>"Report a problem" (Settings → Privacy, --window report): one surface, <paramref name="prefill"/> as the note (a crash file name).</summary>
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

    /// <summary>Runs <paramref name="work"/> on the thread pool and <paramref name="done"/> with its result on the main loop (a failure is logged, done is skipped).</summary>
    public static void Run<T>(Func<T> work, Action<T> done, string what = "background work")
    {
        _ = Task.Run(() =>
        {
            T r;
            try { r = work(); }
            catch (Exception e) { Log.Error($"UI {what} failed", e); return; }
            UiLoop.Post(() => done(r));
        });
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
                Console.Error.WriteLine($"overlay: viewer engine ready in {Environment.TickCount64 - started} ms");
                Log.Info($"Overlay viewer engine ready in {Environment.TickCount64 - started} ms ({engine.Data.Skills.Count} skills, {engine.History.List().Count} saved fights)");
                UiLoop.Post(() =>
                {
                    Engine = engine;
                    Icons.ExtraUrl = engine.Names.ExtraIconUrl;
                    foreach (var a in _waiting) a(engine);
                    _waiting.Clear();
                    EngineReady?.Invoke();
                });
            }
            catch (Exception e) { Console.Error.WriteLine("overlay: viewer engine failed: " + e); Log.Error("Overlay viewer engine failed", e); }
        });
    }

    /// <summary>Runs <paramref name="action"/> with the engine now, or on the main loop once it has loaded.</summary>
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
        Icons.Dispose();
        Engine?.Dispose();
    }
}
