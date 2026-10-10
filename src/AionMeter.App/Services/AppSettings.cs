using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Combat;

namespace AionMeter.App.Services;

public enum HistoryMode
{
    BossesOnly,
    BossesAndLong,
    All,
}

public sealed class AppSettings
{
    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AionMeter");

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

    // Boss respawn timers
    /// <summary>Tray notification this many minutes before a tracked boss respawns; 0 = off.</summary>
    public int BossAlertMinutes { get; set; } = 2;

    /// <summary>Per recurring event (key from data/atlas/events.json): Windows notification and/or sound, 10 and/or 5 minutes before the start.</summary>
    public sealed class EventAlert
    {
        public bool Notify { get; set; } = true;
        public bool Sound { get; set; } = true;
        public bool At10 { get; set; } = true;
        public bool At5 { get; set; } = true;
    }

    public Dictionary<string, EventAlert> EventAlerts { get; set; } = new();
    /// <summary>A start the player confirmed in game per periodic event (key → time): re-phases the cycle until "back to the schedule".</summary>
    public Dictionary<string, DateTimeOffset> EventAnchors { get; set; } = new();

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

    /// <summary>Interface and skill / NPC name language: "en" (default) or "ru".</summary>
    public string Language { get; set; } = "en";

    /// <summary>Fetch skill icons from the official AION 2 game-data CDN (cached locally).</summary>
    public bool DownloadIcons { get; set; } = true;

    /// <summary>
    /// Base url of a FlareSolverr / solvearr proxy (e.g. "http://127.0.0.1:8191"), reached over the private network,
    /// that the price and leaderboard scrapers use to get past a Cloudflare challenge — the same bypass the NAS media
    /// stack uses. Empty = direct requests only (default). Never point at a publicly reachable solver: its /v1 fetches
    /// any url for the caller.
    /// </summary>
    public string CloudflareSolverUrl { get; set; } = "";

    // Updates
    /// <summary>Ask GitHub for a newer version shortly after start and every few hours.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Installed copies download a new version by themselves and install it at a quiet moment.</summary>
    public bool AutoInstallUpdates { get; set; } = false; // akunito build: see Updater.DownloadsDisabled
    /// <summary>A version the user chose to skip ("0.2.0"): not announced again, a newer one is.</summary>
    public string? SkippedUpdate { get; set; }

    // Capture / storage
    public bool SaveHistory { get; set; } = true;
    public HistoryMode HistoryMode { get; set; } = HistoryMode.BossesAndLong;
    /// <summary>For <see cref="HistoryMode.BossesAndLong"/>: non-boss fights shorter than this are not saved.</summary>
    public int HistoryMinSeconds { get; set; } = 20;
    public bool RecordPackets { get; set; }
    public string? CaptureDevice { get; set; }

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
    /// <summary>Language of the item-name search in the Prices window ("en" / "es"), independent of the interface language.</summary>
    public string PriceSearchLanguage { get; set; } = "en";
    /// <summary>Card size per overlay tab ("atlas", "top", "prices"): [width, height] in DIP; the meter tab uses OverlayWidth/Height.</summary>
    public Dictionary<string, double[]> OverlayTabSizes { get; set; } = new();
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
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
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
                s.LayoutVersion = CurrentLayout;
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return new AppSettings { LayoutVersion = CurrentLayout };
    }

    /// <summary>Throw-away settings (render tool): never written to disk.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Transient { get; init; }

    public void Save()
    {
        if (Transient) return;
        Directory.CreateDirectory(AppDataDir);
        // Written aside, then swapped in: Windows shutting the app down mid-write must not leave an empty settings.json.
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void ApplyTo(MeterOptions options)
    {
        options.TargetMode = TargetMode;
        options.IdleTimeoutMs = Math.Clamp(IdleTimeoutSec, 3, 120) * 1000;
        options.BossIdleTimeoutMs = Math.Clamp(BossIdleTimeoutSec, 5, 300) * 1000;
    }
}
