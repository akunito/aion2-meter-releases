using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Ranking;

namespace AionMeter.Tests;

public class TopDpsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_791_480_000_000);

    private static CombatantDetail Player(uint id, string name, GameClass cls, double dps, int server = 1303, IReadOnlyList<CastRecord>? casts = null) =>
        new(id, name, cls, false, (long)(dps * 100), dps, 0.1, 100_000, 0, 0,
            new HitQuality(10, 0, null, null, null, null, null, null, 100, null),
            [new SkillRow(12010000, "Judgment", 10, 0, 5000, 300, 0.5, 500, 400, 600, null, null, null, null, null, null)],
            [], casts ?? [], server);

    private static FightRecord Fight(long combatMs, params CombatantDetail[] players)
    {
        var snap = new EncounterSnapshot(Guid.NewGuid(), "Boss", "Verteron", T0, combatMs, combatMs, false, EncounterEndReason.Idle,
            players.Sum(p => p.Damage), players.Sum(p => p.Dps), new BossSnapshot(9, 2100167, "Boss", 0, 1000), [], 2100167);
        return new FightRecord(1, snap, players);
    }

    [Fact]
    public void Keeps_ten_best_per_class_one_per_player()
    {
        var s = new TopDpsStore(null);
        var many = Enumerable.Range(1, 12).Select(i => Player((uint)i, $"P{i}", GameClass.Templar, i * 1000)).ToArray();
        Assert.Equal(12, s.Consider(Fight(60_000, many), "a.json"));
        var top = s.For(GameClass.Templar);
        Assert.Equal(10, top.Count);
        Assert.Equal("P12", top[0].Name);
        Assert.Equal("P3", top[^1].Name);
        // Same player again, better: replaces its own row, nobody else is pushed out.
        s.Consider(Fight(60_000, Player(5, "P5", GameClass.Templar, 20_000)), "b.json");
        top = s.For(GameClass.Templar);
        Assert.Equal(10, top.Count);
        Assert.Equal("P5", top[0].Name);
        Assert.Single(top, e => e.Name == "P5");
        // Worse than the 10th: ignored.
        Assert.Equal(0, s.Consider(Fight(60_000, Player(99, "Weak", GameClass.Templar, 10)), "c.json"));
    }

    [Fact]
    public void Ignores_short_fights_unknown_classes_and_unnamed_players()
    {
        var s = new TopDpsStore(null);
        Assert.Equal(0, s.Consider(Fight(20_000, Player(1, "Fast", GameClass.Ranger, 9000)), "a.json"));
        Assert.Equal(0, s.Consider(Fight(60_000, Player(2, "Who", GameClass.Unknown, 9000), Player(3, "#4411", GameClass.Ranger, 9000)), "b.json"));
        Assert.Empty(s.Classes);
    }

    [Fact]
    public void Casts_collapse_multi_hits_and_skip_dots_then_rotation_summaries_follow()
    {
        var casts = new List<CastRecord>();
        int[] loop = [12010000, 12020000, 12030000];
        for (var i = 0; i < 30; i++)
        {
            casts.Add(new CastRecord(i * 1000, loop[i % 3], 100, HitFlags.None));
            casts.Add(new CastRecord(i * 1000, loop[i % 3], 50, HitFlags.Multi)); // second hit of the same cast
            casts.Add(new CastRecord(i * 1000 + 500, 12990000, 10, HitFlags.Dot));
        }
        var s = new TopDpsStore(null);
        s.Consider(Fight(60_000, Player(1, "Rot", GameClass.Templar, 5000, casts: casts)), "a.json");
        var e = s.For(GameClass.Templar)[0];
        Assert.Equal(30, e.Casts.Count);
        Assert.Equal(loop, TopDpsStore.Opening(e, 3));
        var (skills, count) = TopDpsStore.Frequent(e, 3, 1)[0];
        Assert.Equal(loop, skills);
        Assert.Equal(10, count);
        var cad = TopDpsStore.Cadence(e);
        Assert.Equal(10, cad[0].Casts);
        Assert.Equal(3.0, cad[0].MeanGapS, 0.01);
    }

    [Fact]
    public void Survives_a_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), "aionmeter-top-tests", Guid.NewGuid().ToString("N"), "top.json");
        var s = new TopDpsStore(path);
        s.Consider(Fight(60_000, Player(1, "A", GameClass.Cleric, 4000), Player(2, "B", GameClass.Ranger, 7000, casts: [new CastRecord(0, 13010000, 1, HitFlags.None)])), "x.json");
        s.Save();
        var again = new TopDpsStore(path);
        Assert.Equal(2, again.Count);
        Assert.Equal("B", again.For(GameClass.Ranger)[0].Name);
        Assert.Equal(13010000, again.For(GameClass.Ranger)[0].Casts[0].S);
        Assert.Equal("x.json", again.For(GameClass.Cleric)[0].Fight);
    }
}
