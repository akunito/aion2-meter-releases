using System.IO;
using AionMeter.Core.Atlas;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Demo;
using AionMeter.Core.Game;
using AionMeter.Core.Armory;
using AionMeter.Core.Character;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Core.Leaderboard;
using AionMeter.Core.Market;
using AionMeter.Core.Ranking;
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
        Log.Info($"Atlas: {Atlas.LoadSeeds(Path.Combine(DataDirectory, "atlas"))} seeded nodes from the map data");
        Names = ItemNames.Load(Path.Combine(DataDirectory, "gamedb", "item-names.json"));
        Log.Info($"Item names: {Names.Count} (es: {Names.CountWith("es")})");
        Top = new TopDpsStore(settings.Transient ? null : Path.Combine(AppSettings.AppDataDir, "top-dps.json"));
        // First run with the ranking: fill it from the fights already on disk, off the UI thread.
        if (!settings.Transient && Top.Count == 0 && History.List(1).Count > 0)
            ThreadPool.QueueUserWorkItem(_ => { try { Top.Rebuild(History, Data); Top.Save(); } catch (Exception ex) { Log.Error("Top DPS rebuild failed", ex); } });
        _objectNames = new ObjectNames(Path.Combine(AppSettings.AppDataDir, "atlas", "objects.json"));
        _userNames = _objectNames.Load();
        foreach (var (code, (name, _)) in _userNames) if (name.Length > 0) Data.Objects[code] = name;
        Atlas.SelfGathering += spot =>
        {
            if (Data.Objects.ContainsKey(spot.Code) || !spot.IsNode) return;
            var now = Environment.TickCount64;
            // The three units of one node arrive as three gather starts within ~20 s: one prompt per node, and if it closed
            // unanswered the next node of that kind asks again (Diego: an unseen 15 s prompt must not silence the kind).
            if (_askedCodes.TryGetValue(spot.Code, out var asked) && now - asked < 20_000) { Log.Info($"Atlas: object {spot.Code} gathered again, asked {(now - asked) / 1000}s ago"); return; }
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
            if (!_replaying && !DemoRunning) { Timers.SetCurrentServer(Tracker.SelfServerId); BindCharacterStores(); }
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
    /// <summary>Ten best DPS per class with their rotations (private branch).</summary>
    public TopDpsStore Top { get; }
    /// <summary>Official character pages (GET only, cached 12 h); used on demand from the Top DPS window.</summary>
    public ArmoryClient Armory { get; } = new(Path.Combine(AppSettings.AppDataDir, "armory"));
    /// <summary>aion2index.com combat-power ranking pages (GET only, one page per 400 ms, one snapshot per day).</summary>
    public Aion2IndexClient Leaderboard { get; } = new(Path.Combine(AppSettings.AppDataDir, "armory", "leaderboard"));
    /// <summary>Bag, second bag, equipped set and warehouse from the login list and the refreshes (private branch).</summary>
    public InventoryStore Inventory { get; } = new(Path.Combine(AppSettings.AppDataDir, "inventory.json"));
    /// <summary>Pets, titles and achievements from the login lists (private branch); saved to state\character.json for the AI side.</summary>
    public CharacterState Character { get; } = new(Path.Combine(AppSettings.AppDataDir, "state", "character.json"));
    /// <summary>Every broker page the player browses, as a growing price history (private branch).</summary>
    public BrokerStore Broker { get; } = new(Path.Combine(AppSettings.AppDataDir, "state", "market.json"));
    /// <summary>Items to buy below a price: aion2.exchange read once an hour per item (private branch).</summary>
    public PriceWatchStore Prices { get; } = new(Path.Combine(AppSettings.AppDataDir, "state", "price-watch.json"));
    private readonly System.Collections.Concurrent.ConcurrentQueue<PriceWatch> _priceHits = new();
    private int _pricePolling;

    /// <summary>Watches that were at or under their maximum on the last poll, each once per poll.</summary>
    public List<PriceWatch> TakePriceHits() { var l = new List<PriceWatch>(); while (_priceHits.TryDequeue(out var h)) l.Add(h); return l; }

    public void PollPricesNow() => _ = PollPricesAsync(Environment.TickCount64);

    private async Task PollPricesAsync(long now)
    {
        if (Interlocked.Exchange(ref _pricePolling, 1) == 1) return;
        try { foreach (var h in await Prices.PollAsync(now).ConfigureAwait(false)) _priceHits.Enqueue(h); }
        catch (Exception e) { Log.Error("Price watch", e); }
        finally { _pricePolling = 0; }
    }

    /// <summary>Offline item names (en/es) for the search boxes; data/gamedb/item-names.json, built from aion2hub + the official es-ES endpoint.</summary>
    public ItemNames Names { get; private set; } = ItemNames.Empty;
    /// <summary>Item names/grades/icons by id (aion2hub.me, cached for good).</summary>
    public ItemCatalog Items { get; } = new(Path.Combine(AppSettings.AppDataDir, "gamedb", "items.json"));
    public MapCalibration? Calibration(int mapId) => MapCalibration.Load(Path.Combine(DataDirectory, "atlas"), mapId);
    /// <summary>Recurring server events (Shugo, Rift, siege, reset) from data/atlas/events.json.</summary>
    public IReadOnlyList<ScheduledEvent> Events { get; } = ScheduledEvent.Load(Path.Combine(DataDirectory, "atlas", "events.json"));
    private readonly ObjectNames _objectNames;
    private readonly Dictionary<int, (string Name, string Category)> _userNames;
    private readonly Dictionary<int, long> _askedCodes = new(); // code → when the prompt was last shown (asked again after 20 s)
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
                _capture.EventDecoded += AtlasOnEvent;
                _capture.EventDecoded += InventoryOnEvent;
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
        if (_capture is Core.Capture.LiveCapture live)
        {
            if (live.Faults != _loggedFaults)
            {
                _loggedFaults = live.Faults;
                Log.Error($"Capture: {live.Faults} packets lost to handler faults", live.LastFault);
            }
            foreach (var (start, ms) in live.TakeSilences())
                Log.Info($"Lag probe: server silent {ms} ms from {DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime():HH:mm:ss.fff}");
        }

        // Persist the name map a few seconds after it changes (not on every packet).
        if (Settings.Transient || _replaying || DemoRunning) return;
        var now = Environment.TickCount64;
        if (Tracker.SelfServerId > 0) Prices.ExchangeServerId = 10000 + Tracker.SelfServerId;
        if (Prices.Due(now)) _ = PollPricesAsync(now);
        if (now - _lastAtlasSave > 30_000) { _lastAtlasSave = now; Atlas.Save(); }
        // The player state on disk is what the AI side reads: a few seconds behind the packets at most, plus a summary with the ages.
        if (now - _lastStateSave > 5_000) { _lastStateSave = now; Inventory.Save(); Character.Save(); Broker.Save(); WriteSummary(); }
        if (now - _lastNamesCheck < 5_000) return;
        _lastNamesCheck = now;
        SaveNames();
    }

    private readonly NameCache _names = new(Path.Combine(AppSettings.AppDataDir, "names-cache.json"));
    private int _savedNamesVersion;
    private long _lastNamesCheck;
    private long _lastAtlasSave;
    private long _lastStateSave;
    private string _lastSummary = "";

    private string _characterKey = "";

    /// <summary>One folder per character (characters/&lt;server&gt;-&lt;name&gt;/) so every alt keeps its own live inventory and lists; the legacy inventory.json and state/character.json mirror the character that is logged in.</summary>
    private void BindCharacterStores()
    {
        var name = Tracker.SelfName; var server = Tracker.SelfServerId;
        if (Settings.Transient || string.IsNullOrEmpty(name) || server <= 0) return;
        var key = $"{server}-{string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))}";
        if (key == _characterKey) return;
        _characterKey = key;
        var dir = Path.Combine(AppSettings.AppDataDir, "characters", key);
        Inventory.Bind(Path.Combine(dir, "inventory.json"), Path.Combine(AppSettings.AppDataDir, "inventory.json"));
        Character.Bind(Path.Combine(dir, "character.json"), Path.Combine(AppSettings.AppDataDir, "state", "character.json"));
        Log.Info($"Character stores bound to {key}");
        _lastSummary = "";
    }

    /// <summary>Every character folder with its Kinah, stack count and inventory age, for the AI side.</summary>
    private List<object> CharacterFolders()
    {
        var list = new List<object>();
        var root = Path.Combine(AppSettings.AppDataDir, "characters");
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var inv = Path.Combine(dir, "inventory.json");
            long kinah = 0; var stacks = 0;
            try
            {
                if (File.Exists(inv))
                    foreach (var st in System.Text.Json.JsonSerializer.Deserialize<List<ItemStack>>(File.ReadAllText(inv)) ?? new()) { stacks++; if (st.Item == InventoryStore.Kinah) kinah = st.Count; }
            }
            catch (Exception) { }
            list.Add(new { key = Path.GetFileName(dir), kinah, stacks, inventoryAt = File.Exists(inv) ? new DateTimeOffset(File.GetLastWriteTime(inv)).ToString("O") : null, current = Path.GetFileName(dir) == _characterKey });
        }
        return list;
    }

    /// <summary>The wallet is a family of bag stacks (item 9301xxxxx: Kina 930100031, Enhance Stone 930100023, Abyss Points 930100003…), so it is always as fresh as the inventory.</summary>
    private List<object> Currencies()
    {
        var list = new List<object>();
        foreach (var st in Inventory.All())
            if (st.Item >= InventoryStore.CurrencyMin && st.Item < InventoryStore.CurrencyMax)
                list.Add(new { id = st.Item, name = Names.Name(st.Item) ?? Items.NameOf(st.Item), count = st.Count });
        return list;
    }

    /// <summary>state/summary.json: when each store last changed, where the player is, what is loaded — rewritten only when something moved.</summary>
    private void WriteSummary()
    {
        var invPath = Path.Combine(AppSettings.AppDataDir, "inventory.json");
        var invAge = Inventory.UpdatedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Inventory.UpdatedMs).ToLocalTime().ToString("O")
                   : File.Exists(invPath) ? new DateTimeOffset(File.GetLastWriteTime(invPath)).ToString("O") : null;
        var chrAge = Character.UpdatedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Character.UpdatedMs).ToLocalTime().ToString("O") : null;
        var key = $"{_characterKey}|{Inventory.UpdatedMs}|{Inventory.CountOf(InventoryStore.Kinah)}|{Character.UpdatedMs}|{Broker.Count}|{Prices.LastPollAt}|{Atlas.MapId}|{(int)Atlas.SelfX}|{(int)Atlas.SelfY}|{Tracker.SelfServerId}|{Inventory.All().Count}";
        if (key == _lastSummary) return;
        _lastSummary = key;
        var o = new
        {
            savedAt = DateTimeOffset.Now.ToString("O"),
            serverId = Tracker.SelfServerId,
            mapId = Atlas.MapId, x = Atlas.SelfX, y = Atlas.SelfY,
            characterKey = _characterKey.Length > 0 ? _characterKey : null,
            characterName = Tracker.SelfName,
            kinah = Inventory.CountOf(InventoryStore.Kinah),
            characters = CharacterFolders(),
            currencies = Currencies(),
            inventory = new { updatedAt = invAge, stacks = Inventory.All().Count, containers = Inventory.ContainerIds },
            character = new { updatedAt = chrAge, pets = Character.Pets.Count, titles = Character.Titles.Count, achievements = Character.Achievements.Count },
            broker = new { listings = Broker.Count },
            prices = new { lastPollAt = Prices.LastPollAt?.ToString("O"), watches = Prices.Count },
            files = new[] { "characters/<key>/inventory.json", "characters/<key>/character.json", "inventory.json (mirror of the current character)", "state/character.json (mirror)", "state/market.json", "state/price-watch.json", "state/price-watch.json.history.jsonl", "names-cache.json" },
        };
        try
        {
            var path = Path.Combine(AppSettings.AppDataDir, "state", "summary.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

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

    private int _atlasFaults;
    private int _inventoryFaults;

    private void InventoryOnEvent(Core.Events.GameEvent e)
    {
        if (e is Core.Events.PetListEvent or Core.Events.TitleListEvent or Core.Events.AchievementListEvent) { try { Character.OnEvent(e); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error("Character state failed", ex); } return; }
        if (e is Core.Events.BrokerPageEvent) { try { Broker.OnEvent(e); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error("Broker store failed", ex); } return; }
        if (e is not Core.Events.InventoryEvent) return;
        try { Inventory.OnEvent(e); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (++_inventoryFaults <= 5) Log.Error("Inventory handler failed", ex);
        }
    }
    private int _loggedFaults;

    /// <summary>
    /// The capture thread swallows handler exceptions per packet (LiveCapture.OnPacketArrival), which would silently drop every
    /// combat record of that packet: the atlas, a side feature, must never cost the meter a hit. Logged, first 5 with the stack.
    /// </summary>
    private void AtlasOnEvent(Core.Events.GameEvent e)
    {
        try { Atlas.OnEvent(e); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (++_atlasFaults <= 5) Log.Error($"Atlas handler failed on {e.GetType().Name}", ex);
            else if (_atlasFaults % 1000 == 0) Log.Info($"Atlas handler failed {_atlasFaults} times");
        }
    }

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
                if (Top.Consider(record, Path.GetFileName(path), Data) > 0) Top.Save();
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
