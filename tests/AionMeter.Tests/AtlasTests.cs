using AionMeter.Core.Atlas;
using AionMeter.Core.Events;

namespace AionMeter.Tests;

public class AtlasTests
{
    private const long T0 = 1_791_458_000_000; // 2026-10-08 ~13:13 CEST

    private static NodeStore Store()
    {
        var s = new NodeStore(Path.Combine(Path.GetTempPath(), "aionmeter-atlas-tests", Guid.NewGuid().ToString("N")));
        s.OnEvent(new ZoneChangedEvent(T0, 1010, "Open world", false));
        s.OnEvent(new SelfIdentifiedEvent(T0, 7859, "Akunita", 1303, GameClass.Templar, 45));
        return s;
    }

    [Fact]
    public void Remembered_node_is_marked_gone_when_the_player_stands_there_and_nothing_is_announced()
    {
        var s = Store();
        s.OnEvent(new ObjectSeenEvent(T0, 17983, 4, 1500012, -5428, -75541, 369, 0, T0 - 60_000));
        s.OnEvent(new ObjectGoneEvent(T0 + 10_000, 17983, false)); // walked away, node still full
        Assert.True(s.Spots(1010)[0].Alive);
        // Back 30 min later, an exact fix 12 m from the spot, no 34 36 for it: it was emptied meanwhile.
        s.OnEvent(new PositionEvent(T0 + 1_800_000, 7859, -5428 + 1200, -75541, 369));
        var spot = s.Spots(1010)[0];
        Assert.False(spot.Alive);
        Assert.True(spot.GoneUnseen);
        Assert.Equal(0, spot.UnitsLeft);
        // A fix 60 m away says nothing (the announce radius is ~100 m, only a close visit is conclusive).
        s.OnEvent(new ObjectSeenEvent(T0 + 1_900_000, 18001, 4, 1500012, -5428, -75541, 369, 0, T0 + 1_850_000));
        Assert.True(s.Spots(1010)[0].Alive);
        Assert.Equal(0, s.Spots(1010)[0].RespawnMinutes); // not learned from a guessed exhaustion time
        s.OnEvent(new ObjectGoneEvent(T0 + 1_950_000, 18001, false));
        s.OnEvent(new PositionEvent(T0 + 1_970_000, 7859, -5428 + 6000, -75541, 369));
        Assert.True(s.Spots(1010)[0].Alive);
    }

    [Fact]
    public void Node_keeps_its_spot_and_units_across_leaving_view()
    {
        // The Inina experiment: full node seen, one unit taken, node leaves view, comes back announcing 1 taken.
        var s = Store();
        s.OnEvent(new ObjectSeenEvent(T0, 17983, 4, 1500042, -37458, -37143, 5962, 0, T0 - 60_000));
        s.OnEvent(new GatherEvent(T0 + 40_000, 17983, 7859, true, 0));
        s.OnEvent(new GatherEvent(T0 + 41_000, 17983, 7859, false, 1));
        s.OnEvent(new ObjectGoneEvent(T0 + 60_000, 17983, Exhausted: false));
        s.OnEvent(new ObjectSeenEvent(T0 + 141_000, 17983, 4, 1500042, -37458.2f, -37143.1f, 5962, 1, T0 - 60_000));
        var spot = Assert.Single(s.Spots(1010));
        Assert.Equal(1500042, spot.Code);
        Assert.True(spot.Alive);
        Assert.Equal(1, spot.Taken);
        Assert.Equal(2, spot.UnitsLeft);
        Assert.Equal(1, spot.Spawns);
    }

    [Fact]
    public void Respawn_at_the_same_spot_gets_a_new_id_and_teaches_the_interval()
    {
        var s = Store();
        s.OnEvent(new ObjectSeenEvent(T0, 51824, 4, 1500004, -36729, -34461, 6013, 0, T0 - 10_000));
        s.OnEvent(new GatherEvent(T0 + 5_000, 51824, 11526, false, 3));
        s.OnEvent(new ObjectGoneEvent(T0 + 5_100, 51824, Exhausted: true));
        Assert.False(Assert.Single(s.Spots(1010)).Alive);
        s.OnEvent(new ObjectSeenEvent(T0 + 430_000, 44252, 4, 1500004, -36729, -34461, 6013, 0, T0 + 425_100)); // back 7 min later
        var spot = Assert.Single(s.Spots(1010));
        Assert.True(spot.Alive);
        Assert.Equal(44252u, spot.LiveId);
        Assert.Equal(0, spot.Taken);
        Assert.Equal(2, spot.Spawns);
        Assert.Equal(7, spot.RespawnMinutes);
    }

