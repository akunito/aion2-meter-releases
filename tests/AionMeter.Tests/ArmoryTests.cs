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
