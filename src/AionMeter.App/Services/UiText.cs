namespace AionMeter.App.Services;

/// <summary>
/// All interface wording, in English (default) or Russian. Plain properties so XAML binds to them:
/// <c>{Binding TabDps, Source={x:Static s:UiText.Current}}</c> in windows, <c>{Binding T.ColDps}</c> in the overlay.
/// Switching language swaps <see cref="Current"/>; the overlay re-binds, other windows are reopened.
/// </summary>
public sealed class UiText
{
    public static readonly string[] Languages = ["en", "ru"];

    public static UiText Current { get; private set; } = new();

    public static string Normalize(string? language) => language == "ru" ? "ru" : "en";

    public static void Use(string? language) => Current = Normalize(language) == "ru" ? Ru : new UiText();

    public string Code { get; init; } = "en";
    public string LanguageName { get; init; } = "English";

    /// <summary>Dates in the interface language ("06 Oct"), not in the Windows one.</summary>
    public System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(Code == "ru" ? "ru-RU" : "en-US");

    public IReadOnlyDictionary<Core.Events.GameClass, string> ClassNames { get; init; } = new Dictionary<Core.Events.GameClass, string>
    {
        [Core.Events.GameClass.Gladiator] = "Gladiator",
        [Core.Events.GameClass.Templar] = "Templar",
        [Core.Events.GameClass.Ranger] = "Ranger",
        [Core.Events.GameClass.Assassin] = "Assassin",
        [Core.Events.GameClass.Sorcerer] = "Sorcerer",
        [Core.Events.GameClass.Elementalist] = "Elementalist",
        [Core.Events.GameClass.Cleric] = "Cleric",
        [Core.Events.GameClass.Chanter] = "Chanter",
        [Core.Events.GameClass.Brawler] = "Brawler",
    };

    public string ClassName(Core.Events.GameClass c) => ClassNames.TryGetValue(c, out var n) ? n : "";

    public IReadOnlyDictionary<Core.Events.HitFlags, string> FlagNames { get; init; } = new Dictionary<Core.Events.HitFlags, string>
    {
        [Core.Events.HitFlags.Critical] = "Critical",
        [Core.Events.HitFlags.Back] = "Back",
        [Core.Events.HitFlags.Front] = "Front",
        [Core.Events.HitFlags.Perfect] = "Perfect",
        [Core.Events.HitFlags.Heavy] = "Smite",
        [Core.Events.HitFlags.Multi] = "Multi-hit",
        [Core.Events.HitFlags.Double] = "Double",
        [Core.Events.HitFlags.Block] = "Block",
        [Core.Events.HitFlags.Parry] = "Parry",
        [Core.Events.HitFlags.Endure] = "Endure",
        [Core.Events.HitFlags.Evade] = "Evade",
        [Core.Events.HitFlags.Dot] = "DoT",
    };

    /// <summary>"Critical · Back" in the current language.</summary>
    public string FlagsText(Core.Events.HitFlags flags) =>
        string.Join(" · ", FlagNames.Where(kv => (flags & kv.Key) != 0).Select(kv => kv.Value));

    // Skill row tooltip in the breakdown
    public string TipSkillDamage { get; init; } = "Damage";
    public string TipSkillHits { get; init; } = "Hits";
    public string TipSkillDotTicks { get; init; } = "DoT ticks";
    public string TipSkillMin { get; init; } = "Min";
    public string TipSkillAvg { get; init; } = "Avg";
    public string TipSkillMax { get; init; } = "Max";

    /// <summary>
    /// Players whose name the server has not sent yet are "#id": show them as "Templar #10388" so the class is
    /// readable at a glance.
    /// </summary>
    public string PlayerLabel(string name, Core.Events.GameClass cls) =>
        name.StartsWith('#') && ClassName(cls) is { Length: > 0 } c ? $"{c} {name}" : name;

    /// <summary>A row of the ranking: a player, or the pseudo-row of pets whose owner is unknown.</summary>
    public string CombatantLabel(uint actorId, string name, Core.Events.GameClass cls) =>
        actorId == Core.Combat.Combatant.UnknownSummonsId ? UnknownSummons : PlayerLabel(name, cls);

    /// <summary>A fight's title as shown: a boss the meter never saw appear has nothing but its id.</summary>
    public string FightTitle(Core.Combat.EncounterSnapshot s) =>
        s.Boss is { NpcCode: 0 } b && s.Title == $"#{b.ActorId}" ? UnknownBoss : s.Title;

    public string UnknownSummons { get; init; } = "Summons (owner unknown)";
    public string UnknownSummonsTip { get; init; } =
        "Pets, spirits and skill effects (Bittercold Wind, Fire Wall …) whose caster could not be identified. Not a player: not counted in places or the player total.";

    // ---------------------------------------------------------------- boss timers
    public string TimersTitle { get; init; } = "Boss timers";
    public string TimersButton { get; init; } = "Boss timers";
    public string HkTimers { get; init; } = "Boss timers";
    public string HkAtlas { get; init; } = "Atlas";
    public string TipTimers { get; init; } = "Boss respawn timers";
    public string TrayTimers { get; init; } = "Boss timers…";
    public string TimersHint { get; init; } =
        "Open a map's field boss list in the game (map → Exploration → field monsters) and its timers come in straight from the server. Field bosses you see die restart their countdown. Bosses of quest and sealed-area instances have no world timer and are not listed. 🔔 = notify before respawn.";
    public string FilterWatched { get; init; } = "🔔 Watched";
    public string TipWatch { get; init; } = "Notify before this boss respawns";
    public string UnknownSlotBoss { get; init; } = "Field boss #{0}";
    public string SourceGame { get; init; } = "From the in-game boss list (read at {0})";
    public string SourceMeter { get; init; } = "Measured by the meter (kills it saw)";
    public string RespawnFromGame { get; init; } = "from game";
    public string TimerAliveSince { get; init; } = "up since {0}";
    public string TimerNoTime { get; init; } = "no timer in the game";
    public string TipServer { get; init; } = "Every server runs its own bosses: these are the timers of this server (your character's, unless picked)";
    public string ServerUnknown { get; init; } = "server ?";
    public string ServerYours { get; init; } = "your character";
    public string MapNumber { get; init; } = "Map {0}";
    public string TimersEmpty { get; init; } = "No timers yet. Kill a world boss (or watch one die) and its countdown starts here.";
    public string TimerAlive { get; init; } = "ALIVE";
    public string TimerDue { get; init; } = "SHOULD BE UP";
    public string TimerSeen { get; init; } = "seen {0}";
    public string TimerSince { get; init; } = "since {0}";
    public string TimerAt { get; init; } = "at {0}";
    public string TimerKilled { get; init; } = "killed {0}";
    public string TimerFoundDead { get; init; } = "found dead {0}";
    public string TimerNeedsInterval { get; init; } = "set respawn time";
    public string TimerLearned { get; init; } = "Measured by the meter: the shortest time from a kill to the boss showing up again";
    public string TipRespawn { get; init; } = "Respawn time after a kill";
    public string RespawnUnknown { get; init; } = "respawn ?";
    public string BtnKilled { get; init; } = "Killed";
    public string TipKilled { get; init; } = "Killed just now — start the countdown";
    public string TipRemoveTimer { get; init; } = "Remove from the list";
    public string AlertLabel { get; init; } = "Notify before respawn";
    public string AlertOff { get; init; } = "off";
    public string AlertSoon { get; init; } = "{0} respawns in about {1} min ({2})";
    public string AlertNow { get; init; } = "{0} should be up now ({1})";
    public string HoursShort { get; init; } = "h";
    public string MinutesShort { get; init; } = "min";

