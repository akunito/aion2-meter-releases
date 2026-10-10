using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;

namespace AionMeter.Tests;

public class ChatLineTests
{
    private static CombatantSnapshot Player(uint id, string name, long damage, double crit = 0.1, long maxHit = 20_000) =>
        new(id, name, GameClass.Elementalist, id == 3, damage, damage / 160.0, damage / 4_000_000.0, 300, crit, maxHit, 0);

    private static readonly CombatantSnapshot[] Party5 =
    [
        Player(1, "Sylvaen", 1_600_000),
        Player(2, "Borgrim", 1_200_000),
        Player(3, "Talwyn", 800_000, crit: 0.15, maxHit: 34_520),
        Player(4, "Aegisa", 240_000),
        Player(5, "Nyxara", 160_000),
    ];

    private static EncounterSnapshot Fight(IReadOnlyList<CombatantSnapshot> players, EncounterEndReason reason = EncounterEndReason.Kill,
        BossSnapshot? boss = null) =>
        new(Guid.NewGuid(), "Balhash", null, DateTimeOffset.Now, 160_000, 160_000, false, reason, 4_000_000, 25_000,
            boss ?? new BossSnapshot(500, 2701250, "Balhash", 0, 4_000_000), players);

    private static readonly ChatWords Russian = new()
    {
        Kill = "УБИТ", Wipe = "ВАЙП", BossLeft = "босс {0}", Party = "группа {0}", Place = "{0}-й из {1}", Damage = "урон {0}",
        Crit = "крит {0}", TopHit = "макс. удар {0}", More = "+ещё {0}",
    };

    [Fact]
    public void Party_line_names_every_number()
    {
        Assert.Equal(
            "Balhash 2:40 KILL | 1.Sylvaen 10K/s 40% | 2.Borgrim 7.5K/s 30% | 3.Talwyn 5K/s 20% | 4.Aegisa 1.5K/s 6% | " +
            "5.Nyxara 1K/s 4% | party 25K/s",
            ChatLine.Party(Fight(Party5)));
    }

    [Fact]
    public void A_world_boss_line_says_how_little_damage_was_sent()
    {
        // The server sent our damage only: of the crowd, hit notices (444 fighting) and some damage over time.
        var crowd = new[] { Party5[2] }
            .Concat(Enumerable.Range(10, 6).Select(i => Player((uint)i, $"Player{i}", 7_000) with { Hits = 0 }))
            .ToList();
        Assert.StartsWith("Balhash 2:40 KILL (444 attacking, damage of 1 sent) | 1.Talwyn",
            ChatLine.Party(Fight(crowd) with { Attackers = 444 }));
        // Saved before the meter read hit notices: the rows tell it, the listed players are all it knows.
        Assert.StartsWith("Balhash 2:40 KILL (7 attacking, damage of 1 sent) | 1.Talwyn", ChatLine.Party(Fight(crowd)));
        Assert.StartsWith("Balhash 2:40 KILL | 1.Sylvaen", ChatLine.Party(Fight(Party5) with { Attackers = 5 }));
    }

    [Fact]
    public void Player_line_gives_place_dps_damage_crit_and_top_hit_in_the_interface_language()
    {
        Assert.Equal(
            "Balhash 2:40 KILL | Talwyn #3 of 5: 5K/s, 800K dmg (20%), crit 15%, top hit 34.52K | party 25K/s",
            ChatLine.Player(Fight(Party5), 3));
        Assert.Equal(
            "Balhash 2:40 УБИТ | Talwyn 3-й из 5: 5K/s, урон 800K (20%), крит 15%, макс. удар 34.52K | группа 25K/s",
            ChatLine.Player(Fight(Party5), 3, Russian));
    }

    [Fact]
    public void Wipe_says_how_far_the_boss_got_when_its_hp_is_known()
    {
        var boss = new BossSnapshot(500, 2701250, "Balhash", 2_900_000, 10_000_000);
        Assert.StartsWith("Balhash 2:40 WIPE (boss 29%) | 1.Sylvaen", ChatLine.Party(Fight(Party5, EncounterEndReason.Wipe, boss)));
        // Seen only mid-fight: its max HP is a guess, so no percentage at all.
        Assert.StartsWith("Balhash 2:40 WIPE | 1.Sylvaen",
            ChatLine.Party(Fight(Party5, EncounterEndReason.Wipe, boss with { MaxHpKnown = false })));
    }

    [Fact]
    public void Big_group_fits_one_message_and_says_how_many_are_left_out()
    {
        var raid = Enumerable.Range(1, 16).Select(i => Player((uint)i, $"Longnamedplayer{i}", 300_000 - i * 1_000)).ToList();
        var line = ChatLine.Party(Fight(raid));
        Assert.True(line.Length <= ChatLine.MaxLength, $"{line.Length}: {line}");
        Assert.StartsWith("Balhash 2:40 KILL | 1.Longnamedplayer1 ", line);
        Assert.Matches(@" \| \+\d+ more \| party 25K/s$", line);
    }

    [Fact]
    public void Pets_without_an_owner_are_nobodys_result()
    {
        var withPets = Party5.Append(new CombatantSnapshot(Combatant.UnknownSummonsId, "Summons", GameClass.Unknown, false, 50_000, 312.5, 0.0125, 20, 0.1, 9_000, 0)).ToList();
        Assert.DoesNotContain("Summons", ChatLine.Party(Fight(withPets)));
        Assert.Contains("Talwyn #3 of 5:", ChatLine.Player(Fight(withPets), 3));
    }

    [Fact]
    public void Discord_table_lines_up_its_columns()
    {
        var expected = string.Join(Environment.NewLine,
            "**Balhash 2:40 KILL** | party 25K/s",
            "```",
            "#  Player      DPS  Damage  Share",
            "1  Sylvaen   10K/s    1.6M  40.0%",
            "2  Borgrim  7.5K/s    1.2M  30.0%",
            "```");
        Assert.Equal(expected, ChatLine.Table(Fight(Party5.Take(2).ToList())));
    }
}