    [Fact]
    public void Store_round_trips_through_its_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aionmeter-atlas-tests", Guid.NewGuid().ToString("N"));
        var s = new NodeStore(dir);
        s.OnEvent(new ZoneChangedEvent(T0, 1010, "Open world", false));
        s.OnEvent(new ObjectSeenEvent(T0, 1, 4, 1500042, -100, -200, 5, 2, T0));
        s.Save();
        var again = new NodeStore(dir);
        again.OnEvent(new ZoneChangedEvent(T0 + 1, 1010, "Open world", false));
        var spot = Assert.Single(again.Spots(1010));
        Assert.Equal(2, spot.Taken);
        Assert.Equal(-200, spot.Y);
        Assert.Equal(0u, spot.LiveId); // ids do not survive a restart
    }

    [Fact]
    public void Two_agreeing_hints_replace_a_stale_self_id()
    {
        var s = Store(); // self 7859 from the "cache"
        s.OnEvent(new LocalPlayerHintEvent(T0, 13853));
        Assert.Equal(7859u, s.SelfId);
        s.OnEvent(new LocalPlayerHintEvent(T0 + 1, 13853));
        Assert.Equal(13853u, s.SelfId);
    }

    [Fact]
    public void Starting_to_gather_puts_the_player_at_the_node()
    {
        var s = Store();
        s.OnEvent(new ObjectSeenEvent(T0, 17983, 4, 1500042, -37458, -37143, 5962, 0, T0));
        s.OnEvent(new GatherEvent(T0 + 5_000, 17983, 7859, true, 0));
        Assert.Equal(-37458f, s.SelfX);
        Assert.Equal(-37143f, s.SelfY);
        Assert.Equal(T0 + 5_000, s.SelfAtMs);
    }

    [Fact]
    public void Self_position_follows_only_the_local_player()
    {
        var s = Store();
        s.OnEvent(new PositionEvent(T0, 1234, 1, 2, 3));
        s.OnEvent(new PositionEvent(T0 + 1, 7859, -33122.3f, -34806.8f, 5977.8f));
        Assert.Equal(-33122.3f, s.SelfX, 0.1);
        Assert.Equal(T0 + 1, s.SelfAtMs);
    }
}

public class EventScheduleTests
{
    [Fact]
    public void Hourly_event_next_start_and_running_window()
    {
        var zone = TimeZoneInfo.Utc;
        var shugo = new AionMeter.Core.Atlas.ScheduledEvent("shugo", "Shugo", zone, Enumerable.Range(0, 24).Select(h => new TimeOnly(h, 0)).ToList(), 10, 5);
        var now = new DateTimeOffset(2026, 10, 8, 18, 21, 0, TimeSpan.Zero);
        var (start, end, running) = shugo.Next(now);
        Assert.False(running);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 19, 0, 0, TimeSpan.Zero), start);
        var (s2, e2, r2) = shugo.Next(new DateTimeOffset(2026, 10, 8, 19, 4, 0, TimeSpan.Zero));
        Assert.True(r2);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 19, 10, 0, TimeSpan.Zero), e2);
    }

    [Fact]
    public void Weekday_event_skips_to_the_next_listed_day()
    {
        var siege = new AionMeter.Core.Atlas.ScheduledEvent("siege", "Siege", TimeZoneInfo.Utc, [new TimeOnly(21, 0)], 90, 15, new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday });
        var now = new DateTimeOffset(2026, 10, 8, 22, 40, 0, TimeSpan.Zero); // Thursday 22:40 UTC: tonight's siege ends 22:30
        var (start, _, running) = siege.Next(now);
        Assert.False(running);
        Assert.Equal(DayOfWeek.Saturday, start.DayOfWeek);
        Assert.Equal(21, start.Hour);
    }
}

public class NodeZonesTests
{
    private static NodeSpot Spot(float x, float y) => new() { Code = 1500012, X = x, Y = y, Alive = true };

    [Fact]
    public void Densest_cluster_first_then_the_rest_with_padded_hulls()
    {
        var spots = new List<NodeSpot>();
        for (var i = 0; i < 6; i++) spots.Add(Spot(1000 * i, 500 * (i % 2)));   // 6 spots within 60 m: zone 1
        spots.Add(Spot(100_000, 100_000)); spots.Add(Spot(102_000, 100_000));    // a pair 20 m apart: zone 2
        spots.Add(Spot(-200_000, 0));                                            // a loner: zone 3
        var zones = NodeZones.Compute(spots, radiusCm: 15_000, top: 5);
        Assert.Equal(3, zones.Count);
        Assert.Equal([6, 2, 1], zones.Select(z => z.Count).ToArray());
        Assert.True(zones[0].Hull.Count >= 4);
        Assert.Equal(8, zones[1].Hull.Count); // the pair becomes a ring
        Assert.InRange(zones[1].CenterX, 100_999, 101_001);
        Assert.Equal(8, zones[2].Hull.Count);
        var top1 = NodeZones.Compute(spots, top: 1);
        Assert.Single(top1);
    }

    [Fact]
    public void Hull_of_a_square_with_an_inner_point_has_four_corners()
    {
        var h = NodeZones.Hull([(0, 0), (10, 0), (10, 10), (0, 10), (5, 5)]);
        Assert.Equal(4, h.Count);
        Assert.DoesNotContain((5f, 5f), h);
    }
}
