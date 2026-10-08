using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Protocol;
using K4os.Compression.LZ4;

namespace AionMeter.Tests;

public class ProtocolTests
{
    private static List<GameEvent> Parse(ushort opcode, string hex)
    {
        var events = new List<GameEvent>();
        var parser = new PacketParser(GameData.Empty, events.Add) { TimeMs = 1_000 };
        parser.Handle(opcode, Wire.FromHex(hex));
        return events;
    }

    // Damage records from a live EU capture (2026-10-04, target 30001, actor 1395), checked by their authors
    // against the in-game Damage Analyzer. Expected: total damage, 1 + additional hits.
    [Theory]
    [InlineData("b1ea013600f30a40c0f4007a038000010b199b5f01000000ac52a40d021818", 30001u, 1395u, 1700L, 3, true)]   // layout 6, 2 extra hits
    [InlineData("b1ea013400f30ae0b7f800cd028bd3276101000000ac52d330010159", 30001u, 1395u, 6227L, 2, false)]         // layout 4 + field + 1 extra hit
    [InlineData("fe9e0224009e9b01c3f8f5000302792b156002000000ac52e10301060200", 36734u, 19870u, 481L, 2, false)]    // spirit, layout 4 without field
    [InlineData("fe9e0224009e9b01c18601001702a7a2980001000000ac52e401030303030100", 36734u, 19870u, 228L, 4, false)]
    [InlineData("fe9e0204009e9b01c3f8f5000302792b156003000000ac52b7030300", 36734u, 19870u, 439L, 1, false)]
    [InlineData("b1ea011600f30a40c0f40063028000010b199b5f01000000ac52d007", 30001u, 1395u, 976L, 1, false)]       // layout 6, no extra hits
    public void Damage_records_decode(string hex, uint target, uint actor, long damage, int hits, bool crit)
    {
        var e = Assert.Single(Parse(Opcodes.Damage, hex));
        var d = Assert.IsType<DamageEvent>(e);
        Assert.Equal(target, d.TargetId);
        Assert.Equal(actor, d.SourceId);
        Assert.Equal(damage, d.Damage);
        Assert.Equal(hits, d.HitCount);
        Assert.Equal(crit, d.Flags.HasFlag(HitFlags.Critical));
        Assert.Equal(10540, d.PowerScalar);
    }

    // Casts from the EU capture of 2026-10-05 22:20: a Sorcerer's Bittercold Wind on boss 46161 (sequence byte above
    // 0x7F, which is not a varint), then the wind entity 42321 announcing its own tick with that variant + 1.
    [Theory]
    [InlineData("c74300 0c28e900 9202 d1e802 b0f930c38dbae8470b71d3c700faa546ea5d01dc6f", 8647u, 46161u, 15280140)]
    [InlineData("d1ca0200 0d28e900 0102 d1ca02 a263c741d3c7ea47bd1ed1c70048a546ee64029a3a", 42321u, 42321u, 15280141)]
    public void Cast_announcements_decode(string hex, uint actor, uint target, int skill)
    {
        var c = Assert.Single(Parse(Opcodes.Cast, hex.Replace(" ", "")).OfType<CastEvent>()); // a position event may ride along
        Assert.Equal(actor, c.ActorId);
        Assert.Equal(target, c.TargetId);
        Assert.Equal(skill, c.SkillCode);
    }

