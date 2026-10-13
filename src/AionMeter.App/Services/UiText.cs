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
    public string WaitingTitle { get; init; } = "";   // the status on the right already says "waiting for combat" (Diego 2026-10-11: it showed twice)
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
    /// <summary>A world boss with a crowd: "444 attacking · damage of 53 sent" (see EncounterSnapshot.MostDamageUnsent).</summary>
    public string FightingCount { get; init; } = "{0} attacking";
    public string DamageSentBy { get; init; } = "damage of {0} sent";
    public string DamageUnsentTip { get; init; } =
        "On a world boss with a crowd the game sends your client the damage of only a few players, you among them. For everyone else it sends that a hit landed, without the number, or their damage over time. So their numbers and every share here are far below the real ones; a meter on this PC cannot see more.";
    public string DamageUnsentLine(Core.Combat.EncounterSnapshot s) =>
        $"{string.Format(FightingCount, s.Fighting)} · {string.Format(DamageSentBy, s.DamageSenders)}";
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
    // A2M-38: Abyss Logs percentile after a boss kill. {0} percentile, {1} class group, {2} boss, {3} median DPS.
    public string AbyssLogsSetting { get; init; } = "After a boss kill, show where your DPS sits among your class this week (Abyss Logs)";
    public string AbyssLogsHint { get; init; } = "Asks api.abysslogs.com for that boss's weekly statistics: only the region and the boss are sent, nothing about you. Cached 6 h.";
    public string AbyssLogsTemplate { get; init; } = "p{0} of {1} on {2} this week · median {3}/s";
    public string AbyssLogsAboveTemplate { get; init; } = "Above every {1} logged on {2} this week · median {3}/s";
    /// <summary>{0} class name: "Templars".</summary>
    public string AbyssLogsClassGroup { get; init; } = "{0}s";
    /// <summary>{0} class group, {1} CP bracket: "Templars 100–150K CP".</summary>
    public string AbyssLogsBracket { get; init; } = "{0} {1} CP";

    public string AbyssLogsLine(Core.Ranking.AbyssLogsPercentile p, Core.Events.GameClass cls, string boss)
    {
        var group = string.Format(AbyssLogsClassGroup, ClassName(cls));
        if (!p.Cell.AllPower) group = string.Format(AbyssLogsBracket, group, $"{Core.Format.Compact(p.Cell.BracketFloor)}–{Core.Format.Compact(p.Cell.BracketCeil)}");
        return string.Format(p.AboveBest ? AbyssLogsAboveTemplate : AbyssLogsTemplate, p.Percentile, group, boss, Core.Format.Compact(p.Median));
    }
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
    public string UpdateInstalledHint { get; init; } = "The meter closes, the new files are copied over this folder and it starts again. Settings, fight history and timers stay.";
    public string UpdatePortableHint { get; init; } = "This is a developer build deployed by hand: it does not replace itself. Download the zip from the release page if you want it.";
    public string UpdateNow { get; init; } = "Update";
    public string UpdateOpenPage { get; init; } = "Open download page";
    public string UpdateLater { get; init; } = "Later";
    public string UpdateSkip { get; init; } = "Skip this version";
    public string UpdateDownloading { get; init; } = "Downloading the update… {0:0}%";
    public string UpdateStarting { get; init; } = "Applying the update…";
    public string UpstreamBalloon { get; init; } = "Upstream published {0}: re-audit and rebase the private build.";
    public string UpdateFailed { get; init; } = "Could not update: {0}. The new version can be downloaded from the release page.";
    public string UpdateBanner { get; init; } = "Version {0} is out — click to update";
    public string TipUpdate { get; init; } = "What's new, and update in one click";
    public string UpdateBalloon { get; init; } = "Version {0} is out. Click here to update.";
    public string UpdatedBalloon { get; init; } = "Updated to version {0}.";
    public string TrayUpdate { get; init; } = "Update to {0}…";
    public string TrayCheckUpdates { get; init; } = "Check for updates";
    public string TrayReport { get; init; } = "Report a problem…";
    public string HelpTitle { get; init; } = "Help";
    public string CharSelectTip { get; init; } = "Character whose bag, warehouse, builds and Craft counts the tabs show. Pick any saved character, also while playing: live data keeps going to the logged-in one, marked (current).";
    public string CharCurrentSuffix { get; init; } = " (current)";
    public string CharViewingTip { get; init; } = "Live data belongs to {0}; you are viewing {1}";
    public string CharViewingNobody { get; init; } = "nobody yet";
    public string TipHelp { get; init; } = "What this tab does and how to use it";
    public string HelpMissing { get; init; } = "No help page for this tab in this build.";
    public string ReportTitle { get; init; } = "Report a problem";
    public string ReportHint { get; init; } = "Describe what happened and what you expected. A zip with the last two logs, crash files, your settings (without private addresses), the state summary and the build info is sent to Diego (he gets a Telegram message), or saved for you to paste on Discord.";
    public string ReportDescribe { get; init; } = "What happened? What were you doing in the game?";
    public string ReportIncludeState { get; init; } = "Include my character state (inventory, titles, pets) — only if the problem is about those";
    public string ReportCreate { get; init; } = "Save zip only";
    public string ReportSend { get; init; } = "Send to Diego";
    public string ReportSending { get; init; } = "Sending…";
    public string ReportLimit { get; init; } = "Limit reached: up to 2 reports a minute, 5 an hour, 10 a day. Next send in {0}.";
    public string ReportSent { get; init; } = "Sent. Diego got it on Telegram (id {0}).";
    public string ReportSendFailed { get; init; } = "Could not send ({0}). The zip is saved and shown in Explorer: paste it on Discord.";
    public string ReportCreated { get; init; } = "Report saved: {0}";
    public string ReportFailed { get; init; } = "Could not create the report: {0}";
    public string ReportCrashNote { get; init; } = "The meter hit an error (see {0} in the report).\n\nWhat was happening: ";
    public string CrashBalloon { get; init; } = "The meter hit an error. Click here to create a report for Diego.";
    public string TrayAtlas { get; init; } = "Atlas (nodes map)";
    public string TrayTop { get; init; } = "Top DPS per class";
    public string TopTitle { get; init; } = "Top DPS";
    public string PricesTitle { get; init; } = "Prices";
    public string AtlasPets { get; init; } = "Pets";
    public string AtlasZoom { get; init; } = "Zoom";
    public string AtlasPetLevel { get; init; } = "level {0}/3";
    public string AtlasPetNeed { get; init; } = "{0} souls per level";
    public string AtlasPetSpawns { get; init; } = "{0} spawns on this map";
    public string AtlasPetLine { get; init; } = "Pet {0}: level {1}/3 · {2} souls per level";
    public string AtlasPetAlso { get; init; } = "Also drops from:";
    public string AtlasPetZones { get; init; } = "Zones:";
    public string AtlasPetsTip { get; init; } = "Spawns of the species whose pet is still below level 3: red = not owned, orange = level 1, yellow = level 2 ({0} pets below 3, {1} with spawns on this map)";
    public string CraftTitle { get; init; } = "Craft";
    public string TrayCraft { get; init; } = "What this character can craft now";
    public string CraftSearch { get; init; } = "search a recipe (English or Spanish)…";
    public string CraftClear { get; init; } = "Clear the search";
    public string CraftMatches { get; init; } = "{0} matches · {1} craftable now";
    public string CraftAll { get; init; } = "All";
    public string CraftShowNear { get; init; } = "also short by 1–2 materials";
    public string CraftOnlyNow { get; init; } = "Only what I can craft now";
    public string CraftScopeNow { get; init; } = "Only what I can craft now";
    public string CraftScopeNear { get; init; } = "Also short by 1–2 materials";
    public string CraftScopeAll { get; init; } = "All recipes";
    public string CraftFavTip { get; init; } = "Pin / unpin: pinned recipes always come first, in every mode";
    public string CraftOnlyNowTip { get; init; } = "On: recipes the current inventory covers completely. Off: also the ones short by one or two kinds of material, with what is missing and where it comes from.";
    public string CraftMissing { get; init; } = "missing";
    public string CraftVariant { get; init; } = "variant (other faction or route)";
    // ---- Items tab (A2M-29)
    public string ItemsTitle { get; init; } = "Items";
    public string TrayItems { get; init; } = "Items: bag, warehouses and the shared account warehouse of your characters";
    public string ItemsSearch { get; init; } = "Find an item (English or Spanish)…";
    public string ItemsLangTip { get; init; } = "Language of item names (the same setting as Craft and Prices)";
    public string ItemsShow { get; init; } = "Show";
    public string ItemsAll { get; init; } = "All";
    public string ItemsEquipment { get; init; } = "Equipment";
    public string ItemsMaterials { get; init; } = "Materials";
    public string ItemsConsumables { get; init; } = "Consumables & misc";
    public string ItemsSort { get; init; } = "Sort";
    public string ItemsSortGame { get; init; } = "Game order";
    public string ItemsSortGrade { get; init; } = "Grade";
    public string ItemsSortName { get; init; } = "Name";
    public string ItemsSortValue { get; init; } = "Market value";
    public string ItemsBag { get; init; } = "Bag";
    public string ItemsCharWh { get; init; } = "Character warehouse";
    public string ItemsAcctWh { get; init; } = "Account warehouse";
    public string ItemsEquipped { get; init; } = "Equipped";
    public string ItemsUnknown17 { get; init; } = "Container 17 (unknown)";
    public string ItemsChipLogin { get; init; } = "login list";
    public string ItemsChipPartial { get; init; } = "partial";
    public string ItemsChipLastSeen { get; init; } = "last seen {0}";
    public string ItemsChipPartialAt { get; init; } = "partial · {0}";
    public string ItemsChipNever { get; init; } = "never opened";
    public string ItemsChipUpdatedBy { get; init; } = "updated by {0} {1}";
    public string ItemsStacks { get; init; } = "{0} stacks";
    public string ItemsStack1 { get; init; } = "1 stack";
    public string ItemsShared { get; init; } = "shared · server {0}";
    public string ItemsValue { get; init; } = "≈ {0} Kina";
    public string ItemsValueTip { get; init; } = "Market value of the tradable stacks: lowest listing × count ({0})";
    public string ItemsTiles { get; init; } = "Tiles";
    public string ItemsList { get; init; } = "List";
    public string ItemsBagIncomplete { get; init; } = "Looks incomplete: {0} stacks and {1} empty slots below slot {2}, not what a login list gives (likely an update taken for a whole-bag resend by an older meter). It fixes itself at the next login.";
    public string ItemsWhPartial { get; init; } = "Only {0} stacks seen (deposits made while the meter ran). Open the warehouse once to read it whole.";
    public string ItemsWhNever { get; init; } = "Not captured yet: the game only sends it when the warehouse window opens.";
    public string ItemsAcctFull { get; init; } = "Full list read by {0} at {1}";
    public string ItemsAcctNewer { get; init; } = "{0} slots updated by {1} at {2}";
    public string ItemsAcctDot { get; init; } = "(blue dot). Kina stored here is in the footer.";
    public string ItemsAcctNoFull { get; init; } = "No full list yet: open the warehouse once with any character of this server.";
    public string ItemsNothing { get; init; } = "Nothing seen here yet.";
    public string ItemsToday { get; init; } = "Today";
    public string ItemsTodaySub { get; init; } = "raids, Conquest dungeons and content tickets";
    public string ItemsTodayReset { get; init; } = "Daily reset {0} (in {1}) · weekly {2} (Wednesday, assumed)";
    public string ItemsTodayNone { get; init; } = "No ticket list yet: it arrives when the character logs in with the meter running.";
    public string ItemsTodayChest { get; init; } = "chest {0}/{1}";
    public string ItemsTodayClaimable { get; init; } = "claimable";
    public string ItemsTodayChestUnknown { get; init; } = "Guaranteed-chest progress not received for this character yet: it comes with the next login or zone change.";
    public string ItemsTodayChestTip { get; init; } = "Expedition window guaranteed chest: +2 per Conquest clear here; claiming it sets the bar back to 0.";
    public string ItemsTodayInferred { get; init; } = "name inferred from the id numbering; meaning of the number not verified";
    public string ItemsTodayLeft { get; init; } = "{0} left";
    public string ItemsDaevaPass { get; init; } = "Daeva Pass";
    public string ItemsEventPass { get; init; } = "Event pass";
    public string ItemsPassLine { get; init; } = "{0}: rewards claimed {1}/{2} · {3} EXP";
    public string ItemsAch { get; init; } = "Achievements";
    public string ItemsAchRight { get; init; } = "{0}/{1} tiers · {2} done · {3} to claim · {4} in progress";
    public string ItemsAchNone { get; init; } = "No achievement list yet: it arrives when the character logs in with the meter running.";
    public string ItemsAchNoCatalog { get; init; } = "The achievement catalogue (data\\gamedb\\achievements.json) is missing: progress is saved but cannot be named.";
    public string ItemsAchNothing { get; init; } = "Nothing to claim.";
    public string ItemsAchTier { get; init; } = "tier {0}/{1} · {2}/{3}";
    public string ItemsAchMore { get; init; } = "+{0} tiers";
    public string ItemsNoChar { get; init; } = "No character seen yet. Log in once with the meter running: the bag comes with the login list, the warehouses when you open them.";
    public string ItemsAcross { get; init; } = "Across characters";
    public string ItemsAcrossHint { get; init; } = "Type in the search box to see every match on every character and warehouse. Meanwhile: stacks split over 3+ places ({0}).";
    public string ItemsNoMatch { get; init; } = "No item matches “{0}” on any character.";
    public string ItemsMatches { get; init; } = "{0} items";
    public string ItemsFrom { get; init; } = "from: {0}";
    public string ItemsUsedIn { get; init; } = "Used in";
    public string ItemsNeeds { get; init; } = "needs {0} · you have {1}";
    public string ItemsMissing { get; init; } = "missing: {0}";
    public string ItemsAllMats { get; init; } = "all materials on the account";
    public string ItemsMore { get; init; } = "+{0} more";
    public string ItemsOpenCraft { get; init; } = "Open in Craft →";
    public string ItemsBound { get; init; } = "bound";
    public string ItemsThisStack { get; init; } = "This stack";
    public string ItemsEverywhere { get; init; } = "Everywhere";
    public string ItemsWhere { get; init; } = "Where";
    public string ItemsMarket { get; init; } = "Market";
    public string ItemsMarketEach { get; init; } = "{0} Kina each";
    public string ItemsFromLbl { get; init; } = "From";
    public string ItemsColName { get; init; } = "Name";
    public string ItemsColCount { get; init; } = "Count";
    public string ItemsColWhere { get; init; } = "Where";
    public string ItemsColAlso { get; init; } = "Also";
    public string ItemsColUnit { get; init; } = "Unit";
    public string ItemsColValue { get; init; } = "Value";
    public string ItemsSlot { get; init; } = "slot {0}";
    public string ItemsFootChar { get; init; } = "Character";
    public string ItemsFootValue { get; init; } = "Value (tradable)";
    public string ItemsFootSeen { get; init; } = "Seen";
    public string ItemsFootNever { get; init; } = "never";
    public string ItemsFootAp { get; init; } = "AP";
    public string ItemsFootEnhance { get; init; } = "Enhance";
    public string ItemsFootValueShort { get; init; } = "Value";
    public string ItemsTkOdyle { get; init; } = "Odyle";
    public string ItemsTkOdyleTip { get; init; } = "Odyle energy: base + bonus";
    public string ItemsTkKeys { get; init; } = "Shugo";
    public string ItemsTkKeysTip { get; init; } = "Shugo keys";
    public string ItemsTkPass { get; init; } = "Pass";
    public string ItemsTkPassTip { get; init; } = "Daeva Pass level (EXP tiers reached; hover a value for EXP and rewards claimed)";
    public string ItemsTkEvent { get; init; } = "Event";
    public string ItemsTkEventTip { get; init; } = "Event pass level";
    public string ItemsTkSeal { get; init; } = "Seal";
    public string ItemsTkSealTip { get; init; } = "Time left of the 1-hour seal-dungeon budget";
    public string ItemsTkSeenTip { get; init; } = "When the meter last saw the character (inventory or tickets; a login sends the full ticket list). Hover a name for its raid entries and held Abyss corridors";
    public string ItemsCaption { get; init; } = "Kina and the other currencies (9301xxxxx) are ordinary bag stacks: they are taken out of the grids and summed here. Value = lowest market listing × count, tradable items only ({0}). Bag and warehouse sizes grow with expansions and are not in the packets: only the stack count is shown.";
    public string ItemsNoMarket { get; init; } = "no market file loaded: Craft → Load market file…";
    public string ItemsMarketFrom { get; init; } = "{0} snapshot {1}";
    public string ItemsLegendUnknown { get; init; } = "grade unknown";
    public string ItemsLegendEnchant { get; init; } = "enchant";
    public string ItemsLegendDot { get; init; } = "slot newer than the full list";
    public string ItemsPlaceBag { get; init; } = "bag";
    public string ItemsPlaceWh { get; init; } = "wh";
    public string ItemsPlaceAcct { get; init; } = "account wh";
    public string ItemsPlaceWorn { get; init; } = "worn";
    public string ItemsGradeUnknown { get; init; } = "grade unknown";
    public string BuildTitle { get; init; } = "Build";
    public string TrayBuild { get; init; } = "Build: skills, stigmas and Daevanion plan of this character";
    public string BuildSubSkills { get; init; } = "Skills & Stigmas";
    public string BuildSubDaevanion { get; init; } = "Daevanion";
    public string BuildDaevanionSoon { get; init; } = "Daevanion planner comes in the next step";
    public string BuildNone { get; init; } = "— no build —";
    public string BuildPickTip { get; init; } = "Builds of this character (saved on this PC)";
    public string BuildNewFromProfile { get; init; } = "New from profile";
    public string BuildNewFromProfileTip { get; init; } = "A new build with every skill the official profile shows, at its current level";
    public string BuildNoProfileForNew { get; init; } = "No official profile for this character yet: log in once or press refresh";
    public string BuildProfileName { get; init; } = "{0} — profile {1}";
    public string BuildImport { get; init; } = "Import…";
    public string BuildImportTip { get; init; } = "Import a build from a questlog.gg skill-builder link";
    public string BuildImportTitle { get; init; } = "Import from questlog.gg";
    public string BuildImportHint { get; init; } = "questlog.gg link: …/skill-builder/<user>?build-id=<n> (skills) or …/daevanion-planner/<user>?build-id=<n> (plan for the active build)";
    public string BuildImportBadUrl { get; init; } = "That is not a questlog.gg link: paste a skill-builder or a daevanion-planner build link";
    public string BuildDeleteTip { get; init; } = "Delete this build";
    public string BuildDeleteConfirm { get; init; } = "Delete the build \"{0}\"?";
    public string BuildSkillLangTip { get; init; } = "Language of skill names and descriptions";
    public string BuildCard { get; init; } = "Build";
    public string BuildOpenSource { get; init; } = "Open on questlog.gg";
    public string BuildShowDone { get; init; } = "Show skills already at the build level";
    public string DaevTitle { get; init; } = "{0} — Daevanion";
    public string DaevTitleNoChar { get; init; } = "Daevanion";
    public string DaevPoints { get; init; } = "{0} pts";
    public string DaevImport { get; init; } = "Import plan…";
    public string DaevImportTip { get; init; } = "Replace this build's Daevanion plan with a questlog.gg daevanion-planner build";
    public string DaevImportTitle { get; init; } = "Import a Daevanion plan from questlog.gg";
    public string DaevImportHint { get; init; } = "questlog.gg daevanion-planner link (…/daevanion-planner/<user>?build-id=<n>)";
    public string DaevImportBadUrl { get; init; } = "That is not a questlog.gg daevanion-planner link";
    public string DaevImporting { get; init; } = "Reading questlog planner build {0}…";
    public string DaevImportNotFound { get; init; } = "questlog has no public planner build {0} for that user";
    public string DaevImportClass { get; init; } = "Planner build {0} is for {1}; this character is {2}";
    public string DaevImported { get; init; } = "Imported \"{0}\": {1} nodes on {2} boards";
    public string DaevImportFailed { get; init; } = "questlog did not answer: {0}";
    public string DaevBoardLine { get; init; } = "{0} / {1} · plan {2}";
    public string DaevHint { get; init; } = "Click on nodes to toggle them. Nodes must be connected to a start node.";
    public string DaevShowOpen { get; init; } = "Show nodes already open";
    public string DaevSkillIcons { get; init; } = "Show Skill Icons";
    public string DaevSelectAll { get; init; } = "Select All";
    public string DaevReset { get; init; } = "Reset";
    public string DaevSelectAllTip { get; init; } = "Plan every node of this board";
    public string DaevResetTip { get; init; } = "Plan only the nodes open now on this board";
    public string DaevMissing { get; init; } = "{0}: {1} of {2} planned nodes still to open";
    public string DaevLegendOpen { get; init; } = "open now ({0}: {1})";
    public string DaevLegendPlan { get; init; } = "in the plan ({0}; bright border = {1} still to open)";
    public string DaevPlanFrom { get; init; } = "plan: {0}";
    public string DaevLegendOffPlan { get; init; } = "open but not in the plan ({0})";
    public string DaevLegendOff { get; init; } = "not in the plan";
    public string DaevNoBuild { get; init; } = "Pick or create a build to plan: the board shows the nodes open now";
    public string DaevNoClass { get; init; } = "No Daevanion boards for this character's class";
    public string DaevNotConnected { get; init; } = "Not connected: plan a neighbour that leads to the start first";
    public string DaevIsOpen { get; init; } = "Already open: the character cannot close it";
    public string DaevStats { get; init; } = "Stats";
    public string DaevStatsPts { get; init; } = "Plan {0} pts · now {1} pts";
    public string DaevColPlan { get; init; } = "Plan";
    public string DaevColNow { get; init; } = "Now";
    public string DaevGroupResource { get; init; } = "Resource";
    public string DaevGroupCritical { get; init; } = "Critical";
    public string DaevGroupAttack { get; init; } = "Attack";
    public string DaevGroupDefense { get; init; } = "Defense";
    public string DaevGroupPvP { get; init; } = "PvP";
    public string DaevGroupSkills { get; init; } = "Skills";
    public string DaevStateOpen { get; init; } = "open";
    public string DaevStatePlanned { get; init; } = "in the plan, not open yet";
    public string DaevStateNot { get; init; } = "not planned";
    public string DaevCost { get; init; } = "{0} · {1} pts";
    public string BuildMissing { get; init; } = "Missing: {0} of {1} skills · {2} levels · {3} not learned";
    public string BuildTarget { get; init; } = "Target: {0}";
    public string BuildTargetQuestlog { get; init; } = "questlog build {0}";
    public string BuildLearned { get; init; } = "Learned: official profile {0}";
    public string BuildNoProfile { get; init; } = "Learned: no official profile yet";
    public string BuildRefresh { get; init; } = "refresh";
    public string BuildRefreshing { get; init; } = "Fetching the official profile…";
    public string BuildRefreshFailed { get; init; } = "Could not fetch the official profile";
    public string BuildActive { get; init; } = "Active";
    public string BuildStigma { get; init; } = "Stigma";
    public string BuildPassive { get; init; } = "Passive";
    public string BuildLegendOk { get; init; } = "at target";
    public string BuildLegendNear { get; init; } = "within 4 levels";
    public string BuildLegendFar { get; init; } = "far or not learned";
    public string BuildLegendOff { get; init; } = "grey = not in the build";
    public string BuildNoClass { get; init; } = "Class not known yet: log in with this character once (the official profile tells the class) or pick a build.";
    public string BuildPickSkill { get; init; } = "Click a skill to see it here. Double-click a skill to put it on the highlighted key (click a key number to highlight it); double-click or right-click a slot to take it off.";
    public string BuildEquip { get; init; } = "Equip";
    public string BuildUnequip { get; init; } = "Unequip";
    public string BuildAddToBuild { get; init; } = "Add to the build";
    public string BuildRemove { get; init; } = "Remove from build";
    public string BuildLevel { get; init; } = "Level";
    public string BuildSpecs { get; init; } = "Specializations";
    public string BuildSpecReq { get; init; } = "Lv. {0}";
    public string BuildSpecLocked { get; init; } = "Needs skill level {0}";
    public string BuildSpecFull { get; init; } = "At most {0} specializations on this skill";
    public string BuildKeyFull { get; init; } = "Key {0} is full";
    public string BuildNoBuild { get; init; } = "Pick a build or create one first";
    public string BuildPriority { get; init; } = "Skill Priority";
    public string BuildClear { get; init; } = "Clear";
    public string BuildClearConfirm { get; init; } = "Clear the whole skill priority?";
    public string BuildMoveLeft { get; init; } = "Move left";
    public string BuildMoveRight { get; init; } = "Move right";
    public string BuildRemoveItem { get; init; } = "Remove";
    public string BuildNothingToAdd { get; init; } = "Every skill of this kind in the build is already in the priority";
    public string BuildMacros { get; init; } = "Macros";
    public string BuildAddMacro { get; init; } = "+ Add macro";
    public string BuildMacroName { get; init; } = "macro name";
    public string BuildMacroAddStep { get; init; } = "+ step";
    public string BuildMacroSave { get; init; } = "Save";
    public string BuildMacroCancel { get; init; } = "Cancel";
    public string BuildNoMacros { get; init; } = "No macros";
    public string BuildDescription { get; init; } = "Description";
    public string BuildNoDescription { get; init; } = "No description";
    public string BuildSave { get; init; } = "Save build (local)";
    public string BuildSaved { get; init; } = "saved {0}";
    public string BuildNotSaved { get; init; } = "not saved yet";
    public string BuildTipNow { get; init; } = "{0} now";
    public string BuildTipNowAnon { get; init; } = "Now";
    public string BuildTipBuild { get; init; } = "Build";
    public string BuildTipSpecs { get; init; } = "Specs";
    public string BuildTipKey { get; init; } = "key {0}";
    public string BuildTipNotLearned { get; init; } = "not learned";
    public string BuildTipNone { get; init; } = "none";
    public string BuildTipAtLevel { get; init; } = "At level {0}";
    public string BuildTipNotes { get; init; } = "When to use · combos";
    public string BuildNoteHint { get; init; } = "Your note for this skill: when to use it, combos, order…";
    public string BuildRelations { get; init; } = "Relations";
    public string BuildImporting { get; init; } = "Fetching questlog build {0}…";
    public string BuildImported { get; init; } = "Imported \"{0}\" from questlog.gg";
    public string BuildImportedOtherClass { get; init; } = "Imported \"{0}\": it is a {1} build, this character is a {2}";
    public string BuildImportNotFound { get; init; } = "questlog.gg has no public build {0} by that author";
    public string BuildImportFailed { get; init; } = "Could not reach questlog.gg: {0}";
    public string BuildCopyQuestlog { get; init; } = "Copy for questlog";
    public string BuildCopyQuestlogTip { get; init; } = "Copies this build as JSON for tools/questlog-publish.js (data\\gamedb in the meter folder), which publishes it from your own questlog.gg session";
    public string BuildCopied { get; init; } = "Copied — paste it with tools/questlog-publish.js in your questlog session";
    public string BuildCopyFailed { get; init; } = "The clipboard is busy, try again";
    public string BuildSuggestTitle { get; init; } = "Suggested builds for {0}";
    public string BuildSuggestHint { get; init; } = "No build chosen for this character. The most-liked public builds of the class on questlog.gg: add one to start from it, search for another, or start from what the character has now.";
    public string BuildSuggestLoading { get; init; } = "Loading…";
    public string BuildSuggestFailed { get; init; } = "questlog.gg did not answer; try Search… later";
    public string BuildSuggestEmpty { get; init; } = "No public builds of this class yet";
    public string BuildSuggestNoClass { get; init; } = "Class not known yet: log in once with this character, or search questlog.gg.";
    public string BuildAdd { get; init; } = "Add";
    public string BuildSearch { get; init; } = "Search…";
    public string BuildByAuthor { get; init; } = "by {0}";
    public string BuildUpdatedOn { get; init; } = "updated {0}";
    public string ImportUrlLabel { get; init; } = "Build link";
    public string ImportGo { get; init; } = "Import";
    public string ImportSearchLabel { get; init; } = "Search public builds";
    public string ImportSearchHint { get; init; } = "Build name, author or tag; empty = most liked";
    public string ImportAllClasses { get; init; } = "All classes";
    public string ImportNext { get; init; } = "Next ›";
    public string ImportPrev { get; init; } = "‹ Previous";
    public string ImportPage { get; init; } = "Page {0} of {1} · {2} builds";
    public string ImportNoResults { get; init; } = "No builds found";
    public string ImportSearching { get; init; } = "Searching questlog.gg…";
    public string ImportFetching { get; init; } = "Fetching the build…";
    public string ImportMineTab { get; init; } = "My builds";
    public string ImportMineTitle { get; init; } = "My builds on questlog.gg ({0})";
    public string ImportUserLabel { get; init; } = "questlog user";
    public string ImportUserHint { get; init; } = "Your questlog.gg user: the code after /skill-builder/ in your links, or paste any of your build or planner links. Learnt from the first link you import; no login.";
    public string ImportMineEmpty { get; init; } = "Set your questlog user to list your builds here: paste one of your build or planner links in the box above.";
    public string ImportMineLoading { get; init; } = "Loading your builds…";
    public string ImportMineSkills { get; init; } = "Skill builds ({0})";
    public string ImportMinePlans { get; init; } = "Daevanion plans ({0})";
    public string ImportMineNone { get; init; } = "None of this class";
    public string ImportMineCount { get; init; } = "{0} skill builds · {1} plans on questlog.gg";
    public string ImportMineRefresh { get; init; } = "Fetch your builds again";
    public string ImportMineBadUser { get; init; } = "That is not a questlog user or link";
    public string ImportPlanKind { get; init; } = "Daevanion plan";
    public string ImportPlanDone { get; init; } = "Plan \"{0}\" imported into \"{1}\": {2} nodes on {3} boards";
    public string ImportPlanNewBuild { get; init; } = "New build from the profile; ";
    public string BuildPlanPaired { get; init; } = " + plan \"{0}\"";
    public string BuildCheckQuestlog { get; init; } = "Check questlog";
    public string BuildCheckQuestlogTip { get; init; } = "Compare this build (and its Daevanion plan) with the versions on questlog.gg";
    public string BuildChecking { get; init; } = "Checking questlog.gg…";
    public string BuildQuestlogNewer { get; init; } = "questlog has a newer version ({0})";
    public string BuildQuestlogUpdate { get; init; } = "Update";
    public string BuildQuestlogUpdateTip { get; init; } = "Imports questlog's version again; your notes stay, and your plan stays unless the plan itself is updated";
    public string BuildQuestlogUpToDate { get; init; } = "Up to date with questlog.gg";
    public string BuildQuestlogGone { get; init; } = "questlog.gg no longer lists this build";
    public string BuildUpdatedFromQuestlog { get; init; } = "Updated from questlog.gg: {0}";
    public string BuildSuggestMine { get; init; } = "Your questlog builds";
    public string BuildSuggestTop { get; init; } = "Most liked";
    public string CraftStatus { get; init; } = "{0} craftable now · {1} almost";
    public string CraftProfit { get; init; } = "Profit";
    public string CraftProfitTip { get; init; } = "Every recipe whose result sells on the market, most profitable first: cheapest listing of the result (after the broker fee) minus the cheapest listings of the materials. Needs a market file.";
    public string CraftLoadMarket { get; init; } = "Load market file…";
    public string CraftLoadMarketTip { get; init; } = "The JSON the market script saved in Downloads (Prices → Copy market script → run it in your browser).";
    public string CraftNoMarket { get; init; } = "No market file yet: Prices → Copy market script → run it in your browser → Craft → Load market file…";
    public string CraftMarketBad { get; init; } = "That file is not a market export.";
    public string CraftMarketFrom { get; init; } = "{0} items priced · {1} · captured {2} · fee {3}%";
    public string CraftSells { get; init; } = "sells";
    public string CraftAfterFee { get; init; } = "after fee";
    public string CraftCost { get; init; } = "cost";
    public string CraftUnpriced { get; init; } = "unpriced";
    public string CraftNow { get; init; } = "now";
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
    public string PricesItemId { get; init; } = "item id (or pick one above)";
    public string PricesMax { get; init; } = "max price";
    public string PricesNotChecked { get; init; } = "not checked yet";
    public string PricesLastPoll { get; init; } = "last check {0}";
    public string PricesChecked { get; init; } = "read {0}";
    public string PricesSampled { get; init; } = "site sample {0}";
    public string PricesHourlyNote { get; init; } = "aion2.exchange samples each item about once an hour: Check now re-reads, it cannot force a new sample";
    public string PricesSearch { get; init; } = "search an item by name…";
    public string PricesSearchLang { get; init; } = "Search language";
    public string PricesExpandTip { get; init; } = "Click for the cheapest listings and the price chart";
    public string PricesCheapest { get; init; } = "CHEAPEST LISTINGS";
    public string PricesColPrice { get; init; } = "price";
    public string PricesColQty { get; init; } = "qty";
    public string PricesColUnit { get; init; } = "unit";
    public string PricesColSeller { get; init; } = "seller";
    public string PricesListingsOff { get; init; } = "Listings endpoint not configured: the site's per-listing call is not known yet. The chart below comes from the hourly samples.";
    public string PricesListingsNone { get; init; } = "No listings right now.";
    public string PricesRange24h { get; init; } = "24h";
    public string PricesRange7d { get; init; } = "7 days";
    public string PricesRange30d { get; init; } = "30 days";
    public string PricesChartEmpty { get; init; } = "No price history yet: it fills from the next check.";
    public string PricesCollecting { get; init; } = "collecting since {0}";
    public string PricesStats { get; init; } = "min {0} · median {1} · max {2}";
    public string PricesBuyBelow { get; init; } = "buy below";
    public string PricesSellAbove { get; init; } = "sell above";
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
    public string TopLoading { get; init; } = "Loading data…";
    public string TopBoardLoading { get; init; } = "Reading the ranking pages (aion2index.com, one per 400 ms)…";
    public string TopBoardHint { get; init; } = "Strongest characters by combat power (aion2index.com, refreshed daily; the game publishes no DPS ranking). Load profile shows their official gear, Daevanion and skill levels to compare with yours.";
    public string TopBoardFailed { get; init; } = "The ranking pages did not answer: {0}";
    public string TopProfileFetch { get; init; } = "Load profile";
    public string TopProfileLoading { get; init; } = "Loading…";
    public string TopProfileMissing { get; init; } = "\"{0}\" on {1} is not on the official character search (renamed, deleted or moved server).";
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
    public string CheckUpdatesAuto { get; init; } = "Check for new versions";
    public string UpdatesHint { get; init; } = "Shortly after start and every 2 hours, from the private build's own release page. The meter only asks which version is the newest; nothing about you or your fights is sent.";
    public string AutoInstallUpdates { get; init; } = "Install updates by itself";
    public string UpdateChannelLabel { get; init; } = "Channel";
    public string UpdateChannelStable { get; init; } = "Stable";
    public string UpdateChannelDev { get; init; } = "Development builds";
    public string UpdateChannelHint { get; init; } = "Stable = releases marked as such. Development builds come out more often and may break; only pick it if Diego asked you to.";
    public string CheckUpstream { get; init; } = "Also watch the public upstream project (maintainer only)";
    public string TelemetryOptIn { get; init; } = "Send anonymous usage statistics once a day";
    public string TelemetryHint { get; init; } = "Build, Windows version, Npcap present, capture health, crash and error counts, tabs used, your saved builds (class, skills, levels, notes, Daevanion plan) and your watched prices and favourite recipes; no names, no chat, no fight data. The same switch keeps the copy that Backup → Restore from code brings back: turning it off also stops the backup.";
    public string SecBackup { get; init; } = "BACKUP";
    public string BackupCodeLabel { get; init; } = "Backup code";
    public string BackupCopy { get; init; } = "Copy";
    public string BackupCopied { get; init; } = "Copied";
    public string BackupHint { get; init; } = "Write this code down: on a new PC, Restore from code brings back your builds, notes, Daevanion plans and watched prices. The copy lives on Diego's server with the anonymous telemetry; turning telemetry off stops it.";
    public string BackupOff { get; init; } = "Anonymous statistics are off (Updates): nothing is being backed up.";
    public string BackupRestore { get; init; } = "Restore from code…";
    public string RestoreTitle { get; init; } = "Restore from code";
    public string RestoreIntro { get; init; } = "Paste the backup code from Settings → Backup on your old PC. Dashes, spaces and case do not matter. Your builds go back to each character (or wait until that character logs in here); the watch list and favourite recipes come back only if they are empty on this PC.";
    public string RestoreGo { get; init; } = "Restore";
    public string RestoreBadCode { get; init; } = "That is not a backup code: it has 32 characters, digits 0-9 and letters A-F.";
    public string RestoreWorking { get; init; } = "Asking the server…";
    public string RestoreNotFound { get; init; } = "Nothing is stored for that code.";
    public string RestoreLimited { get; init; } = "Too many tries for today: try again tomorrow.";
    public string RestoreRefused { get; init; } = "The server refused the request ({0}): update the meter and try again.";
    public string RestoreFailed { get; init; } = "Could not reach the server: {0}";
    public string RestoreBadAnswer { get; init; } = "The server's answer could not be read: {0}";
    public string RestoreDone { get; init; } = "{0} builds restored ({1} placed on characters, {2} waiting for their character to log in)";
    public string RestoreKeptNewer { get; init; } = "{0} kept because the copy on this PC is newer";
    public string RestoreWatchRestored { get; init; } = "watch list restored";
    public string RestoreWatchKept { get; init; } = "watch list kept local";
    public string RestoreNoState { get; init; } = "no watch list stored";
    public string RestoreFavRestored { get; init; } = "favourite recipes restored";
    public string RestoreNoTelemetry { get; init; } = "This PC now uses that code. Anonymous statistics are off, so changes made here are not backed up.";
    public string TelemetryNotice { get; init; } = "To help development the meter sends anonymous usage statistics once a day (build, capture health, crashes; no character names). Turn it off in Settings → Updates.";
    public string SecGame { get; init; } = "GAME";
    public string StartWithGame { get; init; } = "Start and stop with AION 2";
    public string StartWithGameHint { get; init; } = "Registers a task for your Windows user (no administrator): every 10 s it looks for AION2.exe, starts the meter when the game is running and closes it 60 s after the game ends. Uncheck to remove the task.";
    public string CloseWithGame { get; init; } = "Also close the meter a minute after AION 2 closes";
    public string StartWithGameFailed { get; init; } = "Could not register the task: {0}";
    public string VersionDev { get; init; } = "Developer build {0} · deployed by hand (no self-update)";
    public string VersionRelease { get; init; } = "Version {0} ({1}) · updates itself";
    public string AutoInstallHint { get; init; } = "A new version is downloaded in the background and applied right after the meter starts, or when you are not playing (after 10 minutes without a fight): the meter restarts by itself, without any window. Installed copies only.";
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
        FightingCount = "{0} бьют босса",
        DamageSentBy = "урон виден у {0}",
        DamageUnsentTip = "На мировом боссе с толпой игра присылает вашему клиенту урон лишь нескольких игроков, в том числе ваш. Про остальных приходит только «удар попал» — без числа — или их периодический урон. Поэтому их цифры и все доли здесь намного ниже настоящих; метр на этом компьютере больше увидеть не может.",
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
            Fighting = "бьют {0}",
            DamageSentBy = "урон виден у {0}",
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
        AbyssLogsSetting = "После убийства босса показывать место вашего DPS среди класса за неделю (Abyss Logs)",
        AbyssLogsHint = "Запрашивает у api.abysslogs.com недельную статистику босса: отправляются только регион и босс, ничего о вас. Кэш 6 ч.",
        AbyssLogsTemplate = "p{0} среди {1} · {2} · неделя · медиана {3}/s",
        AbyssLogsAboveTemplate = "Выше всех {1} · {2} · неделя · медиана {3}/s",
        AbyssLogsClassGroup = "«{0}»",
        AbyssLogsBracket = "{0} {1} CP",
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
        AtlasZoom = "Масштаб",
        AtlasPetLevel = "уровень {0}/3",
        AtlasPetNeed = "{0} душ на уровень",
        TopLoading = "Загрузка данных…",
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
        CraftVariant = "вариант (другая фракция или путь)",
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
        PricesChecked = "прочитано {0}",
        PricesSampled = "замер сайта {0}",
        PricesHourlyNote = "aion2.exchange замеряет каждый предмет примерно раз в час: «Проверить» перечитывает, но новый замер не вызывает",
        PricesSearch = "поиск предмета по названию…",
        PricesSearchLang = "Язык поиска",
        PricesExpandTip = "Нажмите: самые дешёвые лоты и график цены",
        PricesCheapest = "САМЫЕ ДЕШЁВЫЕ ЛОТЫ",
        PricesColPrice = "цена",
        PricesColQty = "шт.",
        PricesColUnit = "за шт.",
        PricesColSeller = "продавец",
        PricesListingsOff = "Источник лотов не настроен: запрос сайта по отдельным лотам пока неизвестен. График ниже строится по ежечасным замерам.",
        PricesListingsNone = "Сейчас лотов нет.",
        PricesRange24h = "24 ч",
        PricesRange7d = "7 дней",
        PricesRange30d = "30 дней",
        PricesChartEmpty = "Истории цены пока нет: она заполнится после следующей проверки.",
        PricesCollecting = "собирается с {0}",
        PricesStats = "мин {0} · медиана {1} · макс {2}",
        PricesBuyBelow = "купить ниже",
        PricesSellAbove = "продать выше",
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
