namespace AionMeter.Core.Protocol;

/// <summary>
/// Opcodes as the two bytes appear on the wire (first byte, second byte), written as <c>0xAABB</c> where AA is the
/// first byte. Values are for the post-June-2026 client (Global launched after that patch).
/// </summary>
public static class Opcodes
{
    public static ushort Of(byte first, byte second) => (ushort)(first << 8 | second);

    public const ushort Heartbeat = 0x0036;      // 8 data bytes, ~19/s
    public const ushort SelfInfo = 0x3336;       // own character
    public const ushort PlayerInfo = 0x4536;     // another player
    public const ushort PlayerInfoOld = 0x4436;  // pre-June-2026 player record
    public const ushort Spawn = 0x4136;          // NPC / summon / effect entity
    public const ushort Death = 0x4236;
    public const ushort MapLoad = 0x2136;
    public const ushort Teleport = 0x2336;
    public const ushort Cast = 0x0238;           // skill use announcement (players and effect entities)
    public const ushort SkillHit = 0x0938;       // skill effect on a target: caster, 00, skill u32, 01 02, target, f32, target x y z (position = the TARGET's)
    public const ushort HitNotice = 0x0338;      // a hit landed: actor, target, skill — no damage
    public const ushort Damage = 0x0438;
    public const ushort Dot = 0x0538;
    public const ushort RemainHp = 0x008D;
    public const ushort KillCredit = 0x048D;
    public const ushort BattleToggle = 0x218D;   // KR only, unverified on Global
    public const ushort HpMp = 0x1B92;
    public const ushort PartyRoster = 0x0297;
    public const ushort FieldBossList = 0x0191;  // a map's field bosses: alive since / back at (in-game map list)
    // World objects (gathering nodes, interactables) and positions — EU captures 2026-10-08 (atlas branch).
    public const ushort ObjectSpawn = 0x3436;    // object in view: kind, object code, position, spawn time, units taken
    public const ushort ObjectGone = 0x3536;     // 04 03 00 = exhausted; 01 01 00 = left view
    public const ushort GatherStart = 0x3836;    // node, player, 01
    public const ushort GatherResult = 0x3A36;   // node, player, units taken so far (1..3), 7×00
    public const ushort Positions = 0x1A37;      // one entity's position: id, u16 flags, x y z, heading (every entity in view)
    public const ushort EntityState = 0x3C37;    // id, u8, x y z, … (the local player among others; sent on state changes)
    public const ushort ActionState = 0x2B38;    // id, …, u64 ms, id, 01 02, x y z (gathering and other actions; position = last 12 bytes)
    public const ushort ActionState2 = 0x2A38;   // same tail (x y z last 12 bytes); ~1 per 6 s for the local player while acting
    // Sent only about the local player (three EU captures + a live check 2026-10-08, across a zone-load id change): the
    // first varint is the local player's entity id. The own-HP record (00 8d with header 01 01, ~3/s) is the main one;
    // 4a 36, 46 37 and 44 37 agree. 06 38 is NOT self-only (it also carries nearby mobs' skills) — measured live.
    public const ushort SelfStatus = 0x4A36;
    public const ushort SelfState46 = 0x4637;
    public const ushort SelfState44 = 0x4437;
    // Inventory (EU, relog capture 2026-10-08): the login list and the refreshes share one record shape,
    // uid u32, 00, u16, 00, item id u32, count u32, u32 0, container u8, slot u8, then 13 zero bytes (gear: a longer tail with the enchant).
    public const ushort Inventory = 0x1156;       // every container at login (12.7 KB for 188 stacks)
    public const ushort InventoryUpdate = 0x1B56; // count u8, 01, flag, then records: a container re-sent when opened, or the slots a gather/loot/move changed
    // Character collections, sent once at login (same capture):
    public const ushort PetList = 0x0090;         // equipped pet u32, count varint, {pet id u32, pet id u32, level u32} × count (124 pets); more data follows
    public const ushort TitleList = 0x7956;       // count varint, title id u32 × count (182; ids = aion2hub titles, 12010061 = Through Hell and Back)
    public const ushort AchievementList = 0x8356; // u16, then {id u32, state u32, completed u64 ms, 00} rows (1512) before another section
    public const ushort BrokerPage = 0x9156;      // one broker (Mercado) page: u16 list id, u32, then 10 × {item u32, qty u32, u32 0, level u8, 5d, price u64, current u64, u32}

    public static string Name(ushort op) => op switch
    {
        Heartbeat => "Heartbeat",
        SelfInfo => "SelfInfo",
        PlayerInfo or PlayerInfoOld => "PlayerInfo",
        Spawn => "Spawn",
        Death => "Death",
        MapLoad => "MapLoad",
        Teleport => "Teleport",
        Cast => "Cast",
        SkillHit => "SkillHit",
        Inventory => "Inventory",
        PetList => "Pets",
        TitleList => "Titles",
        AchievementList => "Achievements",
        BrokerPage => "Broker",
        InventoryUpdate => "InventoryUpd",
        Damage => "Damage",
        Dot => "Dot",
        RemainHp => "RemainHp",
        KillCredit => "KillCredit",
        BattleToggle => "BattleToggle",
        HpMp => "HpMp",
        PartyRoster => "PartyRoster",
        FieldBossList => "FieldBosses",
        ObjectSpawn => "ObjectSpawn",
        ObjectGone => "ObjectGone",
        GatherStart => "GatherStart",
        GatherResult => "GatherResult",
        Positions => "Position",
        EntityState => "EntityState",
        ActionState or ActionState2 => "ActionState",
        SelfStatus => "SelfStatus",
        SelfState46 => "SelfState",
        SelfState44 => "SelfState",
        _ => $"{op >> 8:x2} {op & 0xFF:x2}",
    };
}
