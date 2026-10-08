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
