using AionMeter.Core.Armory;
using AionMeter.Core.Character;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Protocol;

namespace AionMeter.Tests;

/// <summary>
/// A2M-35 login lists, raw payloads cut from the login of Akunita (Templar) on 2026-10-09 20:17 and of Irin (Ranger) on 2026-10-10 21:10.
/// Expected values are the official profile's (characters/1303-*/profile.json, docs/27): every Daevanion node, the 11 active skill levels
/// with their base/bonus split, the four equipped stigmas, the 16 attributes.
/// </summary>
public class LoginListTests
{
    private static byte[] Bin(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "packets", name));

    private static List<GameEvent> Parse(ushort op, byte[] body, long t = 1_000)
    {
        var events = new List<GameEvent>();
        var parser = new PacketParser(GameData.Empty, events.Add) { TimeMs = t };
        parser.Handle(op, body);
        return events;
    }

    private static T One<T>(ushort op, byte[] body) where T : GameEvent => Assert.IsType<T>(Assert.Single(Parse(op, body).OfType<T>()));

    [Fact]
    public void Daevanion_boards_match_the_official_open_nodes()
    {
        var e = One<DaevanionBoardsEvent>(Opcodes.DaevanionBoards, Bin("daevanion-26e2.bin"));
        Assert.Equal([21, 22, 23, 24, 26], e.Boards.Select(b => b.BoardId));
        // the site: Nezekan 49, Zikel 51, Vaizel 46, Triniel 43, Azphel 0 — the wire adds each board's start node
        Assert.Equal([50, 52, 47, 44, 1], e.Boards.Select(b => b.Nodes.Length));
        Assert.All(e.Boards, b => Assert.Contains(b.BoardId * 10_000 + 113, b.Nodes));
        Assert.Contains(210035, e.Boards[0].Nodes);   // row 3 col 5, Critical Hit Resist on the profile

        var irin = One<DaevanionBoardsEvent>(Opcodes.DaevanionBoards, Bin("irin-daevanion-26e2.bin"));
        Assert.Equal([48, 50, 45, 1, 1], irin.Boards.Select(b => b.Nodes.Length));   // site 47/49/44/0/0
    }

    [Fact]
    public void Daevanion_with_a_foreign_node_or_trailing_byte_is_dropped()
    {
        var b = Bin("daevanion-26e2.bin");
        var extra = b.Append((byte)0).ToArray();
        Assert.Empty(Parse(Opcodes.DaevanionBoards, extra).OfType<DaevanionBoardsEvent>());
        var foreign = (byte[])b.Clone();
        foreign[6] = 0x99; foreign[7] = 0x99; foreign[8] = 0x03;   // first node of board 21 → 235929 (board 23)
        Assert.Empty(Parse(Opcodes.DaevanionBoards, foreign).OfType<DaevanionBoardsEvent>());
    }

    [Fact]
    public void Skill_list_gives_levels_with_base_and_bonus()
    {
        var e = One<SkillListEvent>(Opcodes.SkillList, Bin("skills-0051.bin"));
        Assert.Equal(67, e.Skills.Count);
        var judgment = e.Skills.Single(s => s.Id == 12240000);
        Assert.Equal((14, 10), (judgment.Level, judgment.BaseLevel));                  // profile: Judgment lv14 (+4)
        Assert.Contains(e.Skills, s => s.Id == 12010340 && s.Level == 13);           // Vicious Strike with specs 3+4
        var levels = new CapturedProfile(1, e.Skills.ToList(), 0, [], 0, [], 0, [], 0, []).LevelsByBase();
        // the 11 equipped actives of the profile, all exact
        var expected = new Dictionary<int, int>
        {
            [12010000] = 13, [12040000] = 13, [12130000] = 10, [12100000] = 13, [12240000] = 14, [12340000] = 10,
            [12270000] = 12, [12350000] = 14, [12430000] = 12, [12300000] = 13, [12090000] = 14,
        };
        foreach (var (id, lv) in expected) Assert.Equal(lv, levels[id]);

        var irin = One<SkillListEvent>(Opcodes.SkillList, Bin("irin-skills-0051.bin"));
        Assert.Equal(65, irin.Skills.Count);
    }

    [Fact]
    public void Skill_list_that_breaks_its_self_check_is_dropped()
    {
        var b = (byte[])Bin("skills-0051.bin").Clone();
        b[1 + 5] = 14;   // first record (Shield Smite 13 = 10 + 3) now claims level 14
        Assert.Empty(Parse(Opcodes.SkillList, b).OfType<SkillListEvent>());
        Assert.Empty(Parse(Opcodes.SkillList, Bin("skills-0051.bin")[..^1]).OfType<SkillListEvent>());
    }

    [Fact]
    public void Equipped_skills_carry_specs_and_the_four_stigmas()
    {
        var e = One<EquippedSkillsEvent>(Opcodes.EquippedSkills, Bin("equipped-0039.bin"));
        Assert.Equal(16, e.Skills.Count);
        Assert.Equal([12070000, 12230000, 12310000, 12450000], e.Skills.Where(s => s.IsStigma).Select(s => s.SkillId).Order());
        Assert.Equal([12300010, 12300040, 0], e.Skills.Single(s => s.SkillId == 12300000).Specs);   // Annihilate: 00 51 variant 12300140
    }

    [Fact]
    public void Skill_bar_page_one_holds_the_fifteen_equipped_skills()
    {
        var e = One<SkillBarEvent>(Opcodes.SkillBar, Bin("skillbar-8ce2.bin"));
        var page1 = e.Keys.Where(k => k.Page == 1).ToList();
        Assert.Equal([0, 1, 2, 3, 4, 5, 7], page1.Select(k => k.Slot));
        Assert.Equal([12070000, 12240000, 12100000, 12350000], page1.Single(k => k.Slot == 1).Skills);
        var onBar = page1.SelectMany(k => k.Skills).Distinct().Order().ToArray();
        // 11 actives + 4 stigmas the profile marks equipped
        Assert.Equal([12010000, 12040000, 12070000, 12090000, 12100000, 12130000, 12230000, 12240000, 12270000, 12300000, 12310000, 12340000,
            12350000, 12430000, 12450000], onBar);
    }

    [Fact]
    public void Attributes_match_the_profile_stat_panel()
    {
        var e = One<AttributesEvent>(Opcodes.Attributes, Bin("attributes-4936.bin"));
        Assert.Equal(136, e.Stats.Count);
        int[] profile = [25, 46, 0, 1, 1, 27, 41, 30, 13, 15, 41, 6, 30, 11, 13, 6];   // STR … Space as the site lists them
        Assert.Equal(profile, CapturedProfile.StatTypes.Select(t => e.Stats.GetValueOrDefault(t.Id)));
        Assert.Empty(Parse(Opcodes.Attributes, Bin("attributes-4936.bin")[..^1]).OfType<AttributesEvent>());
    }

    private static CharacterProfile Official(DateTimeOffset fetched) => new(
        new CharacterRef("x", "Akunita", 10, 45, 1303, "Vaizel", "eu"), "Templar", 45, 0, null, null, null,
        [new StatLine("STR", "Might", "20", []), new StatLine("INT", "Intelligence", "0", []), new StatLine("Justice", "Justice [Nezekan]", "30", [])],
        0,
        [new BoardSummary(21, "Nezekan", 1, 88), new BoardSummary(22, "Zikel", 0, 88)],
        [], null, null,
        [
            new SkillLine("Active", "Judgment", 12, true, true, 12240000),
            new SkillLine("Active", "Defiance", 1, false, true, 12260000),
            new SkillLine("Dp", "Doom Shield", 0, false, false, 12070000),
            new SkillLine("Dp", "Taunt", 0, false, false, 12120000),
            new SkillLine("Passive", "Fury", 14, false, true, 12780000),
        ],
        [
            new DaevanionNode(21, 8, 8, 210113, false, "None", "Start", "Nezekan - Start", []),
            new DaevanionNode(21, 3, 5, 210035, false, "Common", "Stat", "Critical Hit Resist", []),
            new DaevanionNode(21, 1, 3, 210003, true, "None", "None", "", []),
            new DaevanionNode(22, 3, 3, 220033, false, "Common", "Stat", "x", []),
        ],
        [], fetched);

    private static CapturedProfile Captured(long t)
    {
        var skills = One<SkillListEvent>(Opcodes.SkillList, Bin("skills-0051.bin")).Skills.ToList();
        var equipped = One<EquippedSkillsEvent>(Opcodes.EquippedSkills, Bin("equipped-0039.bin")).Skills.ToList();
        var bar = One<SkillBarEvent>(Opcodes.SkillBar, Bin("skillbar-8ce2.bin")).Keys.ToList();
        var boards = One<DaevanionBoardsEvent>(Opcodes.DaevanionBoards, Bin("daevanion-26e2.bin")).Boards.ToList();
        var stats = new Dictionary<int, int>(One<AttributesEvent>(Opcodes.Attributes, Bin("attributes-4936.bin")).Stats);
        return new CapturedProfile(t, skills, t, equipped, t, bar, t, boards, t + 60_000, stats);
    }

    [Fact]
    public void Captured_lists_newer_than_the_api_replace_its_skills_nodes_and_stats()
    {
        var fetched = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        var login = 1_760_000_000_000 + 3_600_000;
        var p = Captured(login).Apply(Official(fetched));

        Assert.Equal(14, p.Skills.Single(s => s.Id == 12240000).Level);
        var doom = p.Skills.Single(s => s.Id == 12070000);
        Assert.True(doom.Acquired && doom.Equipped && doom.Level == 1);              // a stigma: 5 slots in 00 39
        Assert.False(p.Skills.Single(s => s.Id == 12120000).Acquired);               // not learned: untouched
        Assert.False(p.Skills.Single(s => s.Id == 12260000).Equipped);               // Defiance is never on the bar
        Assert.Equal(15, p.Skills.Single(s => s.Id == 12780000).Level);

        Assert.False(p.Daevanion.Single(n => n.NodeId == 210113).Open);              // start node stays closed, as on the site
        Assert.True(p.Daevanion.Single(n => n.NodeId == 210035).Open);
        Assert.False(p.Daevanion.Single(n => n.NodeId == 210003).Open);              // open on the old API, closed in the game
        Assert.Equal(1, p.Boards.Single(b => b.Id == 21).Open);
        Assert.Equal(1, p.Boards.Single(b => b.Id == 22).Open);                      // 220033 is open on the wire

        Assert.Equal(["25", "0", "41"], p.Stats.Select(s => s.Value));
        Assert.Equal(login, p.FetchedAt.ToUnixTimeMilliseconds());                    // attributes (a minute later) do not move it
    }

    [Fact]
    public void Captured_lists_older_than_the_api_leave_it_alone()
    {
        var fetched = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        var official = Official(fetched);
        Assert.Same(official, Captured(1_760_000_000_000 - 3_600_000 - 60_000).Apply(official));
        Assert.Same(official, CapturedProfile.Empty.Apply(official));
    }

    [Fact]
    public void Character_state_keeps_the_lists_in_its_file_and_drops_a_stale_stat_sheet_on_a_switch()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aionmeter-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(dir, "a", "character.json");
            var state = new CharacterState(null);
            state.Bind(a);
            state.OnEvent(One<AttributesEvent>(Opcodes.Attributes, Bin("attributes-4936.bin")));   // zone load of character a
            Assert.Equal(0, state.UpdatedMs);                                                     // not a login list
            foreach (var (op, file) in new[] { (Opcodes.SkillList, "skills-0051.bin"), (Opcodes.DaevanionBoards, "daevanion-26e2.bin") })
                foreach (var e in Parse(op, Bin(file), 5_000)) state.OnEvent(e);
            // the next character's identity arrives within 60 s of its lists: they move with it, a's stat sheet does not
            var b = Path.Combine(dir, "b", "character.json");
            state.Bind(b);
            Assert.Equal(67, state.Captured.Skills.Count);
            Assert.Empty(state.Captured.Attributes);
            state.Save();
            var read = CharacterState.ReadCaptured(b);
            Assert.Equal(67, read.Skills.Count);
            Assert.Equal(5_000, read.SkillsMs);
            Assert.Equal([50, 52, 47, 44, 1], read.Boards.Select(x => x.Nodes.Length));
            Assert.Same(CapturedProfile.Empty, CharacterState.ReadCaptured(Path.Combine(dir, "none.json")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