    /// <summary>"1 h 30 min", "45 min".</summary>
    public string Duration(int minutes)
    {
        var h = minutes / 60;
        var m = minutes % 60;
        if (h == 0) return $"{m} {MinutesShort}";
        return m == 0 ? $"{h} {HoursShort}" : $"{h} {HoursShort} {m} {MinutesShort}";
    }

    // ---------------------------------------------------------------- automatic show / hide
    public string AutoShow { get; init; } = "Show by itself when a fight starts";
    public string AutoShowHint { get; init; } =
        "When a boss is fought nearby or you hit any monster. Hide it during a fight and it waits for the next one.";
    public string AutoHide { get; init; } = "Hide again after combat (s, 0 = never)";
    public string ShowOnStart { get; init; } = "Show the overlay when the meter or the game starts";

    // ---------------------------------------------------------------- overlay: columns
    public string ColPlayer { get; init; } = "PLAYER";
    public string ColDps { get; init; } = "DPS";
    public string ColDamage { get; init; } = "DAMAGE";
    public string ColShare { get; init; } = "SHARE";
    public string TipPlace { get; init; } = "Place in this fight by damage dealt";
    public string TipDps { get; init; } = "Damage per second over the fight";
    public string TipDamage { get; init; } = "Total damage dealt in this fight";
    public string TipShare { get; init; } = "Share of the whole group's damage";
    public string TipGs { get; init; } = "Gear score (equipment item level), from the party roster";
    public string TipCp { get; init; } = "Combat power, from the party roster";

    // ---------------------------------------------------------------- overlay: card
    public string LiveFight { get; init; } = "live fight";
    public string LastFight { get; init; } = "last fight";
    public string SavedFight { get; init; } = "saved fight";
    public string ThisSession { get; init; } = "this session";
    public string WaitingLabel { get; init; } = "waiting for combat";
    public string WaitingTitle { get; init; } = "Waiting for combat";
    public string WaitingDetail { get; init; } = "Hit something — the meter starts on its own";
    public string EmptyParty { get; init; } = "Your party appears here as soon as someone lands a hit.";
    public string TopHit { get; init; } = "TOP HIT";
    public string TopHitBy { get; init; } = "Biggest single hit — {0}";
    public string TipScrollFights { get; init; } = "Mouse wheel here: step through fights";
    public string Defeated { get; init; } = "Defeated";
    public string UnknownBoss { get; init; } = "Unknown boss";
    /// <summary>"counted from 82% HP": the meter started (or restarted) after the boss had already lost the rest.</summary>
    public string CountedFrom { get; init; } = "counted from {0} HP";
    public string HpUnknown { get; init; } = "max HP unknown";
    public string TipRestart { get; init; } = "Restart the meter ({0}): the fight so far is saved, counting starts over";
    public string InProgress { get; init; } = "In progress";
    public string Kill { get; init; } = "Kill";
    public string Wipe { get; init; } = "Wipe";
    public string Ended { get; init; } = "Ended";
    public string OpenWorld { get; init; } = "Open world";
    public string OpenFight { get; init; } = "Open the fight";
    public string Fights { get; init; } = "Fights";
    public string NamesLater { get; init; } = "names after next loading screen";
    public string YourPlace { get; init; } = "Your place in this fight";
    public string Party { get; init; } = "party";
    public string Seconds { get; init; } = "s";
    public string SwitchLanguage { get; init; } = "Language: English → Русский";

    // ---------------------------------------------------------------- tray
    public string TrayHide { get; init; } = "Hide overlay";
    public string TrayShow { get; init; } = "Show overlay";
    public string TrayHistory { get; init; } = "Fight history…";
    public string TraySettings { get; init; } = "Settings…";
    public string TrayDemo { get; init; } = "Run demo fight";
    public string TrayReplay { get; init; } = "Replay capture file…";
    public string TrayExit { get; init; } = "Exit";
    public string ClickThroughOn { get; init; } = "Click-through ON — the overlay ignores the mouse. {0} to turn it off.";
    public string ClickThroughOff { get; init; } = "Click-through OFF";

    // ---------------------------------------------------------------- overlay: menus
    public string Live { get; init; } = "Live";
    public string LiveHint { get; init; } = "follows the current fight";
    public string SessionSection { get; init; } = "THIS SESSION";
    public string SavedToday { get; init; } = "SAVED · TODAY";
    public string SavedYesterday { get; init; } = "SAVED · YESTERDAY";
    public string SavedOn { get; init; } = "SAVED · {0}";
    public string AllSaved { get; init; } = "All saved fights ({0})…";
    public string NoFights { get; init; } = "No fights yet — they are saved automatically";
    public string OpenBreakdown { get; init; } = "Open breakdown";
    public string CopyPlayer { get; init; } = "Copy {0}'s result to chat";
    public string CopyParty { get; init; } = "Copy party ranking to chat";
    public string CopyTable { get; init; } = "Copy party table for Discord";
    public string Copied { get; init; } = "Copied — paste it into the chat with Ctrl+V";
    public string CopyFailed { get; init; } = "The clipboard is busy — try again";
    public string TipChat { get; init; } = "Result to chat or Discord";
    /// <summary>What a pasted result says, in this language (numbers keep their units in every language).</summary>
    public Core.ChatWords Chat { get; init; } = Core.ChatWords.English;
    public string RowDamage { get; init; } = "Damage";
    public string RowHits { get; init; } = "Hits";
    public string RowCrit { get; init; } = "Crit";
    public string RowMax { get; init; } = "Max";
    public Func<int, string> Players { get; init; } = n => n == 1 ? "1 player" : $"{n} players";

