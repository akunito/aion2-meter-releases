using System.Text.Json;
using AionMeter.Core.Armory;
using AionMeter.Core.Events;
using AionMeter.Core.GameDb;
using AionMeter.Core.Leaderboard;

namespace AionMeter.Tests;

/// <summary>A2M-44: the unified player card's Core helpers, on Teinmeir's cached profile of 2026-10-13 (Daevanion nodes trimmed).</summary>
public class PlayerCardTests
{
    private static CharacterProfile Teinmeir() =>
        JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "armory", "profile-teinmeir.json")))!;

    [Fact]
    public void Gear_lands_on_the_character_screen_positions()
    {
        var g = ProfileLayout.Arrange(Teinmeir().Equipment);
        Assert.Equal(["Nuakum Helm", "Vakron Pauldrons", "Vakron Breastplate", "Nuakum Greaves", "Vakron Gloves", "Vakron Boots", "Nuakum Cloak"], g.Armour.Select(x => x!.Name));
        Assert.Equal(["MainHand", "SubHand"], g.Weapons.Select(x => x!.Slot));
        Assert.Equal(["Necklace", "Amulet", "Earring1", "Earring2", "Ring1", "Ring2", "Bracelet1", "Bracelet2", "Rune1", "Rune2", "Belt"], g.Accessories.Select(x => x!.Slot));
        Assert.Equal(["Arcana1", "Arcana2", "Arcana3", "Arcana4", "Arcana5"], g.Arcana.Select(x => x.Slot));
        Assert.Empty(g.Other);
        Assert.True(g.HasGear);
    }

    [Fact]
    public void Missing_and_unknown_slots()
    {
        var g = ProfileLayout.Arrange([new EquipItem(99, "Earring2", "E", "Unique", 0, 0, 1, null), new EquipItem(98, "Brooch", "B", "Rare", 0, 0, 2, null)]);
        Assert.Null(g.Accessories[2]);
        Assert.Equal("E", g.Accessories[3]!.Name);
        Assert.Equal("B", Assert.Single(g.Other).Name);
        Assert.False(ProfileLayout.Arrange([]).HasGear);
        Assert.Equal((GearZone.Arcana, 7), ProfileLayout.Place("Arcana7"));
    }

    [Fact]
    public void Tile_texts_and_grades()
    {
        Assert.Equal("+15", ProfileLayout.EnchantText(15));
        Assert.Null(ProfileLayout.EnchantText(0));
        Assert.Equal("x2", ProfileLayout.ExceedText(2));
        Assert.Null(ProfileLayout.ExceedText(0));
        Assert.Equal(Grade.Legendary, ProfileLayout.GradeOf("Legend"));
        Assert.Equal(Grade.Special, ProfileLayout.GradeOf("Special"));
        Assert.Equal(Grade.Unknown, ProfileLayout.GradeOf(null));
        Assert.Equal("Earring 1", ProfileLayout.SlotLabel("Earring1"));
        Assert.Equal("Main Hand", ProfileLayout.SlotLabel("MainHand"));
        Assert.Equal(("Silver Blade Rotan", 2), ProfileLayout.SplitPet("Silver Blade Rotan lv2"));
        Assert.Equal(("Odd pet", 0), ProfileLayout.SplitPet("Odd pet"));
        Assert.Equal(("Dark Veil Wings", 0), ProfileLayout.SplitWing("Dark Veil Wings +0"));
        Assert.Equal(("Justice", "Nezekan"), ProfileLayout.SplitDeity("Justice [Nezekan]"));
    }

    [Fact]
    public void Tabs_show_only_with_data()
    {
        var p = Teinmeir();
        Assert.Equal([CardTab.Dps, CardTab.Accuracy, CardTab.Rotation, CardTab.Defense], CardLayout.Tabs(true, null));
        Assert.Equal([CardTab.Profile, CardTab.Skills, CardTab.Daevanion], CardLayout.Tabs(false, p));
        Assert.Equal(7, CardLayout.Tabs(true, p).Count);
        var bare = p with { Skills = [], Boards = [], DaevanionEffects = [] };
        Assert.Equal([CardTab.Profile], CardLayout.Tabs(false, bare));
        Assert.Empty(CardLayout.Tabs(false, bare with { Equipment = [], Stats = [], CombatPower = 0 }));
    }

    [Fact]
    public void Header_tiles_without_a_fight()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var row = new LeaderboardRow(201, "Teinmeir", "eu", "Vaizel", "vaizel", "TEMPLAR", 45, 151_574, 2658, now.AddHours(-5));
        var card = PlayerCard.Of(row);
        Assert.Equal([(CardTile.CombatPower, "151,574"), (CardTile.ItemLevel, "2,658"), (CardTile.Rank, "#201 EU"), (CardTile.Seen, "5 h")], CardLayout.Tiles(card, now));
        // own character from its profile: no rank, seen = fetched
        var own = PlayerCard.Of(Teinmeir());
        Assert.Equal(GameClass.Templar, own.Class);
        Assert.Equal([CardTile.CombatPower, CardTile.ItemLevel, CardTile.Seen], CardLayout.Tiles(own, now).Select(t => t.Tile));
        Assert.Equal("Templar · Lv. 45 · Vaizel · Pandamonium", CardLayout.Meta("Templar", own));
        Assert.Equal("12 min", CardLayout.Ago(now.AddMinutes(-12), now));
        Assert.Equal("3 d", CardLayout.Ago(now.AddDays(-3.5), now));
    }

    [Fact]
    public void A_late_profile_keeps_the_ranking_numbers()
    {
        var row = new LeaderboardRow(15, "Teinmeir", "eu", "Vaizel", "vaizel", "TEMPLAR", 45, 160_000, 2700, null);
        var card = PlayerCard.Of(row).With(Teinmeir());
        Assert.Equal(160_000, card.Cp);   // the ranking's number stays the tile's
        Assert.Equal("Pandamonium", card.Legion);
        Assert.Equal(1303, card.ServerId);
        Assert.True(card.SameCharacter(new PlayerCard("teinmeir", GameClass.Templar, "Vaizel", 1303)));
        Assert.False(card.SameCharacter(new PlayerCard("teinmeir", GameClass.Templar, "Other", 2001)));
    }

    [Fact]
    public void Stats_skills_and_daevanion()
    {
        var p = Teinmeir();
        Assert.Equal(["STR", "DEX", "INT", "CON", "AGI", "WIS"], ProfileLayout.Primary(p).Select(s => s.Type));
        var divine = ProfileLayout.Divine(p);
        Assert.Equal(10, divine.Count);
        Assert.Equal(["Defense increase +4.1%", "Perfect Chance +4.1%"], divine[0].Effects);

        var groups = ProfileLayout.Skills(p);
        Assert.Equal(["active", "active-off", "stigma", "stigma-off", "stigma-missing", "passive"], groups.Select(g => g.Key));
        Assert.Equal(11, groups[0].Skills.Count);
        Assert.Equal("Defiance", Assert.Single(groups[1].Skills).Name);
        Assert.Equal(8, groups[2].Skills.Count);   // Second Skin: equipped, not acquired — still in the slot
        Assert.Equal(10, groups[5].Skills.Count);

        Assert.Equal([("Nezekan", 64, 88), ("Zikel", 54, 88), ("Vaizel", 55, 88), ("Triniel", 75, 116), ("Azphel", 24, 152)],
            ProfileLayout.Boards(p).Select(b => (b.Name, b.Open, b.Total)));
        var (skills, stats) = ProfileLayout.DaevanionTotals(p);
        Assert.Equal("Vicious Strike +4", skills[0]);
        Assert.Contains("HP +2,800", stats);
        Assert.Contains("Combat Speed +3%", stats);
        Assert.Contains("Multi-hit Chance +4.5%", stats);
        Assert.DoesNotContain(stats, s => s.StartsWith("Pummel", StringComparison.Ordinal));
    }
}
