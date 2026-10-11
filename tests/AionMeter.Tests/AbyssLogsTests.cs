using System.Net;
using AionMeter.Core.Events;
using AionMeter.Core.Ranking;

namespace AionMeter.Tests;

/// <summary>A2M-38. Real api.abysslogs.com/v1 responses of 2026-10-11 (eu, week), trimmed to a few classes.</summary>
public class AbyssLogsTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "abysslogs", name));
    private static AbyssLogsStats Nuakum => AbyssLogs.ParseStats(Fixture("stats-eu-week-2300910.json"))!;

    [Fact]
    public void Stats_parse_with_class_and_cp_bracket_cells()
    {
        var s = Nuakum;
        Assert.Equal(2300910, s.FightKey);
        Assert.Equal("week", s.Period);
        Assert.Equal(11, s.Cells.Count);
        var all = s.Cells.Single(c => c.ClassHint == "Templar" && c.AllPower);
        Assert.Equal(10786, all.Samples);
        Assert.Equal(11303.7, all.Median, 1);
        Assert.Equal(9066.8, all.Top75, 1);
        Assert.Equal(17306.7, all.Top10, 1);
        Assert.NotNull(all.BuiltAt);
        Assert.Empty(AbyssLogs.ParseStats(Fixture("stats-eu-week-2300364-empty.json"))!.Cells);
        Assert.Null(AbyssLogs.ParseStats("<html>"));
    }

    [Fact]
    public void Pick_takes_the_cp_bracket_when_it_has_enough_logs_else_all_cp()
    {
        var s = Nuakum;
        var akunita = AbyssLogs.Pick(s, GameClass.Templar, 72_875)!; // Akunita's CP on 2026-10-08
        Assert.Equal((50_000L, 100_000L, 5201), (akunita.BracketFloor, akunita.BracketCeil, akunita.Samples));
        Assert.True(AbyssLogs.Pick(s, GameClass.Templar, 0)!.AllPower);          // no roster CP
        Assert.True(AbyssLogs.Pick(s, GameClass.Ranger, 160_000)!.AllPower);     // 150–200K bracket: 20 logs only
        Assert.Equal(100_000, AbyssLogs.Pick(s, GameClass.Ranger, 100_000)!.BracketFloor); // floor inclusive
        Assert.Null(AbyssLogs.Pick(s, GameClass.Gladiator, 72_875));             // trimmed out of the fixture
        Assert.Null(AbyssLogs.Pick(s, GameClass.Unknown, 72_875));
        Assert.Null(AbyssLogs.Pick(AbyssLogs.ParseStats(Fixture("stats-eu-week-2300364-empty.json"))!, GameClass.Templar, 0));
    }

    [Fact]
    public void Estimate_interpolates_between_the_known_points()
    {
        var c = AbyssLogs.Pick(Nuakum, GameClass.Templar, 72_875)!; // top75 8054.7, median 9699.0, top25 11571.0, top10 13297.4, lowest 1942.5, highest 20396.1
        Assert.Equal(50, AbyssLogs.Estimate(c, c.Median).Percentile);
        Assert.Equal(75, AbyssLogs.Estimate(c, c.Top25).Percentile);
        Assert.Equal(25, AbyssLogs.Estimate(c, c.Top75).Percentile);
        Assert.Equal(62, AbyssLogs.Estimate(c, (c.Median + c.Top25) / 2).Percentile);
        Assert.InRange(AbyssLogs.Estimate(c, 16_000).Percentile, 91, 99);
        Assert.Equal(0, AbyssLogs.Estimate(c, 1_000).Percentile);              // under the lowest log
        Assert.Equal(12, AbyssLogs.Estimate(c, 1942.47 + (8054.70 - 1942.47) / 2).Percentile);
        var best = AbyssLogs.Estimate(c, 25_000);
        Assert.True(best.AboveBest);
        Assert.Equal(100, best.Percentile);
        Assert.Equal(99, AbyssLogs.Estimate(c, c.Highest - 1).Percentile);
        Assert.Equal(c.Median, best.Median);
    }

    [Fact]
    public void A_junk_lowest_is_read_as_zero_dps()
    {
        var chanter = Nuakum.Cells.Single(x => x.ClassHint == "Chanter" && x.AllPower); // lowest 0.01
        Assert.Equal(12, AbyssLogs.Estimate(chanter, chanter.Top75 / 2).Percentile);
        var odd = chanter with { Lowest = chanter.Top75 * 2 }; // a lowest above top75 makes no sense
        Assert.Equal(12, AbyssLogs.Estimate(odd, chanter.Top75 / 2).Percentile);
    }

    [Fact]
    public void Fight_keys_come_from_the_filters_and_match_the_meter_npc_codes()
    {
        var keys = AbyssLogs.ParseFightKeys(Fixture("filters-eu.json"));
        Assert.Equal(11, keys.Count);
        Assert.Contains(2300910, keys);   // Ferocious Horn Nuakum, the meter's NPC code too
        Assert.Contains(2980040, keys);   // Gatekeeper Pinopi, Nightmare level 1
        Assert.DoesNotContain(2300471, keys); // Transcendent Bakarma is in /bosses but has no statistics
        Assert.Empty(AbyssLogs.ParseFightKeys("{}"));
        Assert.Empty(AbyssLogs.ParseFightKeys("not json"));
    }

    [Theory]
    [InlineData(1303, "eu")]
    [InlineData(2323, "eu")]
    [InlineData(1101, "na")]
    [InlineData(2205, "na")]
    [InlineData(1401, "sa")]
    [InlineData(2510, "asia")]
    [InlineData(1003, "eu")]
    [InlineData(0, "eu")]
    public void Region_of_a_server_id(int server, string region) => Assert.Equal(region, AbyssLogs.RegionOf(server));

    [Fact]
    public void Brawler_has_a_hint_but_unknown_does_not()
    {
        Assert.Equal("Templar", AbyssLogs.ClassHint(GameClass.Templar));
        Assert.Equal("Elementalist", AbyssLogs.ClassHint(GameClass.Elementalist));
        Assert.Null(AbyssLogs.ClassHint(GameClass.Unknown));
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public bool Offline;
        public readonly List<string> Urls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("AION2DpsMeter/", request.Headers.UserAgent.ToString());
            if (Offline) throw new HttpRequestException("offline");
            var body = url.Contains("/statistics/filters") ? Fixture("filters-eu.json")
                : url.Contains("fight=2300910") ? Fixture("stats-eu-week-2300910.json")
                : url.Contains("fight=2980040") ? Fixture("stats-eu-week-2980040.json")
                : Fixture("stats-eu-week-2300364-empty.json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "abysslogs-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Client_asks_once_then_serves_the_cache_and_skips_bosses_without_statistics()
    {
        var dir = TempDir();
        var api = new FakeApi();
        using (var client = new AbyssLogsClient(dir, api))
        {
            var s = await client.StatsAsync("eu", 2300910);
            Assert.Equal(11, s!.Cells.Count);
            Assert.Equal(["https://api.abysslogs.com/v1/statistics/filters?region=eu", "https://api.abysslogs.com/v1/statistics?region=eu&period=week&fight=2300910"], api.Urls);
            Assert.NotNull(await client.StatsAsync("eu", 2300910));
            Assert.Null(await client.StatsAsync("eu", 2300471)); // not a fight key: no request
            Assert.Equal(2, api.Urls.Count);
        }
        // a new process: filters and stats both come from disk
        using (var client = new AbyssLogsClient(dir, api))
        {
            Assert.Equal(11, (await client.StatsAsync("eu", 2300910))!.Cells.Count);
            Assert.Equal(2, api.Urls.Count);
        }
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Client_offline_uses_a_stale_copy_or_nothing()
    {
        var dir = TempDir();
        var api = new FakeApi();
        using (var client = new AbyssLogsClient(dir, api)) Assert.NotNull(await client.StatsAsync("eu", 2980040));
        api.Offline = true;
        using (var client = new AbyssLogsClient(dir, api) { MaxAge = TimeSpan.Zero, KeysMaxAge = TimeSpan.Zero })
        {
            Assert.Equal(8, (await client.StatsAsync("eu", 2980040))!.Cells.Count); // stale, but better than nothing
            Assert.Null(await client.StatsAsync("eu", 2300910));                    // never fetched
        }
        using (var fresh = new AbyssLogsClient(TempDir(), api)) Assert.Null(await fresh.StatsAsync("eu", 2980040));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Notes_survive_a_restart_and_keep_the_newest()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "notes.json");
        var a = Guid.NewGuid();
        new AbyssLogsNotes(path).Set(a, "p62 of Templars on Ferocious Horn Nuakum this week · median 11.3K/s");
        var notes = new AbyssLogsNotes(path);
        Assert.Equal("p62 of Templars on Ferocious Horn Nuakum this week · median 11.3K/s", notes.Get(a));
        Assert.Null(notes.Get(Guid.NewGuid()));
        for (var i = 0; i < AbyssLogsNotes.Keep; i++) notes.Set(Guid.NewGuid(), "x");
        Assert.Null(notes.Get(a));
        Assert.Null(new AbyssLogsNotes(null).Get(a));
        Directory.Delete(dir, true);
    }
}
