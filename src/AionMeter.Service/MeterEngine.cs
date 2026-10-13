using System.IO;
using AionMeter.Core.Atlas;
using AionMeter.Core;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Demo;
using AionMeter.Core.Game;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Core.Character;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Core.Leaderboard;
using AionMeter.Core.Market;
using AionMeter.Core.Ranking;
using AionMeter.Core.Storage;

namespace AionMeter.Service;

/// <summary>
/// Owns the capture → decoder → combat tracker pipeline, fight history and every state file, without WPF (A2M-48): the Windows App
/// subclasses it (MeterService: icons, portraits, updater) and the Linux daemon runs it as is.
/// </summary>
public class MeterEngine : IDisposable
{
    private IEventSource? _capture;
    private IEventSource? _replay;
    private DemoFightSource? _demo;

    /// <param name="availability">Whether Npcap is on this PC (A2M-27); null = probe this PC. Nothing in construction depends on it:
    /// without Npcap every non-capture feature works and the capture source reports <see cref="CaptureAvailability.NoCaptureStatus"/>.</param>
    /// <param name="viewer">A2M-48 Linux UI: the same stores over the daemon's data dir, read-only — no capture (pass no factory), no
    /// network, no state writes; only what the UI itself saves (settings, builds, price-watch list, notes) touches the disk. See
    /// <see cref="OpenViewer"/>.</param>
    public MeterEngine(AppSettings settings, Func<GameData, IEventSource>? captureFactory, CaptureAvailability? availability = null, bool viewer = false)
    {
        Viewer = viewer;
        Settings = settings;
        CaptureFactory = captureFactory;
        Availability = availability ?? CaptureAvailability.OfThisPc();
        Data = GameData.Load(DataDirectory, UiText.Normalize(settings.Language));
        Options = new MeterOptions();
        settings.ApplyTo(Options);
        Tracker = new CombatTracker(Data, Options);
        History = new HistoryStore(Path.Combine(AppSettings.AppDataDir, "fights"));
        Timers = new BossTimers(Path.Combine(AppSettings.AppDataDir, "boss-timers.json"), Data, DataDirectory, persistent: !settings.Transient, readOnly: viewer);
        // render tool (Transient): an empty folder of its own, so the user's remembered nodes and last map stay out of the sample
        Atlas = new NodeStore(settings.Transient ? Path.Combine(Path.GetTempPath(), "aion2-render-atlas-" + Environment.ProcessId) : Path.Combine(AppSettings.AppDataDir, "atlas"));
        Log.Info($"Atlas: {Atlas.LoadSeeds(Path.Combine(DataDirectory, "atlas"))} seeded nodes from the map data");
        Names = ItemNames.Load(Path.Combine(DataDirectory, "gamedb", "item-names.json"));
        Recipes = RecipeBook.Load(Path.Combine(DataDirectory, "gamedb", "recipes.json"));
        Market = Core.Market.MarketSnapshot.Load(MarketPath);
        Pets = PetBook.Load(Path.Combine(DataDirectory, "gamedb", "pets.json"));
        Titles = TitleBook.Load(Path.Combine(DataDirectory, "gamedb", "titles.json"));
        Names.LoadSources(Path.Combine(DataDirectory, "gamedb", "item-sources.json"));
        Grades = ItemGrades.Load(Path.Combine(DataDirectory, "gamedb", "item-grades.json"));
        Names.AttachGrades(Grades);   // grade tags next to ambiguous names + "legendary stone" searches
        NameResolver = new ItemNameResolver(Names, settings.Transient ? null : Path.Combine(AppSettings.AppDataDir, "item-names-extra.json")) { Enabled = !settings.Transient && !settings.Offline && !viewer };
        Account = new AccountWarehouse(settings.Transient ? null : Path.Combine(AppSettings.AppDataDir, "account-warehouse.json"));
        if (!settings.Transient && !viewer) SeedAccountWarehouse();
        Log.Info($"Item names: {Names.Count} (es: {Names.CountWith("es")})");
        Top = new TopDpsStore(settings.Transient ? null : Path.Combine(AppSettings.AppDataDir, "top-dps.json"));
        // First run with the ranking: fill it from the fights already on disk, off the UI thread.
        if (!settings.Transient && !viewer && Top.Count == 0 && History.List(1).Count > 0)
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
        Inventory.Changed += OnLiveInventoryChanged;
        Tracker.EncounterFinished += OnEncounterFinished;
        if (!settings.Transient && !viewer) Prices.Changed += () => Telemetry.QueueState(this);   // hourly polls raise it too: unchanged state is not sent
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
            var line = $"Party roster: {roster.Members.Count} members, GS {gear.Min()}-{gear.Max()}, CP {power.Min()}-{power.Max()}";
            if (line == _lastRosterLine) return; // the server repeats it every few seconds
            _lastRosterLine = line;
            Log.Info(line);
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
        Events = _euEvents;
        Server = ServerContext.Resolve(ServerContext.DefaultRegion, 0, 0, 0, Data.Servers);
        RefreshServer();
        Tracker.SelfIdentified += _ =>
        {
            if (!_replaying && !DemoRunning) { Timers.SetCurrentServer(Tracker.SelfServerId); BindCharacterStores(); RefreshServer(); }
        };
    }

    /// <summary>The Linux UI's read-only engine (A2M-48): no capture, network or state writes.</summary>
    public bool Viewer { get; }

    /// <summary>
    /// A2M-48: the Linux UI's engine over the daemon's data dir (<see cref="AppSettings.AppDataDir"/>): settings.json as it is (loaded, not
    /// re-saved), no capture factory, viewer mode. Slow (game data, catalogues: ~1 s): build it off the UI thread.
    /// </summary>
    public static MeterEngine OpenViewer(AppSettings? settings = null)
    {
        settings ??= AppSettings.Load();
        UiText.Use(settings.Language);
        return new MeterEngine(settings, null, CaptureAvailability.OfThisPc(), viewer: true);
    }

    /// <summary>
    /// A2M-48 daemon: settings.json was saved by the Linux UI (mtime differs from what this process read or wrote): take its values in place
    /// and apply them as a Settings save would (meter options, language, icons, server), without writing the file back. <paramref name="force"/>
    /// re-reads even when the mtime is unchanged (the UI's `reload-settings`). True when something was reloaded.
    /// </summary>
    public bool ReloadSettingsIfChanged(bool force = false)
    {
        if (Settings.Transient || (!force && !Settings.ChangedOnDisk())) return false;
        var fresh = AppSettings.Load();
        var language = Settings.Language;
        var capture = (Settings.CaptureDevice, Settings.RecordPackets);
        Settings.CopyFrom(fresh);
        // the factory reads CaptureDevice / RecordPackets when it opens the adapter: a new choice needs a reopen (the WPF Save does the same)
        if (capture != (Settings.CaptureDevice, Settings.RecordPackets) && CaptureFactory is not null && !Viewer) Task.Run(RestartCapture);
        if (UiText.Normalize(Settings.Language) != UiText.Normalize(language))
        {
            Data.ReloadNames(DataDirectory, Settings.Language);
            UiText.Use(Settings.Language);
        }
        Core.Net.CloudflareHttp.SolverUrl = Settings.CloudflareSolverUrl;
        ApplySettingsInPlace();
        Log.Info($"Settings reloaded from settings.json (language {Settings.Language}, measure {Settings.TargetMode}, server {Settings.ServerId})");
        return true;
    }

