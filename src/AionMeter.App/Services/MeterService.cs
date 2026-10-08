using System.IO;
using AionMeter.Core.Atlas;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Demo;
using AionMeter.Core.Game;
using AionMeter.Core.Storage;

namespace AionMeter.App.Services;

/// <summary>Owns the capture → decoder → combat tracker pipeline and fight history for the whole app.</summary>
public sealed class MeterService : IDisposable
{
    private IEventSource? _capture;
    private IEventSource? _replay;
    private DemoFightSource? _demo;

    public MeterService(AppSettings settings, Func<GameData, IEventSource>? captureFactory)
    {
        Settings = settings;
        CaptureFactory = captureFactory;
        Data = GameData.Load(DataDirectory, UiText.Normalize(settings.Language));
        Icons = new SkillIcons(DataDirectory, settings.DownloadIcons);
        Portraits = new BossPortraits(DataDirectory, settings.DownloadIcons);
        Options = new MeterOptions();
        settings.ApplyTo(Options);
        Tracker = new CombatTracker(Data, Options);
        History = new HistoryStore(Path.Combine(AppSettings.AppDataDir, "fights"));
        Timers = new BossTimers(Path.Combine(AppSettings.AppDataDir, "boss-timers.json"), Data, DataDirectory, persistent: !settings.Transient);
        Updates = new Updater(settings);
        Atlas = new NodeStore(Path.Combine(AppSettings.AppDataDir, "atlas"));
        _objectNames = new ObjectNames(Path.Combine(AppSettings.AppDataDir, "atlas", "objects.json"));
        _userNames = _objectNames.Load();
        foreach (var (code, (name, _)) in _userNames) if (name.Length > 0) Data.Objects[code] = name;
        Atlas.SelfGathering += spot =>
        {
            if (Data.Objects.ContainsKey(spot.Code)) return;
            var now = Environment.TickCount64;
            if (_askedCodes.TryGetValue(spot.Code, out var asked) && now - asked < 10 * 60_000) { Log.Info($"Atlas: object {spot.Code} gathered again, asked {(now - asked) / 1000}s ago"); return; }
            _askedCodes[spot.Code] = now;
            Log.Info($"Atlas: asking the name of object {spot.Code} (spot {spot.X:0},{spot.Y:0})");
            NodeNameWanted?.Invoke(spot.Code);
        };
        Tracker.EncounterFinished += OnEncounterFinished;
        // A recording's or the demo's bosses must not move the live respawn timers.
        Tracker.BossNoticed += n =>
        {
            if (!_replaying && !DemoRunning) Timers.OnNotice(n);
        };
        Tracker.FieldBossListed += list =>
        {
            if (_replaying) return;
            Log.FieldBossList(list);
            Timers.OnList(list);
        };
        // Ranges only, no names: enough to see in a user's log whether the roster still reads right.
        Tracker.RosterReceived += roster =>
        {
            if (_replaying) return;
            var gear = roster.Members.Where(m => m.GearScore > 0).Select(m => m.GearScore).DefaultIfEmpty().ToList();
            var power = roster.Members.Where(m => m.CombatPower > 0).Select(m => m.CombatPower).DefaultIfEmpty().ToList();
            Log.Info($"Party roster: {roster.Members.Count} members, GS {gear.Min()}-{gear.Max()}, CP {power.Min()}-{power.Max()}");
        };

        // Names and bosses learned before a restart (same zone, recent) come back immediately: no "#id" players, and a boss
        // already in view keeps its name and real max HP.
        if (!settings.Transient && _names.Load() is { } cached)
        {
            Tracker.ImportCache(cached);
            Atlas.SetMap(cached.MapId);
            if (cached.SelfId is { } selfId) Atlas.SetSelf(selfId);
            Log.Info($"Restored {cached.Players.Count} cached player names, {cached.Npcs.Count} bosses");
        }
        _savedNamesVersion = Tracker.CacheVersion;

        // Boss timers are per server: follow the character's (from the cache now, then on every login).
        Timers.SetCurrentServer(Tracker.SelfServerId);
        Tracker.SelfIdentified += _ =>
        {
            if (!_replaying && !DemoRunning) Timers.SetCurrentServer(Tracker.SelfServerId);
        };
    }

