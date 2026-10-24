using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Combat;

namespace AionMeter.Service;

public enum HistoryMode
{
    BossesOnly,
    BossesAndLong,
    All,
}

public sealed class AppSettings
{
    /// <summary>
    /// %AppData%\AionMeter on Windows; $XDG_DATA_HOME/AionMeter (~/.local/share/AionMeter) elsewhere (A2M-48): .NET maps ApplicationData
    /// to $XDG_CONFIG_HOME on Linux and LocalApplicationData to $XDG_DATA_HOME. Same layout and file formats on both.
    /// </summary>
    public static string AppDataDir { get; private set; } = DefaultAppDataDir();

    public static string DefaultAppDataDir() => Path.Combine(Environment.GetFolderPath(OperatingSystem.IsWindows()
        ? Environment.SpecialFolder.ApplicationData : Environment.SpecialFolder.LocalApplicationData), "AionMeter");

    /// <summary>Daemon --data-dir and tests; resets <see cref="Log"/>'s file path so the next line goes under the new dir.</summary>
    public static void UseAppDataDir(string dir)
    {
        AppDataDir = Path.GetFullPath(dir);
        Log.ResetPath();
    }

    private static string FilePath => Path.Combine(AppDataDir, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Overlay window
    public double OverlayLeft { get; set; } = 80;
    public double OverlayTop { get; set; } = 160;
    public double OverlayWidth { get; set; } = 460;
    public double OverlayHeight { get; set; } = 330;
    public double BackgroundOpacity { get; set; } = 0.9;
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    public bool OverlayVisible { get; set; } = true;
    /// <summary>Bring the overlay up by itself when a boss is fought nearby or you hit anything.</summary>
    public bool AutoShow { get; set; } = true;
    /// <summary>An overlay that came up by itself hides again after this long out of combat; 0 = stays up.</summary>
    public int AutoHideSeconds { get; set; } = 60;
    /// <summary>Bring the overlay up when the meter starts and when it finds the game, so a player sees it is running.</summary>
    public bool ShowOnStart { get; set; } = true;
    /// <summary>A2M-42: overlay, pop-out cards and every other meter window on all virtual desktops; off = the one they open on.</summary>
    public bool AllDesktops { get; set; } = true;

    // Boss respawn timers
    /// <summary>Alert this many minutes before a tracked boss respawns; 0 = off.</summary>
    public int BossAlertMinutes { get; set; } = 2;
    // Diego 2026-10-10: a Windows toast over the game tanks the FPS; sound only by default, toast opt-in (right-click on the boss pill)
    public bool BossAlertNotify { get; set; } = false;
    public bool BossAlertSound { get; set; } = true;

    /// <summary>Per recurring event (key from data/atlas/events.json): Windows notification and/or sound, 10 and/or 5 minutes before the start.</summary>
    public sealed class EventAlert
    {
        public bool Notify { get; set; } = false;   // Windows toast over the game drops the FPS (Diego, 2026-10-10): sound only unless enabled per event
        public bool Sound { get; set; } = true;
        public bool At10 { get; set; } = true;
        public bool At5 { get; set; } = true;
    }

    public Dictionary<string, EventAlert> EventAlerts { get; set; } = new();
    /// <summary>A start the player confirmed in game per periodic event (key → time): re-phases the cycle until "back to the schedule".</summary>
    public Dictionary<string, DateTimeOffset> EventAnchors { get; set; } = new();
    /// <summary>Cycle length measured from two "Starts now" marks of the same event (minutes); absent = the nominal every_minutes.</summary>
    public Dictionary<string, double> EventPeriods { get; set; } = new();

    public EventAlert AlertFor(string key)
    {
        if (!EventAlerts.TryGetValue(key, out var a)) EventAlerts[key] = a = new EventAlert();
        return a;
    }

    // Meter
    public TargetMode TargetMode { get; set; } = TargetMode.BossOnly;
    /// <summary>True: the leader's bar is full and the rest are sized against it. False: bars show share of party damage.</summary>
    public bool BarsRelativeToTop { get; set; } = true;
    public int MaxRows { get; set; } = 10;
    public const int MinRowSize = 50, MaxRowSize = 100;
    /// <summary>Overlay row size in percent of the original (Settings → Overlay, or Ctrl + wheel over the rows).</summary>
    public int RowSize { get; set; } = 100;
    /// <summary>Gear score and combat power after the names in the overlay (when the party roster gave them).</summary>
    public bool ShowGear { get; set; } = true;
    public bool ShowBossPanel { get; set; } = true;
    public int IdleTimeoutSec { get; set; } = 10;
    public int BossIdleTimeoutSec { get; set; } = 30;

    /// <summary>Interface language: "en" (default) or "es"; "ru" of older builds loads as "en" (A2M-47). Game names follow it unless
    /// <see cref="NamesLanguage"/> says otherwise.</summary>
    public string Language { get; set; } = "en";

    /// <summary>A2M-47 Settings → "Names: items, skills and monsters": "" = same as <see cref="Language"/> (default), "en" or "es". The one
    /// source of truth for every game name (engine skill/NPC/map tables, item names, skill catalogue, Daevanion, recipes); the Items / Build /
    /// Prices language switches write it through <see cref="PickNames"/>. Replaced the per-tab SkillLanguage and PriceSearchLanguage.</summary>
    public string NamesLanguage { get; set; } = "";

    /// <summary>The language game names are shown in: <see cref="NamesLanguage"/> when set, else the interface language.</summary>
    [JsonIgnore]
    public string ResolvedNamesLanguage => ResolveNames(Language, NamesLanguage);

    public static string ResolveNames(string? language, string? names) => names is "en" or "es" ? names : UiText.Normalize(language);

    /// <summary>Settings → Names drop-down rows (GTK and WPF): 0 = same as the interface (""), 1 = English, 2 = Spanish.</summary>
    public static int NamesIndex(string? names) => names switch { "en" => 1, "es" => 2, _ => 0 };
    public static string NamesValue(int index) => index switch { 1 => "en", 2 => "es", _ => "" };

    /// <summary>A per-tab switch picked <paramref name="pick"/>: the interface's own language is stored as "" so the names keep following
    /// the interface (as before, when switching the interface reset every per-tab choice); the other one is stored explicitly.</summary>
    public void PickNames(string pick)
    {
        pick = UiText.Normalize(pick);
        NamesLanguage = pick == UiText.Normalize(Language) ? "" : pick;
    }

    /// <summary>An older file's per-tab keys: either one that differs from the interface language was a deliberate pick (switching the
    /// interface reset both to it), so the names keep that language; both equal to the interface (or absent) → "" (follow it).</summary>
    public static string MigrateNames(string? language, string? skillLanguage, string? priceSearchLanguage)
    {
        var ui = UiText.Normalize(language);
        if (priceSearchLanguage is not null && UiText.Normalize(priceSearchLanguage) != ui) return UiText.Normalize(priceSearchLanguage);
        if (skillLanguage is not null && UiText.Normalize(skillLanguage) != ui) return UiText.Normalize(skillLanguage);
        return "";
    }

    /// <summary>A2M-40, Settings → Game: the shard (eu nae naw la as) the server dropdown lists; only decides when no server is known.</summary>
    public string Region { get; set; } = "eu";
    /// <summary>A2M-40: the server the user plays on; 0 = auto (the logged-in character's, else the last character folder's, else 1303).</summary>
    public int ServerId { get; set; }

    /// <summary>Fetch skill icons from the official AION 2 game-data CDN (cached locally).</summary>
    public bool DownloadIcons { get; set; } = true;
    /// <summary>A2M-38: after a boss kill, ask Abyss Logs where the player's DPS sits among their class this week.</summary>
    public bool AbyssLogsPercentile { get; set; } = true;

    /// <summary>
    /// Base url of a FlareSolverr / solvearr proxy (e.g. "http://127.0.0.1:8191"), reached over the private network,
    /// that the price and leaderboard scrapers use to get past a Cloudflare challenge — the same bypass the NAS media
    /// stack uses. Empty = direct requests only (default). Never point at a publicly reachable solver: its /v1 fetches
    /// any url for the caller.
    /// </summary>
    public string CloudflareSolverUrl { get; set; } = "";

    // Updates
    /// <summary>Ask GitHub for a newer version shortly after start and every few hours.</summary>
    public bool CheckUpdates { get; set; } = true;    // our own release feed (akunito/aion2-meter-releases), 2026-10-10
    /// <summary>Download a new version by itself and apply it at start or at a quiet moment (portable zip over this folder).</summary>
    public bool AutoInstallUpdates { get; set; } = true;
    /// <summary>"stable" (GitHub latest, no pre-releases) or "dev" (newest release including pre-releases).</summary>
    public string UpdateChannel { get; set; } = "stable";
    /// <summary>Also watch the upstream project's releases (a new one means re-audit + rebase); only Diego turns this on.</summary>
    public bool CheckUpstream { get; set; } = false;
    // Files from builds that sent telemetry or kept a server backup still carry Telemetry, Share*, InstallId, ReportSends, *TelemetrySent…:
    // System.Text.Json skips unknown properties by default, so they load and the next Save drops them (AppSettingsCompatTests).
    /// <summary>Bug report parts besides logs + settings (always): character summary, Top DPS rows, inventory. Remembered from the dialog too.</summary>
    public bool ReportSummary { get; set; } = true;
    public bool ReportTopDps { get; set; } = true;
    public bool ReportInventory { get; set; }

    /// <summary>Allocates: call it per send or per report, not per frame.</summary>
    public Core.Diagnostics.SharePolicy CurrentSharePolicy() => new(ReportSummary, ReportTopDps, ReportInventory);
    /// <summary>A2M-27: the "install Npcap" message is offered on the first start without it, once; Settings → Capture re-opens it.</summary>
    public bool NpcapHelpShown { get; set; } = false;
    /// <summary>Scheduled task "AION2 meter autostart" registered: the meter starts with AION2.exe and closes 60 s after it.</summary>
    public bool StartWithGame { get; set; } = false;
    /// <summary>With the task on: also close the meter 60 s after AION2.exe ends. Off by default (Diego 2026-10-11: the meter must only open by itself).</summary>
    public bool CloseWithGame { get; set; } = false;
    /// <summary>A version the user chose to skip ("0.2.0"): not announced again, a newer one is.</summary>
    public string? SkippedUpdate { get; set; }

    // Capture / storage
    public bool SaveHistory { get; set; } = true;
    public HistoryMode HistoryMode { get; set; } = HistoryMode.BossesAndLong;
    /// <summary>For <see cref="HistoryMode.BossesAndLong"/>: non-boss fights shorter than this are not saved.</summary>
    public int HistoryMinSeconds { get; set; } = 20;
    public bool RecordPackets { get; set; }
    /// <summary>Size budget of the captures folder in GB, oldest recordings deleted first (CaptureRetention); 0 = unlimited.</summary>
    public double CaptureKeepGb { get; set; } = 2;
    public string? CaptureDevice { get; set; }
    /// <summary>A2M-27: null = this PC's adapters (Npcap / libpcap); "tcp://host:port" = the packets another meter forwards (a Windows meter in a
    /// VM or under Wine reading the Linux daemon's --forward). Needs no Npcap.</summary>
    public string? CaptureSource { get; set; }
    /// <summary>A2M-27, daemon: "ip:port" to listen on and forward the captured game packets to (pcap stream, plaintext, no auth); null = off.
    /// --forward overrides it; read at start.</summary>
    public string? ForwardListen { get; set; }

    // Hotkeys (modifier+key, parsed by HotkeyManager)
    public string HotkeyToggleOverlay { get; set; } = "Ctrl+Shift+D";
    public string HotkeyReset { get; set; } = "Ctrl+Shift+R";
    public string HotkeyClickThrough { get; set; } = "Ctrl+Shift+L";
    public string HotkeyTimers { get; set; } = "Ctrl+Shift+T";
    public string HotkeyAtlas { get; set; } = "Ctrl+Shift+A";
    // Atlas window placement (DIP); null = not saved yet (NaN is not JSON: it killed the app on its first Save, 2026-10-08).
    public double? AtlasLeft { get; set; }
    public double? AtlasTop { get; set; }
    public double AtlasWidth { get; set; } = 900;
    /// <summary>Node kinds whose best areas are outlined on the atlas (up to two, English names lower-cased).</summary>
    public List<string> AtlasZoneKinds { get; set; } = new();
    /// <summary>Broker cut taken from a sale, used by the craft profit calculator. 10 is an assumption (2026-10-10): adjust when measured on a real sale.</summary>
    public double BrokerFeePercent { get; set; } = 10;
    /// <summary>Craft tab: only recipes the stock allows now (default); off also lists those short by one or two materials (Diego, 2026-10-10).</summary>
    public bool CraftOnlyNow { get; set; } = true;   // superseded by CraftScope, kept for old files
    /// <summary>Craft list scope: "now" (stock covers the recipe), "near" (short by ≤ 2 kinds), "all".</summary>
    public string CraftScope { get; set; } = "now";
    /// <summary>Pinned recipes (output item ids): always listed first, in every scope and in profit mode.</summary>
    public List<int> CraftFavourites { get; set; } = new();
    /// <summary>Language of the in-app help pages ("en" default, "es"); independent of the UI language.</summary>
    public string HelpLanguage { get; set; } = "en";
    /// <summary>Atlas: draw the spawn points of the monsters whose pet souls the character still lacks.</summary>
    public bool AtlasShowPets { get; set; } = true;   // on unless the user switches it off (Diego, 2026-10-10)
    /// <summary>Read from older files only (Items / Craft / Prices name language): <see cref="Load"/> folds it into <see cref="NamesLanguage"/>, never written again.</summary>
    [JsonPropertyName("PriceSearchLanguage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPriceSearchLanguage { get; set; }
    /// <summary>Prices tab, expanded row: chart range "24h", "7d" or "30d".</summary>
    public string PricesRange { get; set; } = "7d";
    /// <summary>Read from older files only (Build skill language): <see cref="Load"/> folds it into <see cref="NamesLanguage"/>, never written again.</summary>
    [JsonPropertyName("SkillLanguage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySkillLanguage { get; set; }
    /// <summary>Build tab: list skills already at the build's level (off hides them from the library and dims them on the bar).</summary>
    public bool BuildShowDone { get; set; } = true;
    /// <summary>Build tab, Daevanion: draw nodes already open at full strength (off dims them to show only what is left).</summary>
    public bool DaevShowOpen { get; set; } = true;
    /// <summary>The user's questlog.gg slug (…/skill-builder/&lt;slug&gt;): "My builds" in the Import dialog and the suggestions card. Learnt from the first questlog link imported, editable in the dialog; no login.</summary>
    public string QuestlogSlug { get; set; } = "";
    /// <summary>Build tab, Daevanion: skill icon over the "Skill Level Up" nodes.</summary>
    public bool DaevSkillIcons { get; set; } = true;
    /// <summary>Browser link of a watched item; {id} and {server} are replaced. The site is a hash-routed SPA: "/#/items/{id}" (Diego, 2026-10-09); "/item/{id}" renders blank.</summary>
    public string PriceItemUrl { get; set; } = "https://aion2.exchange/#/items/{id}";
    public double AtlasHeight { get; set; } = 760;

    /// <summary>Bumped when the overlay layout changes enough that saved sizes no longer fit.</summary>
    public int LayoutVersion { get; set; } // 0 when missing from an older settings file; new installs get it in Load()
    private const int CurrentLayout = 4;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new AppSettings();
                if (s.LayoutVersion < 3)
                {
                    // v3 = card layout (portrait header, two-line rows): the old compact size is too small for it.
                    s.OverlayWidth = Math.Max(s.OverlayWidth, 470);
                    s.OverlayHeight = Math.Max(s.OverlayHeight, 540);
                }
                if (s.LayoutVersion < 4)
                {
                    // v4 = English / Russian switch. Earlier builds picked the language from Windows on their own;
                    // the default is now English until the user chooses.
                    s.Language = "en";
                }
                s.Language = UiText.Normalize(s.Language);
                s.MigrateLegacyNames();
                s.LayoutVersion = CurrentLayout;
                s.KnownStamp = File.GetLastWriteTimeUtc(FilePath);
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return new AppSettings { LayoutVersion = CurrentLayout };
    }

    /// <summary>Folds the per-tab keys of an older file into <see cref="NamesLanguage"/> (an explicit NamesLanguage wins) and drops them.</summary>
    public void MigrateLegacyNames()
    {
        NamesLanguage = NamesLanguage is "en" or "es" ? NamesLanguage : "";
        if (LegacySkillLanguage is null && LegacyPriceSearchLanguage is null) return;
        if (NamesLanguage.Length == 0) NamesLanguage = MigrateNames(Language, LegacySkillLanguage, LegacyPriceSearchLanguage);
        LegacySkillLanguage = LegacyPriceSearchLanguage = null;
    }

    /// <summary>Throw-away settings (render tool): never written to disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Transient { get; init; }

    /// <summary>Daemon --offline and tests: state files are written as usual, but nothing goes to the network (profile sync, price polls, Abyss Logs, questlog names).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Offline { get; set; }


    public void Save()
    {
        if (Transient) return;
        Directory.CreateDirectory(AppDataDir);
        // Written aside, then swapped in: Windows shutting the app down mid-write must not leave an empty settings.json.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
        KnownStamp = File.GetLastWriteTimeUtc(FilePath);
    }

    /// <summary>settings.json's mtime when this instance last read or wrote it: another process saved when the file's differs (A2M-48:
    /// the Linux UI writes settings.json, the daemon reloads it).</summary>
    [JsonIgnore]
    public DateTime KnownStamp { get; private set; }

    /// <summary>True when settings.json was written by someone else since this instance read or wrote it.</summary>
    public bool ChangedOnDisk()
    {
        try { return File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != KnownStamp; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static System.Reflection.PropertyInfo[]? _copied;

    /// <summary>Takes every saved value of <paramref name="other"/> (a fresh <see cref="Load"/>) in place, so the engine's references stay valid;
    /// Transient / Offline (process switches, never saved) stay as they are.</summary>
    public void CopyFrom(AppSettings other)
    {
        _copied ??= typeof(AppSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.SetMethod!.IsPublic && p.GetIndexParameters().Length == 0
                        && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), false) is not [JsonIgnoreAttribute { Condition: JsonIgnoreCondition.Always }])
            .Where(p => !p.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"))
            .ToArray();
        foreach (var p in _copied) p.SetValue(this, p.GetValue(other));
        KnownStamp = other.KnownStamp;
    }

    public void ApplyTo(MeterOptions options)
    {
        options.TargetMode = TargetMode;
        options.IdleTimeoutMs = Math.Clamp(IdleTimeoutSec, 3, 120) * 1000;
        options.BossIdleTimeoutMs = Math.Clamp(BossIdleTimeoutSec, 5, 300) * 1000;
    }
}
