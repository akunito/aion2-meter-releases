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
    // Sent only about the local player (three EU captures 2026-10-08, across a zone-load id change): the first varint is
    // the local player's entity id. 06 38 follows every skill use, 4a 36 comes every ~15 s.
    public const ushort SelfSkillState = 0x0638;
    public const ushort SelfStatus = 0x4A36;

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
        ActionState => "ActionState",
        SelfSkillState => "SelfSkill",
        SelfStatus => "SelfStatus",
        _ => $"{op >> 8:x2} {op & 0xFF:x2}",
    };
}
