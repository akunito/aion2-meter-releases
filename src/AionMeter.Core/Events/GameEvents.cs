namespace AionMeter.Core.Events;

/// <summary>Quality flags of a single hit, normalised from whatever the wire format uses.</summary>
[Flags]
public enum HitFlags : ushort
{
    None = 0,
    Critical = 1 << 0,
    Back = 1 << 1,
    Front = 1 << 2,
    Perfect = 1 << 3,
    Heavy = 1 << 4,      // "Smite" in AionFlex terms
    Multi = 1 << 5,
    Block = 1 << 6,
    Parry = 1 << 7,
    Endure = 1 << 8,     // Iron Wall
    Evade = 1 << 9,
    Dot = 1 << 10,
    Restore = 1 << 11,
    Double = 1 << 12,
}

public enum GameClass : byte
{
    Unknown = 0,
    Gladiator,
    Templar,
    Ranger,
    Assassin,
    Sorcerer,
    Elementalist,
    Cleric,
    Chanter,
    Brawler,
}

/// <summary>Everything the combat engine consumes. Time is Unix milliseconds taken from the packet capture.</summary>
public abstract record GameEvent(long TimeMs);

/// <param name="Damage">Total damage of the record, additional hits included.</param>
/// <param name="HitCount">1 + number of additional hits carried by the record.</param>
/// <param name="PowerScalar">The actor's damage multiplier in 1/100 % (players 16000–22000, mobs 10000); 0 if absent.
/// Summons inherit their owner's value, which lets unannounced summons be merged into the right player.</param>
public sealed record DamageEvent(
    long TimeMs,
    uint SourceId,
    uint TargetId,
    int SkillCode,
    long Damage,
    HitFlags Flags,
    int HitCount = 1,
    int PowerScalar = 0) : GameEvent(TimeMs);

public sealed record HealEvent(long TimeMs, uint SourceId, uint TargetId, int SkillCode, long Amount, HitFlags Flags) : GameEvent(TimeMs);

/// <summary>The local player's own character.</summary>
/// <param name="CombatPower">The character's combat power (CP) as the game shows it; 0 when the record did not carry it.</param>
public sealed record SelfIdentifiedEvent(long TimeMs, uint ActorId, string Name, int ServerId, GameClass Class, int Level = 0,
    long CombatPower = 0) : GameEvent(TimeMs);

/// <summary>One member of the party roster: the only place the server gives other players' gear score and combat power.</summary>
/// <param name="GearScore">Equipment item level ("GS"); 0 when it did not read.</param>
/// <param name="CombatPower">Combat power ("CP"), as on the character sheet.</param>
public sealed record PartyMemberInfo(string Name, int ServerId, int Level, int GearScore, long CombatPower, GameClass Class);

/// <summary>
/// The party roster (<c>02 97</c>), sent whenever the party changes. Members are named, not identified by entity id:
/// the meter joins them to the players it sees by name.
/// </summary>
public sealed record PartyRosterEvent(long TimeMs, IReadOnlyList<PartyMemberInfo> Members) : GameEvent(TimeMs);

public sealed record PlayerSeenEvent(long TimeMs, uint ActorId, string Name, int ServerId, GameClass Class) : GameEvent(TimeMs);

/// <param name="Name">Display name when not resolvable from the NPC tables (demo / tests).</param>
/// <param name="X">Position where it came into view (0 when unknown); Y, Z likewise.</param>
public sealed record NpcSeenEvent(long TimeMs, uint ActorId, int NpcCode, long MaxHp = 0, string? Name = null,
    float X = 0, float Y = 0, float Z = 0) : GameEvent(TimeMs);

/// <summary>
/// A pet / spirit / totem / skill entity. Owner is known by id, only by the character name it carries, or not at all
/// (both empty) — it is still never a player.
/// </summary>
public sealed record SummonSeenEvent(long TimeMs, uint ActorId, uint OwnerId, string? OwnerName, int NpcCode) : GameEvent(TimeMs);

/// <summary>HP update. <paramref name="MaxHp"/> is 0 when the packet does not carry it. <paramref name="IsNpc"/> is
/// true when the record itself says the entity is an NPC; otherwise it may be a player and only updates known NPCs.</summary>
public sealed record NpcHpEvent(long TimeMs, uint ActorId, long CurrentHp, long MaxHp, bool IsNpc = true) : GameEvent(TimeMs);

/// <summary>An entity died, or (<paramref name="AlreadyDead"/>) came into view already dead.</summary>
public sealed record DeathEvent(long TimeMs, uint ActorId, bool AlreadyDead = false) : GameEvent(TimeMs);

