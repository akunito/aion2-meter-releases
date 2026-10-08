using AionMeter.Core.Character;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Protocol;

namespace AionMeter.Tests;

/// <summary>Akunita's login packets, 2026-10-08 (EU).</summary>
public class CharacterTests
{
    private static byte[] Fixture(string name) => Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "character", name)).Trim());

    private static List<GameEvent> Parse(ushort op, byte[] body)
    {
        var events = new List<GameEvent>();
        var parser = new PacketParser(GameData.Empty, events.Add) { TimeMs = 1_000 };
        parser.Handle(op, body);
        return events;
    }

    [Fact]
    public void Pets_titles_and_achievements_are_read_from_the_login_lists()
    {
        var pets = Assert.IsType<PetListEvent>(Assert.Single(Parse(Opcodes.PetList, Fixture("pets-0090.hex"))));
        Assert.Equal(1097, pets.EquippedPet);  // Karnif, as on the official character page
        Assert.Equal(124, pets.Pets.Count);
        Assert.Equal(1, pets.Pets[0].Level);
        Assert.Contains(pets.Pets, p => p.Id == 1097 && p.Level == 3);

        var titles = Assert.IsType<TitleListEvent>(Assert.Single(Parse(Opcodes.TitleList, Fixture("titles-7956.hex"))));
        Assert.Equal(182, titles.Titles.Count);
        Assert.Contains(12010061, titles.Titles); // Through Hell and Back (equipped)

        var ach = Assert.IsType<AchievementListEvent>(Assert.Single(Parse(Opcodes.AchievementList, Fixture("achievements-8356.hex"))));
        Assert.Equal(1512, ach.Rows.Count);
        Assert.Equal(650, ach.Rows.Count(r => r.CompletedMs > 0));
        Assert.Equal(100030000, ach.Rows[0].Id);

        var state = new CharacterState(null);
        state.OnEvent(pets); state.OnEvent(titles); state.OnEvent(ach);
        Assert.Equal(3, state.Pets[1097]);
        Assert.True(state.Titles.Contains(12010061));
        Assert.Equal(1512, state.Achievements.Count);
    }
}