    // Verteron, 2026-10-08 13:12–13:14 CEST: an Inina node (object 1500042) came into view full, one unit was taken,
    // the player walked out of range and came back: the second spawn record differs from the first in one byte.
    [Theory]
    [InlineData("bf8c0104008ae3160000521 2c7001711c70050ba45000000000000000001010116be351ba1010000000000000000000029f0ca5c070000", 0)]
    [InlineData("bf8c0104008ae3160000521 2c7001711c70050ba45000000000000000001010116be351ba1010000010000000000000029f0ca5c070000", 1)]
    public void Object_spawn_decodes_with_units_taken(string hex, int taken)
    {
        var e = Assert.IsType<ObjectSeenEvent>(Assert.Single(Parse(Opcodes.ObjectSpawn, hex.Replace(" ", ""))));
        Assert.Equal(17983u, e.ActorId);
        Assert.Equal(4, e.Kind);
        Assert.Equal(1500042, e.ObjectCode);
        Assert.Equal(-37458, e.X, 0.5);
        Assert.Equal(-37143, e.Y, 0.5);
        Assert.Equal(5962, e.Z, 0.5);
        Assert.Equal(taken, e.Taken);
        Assert.Equal(1791457869334, e.SpawnedAtMs); // 2026-10-08 11:11:09 UTC: the node brotó a minute before it was first seen
    }

    [Theory]
    [InlineData("b28703040300", 50098u, true)]   // Inina 50098 after Akunita took the third unit
    [InlineData("bf8c01010100", 17983u, false)]  // Inina 17983 leaving the client's view
    public void Object_gone_tells_exhausted_from_left_view(string hex, uint id, bool exhausted)
    {
        var e = Assert.IsType<ObjectGoneEvent>(Assert.Single(Parse(Opcodes.ObjectGone, hex)));
        Assert.Equal(id, e.ActorId);
        Assert.Equal(exhausted, e.Exhausted);
    }

    [Theory]
    [InlineData(Opcodes.GatherStart, "b28703b33d01", true, 0)]
    [InlineData(Opcodes.GatherResult, "b28703b33d0100000000000000", false, 1)]
    [InlineData(Opcodes.GatherResult, "b28703b33d0300000000000000", false, 3)]
    public void Gather_start_and_results_decode(ushort op, string hex, bool started, int count)
    {
        var e = Assert.IsType<GatherEvent>(Assert.Single(Parse(op, hex)));
        Assert.Equal(50098u, e.ObjectId);
        Assert.Equal(7859u, e.PlayerId); // Akunita
        Assert.Equal(started, e.Started);
        Assert.Equal(count, e.Count);
    }

    [Theory]
    [InlineData("b33d0000506201c7cef607c70eceba45df7c", 7859u, -33122.3f, -34806.8f, 5977.8f)]                   // 18-byte form
    [InlineData("b33d0c003f3901c7b40208c70000bb45df7c01e803000000000000", 7859u, -33081.2f, -34818.7f, 5984f)]  // 27-byte form
    public void Position_records_decode(string hex, uint id, float x, float y, float z)
    {
        var e = Assert.IsType<PositionEvent>(Assert.Single(Parse(Opcodes.Positions, hex)));
        Assert.Equal(id, e.ActorId);
        Assert.Equal(x, e.X, 0.5);
        Assert.Equal(y, e.Y, 0.5);
        Assert.Equal(z, e.Z, 0.5);
    }

    // The local player's position also rides on state and action records and on most casts (Verteron, 2026-10-08).
    [Theory]
    [InlineData(Opcodes.EntityState, "b33d035aeb0dc779fc0dc70078b84528449d42000001702cb800cb5bf1472d", -36331f, -36348f, 5903f)]
    [InlineData(Opcodes.ActionState, "b33d1138e7000000c80000000000000056d7331ba1010000b33d0102d39100c70c1f07c70000bb45", -32914f, -34591f, 5984f)]
    public void Self_position_from_state_records(ushort op, string hex, float x, float y, float z)
    {
        var p = Assert.IsType<PositionEvent>(Assert.Single(Parse(op, hex)));
        Assert.Equal(7859u, p.ActorId);
        Assert.Equal(x, p.X, 0.6);
        Assert.Equal(y, p.Y, 0.6);
        Assert.Equal(z, p.Z, 0.6);
    }

    [Fact]
    public void Cast_also_gives_the_caster_position_when_it_carries_one()
    {
        var events = Parse(Opcodes.Cast, "c74300 0c28e900 9202 d1e802 b0f930c38dbae8470b71d3c700faa546ea5d01dc6f".Replace(" ", ""));
        Assert.Equal(2, events.Count);
        var p = Assert.IsType<PositionEvent>(events[1]);
        Assert.Equal(8647u, p.ActorId);
        Assert.Equal(119157f, p.X, 0.6);
        Assert.Equal(-108258f, p.Y, 0.6);
        Assert.Equal(21245f, p.Z, 0.6);
    }

