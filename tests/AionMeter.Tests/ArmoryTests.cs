using AionMeter.Core.Armory;

namespace AionMeter.Tests;

/// <summary>Real responses of the official character API for Akunita (Vaizel), saved 2026-10-08.</summary>
public class ArmoryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "armory", name));

    [Fact]
    public void Search_strips_the_highlight_tags_and_keeps_server_and_class_ids()
    {
        var list = ArmoryParser.ParseSearch(Fixture("search.json"));
        Assert.Equal(2, list.Count);
        Assert.Equal("Akunita", list[0].Name);
        Assert.Equal(10, list[0].PcId);
        Assert.Equal(1303, list[0].ServerId);
        Assert.Equal("Vaizel", list[0].ServerName);
        Assert.Equal("Yrakunita", list[1].Name);
    }

    [Fact]
    public void Profile_flattens_info_equipment_and_a_daevanion_board()
    {
        var r = ArmoryParser.ParseSearch(Fixture("search.json"))[0];
        var p = ArmoryParser.Parse(r, Fixture("info.json"), Fixture("equipment.json"), [Fixture("daevanion-21.json")], DateTimeOffset.UnixEpoch);
        Assert.Equal("Templar", p.ClassName);
        Assert.Equal(45, p.Level);
        Assert.Equal(72875, p.CombatPower);
        Assert.Equal(1608, p.ItemLevel);
        Assert.Equal("Through Hell and Back", p.Title);
        Assert.Equal("BlackList", p.Legion);
        Assert.Equal(17, p.Stats.Count);
        Assert.Equal("41", p.Stat("Justice")!.Value);
        Assert.Contains("Perfect Chance +4.1%", p.Stat("Justice")!.Effects);
        Assert.Equal(5, p.Boards.Count);
        Assert.Equal((49, 88), (p.Boards[0].Open, p.Boards[0].Total));
        Assert.Equal(20, p.Equipment.Count);
        var main = p.Equipment.First(e => e.SlotPos == 1);
        Assert.Equal(("Spiritforged Longsword", "Unique", 10), (main.Name, main.Grade, main.Enchant));
        Assert.Equal(35, p.Skills.Count);
        Assert.Equal(["Empyrean Lord's Punishment", "Doom Shield", "Noble Armor", "Battlefield Banner"], p.EquippedDp.Select(s => s.Name).ToArray());
        Assert.Equal(14, p.Skills.First(s => s.Id == 12240000).Level); // Judgment: the meter's cast code joins the skill level
        Assert.Equal("Karnif lv3", p.Pet);
        Assert.Equal(89, p.Daevanion.Count(n => n.BoardId == 21)); // 88 nodes + the Start cell; the 136 empty cells are dropped
        Assert.Equal(49, p.Daevanion.Count(n => n.BoardId == 21 && n.Open));
        Assert.Contains("Judgment +1", p.DaevanionEffects);
        Assert.Equal("Skill Level Up - Annihilate", p.Daevanion.First(n => n.Type == "SkillLevel" && n.Open).Name);
    }

    [Fact]
    public void Missing_fields_degrade_to_empty_instead_of_throwing()
    {
        var r = new CharacterRef("x", "Nobody", 0, 1, 1, "S", "eu");
        var p = ArmoryParser.Parse(r, "{}", "{\"equipment\":null}", null, DateTimeOffset.UnixEpoch);
        Assert.Equal("", p.ClassName);
        Assert.Empty(p.Equipment);
        Assert.Empty(p.Skills);
        Assert.Empty(ArmoryParser.ParseSearch("{\"list\":null}"));
    }
}

/// <summary>A2M-38: ranked players came back "not found" — real searches of 2026-10-13 (region=eu).</summary>
public class ArmoryMatchTests
{
    private static List<CharacterRef> Search(string name) => ArmoryParser.ParseSearch(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "armory", name)));

    private static Dictionary<int, string> RealServers()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "servers.json"))) return AionMeter.Core.Game.GameData.Load(Path.Combine(dir.FullName, "data")).Servers;
        Assert.Fail("data/servers.json not found");
        return [];
    }

    [Fact]
    public void Ranking_server_name_resolves_to_the_id_in_the_players_region()
    {
        var servers = RealServers();
        // "Vaizel" is 1003, 1103, … 1503 (and more): the first one (1003) is not EU, and the EU search never returns it.
        Assert.True(servers.Count(kv => kv.Value == "Vaizel") > 1);
        Assert.Equal(1303, ArmoryClient.ServerIdFor(servers, "Vaizel", 1303));
        Assert.Equal(2304, ArmoryClient.ServerIdFor(servers, "Lumiel", 1303));    // the other faction, same region
        Assert.Equal(1303, ArmoryClient.ServerIdFor(servers, "vaizel", 2318));
        Assert.Equal(0, ArmoryClient.ServerIdFor(servers, "Vaizel", 0));          // own server unknown: ambiguous, match by name instead
        Assert.Equal(0, ArmoryClient.ServerIdFor(servers, "Nowhere", 1303));
    }

    [Fact]
    public void Match_takes_exact_name_on_the_wanted_server()
    {
        var teinmeir = Search("search-teinmeir.json");
        Assert.Equal(1303, ArmoryClient.Match(teinmeir, "Teinmeir", 1303, null)!.ServerId);
        Assert.Null(ArmoryClient.Match(teinmeir, "Teinmeir", 1003, null));        // the id the old lookup asked for
        Assert.Equal(1303, ArmoryClient.Match(teinmeir, "Teinmeir", 0, "Vaizel")!.ServerId);

        // Substring hits ("Noushh", "Inoush") and other servers are skipped.
        var noush = Search("search-noush.json");
        var n = ArmoryClient.Match(noush, "Noush", 1303, null)!;
        Assert.Equal(("Noush", 14), (n.Name, n.PcId));
        Assert.Equal(1301, ArmoryClient.Match(noush, "noush", 0, "Siel")!.ServerId);
        Assert.Null(ArmoryClient.Match(noush, "Noush", 1303 + 1, null));

        // Exact case first: "ВАСИЛИЙ" (Lumiel) is ranked, the four "Василий" are other characters.
        var v = Search("search-vasiliy.json");
        Assert.Equal(2304, ArmoryClient.Match(v, "ВАСИЛИЙ", 2304, null)!.ServerId);
        Assert.Equal("ВАСИЛИЙ", ArmoryClient.Match(v, "ВАСИЛИЙ", 0, "Lumiel")!.Name);
        Assert.Null(ArmoryClient.Match(v, "ВАСИЛИЙ", 0, null));
    }
}

public class LeaderboardTests
{
    [Fact]
    public void Aion2index_ranking_page_rows_are_read_from_the_embedded_payload()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "armory", "aion2index-rankings-p1.html"));
        var (rows, total) = AionMeter.Core.Leaderboard.Aion2IndexClient.ParsePage(html);
        Assert.Equal(50, rows.Count);
        Assert.True(total > 100_000);
        Assert.Equal(1, rows[0].Rank);
        Assert.Equal("gigawhaleYT", rows[0].Name);
        Assert.Equal("Israphel", rows[0].ServerName);
        Assert.Equal(AionMeter.Core.Events.GameClass.Chanter, rows[0].Class);
        Assert.Equal(214645, rows[0].CombatPower);
        Assert.Equal(3273, rows[0].ItemLevel);
        Assert.Empty(AionMeter.Core.Leaderboard.Aion2IndexClient.ParsePage("<html>changed</html>").Rows);
    }
}