    // ---------------------------------------------------------------- breakdown window
    public string TabDps { get; init; } = "DPS";
    public string TabAccuracy { get; init; } = "Accuracy";
    public string TabRotation { get; init; } = "Rotation";
    public string TabDefense { get; init; } = "Defense";
    public string TilePlace { get; init; } = "PLACE";
    public string TileDamage { get; init; } = "DMG";
    public string TileDps { get; init; } = "DPS";
    public string TileContrib { get; init; } = "CONTRIB";
    public string TileTime { get; init; } = "TIME";
    public string DpsTimeline { get; init; } = "DPS TIMELINE";
    public string ThSkill { get; init; } = "SKILL";
    public string ThHits { get; init; } = "HITS";
    public string ThDamage { get; init; } = "DAMAGE";
    public string ThDps { get; init; } = "DPS";
    public string ThAvg { get; init; } = "AVG";
    public string ThMax { get; init; } = "MAX";
    public string ThCrit { get; init; } = "CRIT";
    public string ThShare { get; init; } = "SHARE";
    public string ThBack { get; init; } = "BACK";
    public string ThFront { get; init; } = "FRONT";
    public string ThPerfect { get; init; } = "PERFECT";
    public string ThSmite { get; init; } = "SMITE";
    public string ThMulti { get; init; } = "MULTI";
    public string SortTip { get; init; } = "Sort by this column · click again to reverse";
    public string QHitsMeasured { get; init; } = "{0} hits measured";
    public string QPositional { get; init; } = "positional";
    public string QExtraHits { get; init; } = "extra hits";
    public string QBiggestHit { get; init; } = "BIGGEST HIT";
    public string DefTaken { get; init; } = "DAMAGE TAKEN";
    public string DefTakenPerSec { get; init; } = "TAKEN / S";
    public string DefDotTicks { get; init; } = "DOT TICKS";
    public string DefHits { get; init; } = "{0} hits";
    public string DefDot { get; init; } = "damage over time";
    public string DefNote { get; init; } = "Damage taken counts hits from NPCs while this fight was running. Block / parry / evade details depend on what the server reports.";
    public string RotationHint { get; init; } = "Every cast in order. Gold ring = critical hit · tile size follows damage · DoT ticks hidden.";
    public string You { get; init; } = "you";
    public string PartyDpsSuffix { get; init; } = "party";
    public string TipCopyImage { get; init; } = "Copy as image";
    public string TipMinimize { get; init; } = "Minimize";
    public string TipClose { get; init; } = "Close";

    // ---------------------------------------------------------------- history window
    public string YourFights { get; init; } = "Your fights";
    public string SavedCount { get; init; } = "{0} saved";
    public string ShownOf { get; init; } = "{0} of {1}";
    public string FilterAll { get; init; } = "All";
    public string FilterBosses { get; init; } = "Bosses";
    public string FilterKills { get; init; } = "Kills";
    public string ShowInOverlay { get; init; } = "Show in overlay";
    public string TipShowInOverlay { get; init; } = "Show this fight in the overlay (live returns with the next pull)";
    public string HistoryEmpty { get; init; } = "Kill a boss and it lands here automatically.";
    public string PlayersShort { get; init; } = "pl.";
    public string JustNow { get; init; } = "just now";
    public string MinutesAgo { get; init; } = "{0}m ago";
    public string HoursAgo { get; init; } = "{0}h ago";
    public string DeleteConfirm { get; init; } = "Delete \"{0}\" ({1})?";
    public string TipRefresh { get; init; } = "Refresh";
    public string TipOpenFolder { get; init; } = "Open folder";
    public string TipDelete { get; init; } = "Delete selected fight";
    public string TipOpenBreakdown { get; init; } = "Open the full breakdown";
    public string DamageWord { get; init; } = "damage";

    // ---------------------------------------------------------------- settings window
    public string SettingsTitle { get; init; } = "Settings";
    public string SecLanguage { get; init; } = "LANGUAGE";
    public string LanguageHint { get; init; } = "Interface, skill and monster names. Applied immediately.";
    public string SecOverlay { get; init; } = "OVERLAY";
    public string BarsRelative { get; init; } = "Bars relative to the top player";
    public string BarsRelativeHint { get; init; } = "The leader's bar is full and everyone else is sized against it. Off: bars show share of party damage.";
    public string ShowBossPanel { get; init; } = "Show boss HP bar";
    public string ShowGear { get; init; } = "Show GS / CP after names";
    public string ShowGearHint { get; init; } =
        "Gear score and combat power come with the party roster: your party members and you. Other players have none to show. " +
        "On a narrow overlay the columns step aside so the fight's numbers fit: widen it or make the rows smaller.";
    public string RowsShown { get; init; } = "Rows shown";
    public string BackgroundOpacity { get; init; } = "Background opacity";
    public string RowSize { get; init; } = "Row size";
    public string RowSizeHint { get; init; } = "Also right on the overlay: Ctrl + mouse wheel over the rows.";
    public string SecMeter { get; init; } = "METER";
    public string Measure { get; init; } = "Measure";
    public string MeasureBoss { get; init; } = "Boss only (falls back to all)";
    public string MeasureAll { get; init; } = "Everything you hit";
    public string IdleEnd { get; init; } = "End fight after idle (s)";
    public string IdleBoss { get; init; } = "… while boss alive (s)";
    public string SecHistory { get; init; } = "FIGHT HISTORY";
    public string SaveAuto { get; init; } = "Save fights automatically";
    public string WhichFights { get; init; } = "Which fights";
    public string HistBosses { get; init; } = "Bosses only";
    public string HistBossesLong { get; init; } = "Bosses + longer fights";
    public string HistAll { get; init; } = "Everything (5 s and longer)";
    public string LongerThan { get; init; } = "Longer than (s)";
    public string HistoryHint { get; init; } = "Saved fights stay pickable in the overlay (Fights menu or mouse wheel over the title) and in Fight history.";
    public string HistoryInfo { get; init; } = "{0} fights saved in {1}";
    public string SecCapture { get; init; } = "CAPTURE";
    public string NetworkAdapter { get; init; } = "Network adapter";
    public string AdapterAuto { get; init; } = "Automatic (recommended)";
    public string AdapterHint { get; init; } = "Automatic finds the adapter that carries the AION 2 connection (loopback for ping accelerators). Pick one only if your VPN hides it.";
    public string RecordPackets { get; init; } = "Record game packets to .pcap (for debugging the parser)";
    public string RecordHint { get; init; } = "Recordings contain your own game traffic only, including character names. Keep them private.";
    public string StatusPrefix { get; init; } = "Status: ";
    public string NpcapMissing { get; init; } = "Npcap not installed";
    public string NpcapNeeded { get; init; } =
        "AION2 DPS Meter reads the game's own network traffic through Npcap, a free capture driver — and it is not installed yet.\n\n" +
        "Install it from npcap.com with the default options (ExitLag and other ping boosters work with them too), then start the meter again.\n\n" +
        "Open the download page now?";
    public string SecHotkeys { get; init; } = "HOTKEYS";
    public string HkToggle { get; init; } = "Show / hide overlay";
    public string HkReset { get; init; } = "Restart meter";
    public string HkClick { get; init; } = "Click-through";
    public string HotkeysHint { get; init; } = "Format: Ctrl+Shift+D, Alt+F9 … Applied after saving.";
    public string SecData { get; init; } = "DATA";
    public string DownloadIcons { get; init; } = "Download skill icons and boss portraits";
    public string IconsHint { get; init; } = "Skill icons: official AION 2 CDN (assets.playnccdn.com). Boss portraits: MetaBot.GG (metabot.gg). Each picture is fetched once, then cached.";
    public string DataInfo { get; init; } = "Skill names: {0:#,0} · NPCs: {1:#,0} ({2:#,0} bosses) · DoT list: {3:#,0} · Maps: {4:#,0}\nTables come from GPL-3.0 projects (see data/NOTICE.md). skills.json / npcs.json in the data folder override them.";
    public string OpenDataFolder { get; init; } = "Open data folder";
    public string OpenAppFolder { get; init; } = "Open app folder";
    public string Logs { get; init; } = "Logs";
    public string Disclaimer { get; init; } = "AION2 DPS Meter only listens to your own network connection. It never reads game memory, injects anything or sends a byte to the game. Third-party tools may still be against the game's terms of service — use at your own risk.";
    public string Cancel { get; init; } = "Cancel";
    public string Save { get; init; } = "Save";