    /// <summary>The shipped game data (data/ next to the binary); settable before the first engine for tests and the daemon's --game-data.</summary>
    public static string DataDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "data");

    public AppSettings Settings { get; }
    public GameData Data { get; }
    public MeterOptions Options { get; }
    public CombatTracker Tracker { get; }
    public HistoryStore History { get; }
    public BossTimers Timers { get; }

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
    private AchievementCatalog? _achievementBook;
    /// <summary>Achievement catalogue (data/gamedb/achievements.json, questlog; A2M-15), read on first use.</summary>
    public AchievementCatalog AchievementBook => _achievementBook ??= AchievementCatalog.Load(Path.Combine(DataDirectory, "gamedb", "achievements.json"));
    /// <summary>Achievements or content tickets of the bound character changed (raised from the ≤5 s state save, so coalesced).</summary>
    public event Action? ProgressChanged;
    /// <summary>Skill catalogue per class for the Build tab (data/gamedb/skills, A2M-21).</summary>
    public SkillBook Skills { get; } = new(Path.Combine(DataDirectory, "gamedb", "skills"));
    /// <summary>Daevanion boards per class for the Build tab's planner (data/gamedb/daevanion, A2M-21 phase 4).</summary>
    public DaevanionBook Daevanion { get; } = new(Path.Combine(DataDirectory, "gamedb", "daevanion"));
    /// <summary>The bound character's builds (characters/&lt;key&gt;/builds); a shared folder until a character is known.</summary>
    public BuildStore Builds { get; private set; } = new(Path.Combine(AppSettings.AppDataDir, "builds"));
    /// <summary>Black Cloud shop offers (91 56; was "broker"/state/market.json until A2M-35 showed it is not the player market).</summary>
    public ShopStore Shop { get; } = new(Path.Combine(AppSettings.AppDataDir, "state", "shop.json"));
    /// <summary>Items to buy below a price: aion2.exchange read once an hour per item (private branch).</summary>
    public PriceWatchStore Prices { get; } = new(Path.Combine(AppSettings.AppDataDir, "state", "price-watch.json"));
    private readonly System.Collections.Concurrent.ConcurrentQueue<PriceWatch> _priceHits = new();
    private int _pricePolling;

    /// <summary>Watches that were at or under their maximum on the last poll, each once per poll.</summary>
    public List<PriceWatch> TakePriceHits() { var l = new List<PriceWatch>(); while (_priceHits.TryDequeue(out var h)) l.Add(h); return l; }

    /// <summary>Manual "Check now": one read per 5 minutes (Diego, 2026-10-10: the button must not be spammable; the site samples hourly anyway). Returns false while cooling down.</summary>
    public DateTimeOffset PricesManualUntil { get; private set; } = DateTimeOffset.MinValue;
    /// <summary>A newly added watch is read at once regardless of the manual cooldown.</summary>
    public void PollPricesAfterAdd() { if (!Settings.Offline) _ = PollPricesAsync(Environment.TickCount64); }
    public bool PollPricesNow()
    {
        var now = DateTimeOffset.Now;
        if (now < PricesManualUntil) return false;
        PricesManualUntil = now.AddMinutes(5);
        _ = PollPricesAsync(Environment.TickCount64);
        return true;
    }

    private async Task PollPricesAsync(long now)
    {
        if (Settings.Offline || Viewer || Interlocked.Exchange(ref _pricePolling, 1) == 1) return;
        try
        {
            foreach (var h in await Prices.PollAsync(now).ConfigureAwait(false)) _priceHits.Enqueue(h);
            var st = Prices.LastPollStats;
            if (st.Items > 0) Log.Info($"Price poll: {st.Items} items, {st.Items - st.Solver - st.Errors} direct, {st.Solver} via solver, {st.Errors} failed, {st.Ms} ms");
        }
        catch (Exception e) { Log.Error("Price watch", e); }
        finally { _pricePolling = 0; }
    }

    /// <summary>Offline item names (en/es) for the search boxes; data/gamedb/item-names.json, built from aion2hub + the official es-ES endpoint.</summary>
    public ItemNames Names { get; private set; } = ItemNames.Empty;
    /// <summary>questlog lookups for ids <see cref="Names"/> lacks (%AppData%\AionMeter\item-names-extra.json); Names.Changed fires on arrival.</summary>
    public ItemNameResolver NameResolver { get; private set; } = null!;

    /// <summary>Name for an id the catalogue lacks: queues the questlog lookup, shows "Item n" (or the aion2hub cache) meanwhile.</summary>
    public string UnnamedItem(int id) { NameResolver.Want(id); return Items.NameOf(id); }
    /// <summary>aion2hub recipes (data/gamedb/recipes.json) for the Craft tab and characters/&lt;key&gt;/craftable.json.</summary>
    public RecipeBook Recipes { get; private set; } = RecipeBook.Empty;
    /// <summary>Pets and the monsters that drop their souls (data/gamedb/pets.json) for the atlas "missing pets" layer (A2M-11).</summary>
    public PetBook Pets { get; private set; } = PetBook.Empty;
    /// <summary>The last market export Diego loaded (Craft → Load market file…), kept as market\latest.json; the craft profit calculator reads it.</summary>
    public Core.Market.MarketSnapshot Market { get; private set; } = Core.Market.MarketSnapshot.Empty;
    public static string MarketPath => Path.Combine(AppSettings.AppDataDir, "market", "latest.json");
    /// <summary>Grade and bound flag per item (data/gamedb/item-grades.json, tools/item-grades.py) for the Items tab (A2M-29).</summary>
    public ItemGrades Grades { get; private set; } = ItemGrades.Empty;
    /// <summary>The shared warehouse (container 2) per server, merged by slot from every character's sightings (A2M-32).</summary>
    public AccountWarehouse Account { get; private set; } = new(null);
    /// <summary>The other characters' inventory.json, re-read only when a file changes (Items tab).</summary>
    public InventoryFiles OtherInventories { get; } = new();

    /// <summary>First run with the shared warehouse: build it from the character files already on disk, per server.</summary>
    private void SeedAccountWarehouse()
    {
        try
        {
            var byServer = new Dictionary<string, List<(string, InventoryFile)>>();
            foreach (var key in CharacterKeys())
            {
                var dash = key.IndexOf('-');
                if (dash <= 0 || Account.HasServer(key[..dash])) continue;
                if (InventoryStore.ReadFile(Path.Combine(AppSettings.AppDataDir, "characters", key, "inventory.json")) is not { } f) continue;
                if (!byServer.TryGetValue(key[..dash], out var l)) byServer[key[..dash]] = l = new();
                l.Add((key, f));
            }
            foreach (var (server, list) in byServer) Account.Seed(server, list);
            if (byServer.Count > 0) { Account.Save(); Log.Info($"Account warehouse seeded for {byServer.Count} server(s) from the character files"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Account warehouse seed", ex); }
    }

    /// <summary>The bound character's container 2 into the shared warehouse (every 5 s with the state save; nothing while a login list waits for its character).</summary>
    private void AbsorbAccountWarehouse()
    {
        var dash = _boundKey.IndexOf('-');
        if (dash <= 0 || Inventory.Pending) return;
        var seen = Inventory.Seen(Containers.AccountWarehouse);
        if (seen.SeenMs == 0) return;
        if (Account.Absorb(_boundKey[..dash], _boundKey, seen.FullMs, Inventory.Items(Containers.AccountWarehouse), Inventory.SlotStamps(Containers.AccountWarehouse))) Account.Save();
    }

    public event Action? MarketChanged;
    /// <summary>Copies the export next to the state and reloads it; false when the file does not parse as an export.</summary>
    public bool ImportMarket(string path)
    {
        var snap = Core.Market.MarketSnapshot.Load(path);
        if (snap.Count == 0) return false;
        try { Directory.CreateDirectory(Path.GetDirectoryName(MarketPath)!); File.Copy(path, MarketPath, overwrite: true); }
        catch (IOException ex) { Log.Error("Market import", ex); return false; }
        Market = snap;
        Log.Info($"Market export loaded: {snap.Count} items, {snap.Server}, captured {snap.CapturedAt:yyyy-MM-dd HH:mm}");
        MarketChanged?.Invoke();
        return true;
    }
    /// <summary>Titles (data/gamedb/titles.json): names and bonuses for the login title list → characters/&lt;key&gt;/titles.json (A2M-15).</summary>
    public TitleBook Titles { get; private set; } = TitleBook.Empty;
    /// <summary>Item names/grades/icons by id (aion2hub.me, cached for good).</summary>
    public ItemCatalog Items { get; } = new(Path.Combine(AppSettings.AppDataDir, "gamedb", "items.json"));
    public MapCalibration? Calibration(int mapId) => MapCalibration.Load(Path.Combine(DataDirectory, "atlas"), mapId);
    /// <summary>Recurring server events (Shugo, Rift, siege, daily and weekly reset) from data/atlas/events.json; the resets also feed the Items Today card.</summary>
    /// <summary>On the effective server's clock (A2M-40, <see cref="ScheduledEvent.ForZone"/>); re-read every tick by the pills and alerts.</summary>
    public IReadOnlyList<ScheduledEvent> Events { get; private set; }
    private readonly IReadOnlyList<ScheduledEvent> _euEvents = GameReset.Use(ScheduledEvent.Load(Path.Combine(DataDirectory, "atlas", "events.json")));
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
    /// <summary>A2M-48 daemon: the closed fight with its saved file (null when not kept), on the thread that closed it.</summary>
    public event Action<FightRecord, string?>? FightClosed;
    /// <summary>The 15-minute telemetry/backup tick the App runs from a DispatcherTimer; the daemon calls it from its loop.</summary>
    public Task SendTelemetryIfDueAsync() => Telemetry.SendIfDueAsync(this);
    public Func<GameData, IEventSource>? CaptureFactory { get; }
    public CaptureAvailability Availability { get; }

    /// <summary>The capture-health line (Settings, boss log): adapter counters, "no adapter open" or "Npcap not installed".</summary>
    public string CaptureHealthText() => Availability.HealthText(CaptureHealth());

    /// <summary>Adapter counters of the live capture, null without an open adapter (replay, demo, no Npcap).</summary>
    public CaptureHealth? CaptureHealth() { lock (_captureGate) return (_capture as LiveCapture)?.Health(); }

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

        var replay = new PcapReplaySource(Data, path, realtime: false, () => Availability.Present);
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
        if (Viewer) return;   // nothing to process, and every periodic write below belongs to the daemon
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

        _viewStore?.ReloadIfChanged(Environment.TickCount64);
        // Persist the name map a few seconds after it changes (not on every packet).
        if (Settings.Transient || _replaying || DemoRunning) return;
        var now = Environment.TickCount64;
        if (Tracker.SelfServerId != _resolvedLogin) RefreshServer();   // identity restored from the names cache raises no SelfIdentified either
        if (_boundKey.Length == 0) BindCharacterStores();   // identity restored from the names cache at start-up raises no SelfIdentified
        if (_boundKey.Length > 0 && now - _lastProfileSync > 6 * 3_600_000L && _lastProfileSync > 0) { _lastProfileSync = now; _ = SyncProfileAsync(_boundKey, Tracker.SelfName ?? "", Tracker.SelfServerId); }
        if (Prices.Due(now)) _ = PollPricesAsync(now);
        if (now - _lastAtlasSave > 30_000) { _lastAtlasSave = now; Atlas.Save(); }
        // The player state on disk is what the AI side reads: a few seconds behind the packets at most, plus a summary with the ages.
        if (now - _lastStateSave > 5_000) { _lastStateSave = now; SaveState(); }
        if (now - _lastNamesCheck < 5_000) return;
        _lastNamesCheck = now;
        SaveNames();
    }

    private void SaveState() { Inventory.Save(); AbsorbAccountWarehouse(); Character.Save(); Shop.Save(); WriteSummary(); WriteAchievements(); }

    /// <summary>Daemon stop / end of a replay: the state files and the names cache now, not at the next 5 s tick (A2M-48).</summary>
    public void FlushState()
    {
        if (Settings.Transient || Viewer || _replaying || DemoRunning) return;
        if (_boundKey.Length == 0) BindCharacterStores();
        SaveState();
        Atlas.Save();
        SaveNames();
    }

    private readonly NameCache _names = new(Path.Combine(AppSettings.AppDataDir, "names-cache.json"));

    private int _savedNamesVersion;
    private long _lastNamesCheck;
    private long _lastAtlasSave;
    private long _lastStateSave;
    private string _lastSummary = "";

    // A2M-28 view-while-playing: _boundKey = the character the live packets belong to (Inventory, Character, telemetry state, the
    // account-warehouse feed); _sel.Viewed = the one the tabs show (ViewInventory, Builds, CurrentProfile). Both under _selGate:
    // BindCharacterStores runs on the capture thread (SelfIdentified), SelectCharacter on the UI thread.
    private readonly object _selGate = new();
    private readonly CharacterSelection _sel = new();
    private string _boundKey = "";
    private volatile InventoryStore? _viewStore;   // read-only file store while viewing someone other than the bound character
    /// <summary>The character the live packets belong to ("" until the first identity).</summary>
    public string BoundKey => _boundKey;
    /// <summary>The character the tabs show; the bound one unless the selector picked another.</summary>
    public string CharacterKey { get { lock (_selGate) return _sel.Viewed; } }
    public bool IsViewingOther { get { lock (_selGate) return _sel.IsViewingOther; } }
    /// <summary>The viewed character's inventory: the live <see cref="Inventory"/> when viewing the bound one, else a read-only copy of its file.</summary>
    public InventoryStore ViewInventory => _viewStore ?? Inventory;
    /// <summary>Raised when what <see cref="ViewInventory"/> holds changed (live packets of the viewed character, a reload of its file, a switch).</summary>
    public event Action? ViewInventoryChanged;
    /// <summary>The bound or the viewed character changed (login, or a pick in the selector): tabs re-read their stores.</summary>
    public event Action? CharacterChanged;
    /// <summary>A login sent new skills or Daevanion nodes (A2M-35): <see cref="CurrentProfile"/> may have moved.</summary>
    public event Action? ProfileChanged;
    public List<string> CharacterKeys()
    {
        var dir = Path.Combine(AppSettings.AppDataDir, "characters");
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).OrderBy(n => n).ToList();
    }

    private void OnLiveInventoryChanged() { if (_viewStore is null) ViewInventoryChanged?.Invoke(); }
    private void OnViewStoreChanged() => ViewInventoryChanged?.Invoke();

    /// <summary>Points ViewInventory at the viewed character (call under _selGate after the selection moved).</summary>
    private void SetViewStore()
    {
        var old = _viewStore;
        if (old is not null) old.Changed -= OnViewStoreChanged;
        InventoryStore? next = null;
        if (_sel.IsViewingOther)
        {
            next = InventoryStore.ReadOnly(Path.Combine(AppSettings.AppDataDir, "characters", _sel.Viewed, "inventory.json"));
            next.Changed += OnViewStoreChanged;
        }
        _viewStore = next;
    }

    /// <summary>
    /// The selector (A2M-28). Always allowed: while AION2.exe runs only the view moves (the live stores keep receiving the bound
    /// character's packets); with the game closed a pick also rebinds the live stores, as before. False only for an unknown folder.
    /// </summary>
    public bool SelectCharacter(string key)
    {
        var dir = Path.Combine(AppSettings.AppDataDir, "characters", key);
        if (key.Length == 0 || !Directory.Exists(dir)) return false;
        var running = Core.Capture.GameProcessLocator.FindGameProcessIds().Length > 0;
        lock (_selGate)
        {
            var pick = _sel.Pick(key, running);
            if (pick == CharacterPick.None) return true;
            if (pick == CharacterPick.Rebind)
            {
                _boundKey = key;
                Inventory.Bind(Path.Combine(dir, "inventory.json"), null);   // no legacy mirrors: this is a view, the live login rebinds with them
                Character.Bind(Path.Combine(dir, "character.json"), null);
                _lastSummary = "";
            }
            SetViewStore();
            AdoptRestoredBuilds(key);
            Builds = new BuildStore(Path.Combine(dir, "builds"));
            Log.Info(pick == CharacterPick.Rebind ? $"Character stores switched by hand to {key}" : $"Viewing {key} (live data stays with {(_boundKey.Length > 0 ? _boundKey : "nobody")})");
        }
        CharacterChanged?.Invoke();
        ViewInventoryChanged?.Invoke();
        return true;
    }

    /// <summary>One folder per character (characters/&lt;server&gt;-&lt;name&gt;/) so every alt keeps its own live inventory and lists; the legacy inventory.json and state/character.json mirror the character that is logged in. A new identity also pulls the view to it, whoever was being viewed.</summary>
    private void BindCharacterStores()
    {
        var name = Tracker.SelfName; var server = Tracker.SelfServerId;
        if (Settings.Transient || string.IsNullOrEmpty(name) || server <= 0) return;
        var key = $"{server}-{string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))}";
        var dir = Path.Combine(AppSettings.AppDataDir, "characters", key);
        lock (_selGate)
        {
            if (!_sel.Login(key)) return;
            _boundKey = key;
            Inventory.Bind(Path.Combine(dir, "inventory.json"), Path.Combine(AppSettings.AppDataDir, "inventory.json"));
            Character.Bind(Path.Combine(dir, "character.json"), Path.Combine(AppSettings.AppDataDir, "state", "character.json"));
            SetViewStore();
            AdoptRestoredBuilds(key);
            Builds = new BuildStore(Path.Combine(dir, "builds"));
            Log.Info($"Character stores bound to {key}");
            _lastSummary = "";
        }
        CharacterChanged?.Invoke();
        ViewInventoryChanged?.Invoke();
        _ = SyncProfileAsync(key, name, server);
    }

    /// <summary>Builds restored from the backup code before this character had a folder here (builds-restored/&lt;charhash&gt;/) join it now.</summary>
    private void AdoptRestoredBuilds(string key)
    {
        if (Settings.Transient || Settings.InstallId.Length == 0) return;
        try
        {
            var moved = BackupPlacement.Adopt(AppSettings.AppDataDir, Settings.InstallId, key);
            if (moved > 0) Log.Info($"Backup: {moved} restored build(s) moved to {key}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Backup: restored builds not moved", ex); }
    }

    public sealed record RestoreSummary(int Builds, int Placed, int Waiting, int KeptLocal, bool StateFound, bool WatchRestored, bool FavouritesRestored);

    /// <summary>
    /// A2M-30 restore, on the UI thread after the server answered: the code becomes this install's id (telemetry continues under it),
    /// builds go to the character whose hash matches or wait in builds-restored/, and the watch list / craft favourites come back only
    /// where the local ones are empty.
    /// </summary>
    public RestoreSummary ApplyRestore(string code, RestorePayload payload)
    {
        var s = Settings;
        s.InstallId = code;
        s.BuildTelemetrySent.Clear();
        s.StateTelemetrySent = null; s.StateTelemetryHash = "";
        var placed = BackupPlacement.Place(payload.Builds, AppSettings.AppDataDir, code);
        // what came from the server is already there: no need to send it back tomorrow
        foreach (var r in payload.Builds) s.BuildTelemetrySent[r.Build.Id] = new AppSettings.BuildSent(r.Ts, r.Build.UpdatedAt);
        bool stateFound = false, watchRestored = false, favRestored = false;
        foreach (var st in payload.State)
        {
            if (st.Kind != BackupState.Kind) continue;
            stateFound = true;
            var (watch, favs) = BackupState.Parse(st.Data);
            var local = Prices.All();
            if (BackupState.Takes(local, watch)) { Prices.AddRange(watch); watchRestored = true; }
            if (BackupState.Takes(s.CraftFavourites, favs)) { s.CraftFavourites.AddRange(favs); favRestored = true; }
        }
        try { s.Save(); } catch (IOException ex) { Log.Error("Backup: settings not saved", ex); }
        Log.Info($"Backup restored: {payload.Builds.Count} builds ({placed.Placed} placed, {placed.Waiting} waiting, {placed.KeptLocal} newer here), state {(stateFound ? "found" : "none")}, watch {(watchRestored ? "restored" : "kept")}, favourites {(favRestored ? "restored" : "kept")}");
        if (_boundKey.Length > 0) AdoptRestoredBuilds(_boundKey);
        var viewed = CharacterKey;
        if (viewed.Length > 0 && viewed != _boundKey) AdoptRestoredBuilds(viewed);
        Builds = new BuildStore(Builds.Directory);
        _lastSummary = "";
        CharacterChanged?.Invoke();
        return new RestoreSummary(payload.Builds.Count, placed.Placed, placed.Waiting, placed.KeptLocal, stateFound, watchRestored, favRestored);
    }

    private long _lastProfileSync;
    private int _profileSyncing;
    private (string Path, DateTime Stamp, CharacterProfile? Profile, CapturedProfile? Captured) _profileCache = ("", default, null, null);
    private (string Path, DateTime Stamp, CapturedProfile Captured) _capturedCache = ("", default, CapturedProfile.Empty);

    /// <summary>The captured login lists in another character's character.json, re-read only when the file changes.</summary>
    private CapturedProfile CapturedFor(string key)
    {
        var path = Path.Combine(AppSettings.AppDataDir, "characters", key, "character.json");
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        if (_capturedCache.Path == path && _capturedCache.Stamp == stamp) return _capturedCache.Captured;
        var c = stamp == default ? CapturedProfile.Empty : CharacterState.ReadCaptured(path);
        _capturedCache = (path, stamp, c);
        return c;
    }

    /// <summary>
    /// The viewed character's official profile as last synced (characters/&lt;key&gt;/profile.json, re-read only when the file changes),
    /// else (viewing the logged-in character) the armory cache for its name; null when neither exists.
    /// </summary>
    public CharacterProfile? CurrentProfile
    {
        get
        {
            var key = CharacterKey;
            if (key.Length == 0) return null;
            var live = key == _boundKey;
            var path = Path.Combine(AppSettings.AppDataDir, "characters", key, "profile.json");
            try
            {
                var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
                // A2M-35: what the game sent at the last login wins over the official cache when newer (live store for the bound
                // character, its character.json for another one); the API stays the base (names, gear, nodes' effects) and fallback.
                var captured = live ? Character.Captured : CapturedFor(key);
                if (_profileCache.Path == path && _profileCache.Stamp == stamp && ReferenceEquals(_profileCache.Captured, captured)
                    && (stamp != default || _profileCache.Profile is not null)) return _profileCache.Profile;
                var p = stamp != default ? System.Text.Json.JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(path))
                      : live && Tracker.SelfName is { Length: > 0 } n && Tracker.SelfServerId > 0 ? Armory.Cached(n, Tracker.SelfServerId) : null;
                var merged = p is null ? null : captured.Apply(p);
                _profileCache = (path, stamp, merged, captured);
                return merged;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// A2M-44: the profile the meter has for a character without asking the network — an own character's folder (profile.json with the
    /// captured login lists laid over it, as <see cref="CurrentProfile"/>), else the armory cache; null when neither exists.
    /// </summary>
    public CharacterProfile? KnownProfile(string name, int serverId)
    {
        if (serverId <= 0) serverId = SelfServerOrEffective;   // a fight row without a server id: same server as us (as Top DPS assumes)
        if (serverId <= 0 || name.Length == 0) return null;
        var key = serverId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + name;
        if (string.Equals(CharacterKey, key, StringComparison.OrdinalIgnoreCase) && CurrentProfile is { } current) return current;
        var path = Path.Combine(AppSettings.AppDataDir, "characters", key, "profile.json");
        try
        {
            if (File.Exists(path) && System.Text.Json.JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(path)) is { } own)
                return (string.Equals(key, _boundKey, StringComparison.OrdinalIgnoreCase) ? Character.Captured : CapturedFor(key)).Apply(own);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return Armory.Cached(name, serverId);
    }

    /// <summary>Fetches the viewed character's official profile now, ignoring the cache's age (Build tab → refresh). False when nothing was fetched.</summary>
    public Task<bool> RefreshProfileAsync()
    {
        var key = CharacterKey;
        if (key.Length == 0) return Task.FromResult(false);
        string? name = null; var server = 0;
        if (CurrentProfile is { } p) { name = p.Ref.Name; server = p.Ref.ServerId; }
        else if (key == _boundKey && Tracker.SelfName is { Length: > 0 } n && Tracker.SelfServerId > 0) { name = n; server = Tracker.SelfServerId; }
        else if (key.IndexOf('-') is > 0 and var dash && int.TryParse(key.AsSpan(0, dash), out var sv)) { name = key[(dash + 1)..]; server = sv; }
        return name is null || server <= 0 ? Task.FromResult(false) : SyncProfileAsync(key, name, server, force: true);
    }

    /// <summary>
    /// The official character API: fetched when a character logs in and again every 6 h while playing, into
    /// characters/&lt;key&gt;/profile.json (+ profile.txt, the same text as the Top DPS profile panel). The site lags the game by an
    /// unknown delay; since A2M-35 skills, specs, bar, Daevanion nodes and attributes also come from the login packets
    /// (CharacterState.Captured) and <see cref="CurrentProfile"/> lays them over this file when newer.
    /// </summary>
    private async Task<bool> SyncProfileAsync(string key, string name, int server, bool force = false)
    {
        if (Settings.Offline || Viewer || Interlocked.Exchange(ref _profileSyncing, 1) == 1) return false;
        try
        {
            var p = await Armory.LookupAsync(name, server, withDaevanion: true, force: force).ConfigureAwait(false);
            if (p is not null && DateTimeOffset.UtcNow - p.FetchedAt > TimeSpan.FromHours(6)) p = await Armory.LookupAsync(name, server, withDaevanion: true, force: true).ConfigureAwait(false);
            if (p is null) { Log.Info($"Profile sync: {name} not found on server {server}"); return false; }
            var dir = Path.Combine(AppSettings.AppDataDir, "characters", key);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "profile.json"), System.Text.Json.JsonSerializer.Serialize(p, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(dir, "profile.txt"), ProfileFormatter.Text(p, null, Data));
            _lastProfileSync = Environment.TickCount64;
            _lastSummary = "";
            Log.Info($"Profile sync: {name} (fetched {p.FetchedAt:HH:mm}, {p.Skills.Count} skills, {p.Daevanion.Count} daevanion nodes)");
            return true;
        }
        catch (Exception e) { Log.Error("Profile sync", e); return false; }
        finally { _profileSyncing = 0; }
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
                    foreach (var st in InventoryStore.ReadFile(inv)?.Stacks ?? new()) { stacks++; if (st.Item == InventoryStore.Kinah) kinah = st.Count; }
            }
            catch (Exception) { }
            var prof = Path.Combine(dir, "profile.json");
            list.Add(new { key = Path.GetFileName(dir), kinah, stacks, inventoryAt = File.Exists(inv) ? new DateTimeOffset(File.GetLastWriteTime(inv)).ToString("O") : null,
                           profileAt = File.Exists(prof) ? new DateTimeOffset(File.GetLastWriteTime(prof)).ToString("O") : null, current = Path.GetFileName(dir) == _boundKey });
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

    private long _craftableInventoryMs = -1;

    /// <summary>characters/&lt;key&gt;/craftable.json: recipes the stock allows now and those short by ≤ 2 materials, for the AI side; rewritten when the inventory changed.</summary>
    private void WriteCraftable()
    {
        if (_boundKey.Length == 0 || Recipes.Count == 0 || Inventory.UpdatedMs == _craftableInventoryMs) return;
        _craftableInventoryMs = Inventory.UpdatedMs;
        var opts = Recipes.Evaluate(Inventory.CountOf, 2);
        var rows = new List<object>(opts.Count);
        foreach (var o in opts)
            rows.Add(new { item = o.Recipe.Output, name = Names.Name(o.Recipe.Output) ?? o.Recipe.Name, profession = o.Recipe.Profession, level = o.Recipe.Level, grade = o.Recipe.Grade, times = o.Times,
                           missing = o.Missing.Select(m => new { item = m.Item, name = Names.Name(m.Item), need = m.Need, have = m.Have, source = Names.Source(m.Item) }) });
        try
        {
            var path = Path.Combine(AppSettings.AppDataDir, "characters", _boundKey, "craftable.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { at = DateTimeOffset.Now.ToString("O"), craftable = opts.Count(x => x.Times > 0), rows }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    private int _achievementsVersion = -1;
    private string _achievementsKey = "";
    /// <summary>characters/&lt;key&gt;/achievements.json (A2M-15) when the rows or tickets moved; then tells the Items cards.</summary>
    private void WriteAchievements()
    {
        if (_boundKey.Length == 0) return;
        var v = Character.ProgressVersion;
        if (v == _achievementsVersion && _boundKey == _achievementsKey) return;
        _achievementsVersion = v; _achievementsKey = _boundKey;
        if (Character.Achievements.Count > 0)
        {
            try
            {
                AchievementStore.Write(Path.Combine(AppSettings.AppDataDir, "characters", _boundKey, "achievements.json"), Character.Achievements, Character.AchievementGroups,
                    AchievementBook, id => Names.Name(id), DateTimeOffset.Now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        ProgressChanged?.Invoke();
    }

    private long _titlesCharacterMs = -1;
    /// <summary>
    /// characters/&lt;key&gt;/titles.json: owned titles with their bonuses, the permanent collection totals, and every title of the
    /// character's faction still missing with how it is obtained (grade-first), so the AI side can say what to chase. Rewritten when
    /// the login lists change. Achievements: achievements.json (A2M-15).
    /// </summary>
    private void WriteTitles()
    {
        if (_boundKey.Length == 0 || Titles.Count == 0 || Character.UpdatedMs == _titlesCharacterMs) return;
        _titlesCharacterMs = Character.UpdatedMs;
        var ownedIds = Character.Titles;
        var faction = Titles.FactionOf(ownedIds);
        var owned = new List<object>(ownedIds.Count); var missing = new List<TitleInfo>(600);
        foreach (var t in Titles.All)
        {
            if (ownedIds.Contains(t.Id)) owned.Add(new { id = t.Id, name = t.Name, category = t.Category, grade = t.Grade, equipped = t.Equipped.Select(e => e.Stat + " " + e.Value), collection = t.Collection.Select(e => e.Stat + " " + e.Value) });
            else if (t.Faction == faction) missing.Add(t);
        }
        missing.Sort((a, b) => { var c = TitleBook.GradeRank(a.Grade).CompareTo(TitleBook.GradeRank(b.Grade)); return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name); });
        var unknown = ownedIds.Count(id => Titles.Get(id) is null);
        // titles.txt: the same facts as prose for the AI side (Diego 2026-10-10: "informe de títulos a perseguir"): totals, then the missing
        // titles of this faction ranked by grade and by how much permanent collection bonus they carry, with how to get them
        try
        {
            var totals = Titles.CollectionTotals(ownedIds);
            var sb = new System.Text.StringBuilder(32_768);
            sb.Append("Titles — ").Append(_boundKey).Append(" — ").Append(faction == 'l' ? "Elyos" : faction == 'd' ? "Asmodian" : "?")
              .Append(" — owned ").Append(ownedIds.Count).Append(", missing same-faction ").Append(missing.Count).Append(", written ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
            sb.Append("Collection totals (permanent, from owned titles): ");
            var first = true;
            foreach (var kv in totals.OrderByDescending(k => k.Value)) { if (!first) sb.Append(", "); sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)); first = false; }
            sb.Append("\n\nMissing titles worth chasing (grade, then bonus count; 'how' = aion2hub description):\n");
            var ranked = missing.OrderBy(t => TitleBook.GradeRank(t.Grade)).ThenByDescending(t => t.Collection.Length + t.Equipped.Length).ThenBy(t => t.Name).Take(80);
            foreach (var t in ranked)
            {
                sb.Append("- [").Append(t.Grade).Append("] ").Append(t.Name).Append(" (").Append(t.Category).Append(") — equipped: ")
                  .Append(t.Equipped.Length == 0 ? "-" : string.Join(", ", t.Equipped.Select(e => e.Stat + " " + e.Value)))
                  .Append("; collection: ").Append(t.Collection.Length == 0 ? "-" : string.Join(", ", t.Collection.Select(e => e.Stat + " " + e.Value)))
                  .Append(" — how: ").Append(string.IsNullOrEmpty(t.Description) ? "?" : t.Description).Append('\n');
            }
            sb.Append("\nOwned (").Append(owned.Count).Append("): ").Append(string.Join("; ", Titles.All.Where(t => ownedIds.Contains(t.Id)).Select(t => t.Name))).Append('\n');
            File.WriteAllText(Path.Combine(AppSettings.AppDataDir, "characters", _boundKey, "titles.txt"), sb.ToString());
        }
        catch (IOException) { }
        try
        {
            var path = Path.Combine(AppSettings.AppDataDir, "characters", _boundKey, "titles.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now.ToString("O"), faction = faction == 'l' ? "Elyos" : faction == 'd' ? "Asmodian" : "?", owned = owned.Count, unknownIds = unknown, missingSameFaction = missing.Count,
                collectionTotals = Titles.CollectionTotals(ownedIds),
                ownedTitles = owned,
                missingTitles = missing.Select(t => new { id = t.Id, name = t.Name, category = t.Category, grade = t.Grade, how = t.Description, equipped = t.Equipped.Select(e => e.Stat + " " + e.Value), collection = t.Collection.Select(e => e.Stat + " " + e.Value) })
            }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (IOException) { }
    }

    /// <summary>state/summary.json: when each store last changed, where the player is, what is loaded — rewritten only when something moved.</summary>
    private void WriteSummary()
    {
        var invPath = Path.Combine(AppSettings.AppDataDir, "inventory.json");
        var invAge = Inventory.UpdatedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Inventory.UpdatedMs).ToLocalTime().ToString("O")
                   : File.Exists(invPath) ? new DateTimeOffset(File.GetLastWriteTime(invPath)).ToString("O") : null;
        var chrAge = Character.UpdatedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Character.UpdatedMs).ToLocalTime().ToString("O") : null;
        var key = $"{_boundKey}|{Inventory.UpdatedMs}|{Inventory.CountOf(InventoryStore.Kinah)}|{Character.UpdatedMs}|{Character.ProgressVersion}|{Shop.Count}|{Prices.LastPollAt}|{Atlas.MapId}|{(int)Atlas.SelfX}|{(int)Atlas.SelfY}|{Tracker.SelfServerId}|{Inventory.All().Count}";
        if (key == _lastSummary) return;
        _lastSummary = key;
        WriteCraftable();
        WriteTitles();
        var o = new
        {
            savedAt = DateTimeOffset.Now.ToString("O"),
            serverId = Tracker.SelfServerId,
            mapId = Atlas.MapId, x = Atlas.SelfX, y = Atlas.SelfY,
            characterKey = _boundKey.Length > 0 ? _boundKey : null,
            characterName = Tracker.SelfName,
            kinah = Inventory.CountOf(InventoryStore.Kinah),
            kinahBound = Inventory.CountOf(InventoryStore.KinahBound),
            characters = CharacterFolders(),
            currencies = Currencies(),
            inventory = new { updatedAt = invAge, stacks = Inventory.All().Count, containers = Inventory.ContainerIds },
            character = new { updatedAt = chrAge, pets = Character.Pets.Count, titles = Character.Titles.Count, achievements = Character.Achievements.Count,
                achievementWindow = AchievementWindow(), tickets = TicketSummary(), expeditionChests = ChestSummary(),
                capturedAt = Character.Captured.LatestMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(Character.Captured.LatestMs).ToLocalTime().ToString("O") : null },
            shop = new { offers = Shop.Count },
            prices = new { lastPollAt = Prices.LastPollAt?.ToString("O"), watches = Prices.Count },
            files = new[] { "characters/<key>/inventory.json", "characters/<key>/character.json (login lists: pets, titles, achievements; Captured = skills, specs, skill bar, daevanion nodes, attributes from the game)", "characters/<key>/titles.json (owned titles + bonuses, collection totals, missing same-faction titles with how to get them)", "characters/<key>/achievements.json (achievement progress + claimed step per id, window summary, claimable with rewards; tickets live in character.json Tickets)", "characters/<key>/profile.json (official API: gear, skills+levels, stigmas, daevanion boards; profile.txt readable)", "inventory.json (mirror of the current character)", "state/character.json (mirror)", "state/shop.json (Black Cloud shop offers, 91 56)", "state/price-watch.json", "state/price-watch.json.history.jsonl", "names-cache.json" },
        };
        try
        {
            var path = Path.Combine(AppSettings.AppDataDir, "state", "summary.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    private object? AchievementWindow()
    {
        if (Character.Achievements.Count == 0 || AchievementBook.Count == 0) return null;
        var s = AchievementBook.Summarise(Character.Achievements.Values);
        return new { tiersClaimed = s.TiersClaimed, tiersTotal = s.TiersTotal, completed = s.Completed, claimable = s.Claimable, inProgress = s.InProgress };
    }

    private object[] TicketSummary()
    {
        var l = new List<object>(24);
        foreach (var t in Character.Tickets.Values.OrderBy(t => t.Key))
        {
            if (!TicketBook.IsNamed(t.Key) || TicketBook.IsStub(t.Key)) continue;
            var info = TicketBook.Of(t.Key);
            l.Add(t.IsTime ? new { key = t.Key, name = info.En, remainingMin = t.Time / 60_000 } : (object)new { key = t.Key, name = info.En, @base = t.Base, bonus = t.Bonus });
        }
        return l.ToArray();
    }

    /// <summary>Conquest guaranteed chests of the Expedition window (12 57): progress/cap per dungeon; null before the first 12 57 of this character.</summary>
    private object[]? ChestSummary()
    {
        var chests = Character.Chests;
        if (chests is null) return null;
        var l = new List<object>(6);
        foreach (var (_, info) in TicketBook.Today(Character.Tickets))
        {
            if (TicketBook.ChestProgress(info, chests) is not { } p) continue;
            l.Add(new { dungeon = info.Dungeon, name = info.En, stars = info.Stars, progress = p, cap = info.Chest, claimable = p >= info.Chest });
        }
        return l.ToArray();
    }

    private void SaveNames()
    {
        var version = Tracker.CacheVersion;
        if (version == _savedNamesVersion) return;
        _savedNamesVersion = version;
        _names.Save(Tracker.ExportCache());
    }

    /// <summary>English / Spanish for the interface and for skill / NPC names, applied without a restart.</summary>
    public void SetLanguage(string language)
    {
        language = UiText.Normalize(language);
        Settings.Language = language;
        // A2M-47 "general language": switching the interface also moves the help pages, skill catalogue and item-name
        // search to it; each keeps its own EN/ES selector for overriding afterwards.
        Settings.HelpLanguage = language;
        Settings.SkillLanguage = language;
        Settings.PriceSearchLanguage = language;
        Data.ReloadNames(DataDirectory, language);
        UiText.Use(language);
        Settings.Save();
    }

    // ------------------------------------------------------------------ A2M-40: the server the user plays on

    /// <summary>The effective server and its derived keys; replaced (never mutated) on every change.</summary>
    public ServerContext Server { get; private set; }
    /// <summary>The effective server or region changed (Settings save, a login on another server); raised on any thread.</summary>
    public event Action? ServerChanged;
    private int _resolvedLogin = -1;

    /// <summary>The logged-in character's server, else the effective one: whose server a fight row without an id is on.</summary>
    public int SelfServerOrEffective => Tracker.SelfServerId is > 0 and var s ? s : Server.ServerId;

    /// <summary>Re-resolves <see cref="Server"/> (Settings → login → newest character folder → 1303) and pushes it to every consumer when it moved.</summary>
    public void RefreshServer()
    {
        var login = _replaying || DemoRunning ? 0 : Tracker.SelfServerId;
        _resolvedLogin = Tracker.SelfServerId;
        var chosen = Settings.ServerId;
        var next = ServerContext.Resolve(Settings.Region, chosen, login, chosen > 0 || login > 0 ? 0 : LastCharacterServer(), Data.Servers, Data.ServersByRegion);
        var prev = Server;
        if (next == prev && _serverApplied) return;
        Server = next;
        Log.Info($"Server: {next}");
        Prices.ExchangeServerId = next.ExchangeKey;
        Armory.Region = next.ArmoryRegion;
        Timers.Pin(next.Source == ServerSource.Settings ? next.ServerId : 0);
        if (!_serverApplied || next.Region != prev.Region)
        {
            try { Events = ScheduledEvent.ForZone(_euEvents, TimeZoneInfo.FindSystemTimeZoneById(next.TimeZoneId)); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { Events = _euEvents; Log.Error($"Events: no time zone {next.TimeZoneId}, EU clock kept", ex); }
        }
        var repoll = _serverApplied && next.ExchangeKey != prev.ExchangeKey && !Settings.Transient && Prices.Count > 0;
        _serverApplied = true;
        if (repoll) _ = PollPricesAsync(Environment.TickCount64);   // the watch list shows another server's prices now
        ServerChanged?.Invoke();
    }
    private bool _serverApplied;

    /// <summary>The server of the most recently written characters/&lt;server&gt;-&lt;name&gt; folder; 0 when there is none.</summary>
    private int LastCharacterServer()
    {
        if (Settings.Transient) return 0;
        try
        {
            var dir = Path.Combine(AppSettings.AppDataDir, "characters");
            if (!Directory.Exists(dir)) return 0;
            var best = 0; var stamp = DateTime.MinValue;
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
            {
                var id = ServerContext.ServerOfKey(d.Name);
                if (id <= 0) continue;
                var f = new FileInfo(Path.Combine(d.FullName, "inventory.json"));
                var t = f.Exists ? f.LastWriteTimeUtc : d.LastWriteTimeUtc;
                if (t > stamp) { stamp = t; best = id; }
            }
            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary><see cref="ApplySettings"/> without writing settings.json (the daemon after a reload, the Linux UI after its own save).</summary>
    public void ApplySettingsInPlace()
    {
        Settings.ApplyTo(Options);
        OnSettingsApplied();
        RefreshServer();
    }

    public void ApplySettings()
    {
        Settings.ApplyTo(Options);
        OnSettingsApplied();
        Settings.Save();
        RefreshServer();
    }

    /// <summary>The App's icon caches follow the "download icons" switch here.</summary>
    protected virtual void OnSettingsApplied() { }

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


    /// <summary>Fights saved before map names were known carry "Map 1110": resolve those now.</summary>
    public string? DisplayZone(string? zone) =>
        zone is not null && zone.StartsWith("Map ", StringComparison.Ordinal) && int.TryParse(zone.AsSpan(4), out var id)
            ? Data.MapName(id)
            : zone;

    private bool _exiting;

    private int _atlasFaults;
    private int _inventoryFaults;

    /// <summary>The last character window the player opened on someone else (<c>50 36</c>, A2M-35). No UI yet.</summary>
    public Core.Events.InspectEvent? LastInspect { get; private set; }

    private void InventoryOnEvent(Core.Events.GameEvent e)
    {
        if (e is Core.Events.InspectEvent inspect)
        {
            LastInspect = inspect;
            Log.Info($"Inspect: {inspect.Name} {inspect.Class} lvl {inspect.Level} CP {inspect.CombatPower}, {inspect.Items.Count} items (GS {inspect.GearScore}, legion {inspect.Legion})");
            return;
        }
        if (e is Core.Events.PetListEvent or Core.Events.TitleListEvent or Core.Events.AchievementListEvent or Core.Events.SkillListEvent
            or Core.Events.EquippedSkillsEvent or Core.Events.SkillBarEvent or Core.Events.DaevanionBoardsEvent or Core.Events.AttributesEvent
            or Core.Events.AchievementUpdateEvent or Core.Events.AchievementClaimEvent or Core.Events.ContentTicketsEvent
            or Core.Events.ExpeditionChestsEvent)
        {
            try { Character.OnEvent(e); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error("Character state failed", ex); }
            // a relog into the same character binds nothing new, so CharacterChanged would not tell the Build tab
            if (e is Core.Events.SkillListEvent or Core.Events.DaevanionBoardsEvent) ProfileChanged?.Invoke();
            return;
        }
        if (e is Core.Events.ShopPageEvent) { try { Shop.OnEvent(e); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error("Shop store failed", ex); } return; }
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

    private string? _lastRosterLine;
    private CaptureHealth? _lastHealth;

    /// <summary>
    /// One line per boss fight: how many players' hits arrived, and whether the capture lost packets since the last one —
    /// tells "the server never sent it" (only own hits, nothing dropped) from "we lost it" in a player's log.
    /// </summary>
    private void LogBossFight(EncounterSnapshot s)
    {
        if (_replaying || DemoRunning || s.Boss is null) return;
        CaptureHealth? health;
        lock (_captureGate) health = (_capture as LiveCapture)?.Health();
        var own = s.MostDamageUnsent ? " (most damage not sent)" : "";
        var capture = "";
        if (health is { } now)
        {
            // The adapter's counters restart when it is reopened.
            var before = _lastHealth is { } last && last.Received <= now.Received && last.Gaps <= now.Gaps ? last : default;
            capture = $"; capture since the last boss: {now.Received - before.Received:N0} packets, {now.Dropped - before.Dropped:N0} dropped, " +
                      $"{now.Gaps - before.Gaps:N0} stream gaps";
        }
        else if (!Availability.Present) capture = "; capture: " + CaptureAvailability.NpcapNotInstalled;
        _lastHealth = health;
        Log.Info($"Boss fight {s.Title} ({s.Reason}, {Format.Clock(s.CombatMs)}): {s.Fighting} attacking, {s.PlayerCount} listed, " +
                 $"{s.DamageSenders} with hits{own}, " +
                 $"counted {Format.Compact(s.TotalDamage)}{capture}");
    }

    private void OnEncounterFinished(FightRecord record)
    {
        LogBossFight(record.Summary);
        RankOnAbyssLogs(record.Summary);
        if (!ShouldSave(record.Summary)) { if (!_replaying && !DemoRunning) FightClosed?.Invoke(record, null); return; }
        if (_exiting) Save(); // the process is about to end: a worker thread would not get to it
        else ThreadPool.QueueUserWorkItem(_ => Save());

        void Save()
        {
            try
            {
                var path = History.Save(record);
                FightSaved?.Invoke(path);
                FightClosed?.Invoke(record, path);
                if (Top.Consider(record, Path.GetFileName(path), Data) > 0) Top.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Saving fight failed", ex);
            }
        }
    }

    public virtual void Dispose()
    {
        var live = !Settings.Transient && !Viewer && !_replaying && !DemoRunning;
        lock (_captureGate) _capture?.Dispose(); // no more packets: the running fight is as complete as it gets
        if (!Settings.Transient && !Viewer) Atlas.Save();   // the render tool's sample nodes are never written
        _replay?.Dispose();
        _demo?.Dispose();
        if (live)
        {
            // Closing the meter mid-fight keeps the fight so far, like the restart button does.
            _exiting = true;
            Tracker.Reset();
            SaveNames();
        }
        NameResolver.Dispose();
    }

    // ---------------------------------------------------------------- A2M-38: Abyss Logs percentile after a boss kill

    /// <summary>Abyss Logs open API (GET only, region + boss code, 400 ms pacing, 6 h cache under AppData\abysslogs).</summary>
    public AbyssLogsClient AbyssLogs { get; } = new(Path.Combine(AppSettings.AppDataDir, "abysslogs"));
    private AbyssLogsNotes? _fightNotes;
    /// <summary>The Abyss Logs line of each fight by id (kept on disk except for the render tool).</summary>
    public AbyssLogsNotes FightNotes => LazyInitializer.EnsureInitialized(ref _fightNotes,
        () => new AbyssLogsNotes(Settings.Transient ? null : Path.Combine(AppSettings.AppDataDir, "abysslogs", "notes.json")));
    /// <summary>Raised on a worker thread when a kill's percentile is known: (fight id, line).</summary>
    public event Action<Guid, string>? AbyssLogsReady;
    /// <summary>The newest line and when it came (TickCount64): the overlay shows it for 60 s.</summary>
    public (Guid Fight, string Text, long At)? LastAbyssLogs { get; private set; }
    public string? AbyssLogsNoteFor(Guid fight) => FightNotes.Get(fight);

    /// <summary>Sets a fight's line as if it had come from Abyss Logs (render tool sample).</summary>
    public void ShowAbyssLogs(Guid fight, string text)
    {
        FightNotes.Set(fight, text);
        LastAbyssLogs = (fight, text, Environment.TickCount64);
        AbyssLogsReady?.Invoke(fight, text);
    }

    private void RankOnAbyssLogs(EncounterSnapshot s)
    {
        if (_replaying || DemoRunning || Settings.Transient || Settings.Offline || Viewer || !Settings.AbyssLogsPercentile) return;

        // kills only: Abyss Logs ranks cleared fights, and a wipe's DPS is not comparable
        if (s.Boss is not { NpcCode: > 0 } boss || s.Reason != EncounterEndReason.Kill) return;
        CombatantSnapshot? self = null;
        foreach (var c in s.Combatants) if (c.IsSelf) { self = c; break; }
        if (self is not { Dps: > 0 }) return;
        var cls = self.Class != Core.Events.GameClass.Unknown ? self.Class : Tracker.SelfClass;
        var srv = Server;   // a server picked in Settings wins; otherwise the shard of the fighter's own server id
        var region = srv.Source == ServerSource.Settings || self.ServerId == 0 ? srv.AbyssLogsRegion : Core.Ranking.AbyssLogs.RegionOf(self.ServerId);
        var (id, code, name, dps, power) = (s.Id, boss.NpcCode, boss.Name, self.Dps, self.CombatPower);
        _ = Task.Run(async () =>
        {
            try
            {
                var stats = await AbyssLogs.StatsAsync(region, code).ConfigureAwait(false);
                if (stats is null || Core.Ranking.AbyssLogs.Pick(stats, cls, power) is not { } cell)
                {
                    Log.Info($"Abyss Logs: no {cls} statistics for {code} ({region})");
                    return;
                }
                var est = Core.Ranking.AbyssLogs.Estimate(cell, dps);
                var text = UiText.Current.AbyssLogsLine(est, cls, name);
                Log.Info($"Abyss Logs: {code} {cls} {Format.Compact(dps)}/s CP {power} -> p{est.Percentile} of {cell.Samples} logs ({region})");
                ShowAbyssLogs(id, text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error("Abyss Logs percentile failed", ex); }
        });
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
