using System.Net;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Game;

namespace AionMeter.Tests;

public class EngineTests
{
    [Fact]
    public void Reassembler_reorders_trims_and_skips_lost_data()
    {
        var output = new List<byte>();
        var gaps = 0;
        var r = new TcpReassembler(d => output.AddRange(d.ToArray()), () => gaps++);
        r.Start(100);
        r.Push(105, [5, 6, 7], 0);        // out of order
        r.Push(100, [0, 1, 2, 3, 4], 0);  // fills the hole
        r.Push(103, [3, 4, 5, 6], 0);     // duplicate
        r.Push(108, [8, 9], 0);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (byte)i), output);

        r.Push(120, [20], 0);             // 110..119 never arrive
        r.Push(121, [21], 5_000);
        Assert.Equal(1, gaps);
        Assert.Equal([20, 21], output.Skip(10));
    }

    private static CombatTracker Tracker(TargetMode mode = TargetMode.BossOnly) =>
        new(GameData.Empty, new MeterOptions { TargetMode = mode });

    private const uint Me = 100, Mate = 101, Boss = 500, Add = 501, Pet = 600;

    private static void Setup(CombatTracker t, long time = 0)
    {
        t.Process(new SelfIdentifiedEvent(time, Me, "Ilvane", 1304, GameClass.Elementalist));
        t.Process(new PlayerSeenEvent(time, Mate, "Borgrim", 1304, GameClass.Gladiator));
        t.Process(new NpcSeenEvent(time, Boss, 2300243, 120_000_000));
        t.Process(new NpcSeenEvent(time, Add, 2000002, 50_000));
        t.Process(new SummonSeenEvent(time, Pet, Me, null, 0));
    }

    [Fact]
    public void Boss_fight_ends_on_kill_and_credits_summons_to_owner()
    {
        var t = Tracker();
        FightRecord? saved = null;
        t.EncounterFinished += r => saved = r;
        Setup(t);

        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 1_000_000, HitFlags.Critical));
        t.Process(new DamageEvent(2_000, Pet, Boss, 16900000, 500_000, HitFlags.None));
        t.Process(new DamageEvent(3_000, Mate, Boss, 11020000, 2_000_000, HitFlags.Back));
        t.Process(new DamageEvent(3_500, Me, Add, 16040000, 9_999, HitFlags.None)); // add during boss: ignored
        t.Process(new DamageEvent(4_000, Boss, Mate, 2_300_001, 30_000, HitFlags.None)); // boss hits back
        t.Process(new NpcHpEvent(5_000, Boss, 0, 120_000_000));

        Assert.NotNull(saved);
        var s = saved!.Summary;
        Assert.Equal(EncounterEndReason.Kill, s.Reason);
        Assert.Equal(3_500_000, s.TotalDamage);
        Assert.Equal(2, s.Combatants.Count);
        var me = s.Combatants.Single(c => c.ActorId == Me);
        Assert.True(me.IsSelf);
        Assert.Equal(1_500_000, me.Damage);
        Assert.Equal(30_000, s.Combatants.Single(c => c.ActorId == Mate).DamageTaken);
        Assert.Equal(1_500_000 / 2.0, me.Dps, 3); // 1 s → 3 s fight window is 2 s... first hit to last outgoing hit
    }

    [Fact]
    public void Trailing_dot_after_kill_does_not_open_new_segment()
    {
        var t = Tracker();
        Setup(t);
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 1_000, HitFlags.None));
        t.Process(new DeathEvent(2_000, Boss));
        t.Process(new DamageEvent(2_100, Me, Boss, 16040000, 50, HitFlags.Dot));
        var segments = t.Segments();
        Assert.Single(segments);
        Assert.False(segments[0].IsActive);
    }

    [Fact]
    public void Wipe_is_detected_when_boss_heals_to_full()
    {
        var t = Tracker();
        Setup(t);
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 50_000_000, HitFlags.None));
        t.Process(new NpcHpEvent(1_100, Boss, 70_000_000, 120_000_000));
        t.Process(new NpcHpEvent(9_000, Boss, 120_000_000, 120_000_000));
        Assert.Equal(EncounterEndReason.Wipe, t.Segments()[0].Reason);
    }

    [Fact]
    public void Idle_timeout_closes_trash_segment()
    {
        var t = Tracker(TargetMode.All);
        Setup(t);
        t.Process(new DamageEvent(1_000, Me, Add, 16040000, 1_000, HitFlags.None));
        t.Tick(20_000);
        Assert.Equal(EncounterEndReason.Idle, t.Segments()[0].Reason);
        t.Process(new DamageEvent(21_000, Me, Add, 16040000, 1_000, HitFlags.None));
        Assert.Equal(2, t.Segments().Count);
        Assert.True(t.Segments()[0].IsActive);
    }

    [Fact]
    public void Unnamed_player_using_item_skill_still_counts_on_boss()
    {
        // Players already in the zone when the meter started have no identity record. One of them fires a
        // 7-digit godstone proc during trash, then the boss is pulled in boss-only mode.
        var t = Tracker();
        t.Process(new NpcSeenEvent(0, Boss, 2300243, 120_000_000));
        t.Process(new NpcSeenEvent(0, Add, 2000002, 50_000));
        t.Process(new DamageEvent(1_000, 13320, Add, 16040000, 1_000, HitFlags.None));
        t.Process(new DamageEvent(1_100, 13320, Add, 3_050_001, 500, HitFlags.None));
        t.Process(new DamageEvent(2_000, 13304, Boss, 18040000, 7_000, HitFlags.None));
        t.Process(new DamageEvent(2_100, 13320, Boss, 16040000, 9_000, HitFlags.None));
        var boss = t.Snapshot(null, 3_000)!;
        Assert.Equal(16_000, boss.TotalDamage);
        Assert.Equal(2, boss.Combatants.Count);
    }

    [Fact]
    public void Unannounced_summon_is_folded_into_owner_by_power_scalar()
    {
        var t = Tracker(TargetMode.All);
        t.Process(new SelfIdentifiedEvent(0, Me, "Celesta", 2305, GameClass.Cleric));
        int[] rotation = [17010000, 17020000, 17030000, 17040000, 17050000, 17060000];
        for (var i = 0; i < 12; i++)
            t.Process(new DamageEvent(1_000 + i * 100, Me, Boss, rotation[i % rotation.Length], 1_000, HitFlags.None, 1, 18_500));
        for (var i = 0; i < 4; i++) // Divine Aura entity, never spawned, owner scalar
            t.Process(new DamageEvent(1_050 + i * 200, 777, Boss, 17153450, 300, HitFlags.None, 1, 18_500));
        t.Tick(2_500);
        var snap = t.Snapshot(null, 2_500)!;
        var me = Assert.Single(snap.Combatants);
        Assert.Equal(13_200, me.Damage);
    }

    [Fact]
    public void Cached_names_survive_restart_but_not_zone_change_or_class_mismatch()
    {
        var first = Tracker(TargetMode.All);
        first.Process(new SelfIdentifiedEvent(0, Me, "Sylvaen", 2305, GameClass.Elementalist));
        first.Process(new PlayerSeenEvent(0, Mate, "Quill", 2305, GameClass.Sorcerer));
        first.Process(new PlayerSeenEvent(0, 102, "Faelis", 2305, GameClass.Elementalist));
        var cache = first.ExportCache();

        // Restart in the same zone: names come back.
        var second = Tracker(TargetMode.All);
        second.ImportCache(cache);
        second.Process(new DamageEvent(1_000, Mate, Boss, 15020000, 1_000, HitFlags.None));
        // Id 102 now fights with Gladiator skills: it is someone else, the cached "Faelis" must go.
        second.Process(new DamageEvent(1_100, 102, Boss, 11020000, 1_000, HitFlags.None));
        var snap = second.Snapshot(null, 2_000)!;
        Assert.Contains(snap.Combatants, c => c.Name == "Quill");
        Assert.DoesNotContain(snap.Combatants, c => c.Name == "Faelis");

        // A loading screen reissues ids: only our own name is kept.
        second.Process(new ZoneChangedEvent(3_000, 600021, "Fire Temple", true));
        Assert.Equal(["Sylvaen"], second.ExportCache().Players.Select(p => p.Name));
    }

    /// <summary>A meter that saw the boss come into view, then restarted mid-fight (the button, an update, a crash).</summary>
    private static SessionState CacheWithBoss(long maxHp)
    {
        var before = Tracker();
        before.Process(new SelfIdentifiedEvent(0, Me, "Sylvaen", 2305, GameClass.Elementalist));
        before.Process(new NpcSeenEvent(0, Boss, 2300218, maxHp));
        before.Process(new NpcSeenEvent(0, Add, 2000002, 50_000)); // not a boss: not worth keeping
        return before.ExportCache();
    }

    [Fact]
    public void Restart_mid_fight_keeps_the_boss_and_says_how_much_went_uncounted()
    {
        var cache = CacheWithBoss(10_000_000);
        Assert.Equal([new CachedNpc(Boss, 2300218, 10_000_000)], cache.Npcs);

        var t = Tracker();
        t.ImportCache(cache);
        // The boss lost 2M before the restart; the restarted meter counts the next 1M.
        t.Process(new NpcHpEvent(1_000, Boss, 8_000_000, 0));
        t.Process(new DamageEvent(1_100, Me, Boss, 16040000, 1_000_000, HitFlags.None));
        t.Process(new NpcHpEvent(1_200, Boss, 7_000_000, 0));

        var boss = t.Snapshot(null, 2_000)!.Boss!;
        Assert.Equal(2300218, boss.NpcCode);
        Assert.Equal(10_000_000, boss.MaxHp);
        Assert.True(boss.MaxHpKnown);
        Assert.Equal(0.7, boss.HpFraction, 3);
        Assert.Equal(2_000_000, boss.Uncounted);
        Assert.Equal(0.8, boss.CountedFrom!.Value, 3);
    }

    [Fact]
    public void Boss_never_seen_appearing_has_an_unknown_max_hp()
    {
        var t = Tracker();
        t.Process(new SelfIdentifiedEvent(0, Me, "Sylvaen", 2305, GameClass.Elementalist));
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 100_000, HitFlags.None));
        t.Process(new NpcHpEvent(1_100, Boss, 8_078_420, 0)); // first sight, mid-fight: its real max is higher

        var boss = t.Snapshot(null, 2_000)!.Boss!;
        Assert.Equal(0, boss.NpcCode);
        Assert.Equal(8_078_420, boss.MaxHp);
        Assert.False(boss.MaxHpKnown);
        Assert.Null(boss.CountedFrom);
        Assert.Empty(t.ExportCache().Npcs); // a guessed max is not worth keeping
    }

    [Fact]
    public void Fully_counted_kill_has_nothing_uncounted()
    {
        var t = Tracker();
        Setup(t);
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 40_000_000, HitFlags.None));
        t.Process(new NpcHpEvent(1_100, Boss, 80_000_000, 0));
        t.Process(new DamageEvent(2_000, Mate, Boss, 11020000, 40_000_000, HitFlags.None));
        t.Process(new DamageEvent(2_050, Me, Boss, 16040000, 40_000_000, HitFlags.None));
        FightRecord? saved = null;
        t.EncounterFinished += r => saved = r;
        t.Process(new NpcHpEvent(2_100, Boss, 0, 120_000_000));

        Assert.Equal(0, saved!.Summary.Boss!.Uncounted);
        Assert.Null(saved.Summary.Boss.CountedFrom);
    }

    [Fact]
    public void Cached_boss_is_forgotten_when_its_id_now_belongs_to_another_npc()
    {
        var t = Tracker();
        t.ImportCache(CacheWithBoss(10_000_000));
        // More HP than the cached boss ever had: ids were reissued since the cache was written.
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 1_000, HitFlags.None));
        t.Process(new NpcHpEvent(1_100, Boss, 30_000_000, 0));

        var boss = t.Snapshot(null, 2_000)!.Boss!;
        Assert.Equal(0, boss.NpcCode);
        Assert.False(boss.MaxHpKnown);
        Assert.Equal($"#{Boss}", boss.Name);
    }

    [Fact]
    public void Fresh_announcement_replaces_the_cached_boss()
    {
        var t = Tracker();
        t.ImportCache(CacheWithBoss(10_000_000));
        t.Process(new NpcSeenEvent(500, Boss, 2300206, 5_220_000));
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 1_000, HitFlags.None));

        var boss = t.Snapshot(null, 2_000)!.Boss!;
        Assert.Equal(2300206, boss.NpcCode);
        Assert.Equal(5_220_000, boss.MaxHp);
    }

    [Fact]
    public void Reset_saves_the_running_fight_and_keeps_what_the_meter_knows()
    {
        var t = Tracker();
        Setup(t);
        FightRecord? saved = null;
        t.EncounterFinished += r => saved = r;
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 1_000_000, HitFlags.None));
        t.Process(new NpcHpEvent(1_100, Boss, 119_000_000, 0));

        t.Reset();
        Assert.Equal(EncounterEndReason.Manual, saved!.Summary.Reason);
        Assert.Null(t.Snapshot(null, 2_000)); // the live view starts empty

        t.Process(new DamageEvent(3_000, Mate, Boss, 11020000, 2_000_000, HitFlags.None));
        t.Process(new NpcHpEvent(3_100, Boss, 117_000_000, 0));
        var snap = t.Snapshot(null, 4_000)!;
        Assert.Equal("Borgrim", Assert.Single(snap.Combatants).Name);
        Assert.Equal(120_000_000, snap.Boss!.MaxHp);
        Assert.Equal(1_000_000, snap.Boss.Uncounted); // the first fight's hit, now in its own saved segment
    }

    [Fact]
    public void Skill_effect_is_tied_to_its_caster_by_the_cast_variant()
    {
        // Two unnamed Sorcerers cast Bittercold Wind a moment apart; their skill is levelled differently.
        var t = Tracker(TargetMode.All);
        t.Process(new SelfIdentifiedEvent(0, Me, "Sylvaen", 2305, GameClass.Elementalist));
        t.Process(new DamageEvent(500, 201, Boss, 15020000, 1_000, HitFlags.None));
        t.Process(new DamageEvent(500, 202, Boss, 15020000, 1_000, HitFlags.None));
        t.Process(new CastEvent(1_000, 201, Boss, 15280140));
        t.Process(new CastEvent(1_100, 202, Boss, 15280340));
        // The wind spawns with no owner, hits, then announces its own tick with 201's variant.
        t.Process(new SummonSeenEvent(1_450, 9001, 0, null, 2920655));
        t.Process(new DamageEvent(1_480, 9001, Boss, 15280000, 2_000, HitFlags.None));
        t.Process(new CastEvent(1_500, 9001, 9001, 15280141));

        var snap = t.Snapshot(null, 2_000)!;
        Assert.Equal(2, snap.PlayerCount);
        Assert.Equal(3_000, snap.Combatants.Single(c => c.ActorId == 201).Damage);
    }

    [Fact]
    public void Pets_without_an_owner_form_one_row_that_is_not_a_player()
    {
        var t = Tracker(TargetMode.All);
        t.Process(new SelfIdentifiedEvent(0, Me, "Sylvaen", 2305, GameClass.Elementalist));
        t.Process(new DamageEvent(1_000, Me, Boss, 16040000, 10_000, HitFlags.None));
        for (uint id = 9001; id <= 9003; id++)
        {
            t.Process(new SummonSeenEvent(1_000, id, 0, null, 2920202));
            t.Process(new DamageEvent(1_200, id, Boss, 15280000, 500, HitFlags.None)); // class skill, still not a player
        }
        t.Process(new DamageEvent(1_300, 9100, Boss, 100014, 200, HitFlags.None)); // a spirit spawned before we looked

        t.Tick(5_000); // owners get a few seconds to show up before the pets are set aside
        var snap = t.Snapshot(null, 5_000)!;
        Assert.Equal(1, snap.PlayerCount);
        var pets = Assert.Single(snap.Combatants, c => c.IsUnknownSummons);
        Assert.Equal(1_700, pets.Damage);
        Assert.Same(pets, snap.Combatants[^1]);
    }

    [Fact]
    public void World_boss_sightings_and_deaths_reach_the_respawn_timers()
    {
        var data = GameData.Empty;
        data.Npcs[2400419] = new NpcDef(2400419, "Special Operations Leader Linx", IsBoss: true, IsDummy: false);
        var t = new CombatTracker(data, new MeterOptions());
        var notices = new List<BossNotice>();
        t.BossNoticed += notices.Add;
        t.Process(new NpcSeenEvent(0, Boss, 2400419, 160_000_000));
        t.Process(new NpcHpEvent(1_000, Boss, 1_000, 160_000_000));
        t.Process(new NpcHpEvent(2_000, Boss, 0, 160_000_000));
        t.Process(new DeathEvent(2_050, Boss)); // the same death again: reported once
        t.Process(new NpcSeenEvent(3_000, Add, 2000002, 50_000)); // not a boss
        Assert.Equal([BossNoticeKind.Alive, BossNoticeKind.Killed], notices.Select(n => n.Kind));
        Assert.All(notices, n => Assert.Equal(2400419, n.NpcCode));
    }

    [Fact]
    public void Field_boss_slots_are_the_map_block_in_code_order()
    {
        // Altgard's 24 field bosses (2400xxx), plus training dummies in the same block that the list leaves out.
        int[] altgard =
        [
            2400017, 2400074, 2400140, 2400141, 2400212, 2400223, 2400274, 2400335, 2400353, 2400358, 2400419, 2400424,
            2400425, 2400474, 2400504, 2400593, 2400607, 2400608, 2400659, 2400709, 2400800, 2400853, 2400854, 2400855,
        ];
        var data = GameData.Empty;
        foreach (var c in altgard) data.Npcs[c] = new NpcDef(c, $"boss {c}", IsBoss: true, IsDummy: false);
        foreach (var c in new[] { 2400032, 2400035, 2400392 }) data.Npcs[c] = new NpcDef(c, "Training Scarecrow", true, true);
        data.Npcs[2101122] = new NpcDef(2101122, "Soul Ruler Kashapa (Elyos)", true, false);

        Assert.Equal(2400800, data.FieldBossInSlot(2400, 1110, 111021, 24)); // Immortal Gartua
        Assert.Equal(2400017, data.FieldBossInSlot(2400, 1110, 111001, 24)); // Melted Danar
        Assert.Equal(0, data.FieldBossInSlot(2400, 1110, 111001, 23));       // count mismatch: not this block
    }

    [Fact]
    public void Field_boss_slot_from_shipped_list()
    {
        // Verteron's 24 field bosses span two code blocks (2100003 … 2101131) and block 2100 holds bosses the list
        // leaves out, so the block rule cannot name them: data/field_boss_maps.json lists them in slot order instead.
        // Checked on the 2026-10-08 EU capture against the in-game list: alive flags and all 20 respawn times agreed.
        int[] verteron =
        [
            2100003, 2100040, 2100050, 2100076, 2100077, 2100079, 2100141, 2100177, 2100178, 2100582, 2100617, 2100661,
            2100708, 2100718, 2100876, 2100877, 2100988, 2100989, 2100991, 2101016, 2101074, 2101120, 2101122, 2101131,
        ];
        Assert.Equal(2100003, GameData.FieldBossInSlot(verteron, 1010, 101001, 24)); // Neikel of the East
        Assert.Equal(2100141, GameData.FieldBossInSlot(verteron, 1010, 101007, 24)); // Blooming Korin
        Assert.Equal(2101131, GameData.FieldBossInSlot(verteron, 1010, 101024, 24)); // High Commander Lagta
        Assert.Equal(0, GameData.FieldBossInSlot(verteron, 1010, 101025, 24));       // past the list
        Assert.Equal(0, GameData.FieldBossInSlot(verteron, 1010, 101001, 23));       // count mismatch
    }

    [Fact]
    public void Npc_skill_marks_unknown_source_as_mob()
    {
        var t = Tracker(TargetMode.All);
        t.Process(new SelfIdentifiedEvent(0, Me, "Ilvane", 1304, GameClass.Elementalist));
        t.Process(new DamageEvent(1_000, Me, 777, 16040000, 1_000, HitFlags.None));
        t.Process(new DamageEvent(1_100, 778, Me, 2_100_001, 300, HitFlags.None)); // unseen mob hits me
        var snap = t.Snapshot(null, 2_000)!;
        Assert.Single(snap.Combatants);
        Assert.Equal(300, snap.Combatants[0].DamageTaken);
    }

    [Fact]
    public void Pipeline_locks_on_heartbeats_and_replays_buffered_identity()
    {
        var events = new List<GameEvent>();
        var p = new PacketPipeline(GameData.Empty, events.Add) { LockThreshold = 3 };
        var key = new FlowKey(IPAddress.Parse("193.202.112.99"), 13328, IPAddress.Parse("192.168.1.5"), 50000);
        byte[] Frame(byte[] payload) => [(byte)(payload.Length + 4), .. payload];
        var hb = Frame([0x00, 0x36, 0, 0, 0, 0, 0, 0, 0, 0]);
        var self = Frame([0x33, 0x36, .. AionMeter.Core.Protocol.Wire.FromHex("ed745e91c12837064e696d61726118051e000000011c000000")]);

        uint seq = 1000;
        p.OnSegment(0, key, seq - 1, syn: true, finOrRst: false, []);
        foreach (var chunk in new[] { self, hb, hb, hb, hb })
        {
            p.OnSegment(10, key, seq, false, false, chunk);
            seq += (uint)chunk.Length;
        }
        Assert.Equal(1, p.GameFlows);
        Assert.Contains(events, e => e is SelfIdentifiedEvent { Name: "Nimara" });
    }
}
