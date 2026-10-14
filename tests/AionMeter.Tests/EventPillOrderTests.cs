using AionMeter.Core.Atlas;
using AionMeter.Service.Overlay;
using Xunit;

namespace AionMeter.Tests;

public class EventPillOrderTests
{
    private static ScheduledEvent Ev(string key, bool boss = false, bool hidden = false) =>
        new(key, key, TimeZoneInfo.Utc, [new TimeOnly(1, 0)], 10, 5, null, 0, boss, hidden);

    [Fact]
    public void Boss_events_come_first_and_hidden_ones_get_no_pill()
    {
        var events = new[] { Ev("shugo"), Ev("invasion"), Ev("rift"), Ev("kaira", boss: true), Ev("siege"), Ev("reset", hidden: true), Ev("weekly") };
        var order = OverlayBuilder.EventPillOrder(events);
        Assert.Equal(new[] { 3, 0, 1, 2, 4, 6 }, order);
    }

    [Fact]
    public void Shipped_catalogue_marks_kaira_as_boss_and_hides_the_daily_reset()
    {
        var path = Path.Combine(RepoRoot(), "data", "atlas", "events.json");
        var events = ScheduledEvent.Load(path);
        var kaira = Assert.Single(events, e => e.Key == "kaira");
        Assert.True(kaira.Boss);
        var reset = Assert.Single(events, e => e.Key == "reset");
        Assert.True(reset.Hidden);
        Assert.DoesNotContain(OverlayBuilder.EventPillOrder(events), i => events[i].Key == "reset");
        Assert.Equal("kaira", events[OverlayBuilder.EventPillOrder(events)[0]].Key);
    }

    private static string RepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "data", "atlas", "events.json"))) d = Path.GetDirectoryName(d);
        return d ?? throw new InvalidOperationException("data/atlas/events.json not found above " + AppContext.BaseDirectory);
    }
}