    // Updates
    public string UpdateTitle { get; init; } = "Update available";
    public string UpdateHeadline { get; init; } = "AION2 DPS Meter {0} is out";
    public string UpdateYouHave { get; init; } = "You have {0}.";
    public string UpdateReleased { get; init; } = "You have {0}. The new one was released {1}.";
    public string UpdateWhatsNew { get; init; } = "WHAT'S NEW";
    public string UpdateInstalledHint { get; init; } = "The meter closes, the installer updates it and starts it again. Settings, fight history and timers stay.";
    public string UpdatePortableHint { get; init; } = "This copy was unzipped by hand, so it cannot replace itself: download the new zip and unzip it over this folder, or switch to the installer.";
    public string UpdateNow { get; init; } = "Update";
    public string UpdateOpenPage { get; init; } = "Open download page";
    public string UpdateLater { get; init; } = "Later";
    public string UpdateSkip { get; init; } = "Skip this version";
    public string UpdateDownloading { get; init; } = "Downloading the installer… {0:0}%";
    public string UpdateStarting { get; init; } = "Starting the installer…";
    public string UpdateFailed { get; init; } = "Could not update: {0}. The new version can be downloaded from the release page.";
    public string UpdateBanner { get; init; } = "Version {0} is out — click to update";
    public string TipUpdate { get; init; } = "What's new, and update in one click";
    public string UpdateBalloon { get; init; } = "Version {0} is out. Click here to update.";
    public string UpdatedBalloon { get; init; } = "Updated to version {0}.";
    public string TrayUpdate { get; init; } = "Update to {0}…";
    public string TrayCheckUpdates { get; init; } = "Check for updates";
    public string TrayAtlas { get; init; } = "Atlas (nodes map)";
    public string TrayTop { get; init; } = "Top DPS per class";
    public string TopTitle { get; init; } = "Top DPS";
    public string PricesTitle { get; init; } = "Prices";
    public string AtlasPets { get; init; } = "Pets";
    public string AtlasPetSpawns { get; init; } = "{0} spawns on this map";
    public string AtlasPetLine { get; init; } = "Pet {0}: level {1}/3 · {2} souls per level";
    public string AtlasPetAlso { get; init; } = "Also drops from:";
    public string AtlasPetZones { get; init; } = "Zones:";
    public string AtlasPetsTip { get; init; } = "Spawns of the species whose pet is still below level 3: red = not owned, orange = level 1, yellow = level 2 ({0} pets below 3, {1} with spawns on this map)";
    public string CraftTitle { get; init; } = "Craft";
    public string TrayCraft { get; init; } = "What this character can craft now";
    public string CraftSearch { get; init; } = "search a recipe…";
    public string CraftAll { get; init; } = "All";
    public string CraftShowNear { get; init; } = "also short by 1–2 materials";
    public string CraftMissing { get; init; } = "missing";
    public string CraftStatus { get; init; } = "{0} craftable now · {1} almost";
    public string PopOut { get; init; } = "Open this tab in another card (the copy has an X to close it)";
    public string CloseCopy { get; init; } = "Close this card";
    public string TrayPrices { get; init; } = "Price watch (aion2.exchange, once an hour)";
    public string PricesHint { get; init; } = "Items to buy below a price. aion2.exchange is read once an hour per item; a Windows notification and a chime fire while the lowest listing is at or under your maximum. The globe opens the item on the site.";
    public string PriceHit { get; init; } = "{0}: {1:N0} Kinah (≤ {2:N0}), {3} listings";
    public string PriceHitSell { get; init; } = "{0}: {1:N0} Kinah (≥ {2:N0}) — time to sell, {3} listings";
    public string PricesSell { get; init; } = "sell above";
    public string PricesQty { get; init; } = "units held";
    public string PricesPaid { get; init; } = "paid each";
    public string PricesCopyMarket { get; init; } = "Copy market script";
    public string PricesCopyItems { get; init; } = "Copy watched-items script";
    public string PricesCopyHint { get; init; } = "Script copied. In Brave: open aion2.exchange, F12 → Console, paste, Enter. The JSON lands in Downloads.";
    public string PricesCopyTip { get; init; } = "Browser-console script that exports the whole market of your server (one request per 400 ms) from your own session; the watched-items one reads just these items on every server.";
    public string PricesAdd { get; init; } = "Add";
    public string PricesCheckNow { get; init; } = "Check now";
    public string PricesItemId { get; init; } = "item id";
    public string PricesMax { get; init; } = "max price";
    public string PricesNotChecked { get; init; } = "not checked yet";
    public string PricesLastPoll { get; init; } = "last check {0}";
    public string PricesSearch { get; init; } = "search an item by name…";
    public string PricesSearchLang { get; init; } = "Search language";
    public string TopCount { get; init; } = "{0} players kept";
    public string TopHint { get; init; } = "The ten best DPS this meter has seen per class (one fight per player, 30 s or longer). Click a row for skill shares, opener, repeated sequences and casts per minute; Open shows the saved fight.";
    public string TopEmpty { get; init; } = "No fight of 30 s or more with a known class yet. The ranking fills itself as fights finish.";
    public string TopOpenFight { get; init; } = "Open";
    public string TopSkills { get; init; } = "DAMAGE SHARE";
    public string TopOpening { get; init; } = "OPENER (first 16 casts)";
    public string TopRepeated { get; init; } = "MOST REPEATED 3-CAST SEQUENCES";
    public string TopCadence { get; init; } = "CASTS PER MINUTE";
    public string TopProfile { get; init; } = "PROFILE (official character page)";
    public string EventMenuNotify { get; init; } = "Windows notification";
    public string AtlasZonesTip { get; init; } = "{0}: outline its 5 densest areas (up to two kinds at once)";
    public string EventMenuSound { get; init; } = "Sound";
    public string EventMenuAt10 { get; init; } = "Alert 10 min before";
    public string EventMenuSyncNow { get; init; } = "Starts now (sync the cycle)";
    public string EventMenuResetSync { get; init; } = "Back to the schedule";
    public string EventMenuAt5 { get; init; } = "Alert 5 min before";
    public string EventStartsAt { get; init; } = "{0} starts at {1} ({2} min)";
    public string TopScopeHistory { get; init; } = "My fights";
    public string TopScopeServer { get; init; } = "Server {0}";
    public string TopScopeGlobal { get; init; } = "Europe";
    public string TopBoardLoading { get; init; } = "Reading the ranking pages (aion2index.com, one per 400 ms)…";
    public string TopBoardHint { get; init; } = "Strongest characters by combat power (aion2index.com, refreshed daily; the game publishes no DPS ranking). Load profile shows their official gear, Daevanion and skill levels to compare with yours.";
    public string TopBoardFailed { get; init; } = "The ranking pages did not answer: {0}";
    public string TopProfileFetch { get; init; } = "Load profile";
    public string TopProfileLoading { get; init; } = "Loading…";
    public string TopProfileMissing { get; init; } = "Not found on the official character search (renamed, deleted or another server).";
    public string TopProfileFailed { get; init; } = "The character page did not answer: {0}";
    public string AtlasTitle { get; init; } = "Atlas";
    public string AtlasNoMap { get; init; } = "No calibrated map for zone {0}. Nodes are still being recorded.";
    public string AtlasFollow { get; init; } = "Follow me";
    public string NameNodeTitle { get; init; } = "New kind of node";
    public string NameNodeQuestion { get; init; } = "What are you gathering? (object {0}) — the atlas will use this name for every node of this kind.";
    public string NameNodeName { get; init; } = "Name";
    public string NameNodeCategory { get; init; } = "Category";
    public string NameNodeSave { get; init; } = "Save";
    public string NameNodeSkip { get; init; } = "Close";
    public string AtlasHint { get; init; } = "Drag to pan, wheel to zoom, right-click a node to name its kind. Green = node in view (units left), grey = remembered (back in … when learned), skull = field boss.";
    public string SecUpdates { get; init; } = "UPDATES";
    public string CheckUpdatesAuto { get; init; } = "Check GitHub for new versions";
    public string UpdatesHint { get; init; } = "Shortly after start and every 2 hours. The meter only asks GitHub which version is the newest; nothing about you or your fights is sent.";
    public string AutoInstallUpdates { get; init; } = "Install updates by itself";
    public string AutoInstallHint { get; init; } = "A new version is downloaded in the background and installed when you are not playing (or after 10 minutes without a fight): the meter restarts by itself, without any window. Installed copies only.";
    public string UpdateBannerReady { get; init; } = "Update {0} is ready — it installs itself when you are not playing";
    public string UpdateReadyBalloon { get; init; } = "Version {0} is downloaded and installs itself when you are not playing. Click here to install it now.";
    public string VersionInstalled { get; init; } = "Version {0} · installed";
    public string VersionPortable { get; init; } = "Version {0} · portable (zip)";
    public string CheckNow { get; init; } = "Check now";
    public string UpdateChecking { get; init; } = "Checking…";
    public string UpToDate { get; init; } = "You have the newest version.";
    public string UpdateFound { get; init; } = "Version {0} is out.";
    public string UpdateCheckFailed { get; init; } = "Could not reach GitHub: {0}";

