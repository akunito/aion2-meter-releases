using System.Text.Json;
using AionMeter.Core.Atlas;

namespace AionMeter.Tests;

/// <summary>The footer boss pill on Diego's real boss-timers.json of 2026-10-13 (Vaizel, trimmed to 11 timers).</summary>
public class FieldBossPillTests
{
    private const int Map1010 = 1010, OtherField = 1110;

    private sealed record Row(int NpcCode, int MapId, DateTimeOffset? LastKill, DateTimeOffset? LastSeenAlive, DateTimeOffset? LastSeenDead,
        int RespawnMinutes, DateTimeOffset? ListedAt, bool ListedAlive, DateTimeOffset? ListedTime, bool Watch);

    private static List<Row> Load() =>
        JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bosstimers", "boss-timers-2026-10-13.json")))!;

    private static List<BossSpawn> Spawns(List<Row> rows) =>
        rows.Select(r => new BossSpawn(r.NpcCode, r.MapId, FieldBossPill.NextSpawn(r.ListedAt, r.ListedAlive, r.ListedTime, r.LastKill, r.RespawnMinutes),
            FieldBossPill.AliveNow(r.LastKill, r.LastSeenAlive, r.LastSeenDead, r.ListedAt, r.ListedAlive))).ToList();

    private static int PickCode(List<Row> rows, int map, string now)
    {
        var i = FieldBossPill.Pick(Spawns(rows), map, DateTimeOffset.Parse(now));
        return i < 0 ? 0 : rows[i].NpcCode;
    }

    [Fact]
    public void Picks_the_soonest_spawn_not_the_watched_kutar()
    {
        var rows = Load();
        // Old rule took the watched pool only (Kutar, Korin, Aulla, …): Korin 10:14. Gartua (unwatched) comes back at 10:09.
        Assert.Equal(2101074, PickCode(rows, Map1010, "2026-10-13T09:50:00+02:00"));
        // Kutar killed at 09:55 (30 min respawn → 10:25) does not jump the queue.
        var i = rows.FindIndex(r => r.NpcCode == 2100040);
        rows[i] = rows[i] with { LastKill = DateTimeOffset.Parse("2026-10-13T09:55:00+02:00") };
        Assert.Equal(2101074, PickCode(rows, Map1010, "2026-10-13T09:56:00+02:00"));
        // Each spawn moves the pill on: Gartua 10:09 → Lagta 10:12 → Korin 10:14 → Kutar 10:25.
        Assert.Equal(2101131, PickCode(rows, Map1010, "2026-10-13T10:10:00+02:00"));
        Assert.Equal(2100141, PickCode(rows, Map1010, "2026-10-13T10:13:00+02:00"));
        Assert.Equal(2100040, PickCode(rows, Map1010, "2026-10-13T10:15:00+02:00"));
        // Aulla 10:57 after Kutar has respawned.
        Assert.Equal(2100988, PickCode(rows, Map1010, "2026-10-13T10:26:00+02:00"));
    }

    [Fact]
    public void Prefers_the_current_map_and_falls_back_to_the_server()
    {
        var rows = Load();
        // On 10-11 at 15:00 the other field's list was fresh: its 15:05 boss wins over anything on map 1010.
        Assert.Equal(2400074, PickCode(rows, OtherField, "2026-10-11T15:00:00+02:00"));
        // Two days later that list is stale (every time is past): the server's soonest, on map 1010.
        Assert.Equal(2101074, PickCode(rows, OtherField, "2026-10-13T09:50:00+02:00"));
        // A city / instance (no timers there) and an unknown map behave the same.
        Assert.Equal(2101074, PickCode(rows, 200004, "2026-10-13T09:50:00+02:00"));
        Assert.Equal(2101074, PickCode(rows, 0, "2026-10-13T09:50:00+02:00"));
    }

    [Fact]
    public void Skips_living_bosses_unknown_slots_and_timers_without_a_respawn()
    {
        var rows = Load();
        var spawns = Spawns(rows);
        var at = (int code) => spawns[rows.FindIndex(r => r.NpcCode == code)];
        Assert.True(at(2100040).AliveNow);      // Kutar listed alive at 09:48
        Assert.Null(at(2701157).NextSpawn);     // a kill with no known interval
        Assert.Equal(-1, FieldBossPill.Pick(spawns, Map1010, DateTimeOffset.Parse("2026-10-13T13:00:00+02:00")));
        // The unknown slot (-2001) is never a candidate even when it is the only pending one.
        Assert.Equal(-1, FieldBossPill.Pick([new BossSpawn(-2001, 20, DateTimeOffset.MaxValue, false)], 20, DateTimeOffset.UnixEpoch));
    }
}
