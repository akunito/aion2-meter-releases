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
    public void Self_position_follows_only_the_local_player()
    {
        var s = Store();
        s.OnEvent(new PositionEvent(T0, 1234, 1, 2, 3));
        s.OnEvent(new PositionEvent(T0 + 1, 7859, -33122.3f, -34806.8f, 5977.8f));
        Assert.Equal(-33122.3f, s.SelfX, 0.1);
        Assert.Equal(T0 + 1, s.SelfAtMs);
    }
}
