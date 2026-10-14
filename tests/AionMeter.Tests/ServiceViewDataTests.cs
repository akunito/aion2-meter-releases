using AionMeter.Core.Atlas;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Ranking;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Tests;

/// <summary>
/// A2M-48 phase 3c: what the card's Atlas, Boss Timers and Top DPS tabs read from the daemon — the `position` builder (drawn point, drift
/// rule, heading, spots in view), the viewer NodeStore merging those spots and following the daemon's map file, and the mtime reloads of
/// boss-timers.json, top-dps.json and atlas/objects.json.
/// </summary>
public class ServiceViewDataTests
{
    private const long T0 = 1_791_458_000_000;

    private static string Temp() => Path.Combine(Path.GetTempPath(), "aionmeter-3c-" + Guid.NewGuid().ToString("N"));

    private static NodeStore Daemon(string dir)
    {
        var s = new NodeStore(dir);
        s.OnEvent(new ZoneChangedEvent(T0, 1010, "Open world", false));
        s.OnEvent(new SelfIdentifiedEvent(T0, 7859, "Akunita", 1303, GameClass.Templar, 45));
        return s;
    }

    [Fact]
    public void Position_follows_the_fix_reports_its_age_on_the_stream_clock_and_the_heading_of_the_last_move()
    {
        var a = Daemon(Temp());
        var b = new PositionBuilder();
        var p = b.Build(a, T0);
        Assert.Equal(1010, p.Map);
        Assert.Equal(0, p.X);   // no fix yet: nothing to draw
        Assert.False(p.Exact);

        a.OnEvent(new PositionEvent(T0 + 1_000, 7859, 10_000, 20_000, 300));
        p = b.Build(a, a.LastEventMs + 12_000);
        Assert.True(p.Exact);
        Assert.Equal(10_000, p.X);
        Assert.Equal(20_000, p.Y);
        Assert.Equal(12, p.AgeS);
        Assert.Null(p.Heading);   // one point: no direction yet

        a.OnEvent(new PositionEvent(T0 + 3_000, 7859, 10_000, 20_500, 300));   // 5 m along +y
        p = b.Build(a, a.LastEventMs);
        Assert.NotNull(p.Heading);
        Assert.Equal(90, p.Heading!.Value, 1);
        a.OnEvent(new PositionEvent(T0 + 4_000, 7859, 10_050, 20_500, 300));   // 0.5 m: noise, the heading holds
        Assert.Equal(90, b.Build(a, a.LastEventMs).Heading!.Value, 1);
        a.OnEvent(new PositionEvent(T0 + 5_000, 7859, 9_000, 20_500, 300));    // 10 m along -x
        Assert.Equal(180, Math.Abs(b.Build(a, a.LastEventMs).Heading!.Value), 1);
    }

    [Fact]
    public void Position_switches_to_the_centroid_only_after_15_s_far_from_a_stale_fix()
    {
        var a = Daemon(Temp());
        var b = new PositionBuilder();
        a.OnEvent(new PositionEvent(T0, 7859, 0, 0, 0));
        a.OnEvent(new PositionEvent(T0, 7859, 1, 1, 0));   // a fix at the origin
        // others in view 100 m away, for 40 s (no fix of our own meanwhile)
        PositionModel p = null!;
        for (var t = 1; t <= 40; t++)
        {
            for (var i = 0; i < 5; i++) a.OnEvent(new PositionEvent(T0 + t * 1000L, 9000u + (uint)i, 10_000 + i * 10, 10_000, 0));
            p = b.Build(a, T0 + t * 1000L);
            if (t == 15) Assert.True(p.Exact, "within 20 s of the fix the fix holds");
        }
        Assert.False(p.Exact);
        Assert.True(p.X > 5_000 && p.Y > 5_000, $"centroid expected, got ({p.X},{p.Y})");
        Assert.True(p.Samples >= 3);
        Assert.True(PositionBuilder.Same(p, b.Build(a, T0 + 40_000)));
    }