/// <summary>One field boss in the map's boss list: alive since <paramref name="AtMs"/>, or back at <paramref name="AtMs"/>.</summary>
/// <param name="SlotId">Map id × 100 + the boss's place in the list (the map's bosses sorted by NPC code).</param>
public sealed record FieldBossSlot(int SlotId, bool Alive, long AtMs, float X = 0, float Y = 0, float Z = 0);

/// <summary>
/// The field boss list of a map (<c>01 91</c>), sent while the in-game map shows it — the respawn timers straight from
/// the server. <paramref name="Count"/> is what the header announces; <paramref name="Slots"/> may stop short of it when
/// a slot could not be read (<see cref="Complete"/> is then false and <paramref name="Raw"/> helps work out why).
/// <paramref name="ServerId"/> is the local character's server (set by the tracker): every server runs its own timers.
/// </summary>
public sealed record FieldBossListEvent(long TimeMs, int MapId, int Count, IReadOnlyList<FieldBossSlot> Slots, byte[] Raw, int ServerId = 0)
    : GameEvent(TimeMs)
{
    public bool Complete => Slots.Count == Count;
}

/// <summary>
/// A skill use announcement (<c>02 38</c>). <paramref name="SkillCode"/> is the raw variant (e.g. 15280140): an effect
/// entity's own ticks carry its caster's variant with a different last digit, which is how it is tied to its owner.
/// </summary>
public sealed record CastEvent(long TimeMs, uint ActorId, uint TargetId, int SkillCode) : GameEvent(TimeMs);

/// <summary>Server-side combat state toggle for an NPC (boss engaged / disengaged). KR only so far.</summary>
public sealed record BattleStateEvent(long TimeMs, uint ActorId, bool InCombat) : GameEvent(TimeMs);

/// <summary>
/// A world object came into view (<c>34 36</c>): gathering nodes (object codes 15xxxxx: Inina 1500042, Orichalcum
/// 1500004) and interactables. <paramref name="Taken"/> = units already gathered from the node (0–3, the same counter
/// <see cref="GatherEvent"/> carries), <paramref name="SpawnedAtMs"/> = when the node itself appeared (server Unix ms).
/// </summary>
public sealed record ObjectSeenEvent(long TimeMs, uint ActorId, byte Kind, int ObjectCode, float X, float Y, float Z, int Taken, long SpawnedAtMs) : GameEvent(TimeMs);

/// <summary>An object left (<c>35 36</c>): <paramref name="Exhausted"/> after its last unit, otherwise it only left the client's view.</summary>
public sealed record ObjectGoneEvent(long TimeMs, uint ActorId, bool Exhausted) : GameEvent(TimeMs);

/// <summary>Stacks of the local character's containers: the whole set at login (Full) or a refresh of some slots/containers.</summary>
public sealed record InventoryEvent(long TimeMs, bool Full, IReadOnlyList<Inventory.ItemStack> Stacks) : GameEvent(TimeMs);

public sealed record PetListEvent(long TimeMs, int EquippedPet, IReadOnlyList<Character.PetOwned> Pets) : GameEvent(TimeMs);
public sealed record TitleListEvent(long TimeMs, IReadOnlyList<int> Titles) : GameEvent(TimeMs);
public sealed record AchievementListEvent(long TimeMs, IReadOnlyList<Character.AchievementRow> Rows) : GameEvent(TimeMs);

/// <summary>Gathering on a node by any player in view: a start (<c>38 36</c>, <paramref name="Count"/> = 0) or a
/// result (<c>3a 36</c>, <paramref name="Count"/> = units taken from the node so far, shared by all gatherers).</summary>
public sealed record GatherEvent(long TimeMs, uint ObjectId, uint PlayerId, bool Started, int Count) : GameEvent(TimeMs);

/// <summary>A record the server sends only about the local player: its entity id, without name or class (for the atlas; the
/// combat tracker keeps using <see cref="SelfIdentifiedEvent"/>).</summary>
public sealed record LocalPlayerHintEvent(long TimeMs, uint ActorId) : GameEvent(TimeMs);

/// <summary>An entity's position (<c>1a 37</c>, sent for every entity in view, the local player included).</summary>
public sealed record PositionEvent(long TimeMs, uint ActorId, float X, float Y, float Z) : GameEvent(TimeMs);

/// <summary>A map load. <paramref name="IsTeleport"/> is true for a re-load inside the same map.</summary>
public sealed record ZoneChangedEvent(long TimeMs, int MapId, string ZoneName, bool IsDungeon, bool IsTeleport = false) : GameEvent(TimeMs);