    // Altgard's field boss list (map 1110) as the in-game map showed it at 00:30:07 on 2026-10-06 (UTC+3): 24 slots,
    // 3 alive with a position, a few with an extra byte before the time. Checked against the in-game list.
    private const string AltgardList =
        "00005604000018009de30602729d660ea10100000199e30691939bc7645092c700ecdd467f04f30da1010000009fe3066d013f0ea1010000" +
        "00a0e3066efe360ea1010000009ae306c4e9010ea1010000009be306a54a140ea1010000009ee306bc74360ea1010000009ce30630852c0e" +
        "a101000001a1e30600a57447800a0dc800f0db45095a05d40da101000000a2e3061f365b0ea101000000a3e306d4eb670ea101000001a4e3" +
        "06413f42482dc5e5c700b6974683a4f40da101000000ace30650d2de0ea101000000a5e3068054070ea101000000a6e306dae65e0ea10100" +
        "0000a7e3061737050ea101000000a8e306001166e80ea101000000a9e306ef5f4d0ea101000000aae30668a8020ea101000000abe306c284" +
        "d30ea101000000ade306fbccc10ea101000000aee3061cfdc60ea101000000afe306bf00d70ea101000000b0e3063501d10ea10100000000" +
        "00";

    [Fact]
    public void Field_boss_list_decodes_every_slot()
    {
        var list = Assert.IsType<FieldBossListEvent>(Assert.Single(Parse(Opcodes.FieldBossList, AltgardList)));
        Assert.Equal(1110, list.MapId);
        Assert.Equal(Enumerable.Range(111001, 24), list.Slots.Select(s => s.SlotId).Order());
        Assert.Equal([111001, 111009, 111012], list.Slots.Where(s => s.Alive).Select(s => s.SlotId));

        var danar = list.Slots.Single(s => s.SlotId == 111001); // alive since 00:23:12 local
        Assert.InRange(danar.X, -79656f, -79654f);
        Assert.Equal(new DateTime(2026, 10, 5, 21, 23, 12), Trim(danar.AtMs));
        // Immortal Gartua: back at 04:09:04 local, exactly what the game's list said ("3 h 31 min 41 s" at 00:37:23).
        Assert.Equal(new DateTime(2026, 10, 6, 1, 9, 4), Trim(list.Slots.Single(s => s.SlotId == 111021).AtMs));
        // Slots carrying the unexplained extra byte still decode.
        Assert.Equal(new DateTime(2026, 10, 5, 23, 29, 28), Trim(list.Slots.Single(s => s.SlotId == 111005).AtMs));

        static DateTime Trim(long ms)
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second);
        }
    }

    [Fact]
    public void Field_boss_list_reads_slots_without_a_time()
    {
        // List 20 (8 scheduled bosses, 2026-10-06 01:07): slot 2001 carries the extra byte, slot 2002 has no time (0).
        const string hex =
            "000014000000080" + "0d10f00f187f20ea1010000" + "00d20f0000000000000000" + "00d30f60eb0d22a1010000" +
            "00d40f60eb0d22a1010000" + "00d50f60eb0d22a1010000" + "00d60fa006031da1010000" + "00d80fa006031da1010000" +
            "00d70fa006031da1010000" + "000000";
        var list = Assert.IsType<FieldBossListEvent>(Assert.Single(Parse(Opcodes.FieldBossList, hex)));
        Assert.True(list.Complete);
        Assert.Equal(20, list.MapId);
        Assert.Equal([2001, 2002, 2003, 2004, 2005, 2006, 2008, 2007], list.Slots.Select(s => s.SlotId));
        Assert.Equal(0, list.Slots[1].AtMs);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 2, 2, 17, TimeSpan.Zero), DateTimeOffset.FromUnixTimeMilliseconds(list.Slots[0].AtMs / 1000 * 1000));
        Assert.All(list.Slots.Skip(2).Take(3), s => Assert.Equal(new DateTimeOffset(2026, 10, 9, 19, 5, 0, TimeSpan.Zero), DateTimeOffset.FromUnixTimeMilliseconds(s.AtMs)));
    }

    [Fact]
    public void Ownerless_skill_effect_spawn_is_announced_as_a_summon()
    {
        // Bittercold Wind entity 42321: kind 0x1F, no name, no owner block.
        var e = Parse(Opcodes.Spawn, "d1ca021f0000cf902c0040025ec7ea47f11ed1c70048a546a263c741b911019d549d54d20f0000d20f0000" +
                                     "0000000000000000000000000000c8f401006400000000f049020001000000");
        var s = Assert.IsType<SummonSeenEvent>(Assert.Single(e));
        Assert.Equal(42321u, s.ActorId);
        Assert.Equal(0u, s.OwnerId);
        Assert.Null(s.OwnerName);
    }

    [Fact]
    public void Layout6_block_carries_position_and_quality()
    {
        // mods 0x80 (no known bit), dir 0x01 = back
        var d = (DamageEvent)Parse(Opcodes.Damage, "b1ea011600f30a40c0f40063028000010b199b5f01000000ac52d007")[0];
        Assert.True(d.Flags.HasFlag(HitFlags.Back));
        Assert.False(d.Flags.HasFlag(HitFlags.Perfect));
        Assert.Equal(16040000, d.SkillCode);
    }

    [Fact]
    public void Self_record_gives_name_server_class_level()
    {
        // Nimara, entity 14957, server 1304, class 30 = Cleric, level 28 (EU capture 2026-10-04).
        var e = Parse(Opcodes.SelfInfo, "ed745e91c12837064e696d61726118051e000000011c0000007f0100007f0100001c000000d002040000000000");
        var s = Assert.IsType<SelfIdentifiedEvent>(Assert.Single(e));
        Assert.Equal(14957u, s.ActorId);
        Assert.Equal("Nimara", s.Name);
        Assert.Equal(1304, s.ServerId);
        Assert.Equal(GameClass.Cleric, s.Class);
        Assert.Equal(28, s.Level);
    }

    [Fact]
    public void Map_loads_and_teleports()
    {
        var events = new List<GameEvent>();
        var parser = new PacketParser(GameData.Empty, events.Add);
        parser.Handle(Opcodes.MapLoad, Wire.FromHex("01000000d52709003b1a350000000000f7e646460d7fb0c6"));
        parser.Handle(Opcodes.MapLoad, Wire.FromHex("02000000d5270900d74c390000000000a8f805c610861245"));
        parser.Handle(Opcodes.MapLoad, Wire.FromHex("01000000f2030000dd7f3c00000000006868d047d0c62c47"));
        var z = events.Cast<ZoneChangedEvent>().ToList();
        Assert.Equal([600021, 600021, 1010], z.Select(x => x.MapId));
        Assert.Equal([false, true, false], z.Select(x => x.IsTeleport));
        Assert.True(z[0].IsDungeon);
        Assert.False(z[2].IsDungeon);
    }

    [Fact]
    public void Dot_heal_and_damage_are_separated_by_effect_type()
    {
        // target 30001, effect, actor 1395, unknown 0, skill 16040000 ×100, amount 500
        var skill = BitConverter.GetBytes(1_604_000_000u);
        string Rec(byte effect) => "b1ea01" + effect.ToString("x2") + "f30a00" + Convert.ToHexString(skill) + "f403";
        Assert.IsType<DamageEvent>(Assert.Single(Parse(Opcodes.Dot, Rec(0x02))));
        Assert.IsType<HealEvent>(Assert.Single(Parse(Opcodes.Dot, Rec(0x0B))));
        Assert.Empty(Parse(Opcodes.Dot, Rec(0x08))); // buff tick
        var d = (DamageEvent)Parse(Opcodes.Dot, Rec(0x0A))[0];
        Assert.Equal(500, d.Damage);
        Assert.Equal(16040000, d.SkillCode);
        Assert.True(d.Flags.HasFlag(HitFlags.Dot));
    }

    // ------------------------------------------------------------------ framing

    private static byte[] Varint(uint v)
    {
        var list = new List<byte>();
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            list.Add(b);
        } while (v != 0);
        return list.ToArray();
    }

    /// <summary>Frame = varint(len) + payload where len = payload length + 4.</summary>
    private static byte[] Frame(byte[] payload) => [.. Varint((uint)payload.Length + 4), .. payload];

    private static byte[] Packet(ushort opcode, byte[] body) => Frame([(byte)(opcode >> 8), (byte)opcode, .. body]);

    private static readonly byte[] Heartbeat = Packet(Opcodes.Heartbeat, new byte[8]);

    [Fact]
    public void Heartbeat_frame_matches_signature()
    {
        Assert.Equal(11, Heartbeat.Length);
        Assert.Equal(new byte[] { 0x0E, 0x00, 0x36 }, Heartbeat[..3]);
    }

    [Fact]
    public void Decoder_handles_padding_split_segments_and_large_frames()
    {
        var seen = new List<(ushort Op, int Len)>();
        var dec = new FrameDecoder((op, body) => seen.Add((op, body.Length)));
        var big = Packet(0x1234, new byte[300]); // 2-byte length varint
        byte[] stream = [.. Heartbeat, 0x00, 0x00, .. big, .. Heartbeat];
        dec.Feed(stream.AsSpan(0, 7));
        dec.Feed(stream.AsSpan(7, 100));
        dec.Feed(stream.AsSpan(107));
        Assert.Equal([(Opcodes.Heartbeat, 8), ((ushort)0x1234, 300), (Opcodes.Heartbeat, 8)], seen);
    }

    [Fact]
    public void Decoder_unpacks_lz4_bundles()
    {
        byte[] inner = [.. Packet(0x4136, new byte[20]), .. Packet(0x0438, new byte[40])];
        var compressed = new byte[LZ4Codec.MaximumOutputSize(inner.Length)];
        var n = LZ4Codec.Encode(inner, compressed);
        byte[] bundle = Frame([0xFF, 0xFF, .. BitConverter.GetBytes((uint)inner.Length), .. compressed[..n]]);

        var seen = new List<ushort>();
        var dec = new FrameDecoder((op, _) => seen.Add(op));
        dec.Feed([.. Heartbeat, .. bundle, .. Heartbeat]);
        Assert.Equal([Opcodes.Heartbeat, (ushort)0x4136, (ushort)0x0438, Opcodes.Heartbeat], seen);
        Assert.Equal(1, dec.Bundles);
    }

    [Fact]
    public void Decoder_resyncs_on_heartbeat_after_a_gap()
    {
        var seen = new List<ushort>();
        var dec = new FrameDecoder((op, _) => seen.Add(op));
        var dmg = Packet(Opcodes.Damage, new byte[30]);
        dec.Feed(dmg.AsSpan(0, 10)); // half a frame, then the rest is lost
        dec.Reset();
        dec.Feed([.. dmg.AsSpan(15), .. Heartbeat, .. Packet(0x4136, new byte[5])]);
        Assert.Equal([Opcodes.Heartbeat, (ushort)0x4136], seen);
    }

    [Fact]
    public void Decoder_started_mid_stream_waits_for_heartbeat()
    {
        var seen = new List<ushort>();
        var dec = new FrameDecoder((op, _) => seen.Add(op), startsAligned: false);
        var dmg = Packet(Opcodes.Damage, new byte[30]);
        dec.Feed([.. dmg.AsSpan(5), .. Heartbeat, .. Packet(0x4136, new byte[5])]);
        Assert.Equal([Opcodes.Heartbeat, (ushort)0x4136], seen);
    }
}