    private static readonly UiText Ru = new()
    {
        Code = "ru",
        LanguageName = "Русский",
        ClassNames = new Dictionary<Core.Events.GameClass, string>
        {
            [Core.Events.GameClass.Gladiator] = "Гладиатор",
            [Core.Events.GameClass.Templar] = "Страж",
            [Core.Events.GameClass.Ranger] = "Стрелок",
            [Core.Events.GameClass.Assassin] = "Убийца",
            [Core.Events.GameClass.Sorcerer] = "Волшебник",
            [Core.Events.GameClass.Elementalist] = "Заклинатель",
            [Core.Events.GameClass.Cleric] = "Целитель",
            [Core.Events.GameClass.Chanter] = "Чародей",
            [Core.Events.GameClass.Brawler] = "Боец",
        },
        FlagNames = new Dictionary<Core.Events.HitFlags, string>
        {
            [Core.Events.HitFlags.Critical] = "Крит",
            [Core.Events.HitFlags.Back] = "Со спины",
            [Core.Events.HitFlags.Front] = "Спереди",
            [Core.Events.HitFlags.Perfect] = "Идеальный",
            [Core.Events.HitFlags.Heavy] = "Сокрушение",
            [Core.Events.HitFlags.Multi] = "Доп. удары",
            [Core.Events.HitFlags.Double] = "Двойной",
            [Core.Events.HitFlags.Block] = "Блок",
            [Core.Events.HitFlags.Parry] = "Парирование",
            [Core.Events.HitFlags.Endure] = "Стойкость",
            [Core.Events.HitFlags.Evade] = "Уклонение",
            [Core.Events.HitFlags.Dot] = "DoT",
        },
        UnknownSummons = "Призывы (владелец неизвестен)",
        UnknownSummonsTip = "Питомцы, духи и эффекты умений (Леденящий ветер, Стена огня …), хозяина которых определить не удалось. Это не игрок: в местах и числе игроков не учитывается.",

        TimersTitle = "Таймеры боссов",
        TimersButton = "Таймеры боссов",
        HkTimers = "Таймеры боссов",
        HkAtlas = "Атлас",
        TipTimers = "Таймеры респауна боссов",
        TrayTimers = "Таймеры боссов…",
        TimersHint = "Откройте в игре список полевых боссов карты (карта → Исследование → полевые монстры) — таймеры придут прямо с сервера. Убийство полевого босса рядом с вами перезапускает его отсчёт. У боссов квестовых и запечатанных инстансов нет общего таймера — их здесь нет. 🔔 — оповещать перед респауном.",
        FilterWatched = "🔔 Отслеживаемые",
        TipWatch = "Оповещать перед респауном этого босса",
        UnknownSlotBoss = "Полевой босс №{0}",
        SourceGame = "По списку боссов в игре (обновлено в {0})",
        SourceMeter = "Посчитано метром (по убийствам, которые он видел)",
        RespawnFromGame = "из игры",
        TimerAliveSince = "с {0}",
        TimerNoTime = "в игре нет таймера",
        TipServer = "На каждом сервере свои боссы: здесь таймеры этого сервера (вашего персонажа, если не выбран другой)",
        ServerUnknown = "сервер ?",
        ServerYours = "ваш персонаж",
        MapNumber = "Карта {0}",
        TimersEmpty = "Таймеров пока нет. Убейте полевого босса (или увидьте, как его убили) — здесь начнётся отсчёт.",
        TimerAlive = "ЖИВ",
        TimerDue = "ДОЛЖЕН БЫТЬ",
        TimerSeen = "замечен {0}",
        TimerSince = "с {0}",
        TimerAt = "в {0}",
        TimerKilled = "убит {0}",
        TimerFoundDead = "найден мёртвым {0}",
        TimerNeedsInterval = "задайте респаун",
        TimerLearned = "Измерено метром: самое короткое время от убийства до появления босса",
        TipRespawn = "Время респауна после убийства",
        RespawnUnknown = "респаун ?",
        BtnKilled = "Убит",
        TipKilled = "Убит только что — запустить отсчёт",
        TipRemoveTimer = "Убрать из списка",
        AlertLabel = "Оповещать до респауна",
        AlertOff = "выкл",
        AlertSoon = "{0} появится примерно через {1} мин ({2})",
        AlertNow = "{0} уже должен появиться ({1})",
        HoursShort = "ч",
        MinutesShort = "мин",

        AutoShow = "Показывать сам, когда начинается бой",
        AutoShowHint = "Когда рядом бьют босса или вы ударили любого моба. Если скрыть его во время боя — появится в следующем.",
        AutoHide = "Скрывать после боя через (с, 0 — нет)",
        ShowOnStart = "Показывать оверлей при запуске метра и игры",

        TipSkillDamage = "Урон",
        TipSkillHits = "Удары",
        TipSkillDotTicks = "Тики DoT",
        TipSkillMin = "Мин",
        TipSkillAvg = "Сред",
        TipSkillMax = "Макс",

        ColPlayer = "ИГРОК",
        ColDps = "УРОН/С",
        ColDamage = "ВСЕГО",
        ColShare = "ДОЛЯ",
        TipPlace = "Место в бою по нанесённому урону",
        TipDps = "Урон в секунду за время боя (DPS)",
        TipDamage = "Весь урон, нанесённый за бой",
        TipShare = "Доля от урона всей группы",
        TipGs = "Уровень экипировки (GS), из состава группы",
        TipCp = "Боевая мощь (CP), из состава группы",

        LiveFight = "идёт бой",
        LastFight = "последний бой",
        SavedFight = "сохранённый бой",
        ThisSession = "этот сеанс",
        WaitingLabel = "ожидание боя",
        WaitingTitle = "Ожидание боя",
        WaitingDetail = "Атакуйте цель — метр запустится сам",
        EmptyParty = "Группа появится здесь после первого удара.",
        TopHit = "МАКС. УДАР",
        TopHitBy = "Самый сильный удар — {0}",
        TipScrollFights = "Колесо мыши здесь: листать бои",
        Defeated = "Повержен",
        UnknownBoss = "Неизвестный босс",
        CountedFrom = "учтено с {0} HP",
        HpUnknown = "макс. HP неизвестен",
        TipRestart = "Перезапустить метр ({0}): бой сохранится, подсчёт начнётся заново",
        InProgress = "Идёт бой",
        Kill = "Убит",
        Wipe = "Вайп",
        Ended = "Завершён",
        OpenWorld = "Открытый мир",
        OpenFight = "Разбор боя",
        Fights = "Бои",
        NamesLater = "ники появятся после загрузки локации",
        YourPlace = "Ваше место в этом бою",
        Party = "группа",
        Seconds = "с",
        SwitchLanguage = "Язык: Русский → English",

        TrayHide = "Скрыть оверлей",
        TrayShow = "Показать оверлей",
        TrayHistory = "История боёв…",
        TraySettings = "Настройки…",
        TrayDemo = "Демо-бой",
        TrayReplay = "Воспроизвести запись…",
        TrayExit = "Выход",
        ClickThroughOn = "Клики сквозь оверлей ВКЛ — он не реагирует на мышь. {0} — выключить.",
        ClickThroughOff = "Клики сквозь оверлей ВЫКЛ",

        Live = "Текущий бой",
        LiveHint = "следит за боем в реальном времени",
        SessionSection = "ЭТОТ СЕАНС",
        SavedToday = "СОХРАНЕНО · СЕГОДНЯ",
        SavedYesterday = "СОХРАНЕНО · ВЧЕРА",
        SavedOn = "СОХРАНЕНО · {0}",
        AllSaved = "Все сохранённые бои ({0})…",
        NoFights = "Боёв пока нет — они сохраняются автоматически",
        OpenBreakdown = "Открыть разбор",
        CopyPlayer = "Скопировать результат {0} в чат",
        CopyParty = "Скопировать рейтинг группы в чат",
        CopyTable = "Скопировать таблицу группы для Discord",
        Copied = "Скопировано — вставьте в чат: Ctrl+V",
        CopyFailed = "Буфер обмена занят — попробуйте ещё раз",
        TipChat = "Результат в чат или Discord",
        Chat = new Core.ChatWords
        {
            Kill = "УБИТ",
            Wipe = "ВАЙП",
            BossLeft = "босс {0}",
            Party = "группа {0}",
            Place = "{0}-й из {1}",
            Damage = "урон {0}",
            Crit = "крит {0}",
            TopHit = "макс. удар {0}",
            More = "+ещё {0}",
            ColPlayer = "Игрок",
            ColDps = "DPS",
            ColDamage = "Урон",
            ColShare = "Доля",
        },
        RowDamage = "Урон",
        RowHits = "Удары",
        RowCrit = "Крит",
        RowMax = "Макс",
        Players = n => $"{n} {RuPlural(n, "игрок", "игрока", "игроков")}",

        TabDps = "Урон",
        TabAccuracy = "Точность",
        TabRotation = "Ротация",
        TabDefense = "Защита",
        TilePlace = "МЕСТО",
        TileDamage = "УРОН",
        TileDps = "DPS",
        TileContrib = "ВКЛАД",
        TileTime = "ВРЕМЯ",
        DpsTimeline = "DPS ПО ВРЕМЕНИ",
        ThSkill = "УМЕНИЕ",
        ThHits = "УДАРЫ",
        ThDamage = "УРОН",
        ThDps = "DPS",
        ThAvg = "СРЕДН.",
        ThMax = "МАКС.",
        ThCrit = "КРИТ",
        ThShare = "ДОЛЯ",
        ThBack = "СПИНА",
        ThFront = "ФРОНТ",
        ThPerfect = "ИДЕАЛ",
        ThSmite = "СОКРУШ.",
        ThMulti = "МУЛЬТИ",
        SortTip = "Сортировать по этой колонке · ещё щелчок — в обратном порядке",
        QHitsMeasured = "ударов учтено: {0}",
        QPositional = "по позиции",
        QExtraHits = "доп. удары",
        QBiggestHit = "МАКС. УДАР",
        DefTaken = "ПОЛУЧЕНО УРОНА",
        DefTakenPerSec = "ПОЛУЧЕНО / С",
        DefDotTicks = "ТИКИ DOT",
        DefHits = "ударов: {0}",
        DefDot = "периодический урон",
        DefNote = "Полученный урон — удары от NPC за время этого боя. Детали блока / парирования / уклонения зависят от того, что присылает сервер.",
        RotationHint = "Все применения по порядку. Золотое кольцо — крит · размер плитки зависит от урона · тики DoT скрыты.",
        You = "вы",
        PartyDpsSuffix = "группа",
        TipCopyImage = "Скопировать картинкой",
        TipMinimize = "Свернуть",
        TipClose = "Закрыть",

        YourFights = "Ваши бои",
        SavedCount = "сохранено: {0}",
        ShownOf = "{0} из {1}",
        FilterAll = "Все",
        FilterBosses = "Боссы",
        FilterKills = "Убийства",
        ShowInOverlay = "Показать в оверлее",
        TipShowInOverlay = "Показать этот бой в оверлее (с началом нового боя вернётся живой режим)",
        HistoryEmpty = "Убейте босса — бой сохранится здесь автоматически.",
        PlayersShort = "игр.",
        JustNow = "только что",
        MinutesAgo = "{0} мин назад",
        HoursAgo = "{0} ч назад",
        DeleteConfirm = "Удалить «{0}» ({1})?",
        TipRefresh = "Обновить",
        TipOpenFolder = "Открыть папку",
        TipDelete = "Удалить выбранный бой",
        TipOpenBreakdown = "Открыть полный разбор",
        DamageWord = "урона",

        SettingsTitle = "Настройки",
        SecLanguage = "ЯЗЫК",
        LanguageHint = "Интерфейс, названия умений и монстров. Применяется сразу.",
        SecOverlay = "ОВЕРЛЕЙ",
        BarsRelative = "Полосы относительно лидера",
        BarsRelativeHint = "Полоса лидера полная, остальные — пропорционально ему. Выключено: полосы показывают долю урона группы.",
        ShowBossPanel = "Показывать полосу HP босса",
        ShowGear = "Показывать GS / CP после ников",
        ShowGearHint = "GS и CP сервер присылает в составе группы: они есть у членов вашей группы и у вас. У остальных игроков их нет. " +
                       "На узком оверлее колонки прячутся, чтобы влезли цифры боя: сделайте оверлей шире или строки меньше.",
        RowsShown = "Строк в списке",
        BackgroundOpacity = "Плотность фона",
        RowSize = "Размер строк",
        RowSizeHint = "Или прямо на оверлее: Ctrl + колесо мыши над строками.",
        SecMeter = "МЕТР",
        Measure = "Считать",
        MeasureBoss = "Только босса (иначе всё)",
        MeasureAll = "Всё, по чему бьёте",
        IdleEnd = "Конец боя после простоя (с)",
        IdleBoss = "… пока босс жив (с)",
        SecHistory = "ИСТОРИЯ БОЁВ",
        SaveAuto = "Сохранять бои автоматически",
        WhichFights = "Какие бои",
        HistBosses = "Только боссы",
        HistBossesLong = "Боссы + длинные бои",
        HistAll = "Все (от 5 с)",
        LongerThan = "Длиннее (с)",
        HistoryHint = "Сохранённые бои можно выбрать в оверлее (меню «Бои» или колесо мыши над названием) и в окне истории.",
        HistoryInfo = "Сохранено боёв: {0} — {1}",
        SecCapture = "ЗАХВАТ",
        NetworkAdapter = "Сетевой адаптер",
        AdapterAuto = "Автоматически (рекомендуется)",
        AdapterHint = "Автоматически выбирается адаптер с соединением AION 2 (loopback для ускорителей пинга). Выбирайте вручную, только если VPN его скрывает.",
        RecordPackets = "Записывать игровые пакеты в .pcap (для отладки)",
        RecordHint = "В записи только ваш игровой трафик, включая ники. Не публикуйте их.",
        StatusPrefix = "Статус: ",
        NpcapMissing = "Npcap не установлен",
        NpcapNeeded =
            "AION2 DPS Meter читает сетевой трафик игры через Npcap — бесплатный драйвер захвата, а он ещё не установлен.\n\n" +
            "Установите его с npcap.com с настройками по умолчанию (с ними работают и ExitLag, и другие ускорители пинга), затем запустите метр снова.\n\n" +
            "Открыть страницу загрузки?",
        SecHotkeys = "ГОРЯЧИЕ КЛАВИШИ",
        HkToggle = "Показать / скрыть оверлей",
        HkReset = "Перезапустить метр",
        HkClick = "Клики сквозь оверлей",
        HotkeysHint = "Формат: Ctrl+Shift+D, Alt+F9 … Применяется после сохранения.",
        SecData = "ДАННЫЕ",
        DownloadIcons = "Скачивать иконки умений и портреты боссов",
        IconsHint = "Иконки умений — официальный CDN AION 2 (assets.playnccdn.com), портреты боссов — MetaBot.GG (metabot.gg). Каждая картинка скачивается один раз, затем берётся из кэша.",
        DataInfo = "Умений: {0:#,0} · NPC: {1:#,0} (боссов: {2:#,0}) · DoT: {3:#,0} · Карт: {4:#,0}\nТаблицы взяты из проектов под GPL-3.0 (см. data/NOTICE.md). skills.json / npcs.json в папке data их переопределяют.",
        OpenDataFolder = "Папка данных",
        OpenAppFolder = "Папка программы",
        Logs = "Логи",
        Disclaimer = "AION2 DPS Meter только слушает ваше собственное сетевое соединение. Он не читает память игры, ничего не внедряет и не отправляет в игру ни байта. Тем не менее сторонние программы могут нарушать правила игры — используйте на свой риск.",
        Cancel = "Отмена",
        Save = "Сохранить",
        UpdateTitle = "Доступно обновление",
        UpdateHeadline = "Вышла версия AION2 DPS Meter {0}",
        UpdateYouHave = "У вас {0}.",
        UpdateReleased = "У вас {0}. Новая выпущена {1}.",
        UpdateWhatsNew = "ЧТО НОВОГО",
        UpdateInstalledHint = "Метр закроется, установщик обновит его и запустит снова. Настройки, история боёв и таймеры сохранятся.",
        UpdatePortableHint = "Эта копия распакована из zip и не может заменить себя сама: скачайте новый архив и распакуйте его поверх этой папки или перейдите на установщик.",
        UpdateNow = "Обновить",
        UpdateOpenPage = "Открыть страницу загрузки",
        UpdateLater = "Позже",
        UpdateSkip = "Пропустить эту версию",
        UpdateDownloading = "Скачивание установщика… {0:0}%",
        UpdateStarting = "Запуск установщика…",
        UpdateFailed = "Не удалось обновить: {0}. Новую версию можно скачать со страницы релиза.",
        UpdateBanner = "Вышла версия {0} — нажмите, чтобы обновить",
        TipUpdate = "Что нового — и обновление в один клик",
        UpdateBalloon = "Вышла версия {0}. Нажмите сюда, чтобы обновить.",
        UpdatedBalloon = "Метр обновлён до версии {0}.",
        TrayUpdate = "Обновить до {0}…",
        TrayCheckUpdates = "Проверить обновления",
        TrayAtlas = "Атлас (карта ресурсов)",
        AtlasTitle = "Атлас",
        PricesTitle = "Цены",
        AtlasPets = "Питомцы",
        AtlasPetSpawns = "{0} точек появления на этой карте",
        AtlasPetLine = "Питомец {0}: уровень {1}/3 · {2} душ на уровень",
        AtlasPetAlso = "Также выпадает с:",
        AtlasPetZones = "Зоны:",
        AtlasPetsTip = "Точки появления монстров, чьи души питомцев ещё не собраны ({0} не хватает, {1} с точками на этой карте)",
        CraftTitle = "Крафт",
        TrayCraft = "Что этот персонаж может скрафтить сейчас",
        CraftSearch = "поиск рецепта…",
        CraftAll = "Все",
        CraftShowNear = "и те, где не хватает 1–2 материалов",
        CraftMissing = "не хватает",
        CraftStatus = "{0} можно скрафтить · {1} почти",
        PopOut = "Открыть эту вкладку в отдельной карточке (у копии есть крестик)",
        CloseCopy = "Закрыть эту карточку",
        TrayPrices = "Слежение за ценами (aion2.exchange, раз в час)",
        PricesHint = "Предметы, которые нужно купить дешевле заданной цены. aion2.exchange читается раз в час на предмет; уведомление Windows и сигнал звучат, пока минимальный лот не выше вашего максимума. Глобус открывает предмет на сайте.",
        PriceHit = "{0}: {1:N0} кинар (≤ {2:N0}), лотов: {3}",
        PriceHitSell = "{0}: {1:N0} кинар (≥ {2:N0}) — пора продавать, лотов: {3}",
        PricesSell = "продать выше",
        PricesQty = "штук в наличии",
        PricesPaid = "цена покупки",
        PricesCopyMarket = "Скопировать скрипт рынка",
        PricesCopyItems = "Скопировать скрипт по предметам",
        PricesCopyHint = "Скрипт скопирован. В браузере: открыть aion2.exchange, F12 → Console, вставить, Enter. JSON сохранится в «Загрузки».",
        PricesCopyTip = "Скрипт для консоли браузера: выгружает весь рынок вашего сервера (один запрос в 400 мс) из вашей сессии; второй читает только отслеживаемые предметы на всех серверах.",
        PricesAdd = "Добавить",
        PricesCheckNow = "Проверить сейчас",
        PricesItemId = "id предмета",
        PricesMax = "макс. цена",
        PricesNotChecked = "ещё не проверялось",
        PricesLastPoll = "последняя проверка {0}",
        PricesSearch = "поиск предмета по названию…",
        PricesSearchLang = "Язык поиска",
        AtlasNoMap = "Для зоны {0} нет откалиброванной карты. Ресурсы всё равно записываются.",
        AtlasFollow = "Следовать за мной",
        NameNodeTitle = "Новый вид ресурса",
        NameNodeQuestion = "Что вы добываете? (объект {0}) — атлас будет использовать это имя для всех таких ресурсов.",
        NameNodeName = "Название",
        NameNodeCategory = "Категория",
        NameNodeSave = "Сохранить",
        NameNodeSkip = "Закрыть",
        AtlasHint = "Перетаскивание — сдвиг, колесо — масштаб. Зелёный = ресурс в поле зрения (осталось единиц), серый = запомненный, череп = полевой босс.",
        SecUpdates = "ОБНОВЛЕНИЯ",
        CheckUpdatesAuto = "Проверять новые версии на GitHub",
        UpdatesHint = "Вскоре после запуска и раз в 2 часа. Метр только спрашивает у GitHub номер новой версии — ничего о вас и ваших боях не отправляется.",
        AutoInstallUpdates = "Устанавливать обновления самому",
        AutoInstallHint = "Новая версия скачивается в фоне и ставится, когда вы не в игре (или после 10 минут без боя): метр перезапустится сам, без всяких окон. Только для установленной версии.",
        UpdateBannerReady = "Обновление {0} готово — установится само, когда вы не в игре",
        UpdateReadyBalloon = "Версия {0} скачана и установится сама, когда вы не в игре. Нажмите сюда, чтобы установить сейчас.",
        VersionInstalled = "Версия {0} · установлена",
        VersionPortable = "Версия {0} · без установки (zip)",
        CheckNow = "Проверить сейчас",
        UpdateChecking = "Проверка…",
        UpToDate = "У вас последняя версия.",
        UpdateFound = "Вышла версия {0}.",
        UpdateCheckFailed = "Не удалось связаться с GitHub: {0}",
    };

    private static string RuPlural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }
}