    public static string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    public AppSettings Settings { get; }
    public GameData Data { get; }
    public SkillIcons Icons { get; }
    public BossPortraits Portraits { get; }
    public MeterOptions Options { get; }
    public CombatTracker Tracker { get; }
    public HistoryStore History { get; }
    public BossTimers Timers { get; }
    /// <summary>New versions on GitHub; checks only run once the app calls <see cref="Updater.Start"/>.</summary>
    public Updater Updates { get; }
    /// <summary>Gathering nodes and the player's position, per map (private atlas branch).</summary>
    public NodeStore Atlas { get; }
    public MapCalibration? Calibration(int mapId) => MapCalibration.Load(Path.Combine(DataDirectory, "atlas"), mapId);
    private readonly ObjectNames _objectNames;
    private readonly Dictionary<int, (string Name, string Category)> _userNames;
    private readonly Dictionary<int, long> _askedCodes = new(); // code → when the prompt was last shown (asked again after 10 min)
    /// <summary>The player just started gathering an object code nobody has named yet (capture thread).</summary>
    public event Action<int>? NodeNameWanted;

    public void NameObject(int code, string name, string category)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        _userNames[code] = (name, category.Trim());
        Data.Objects[code] = name;
        _objectNames.Save(_userNames);
        Log.Info($"Object {code} named \"{name}\" ({category})");
    }
    public Func<GameData, IEventSource>? CaptureFactory { get; }

    public CaptureStatus CaptureStatus =>
        _replay is { Status.State: CaptureState.Capturing } r ? r.Status
        : _restarting ? new CaptureStatus(CaptureState.Starting, "Starting capture…")
        : _capture?.Status ?? new CaptureStatus(CaptureState.Stopped, "Capture not available");

    public bool DemoRunning => _demo?.IsRunning == true;

    /// <summary>Raised (on a worker thread) after a fight was written to disk.</summary>
    public event Action<string>? FightSaved;

    /// <summary>
    /// Plays a recorded capture through the meter as fast as it parses. Live events are detached meanwhile (their
    /// timestamps would interleave with the recording's) and replayed fights are not saved to history again.
    /// </summary>
    public void Replay(string path)
    {
        _replay?.Dispose();
        Tracker.Reset();
        _replaying = true;
        if (_capture is not null) _capture.EventDecoded -= Tracker.Process;

        var replay = new PcapReplaySource(Data, path, realtime: false);
        replay.EventDecoded += Tracker.Process;
        replay.StatusChanged += s =>
        {
            if (s.State != CaptureState.Stopped) return;
            Tracker.Tick(Tracker.LastEventMs + 3_600_000); // close the recording's last fight
            _savedNamesVersion = Tracker.CacheVersion; // names from a recording must not overwrite the live cache
            _replaying = false;
            if (_capture is not null) _capture.EventDecoded += Tracker.Process;
        };
        _replay = replay;
        replay.Start();
    }

    private volatile bool _replaying;

    private readonly object _captureGate = new();
    private volatile bool _restarting;

    public void StartCapture()
    {
        lock (_captureGate)
        {
            if (CaptureFactory is null || _capture is not null) return;
            try
            {
                _capture = CaptureFactory(Data);
                _capture.EventDecoded += Tracker.Process;
                _capture.EventDecoded += Atlas.OnEvent;
                _capture.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Capture start failed", ex);
                _capture?.Dispose();
                _capture = new FailedSource(ex.Message);
            }
        }
    }

    public void RestartCapture()
    {
        lock (_captureGate)
        {
            _restarting = true;
            try
            {
                _capture?.Dispose();
                _capture = null;
                StartCapture();
            }
            finally
            {
                _restarting = false;
            }
        }
    }

    /// <summary>
    /// The "restart meter" button: the fight so far is closed and saved, capture starts over (adapter lookup, stream
    /// sync) and the live view stays empty until the next hit. Names and bosses the meter already knows stay known —
    /// unlike closing and starting the app in the middle of a fight.
    /// </summary>
    public void Restart()
    {
        _demo?.Dispose();
        Tracker.Reset();
        if (_replaying) return;
        _restarting = true;
        Task.Run(RestartCapture); // closing the adapter can take a moment: not on the UI thread
    }

    public void StartDemo(int seconds = 45)
    {
        _demo?.Dispose();
        _demo = new DemoFightSource(Tracker.Process);
        _demo.Start(TimeSpan.FromSeconds(seconds));
    }

    public void Tick()
    {
        // The wall clock means nothing to a recording's timestamps: idle checks wait until the replay is done.
        if (!_replaying) Tracker.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // Persist the name map a few seconds after it changes (not on every packet).
        if (Settings.Transient || _replaying || DemoRunning) return;
        var now = Environment.TickCount64;
        if (now - _lastAtlasSave > 30_000) { _lastAtlasSave = now; Atlas.Save(); }
        if (now - _lastNamesCheck < 5_000) return;
        _lastNamesCheck = now;
        SaveNames();
    }

    private readonly NameCache _names = new(Path.Combine(AppSettings.AppDataDir, "names-cache.json"));
    private int _savedNamesVersion;
    private long _lastNamesCheck;
    private long _lastAtlasSave;

    private void SaveNames()
    {
        var version = Tracker.CacheVersion;
        if (version == _savedNamesVersion) return;
        _savedNamesVersion = version;
        _names.Save(Tracker.ExportCache());
    }

    /// <summary>English / Russian for the interface and for skill / NPC names, applied without a restart.</summary>
    public void SetLanguage(string language)
    {
        language = UiText.Normalize(language);
        Settings.Language = language;
        Data.ReloadNames(DataDirectory, language);
        UiText.Use(language);
        Settings.Save();
    }

    public void ApplySettings()
    {
        Settings.ApplyTo(Options);
        Icons.Enabled = Settings.DownloadIcons;
        Portraits.Enabled = Settings.DownloadIcons;
        Settings.Save();
    }

    /// <summary>Boss fights are always kept; trash only when long enough (or everything, per settings).</summary>
    private bool ShouldSave(EncounterSnapshot s)
    {
        if (_replaying || DemoRunning || !Settings.SaveHistory || s.CombatMs < Options.MinSavedFightMs) return false;
        var boss = s.Boss is not null;
        return Settings.HistoryMode switch
        {
            HistoryMode.BossesOnly => boss,
            HistoryMode.BossesAndLong => boss || s.CombatMs >= Settings.HistoryMinSeconds * 1000L,
            _ => true,
        };
    }

    /// <summary>Fight title in the current language (fights saved earlier may have used another one, or none).</summary>
    public string DisplayTitle(string savedTitle, int bossCode, int targetCode)
    {
        var code = bossCode != 0 ? bossCode : targetCode;
        if (code == 0 || !Data.Npcs.ContainsKey(code)) return savedTitle;
        var name = Data.NpcName(code);
        var plus = bossCode == 0 ? savedTitle.LastIndexOf(" +", StringComparison.Ordinal) : -1;
        return plus > 0 ? name + savedTitle[plus..] : name;
    }

    public string DisplayTitle(HistoryEntry e) => DisplayTitle(e.Title, e.BossCode, e.TargetCode);

    /// <summary>Portrait of the fight's boss, or of its main target when there was none (null until downloaded).</summary>
    public System.Windows.Media.ImageSource? PortraitOf(EncounterSnapshot s) => Portraits.Of(s);

    public System.Windows.Media.ImageSource? PortraitOf(HistoryEntry e) => Portraits.Of(e.BossCode, e.TargetCode);

    /// <summary>Fights saved before map names were known carry "Map 1110": resolve those now.</summary>
    public string? DisplayZone(string? zone) =>
        zone is not null && zone.StartsWith("Map ", StringComparison.Ordinal) && int.TryParse(zone.AsSpan(4), out var id)
            ? Data.MapName(id)
            : zone;

    private bool _exiting;

    private void OnEncounterFinished(FightRecord record)
    {
        if (!ShouldSave(record.Summary)) return;
        if (_exiting) Save(); // the process is about to end: a worker thread would not get to it
        else ThreadPool.QueueUserWorkItem(_ => Save());

        void Save()
        {
            try
            {
                var path = History.Save(record);
                FightSaved?.Invoke(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Saving fight failed", ex);
            }
        }
    }

    public void Dispose()
    {
        var live = !Settings.Transient && !_replaying && !DemoRunning;
        lock (_captureGate) _capture?.Dispose(); // no more packets: the running fight is as complete as it gets
        Atlas.Save();
        _replay?.Dispose();
        _demo?.Dispose();
        if (live)
        {
            // Closing the meter mid-fight keeps the fight so far, like the restart button does.
            _exiting = true;
            Tracker.Reset();
            SaveNames();
        }
        Updates.Dispose();
    }

    /// <summary>Placeholder source that only reports why capture could not start.</summary>
    private sealed class FailedSource(string message) : IEventSource
    {
        public event Action<Core.Events.GameEvent>? EventDecoded { add { } remove { } }
        public event Action<CaptureStatus>? StatusChanged { add { } remove { } }
        public CaptureStatus Status { get; } = new(CaptureState.Error, message);
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
