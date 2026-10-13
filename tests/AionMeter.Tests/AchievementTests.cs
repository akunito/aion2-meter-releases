using AionMeter.Core.Character;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Protocol;

namespace AionMeter.Tests;

/// <summary>
/// A2M-15 achievements and content tickets (docs/27). Payloads cut from our captures: Akunita's login 2026-10-11 14:03:44 (83 56, 0b 61),
/// the claim of "Sovereign of the Skies VI" (320101200) on 2026-10-10 21:08:26 (8a 56), a play-time counter (84 56, 21:09:29), a multi-group
/// claim (14:03:12), the Odyle delta at 14:00:23 and the seal-dungeon timer running out at 15:05:19 (0c 61). Times local (CEST).
/// </summary>
public class AchievementTests
{
    private static byte[] Bin(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "packets", name));
    private static AchievementCatalog Catalog() => AchievementCatalog.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "character", "achievements-catalog.json")));

    private static T One<T>(ushort op, byte[] body, long t = 1_000) where T : GameEvent
    {
        var events = new List<GameEvent>();
        new PacketParser(GameData.Empty, events.Add) { TimeMs = t }.Handle(op, body);
        return Assert.IsType<T>(Assert.Single(events.OfType<T>()));
    }

    [Fact]
    public void Login_list_reads_every_row_and_the_tab_reward_tracks()
    {
        var e = One<AchievementListEvent>(Opcodes.AchievementList, Bin("ach-8356-akunita-20261011.bin"));
        Assert.Equal(2327, e.Rows.Count);   // the old reader stopped at 1,512 (ids 100–199 M only)
        var byId = e.Rows.ToDictionary(r => r.Id);
        Assert.Equal(new AchievementRow(320101200, 300, 1791659068007, 6), byId[320101200]);   // claimed on 10-10 21:08:26: step 6
        Assert.Equal(2, byId[300100300].Step);                                                // "Los cinco destinos ocultos II", claimed 10-10 22:31
        // the window's completed counter (467 on 10-10 22:32) = claimed tiers summed over the achievement rows
        Assert.Equal(467, e.Rows.Where(r => r.Id is >= 300_000_000 and < 400_000_000).Sum(r => r.Step));
        Assert.NotNull(e.Groups);
        Assert.Equal(23, e.Groups!.Count);
        Assert.Equal(300000101, e.Groups[0].Id);
        Assert.Equal([true, false, false, false, false, false], e.Groups[0].Claimed);
        Assert.Equal([true, true, true, true, true, true, false], e.Groups.Single(g => g.Id == 300000302).Claimed);
    }

    [Fact]
    public void Old_login_fixture_now_reads_2327_rows()
    {
        var hex = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "character", "achievements-8356.hex")).Trim();
        var e = One<AchievementListEvent>(Opcodes.AchievementList, Convert.FromHexString(hex));
        Assert.Equal(2327, e.Rows.Count);
        Assert.Equal(23, e.Groups!.Count);
        Assert.Equal(446, e.Rows.Where(r => r.Id is >= 300_000_000 and < 400_000_000).Sum(r => r.Step));   // 2026-10-08
    }

    [Fact]
    public void Live_update_moves_the_counter_and_a_claim_moves_the_step()
    {
        var state = new CharacterState(null);
        state.OnEvent(One<AchievementListEvent>(Opcodes.AchievementList, Bin("ach-8356-akunita-20261011.bin")));
        var v0 = state.ProgressVersion;

        var upd = One<AchievementUpdateEvent>(Opcodes.AchievementUpdate, Bin("ach-8456.bin"));
        Assert.Equal(new AchievementRow(301100300, 5297, 1791659368108, 6), Assert.Single(upd.Rows));
        state.OnEvent(upd);
        Assert.Equal(5297, state.Achievements[301100300].Count);

        var claim = One<AchievementClaimEvent>(Opcodes.AchievementClaim, Bin("ach-8a56-claim.bin"));
        Assert.Equal((300000301, 320101200, 6), Assert.Single(claim.Claims));
        state.OnEvent(claim with { Claims = [(300000301, 320101200, 7)] });
        Assert.Equal(7, state.Achievements[320101200].Step);
        Assert.Equal(300, state.Achievements[320101200].Count);   // a claim keeps the counter
        Assert.True(state.ProgressVersion > v0);
        Assert.Equal(2327, state.Achievements.Count);

        var multi = One<AchievementClaimEvent>(Opcodes.AchievementClaim, Bin("ach-8a56-multi.bin"));
        Assert.Equal(9, multi.Claims.Count);
        Assert.Contains((300000302, 321102100, 1), multi.Claims);   // the group that carries a bool and four claims
        Assert.Contains((300000304, 323100700, 6), multi.Claims);
    }

    [Fact]
    public void Progress_against_the_catalogue()
    {
        var cat = Catalog();
        var sky = cat.Get(320101200)!;
        Assert.Equal(300, sky.Tiers[5].Goal);
        var before = AchievementCatalog.Progress(new AchievementRow(320101200, 300, 0, 5), sky);
        Assert.Equal(AchievementState.Claimable, before.State);
        Assert.Equal((6, 300, 1), (before.NextTier, before.Goal, before.ClaimableTiers));
        Assert.Equal("Sovereign of the Skies", sky.NameEn);   // questlog's tier-1 numeral stripped
        Assert.Equal(6, sky.Tiers.Length);                       // so the claim of 10-10 finished it
        Assert.Equal(AchievementState.Done, AchievementCatalog.Progress(new AchievementRow(320101200, 300, 0, 6), sky).State);
        var fates = AchievementCatalog.Progress(new AchievementRow(300100300, 10, 0, 2), cat.Get(300100300));
        Assert.Equal(AchievementState.InProgress, fates.State);
        Assert.Equal((3, 30), (fates.NextTier, fates.Goal));
        Assert.True(fates.Percent is > 0.3 and < 0.34);
        Assert.Equal(AchievementState.NotStarted, AchievementCatalog.Progress(new AchievementRow(320101200, 0, 0, 0), sky).State);
        Assert.Equal(AchievementState.Unknown, AchievementCatalog.Progress(new AchievementRow(120000100, 3, 0, 0), null).State);

        var sum = cat.Summarise([new AchievementRow(320101200, 300, 0, 5), new AchievementRow(300100300, 10, 0, 2), new AchievementRow(910430800, 1, 0, 0)]);
        Assert.Equal(2, sum.Achievements);   // 910430800 is a Daeva Pass mission, not the achievement window
        Assert.Equal(1, sum.Claimable);
        Assert.Equal(320101200, Assert.Single(sum.ClaimableList).Row.Id);
        Assert.Equal(7, sum.TiersClaimed);
    }

    [Fact]
    public void Achievements_json_is_written()
    {
        var state = new CharacterState(null);
        state.OnEvent(One<AchievementListEvent>(Opcodes.AchievementList, Bin("ach-8356-akunita-20261011.bin")));
        var path = Path.Combine(Path.GetTempPath(), "aionmeter-ach-" + Guid.NewGuid().ToString("N"), "achievements.json");
        AchievementStore.Write(path, state.Achievements, state.AchievementGroups, Catalog(), id => id == 930100001 ? "Kina (Bound)" : null, DateTimeOffset.UnixEpoch);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var rows = doc.RootElement.GetProperty("rows");
        var sky = rows.EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == 320101200);
        Assert.Equal(300, sky.GetProperty("count").GetInt32());
        Assert.Equal(6, sky.GetProperty("claimedStep").GetInt32());
        Assert.Equal(23, doc.RootElement.GetProperty("groups").GetArrayLength());
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Ticket_snapshot_and_deltas()
    {
        var snap = One<ContentTicketsEvent>(Opcodes.TicketList, Bin("tickets-0b61.bin"));
        Assert.True(snap.Snapshot);
        Assert.Equal(73, snap.Tickets.Count);
        var k = snap.Tickets.ToDictionary(t => t.Key);
        Assert.Equal((240, 110), (k[60_000_001].Base, k[60_000_001].Bonus));   // Odyle 240 (+110)
        Assert.Equal(14, k[1].Total);                                           // Shugo keys at the cap
        Assert.Equal(23_916_000, k[3].Time);                                    // a 7 h time ticket, 21.4 min spent
        Assert.True(k[3].IsTime);
        Assert.Equal(0, k[6].Mask);                                             // spent: no fields at all
        Assert.Equal(1, k[90_000_002].Base);                                    // Ludra boss clear left this week
        Assert.Equal("Odyle energy", TicketBook.Of(60_000_001).En);
        Assert.Equal("Krao Cave (exploration)", TicketBook.Of(60_000_101).En);
        Assert.Equal(TicketBook.Explore, TicketBook.Of(60_000_101).Mode);
        Assert.Equal((TicketBook.Conquest, 2, 21), (TicketBook.Of(60_007_201).Mode, TicketBook.Of(60_007_201).Stars, TicketBook.Of(60_007_201).Chest));
        Assert.Equal("Ticket 7", TicketBook.Of(7).En);

        var odyle = One<ContentTicketsEvent>(Opcodes.TicketUpdate, Bin("ticket-0c61-odyle.bin"));   // opt 01 → u32 after the reason byte
        var o = Assert.Single(odyle.Tickets);
        Assert.Equal((60_000_001, 800, 90), (o.Key, o.Base, o.Bonus));
        var spent = Assert.Single(One<ContentTicketsEvent>(Opcodes.TicketUpdate, Bin("ticket-0c61-spent.bin")).Tickets);
        Assert.Equal((6, 0L), (spent.Key, spent.Time));

        var state = new CharacterState(null);
        state.OnEvent(snap); state.OnEvent(odyle);
        Assert.Equal(73, state.Tickets.Count);
        Assert.Equal(800, state.Tickets[60_000_001].Base);
        Assert.Equal(14, state.Tickets[1].Total);
    }

    [Fact]
    public void Today_card_lists_conquest_dungeons_not_exploration()
    {
        // Akunita's login snapshot; Expedition window of 2026-10-13: Conquest tab Krao★ Draupnir★ Urugugu★★ Vakron★★ Fire Temple★★★ Horn Den★★★
        // (guaranteed chest 0/14, 0/14, 18/21, 15/21, 0/28, 0/28), "cube rewards 10/10", header Odyle "75 (+1110)/840"
        var snap = One<ContentTicketsEvent>(Opcodes.TicketList, Bin("tickets-0b61.bin"));
        var rows = TicketBook.Today(snap.Tickets.ToDictionary(t => t.Key));
        var keys = rows.Select(r => r.T.Key).ToList();
        Assert.DoesNotContain(keys, k => TicketBook.Of(k).Mode == TicketBook.Explore);
        Assert.DoesNotContain(60_004_201, keys);                               // conquest key with no Expedition window entry
        Assert.DoesNotContain(7, keys);                                        // unnamed: character.json only
        Assert.DoesNotContain(10_000_007, keys);
        Assert.Equal(new[] { 60_000_201, 60_003_201, 60_001_201, 60_007_201, 60_002_201, 60_009_201 },
            rows.Where(r => r.Info.Mode == TicketBook.Conquest && r.Info.Stars > 0).Select(r => r.T.Key));
        Assert.Equal(new[] { 60_005_301, 60_006_301 }, rows.Where(r => r.Info.Mode == TicketBook.Transcendence).Select(r => r.T.Key));
        Assert.Equal(60_000_001, keys[0]);
        Assert.Equal(1, keys[1]);
        Assert.True(keys.IndexOf(90_000_006) < keys.IndexOf(60_000_008));    // raids before dungeons
        Assert.True(keys.IndexOf(60_000_009) < keys.IndexOf(60_000_201));    // shared conquest counters before the dungeons
        var odyle = rows[0];
        Assert.Equal("240/840 + 110", TicketBook.Value(odyle.T, odyle.Info));
        var cube = rows.Single(r => r.T.Key == 60_000_009);
        Assert.Equal("10/10", TicketBook.Value(cube.T, cube.Info));
        Assert.Equal(new[] { 14, 14, 21, 21, 28, 28 }, rows.Where(r => r.Info.Stars > 0).Select(r => r.Info.Chest));
    }

    [Fact]
    public void Reset_boundaries_are_16_00_Seoul_and_Wednesday()
    {
        var now = new DateTimeOffset(2026, 10, 11, 14, 0, 0, TimeSpan.FromHours(2));   // Sunday 21:00 KST
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 16, 0, 0, TimeSpan.FromHours(9)), GameReset.NextDaily(now));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 16, 0, 0, TimeSpan.FromHours(9)), GameReset.NextWeekly(now));
        var morning = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.FromHours(2));  // Wednesday 15:00 KST
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 16, 0, 0, TimeSpan.FromHours(9)), GameReset.NextWeekly(morning));
    }

    // ------------------------------------------------------------------ Items tab "Tickets" table (per-character overview)

    private static string CharFixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "character", name);
    private static AchievementCatalog PassCatalog() => AchievementCatalog.Parse(File.ReadAllBytes(CharFixture("pass-catalog.json")));

    [Fact]
    public void Tickets_row_from_arielkas_real_file()
    {
        // characters/1303-Arielka/character.json of 2026-10-11 21:07, trimmed to the pass rows; tickets as written (two 0c 61 deltas, no login snapshot)
        var p = new CharacterFiles().Get(CharFixture("character-arielka.json"));
        Assert.NotNull(p);
        Assert.Equal(2, p!.Tickets.Count);
        var r = TicketsOverview.Of(p, PassCatalog());
        Assert.True(r.Any);
        Assert.Equal("840+100", r.Odyle);
        Assert.Equal("22", r.DaevaPass);   // 35,220 EXP reaches tier 22 of 101 = the claimed step
        Assert.Equal("16", r.EventPass);   // 11,440 EXP, tier 16 of 51
        Assert.Equal(TicketsOverview.None, r.Keys);
        Assert.Equal(TicketsOverview.None, r.Seal);
        Assert.Equal(TicketsOverview.None, r.Raids);
        Assert.Equal(TicketsOverview.None, r.Corridors);
        Assert.Equal(1791747072724, r.SeenMs);   // newest ticket, not the login-list time
        Assert.Contains("rewards claimed 22", r.PassTip);
        // unnamed key 10 (base 14) never lands in a column
        Assert.DoesNotContain("14", r.Odyle + r.Keys + r.Seal + r.Raids + r.Corridors);
    }

    [Fact]
    public void Tickets_row_of_an_old_build_file_is_empty()
    {
        // Akunita's file from a build before A2M-15: achievement rows with "State" instead of Count/Step, no Tickets key
        var p = new CharacterFiles().Get(CharFixture("character-akunita.json"));
        Assert.NotNull(p);
        Assert.Empty(p!.Tickets);
        var r = TicketsOverview.Of(p, PassCatalog());
        Assert.False(r.Any);
        Assert.Equal(0, r.SeenMs);
        Assert.Equal(TicketsOverview.None, r.Odyle);
        Assert.False(TicketsOverview.Of(null, PassCatalog()).Any);
        Assert.Null(new CharacterFiles().Get(CharFixture("missing.json")));
    }

    [Fact]
    public void Tickets_row_round_trips_akunitas_login_through_character_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2m-tickets-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "character.json");
        try
        {
            var state = new CharacterState(path);
            state.OnEvent(One<AchievementListEvent>(Opcodes.AchievementList, Bin("ach-8356-akunita-20261011.bin"), 1791670000000));
            state.OnEvent(One<ContentTicketsEvent>(Opcodes.TicketList, Bin("tickets-0b61.bin"), 1791670024000));
            state.Bind(path);   // same file: the login lists belong here, written now
            var files = new CharacterFiles();
            var p = files.Get(path);
            Assert.NotNull(p);
            Assert.Equal(73, p!.Tickets.Count);
            var r = TicketsOverview.Of(p, PassCatalog());
            Assert.Equal("240+110", r.Odyle);
            Assert.Equal("14", r.Keys);
            Assert.Equal("0:00 h", r.Seal);                   // key 6 spent: mask 0
            Assert.Equal("4/1 · 4/2 · 4/2", r.Raids);         // Ludra, Corroded, Muspel: entries/boss clears
            Assert.Equal("0:02 h", r.Corridors);              // only 10000002 held (130,000 ms)
            Assert.Equal("58", r.DaevaPass);                  // 100,000 EXP = tier 58 (docs/27)
            Assert.Equal(1791670024000, r.SeenMs);
            Assert.Contains("Abyssal Forge: Ludra: 4 entries, 1 boss clears", r.RaidsTip);
            Assert.Same(p, files.Get(path));                  // unchanged mtime: cached

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.NotSame(p, files.Get(path));               // new mtime: read again
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Tickets_hours_and_pass_level()
    {
        Assert.Equal("0:42 h", TicketsOverview.Hours(42 * 60_000 + 59_000));
        Assert.Equal("0:59 h", TicketsOverview.Hours(3_594_759));
        Assert.Equal("7:00 h", TicketsOverview.Hours(25_200_000));
        Assert.Equal("0:00 h", TicketsOverview.Hours(-5));
        var row = new AchievementRow(TicketsOverview.DaevaPassId, 1499, 0, 0);
        Assert.Equal(0, TicketsOverview.PassLevel(row, PassCatalog().Get(TicketsOverview.DaevaPassId)));
        Assert.Equal(1, TicketsOverview.PassLevel(row with { Count = 1500 }, PassCatalog().Get(TicketsOverview.DaevaPassId)));
        Assert.Equal(7, TicketsOverview.PassLevel(row with { Step = 7 }, null));   // no catalogue: the claimed step
    }
}