    [Fact]
    public void Position_lists_the_spots_in_view_and_the_viewer_merges_them_then_follows_the_map_file()
    {
        var dir = Temp();
        var a = Daemon(dir);
        a.OnEvent(new ObjectSeenEvent(T0, 17983, 4, 1500012, -5428, -75541, 369, 1, T0 - 60_000));
        a.OnEvent(new ObjectSeenEvent(T0, 17984, 4, 1500013, -6000, -75000, 369, 0, T0 - 60_000));
        a.Save();
        var p = new PositionBuilder().Build(a, T0);
        Assert.Equal(2, p.Live.Length);
        Assert.Contains(p.Live, l => l.Code == 1500012 && l.Taken == 1);

        // the UI's viewer store: the daemon's file of a minute ago (spots not in view), then the live list on top
        var v = new NodeStore(dir);
        v.SetMap(1010);
        Assert.All(v.Spots(1010), s => Assert.False(s.InView));
        v.MergeLive(1010, p.Live, T0);
        var merged = v.Spots(1010);
        Assert.Equal(2, merged.Count(s => s.InView && s.Alive));
        Assert.Equal(2, merged.First(s => s.Code == 1500012).UnitsLeft);

        // one left the view in the daemon: the next list drops it, the viewer marks it out of view (still alive: "2?")
        a.OnEvent(new ObjectGoneEvent(T0 + 5_000, 17984, false));
        v.MergeLive(1010, new PositionBuilder().Build(a, T0 + 5_000).Live, T0 + 5_000);
        var gone = v.Spots(1010).First(s => s.Code == 1500013);
        Assert.False(gone.InView);
        Assert.True(gone.Alive);

        // the other was emptied; the daemon's next save reaches the viewer by mtime, the in-view mark of the live one survives the reload
        a.OnEvent(new ObjectGoneEvent(T0 + 6_000, 17983, true));
        a.OnEvent(new ObjectSeenEvent(T0 + 6_000, 17990, 4, 1500014, -7000, -75000, 369, 0, T0));
        v.MergeLive(1010, new PositionBuilder().Build(a, T0 + 6_000).Live, T0 + 6_000);
        Assert.False(v.ReloadIfChanged(1010));   // unchanged file
        Thread.Sleep(20);
        a.Save();
        File.SetLastWriteTimeUtc(Path.Combine(dir, "1010.json"), DateTime.UtcNow.AddSeconds(1));   // coarse mtime file systems
        Assert.True(v.ReloadIfChanged(1010));
        var after = v.Spots(1010);
        Assert.False(after.First(s => s.Code == 1500012).Alive);
        Assert.True(after.First(s => s.Code == 1500014).InView);
        Assert.False(v.ReloadIfChanged(1010));
    }

    [Fact]
    public void Viewer_stores_follow_the_daemon_files_by_mtime()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        try
        {
            // boss-timers.json: the daemon's store writes (a `timer` command), the UI's read-only copy reloads
            var data = GameData.Load(RepoData());
            var path = Path.Combine(root, "boss-timers.json");
            var daemon = new BossTimers(path, data, RepoData());
            var viewer = new BossTimers(path, data, RepoData(), readOnly: true);
            Assert.False(viewer.ReloadIfChanged());
            daemon.MarkKilled(1303, 2_400_001);
            daemon.SetRespawn(1303, 2_400_001, 60);   // a hand-set interval keeps an unlisted boss
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var changed = 0;
            viewer.Changed += () => changed++;
            Assert.True(viewer.ReloadIfChanged());
            Assert.Equal(1, changed);
            var t = Assert.Single(viewer.ForServer(1303));
            Assert.Equal(60, t.RespawnMinutes);
            Assert.True(t.Watch);
            Assert.False(viewer.ReloadIfChanged());
            viewer.MarkKilled(1303, 2_400_002);   // read-only: never written
            Assert.DoesNotContain("2400002", File.ReadAllText(path));

            // top-dps.json
            var top = Path.Combine(root, "top-dps.json");
            File.WriteAllText(top, "[]");
            var ui = new TopDpsStore(top);
            Assert.Equal(0, ui.Count);
            File.WriteAllText(top, """[{"Name":"Akunita","Class":"Templar","ServerId":1303,"IsSelf":true,"Dps":12345.6,"Damage":999999,"CombatMs":60000,"Title":"Boss","Zone":null,"BossCode":1,"StartedAt":"2026-10-13T10:00:00+00:00","Fight":"f.json","Skills":[],"Casts":[]}]""");
            File.SetLastWriteTimeUtc(top, DateTime.UtcNow.AddSeconds(3));
            Assert.True(ui.ReloadIfChanged());
            Assert.Equal(1, ui.Count);
            Assert.Equal("Akunita", ui.For(GameClass.Templar)[0].Name);
            Assert.False(ui.ReloadIfChanged());

            // atlas/objects.json: the UI names a node, the daemon notices
            var names = Path.Combine(root, "atlas", "objects.json");
            var mine = new ObjectNames(names);
            var theirs = new ObjectNames(names);
            theirs.Load();
            mine.Save(new Dictionary<int, (string, string)> { [1500012] = ("Orichalcum", "mineral") });
            File.SetLastWriteTimeUtc(names, DateTime.UtcNow.AddSeconds(4));
            Assert.True(theirs.ChangedOnDisk());
            Assert.Equal("Orichalcum", theirs.Load()[1500012].Name);
            Assert.False(theirs.ChangedOnDisk());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static string RepoData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "servers.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/servers.json above " + AppContext.BaseDirectory);
    }
}
