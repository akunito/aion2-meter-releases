using AionMeter.Core.Atlas;
using AionMeter.Core.Game;
using AionMeter.Core.Ranking;

namespace AionMeter.Tests;

/// <summary>A2M-40: the effective server (Settings → login → last character folder → 1303) and the keys every consumer derives from it.</summary>
public class ServerContextTests
{
    private static readonly Dictionary<int, string> Names = new() { [1303] = "Vaizel", [2303] = "Triniel", [1101] = "Siel", [1203] = "Vaizel", [1405] = "Yustiel", [2510] = "Kromede" };
    private static readonly Dictionary<string, int[]> ByRegion = new() { ["eu"] = [1301, 1303, 2303], ["nae"] = [1101, 2101], ["as"] = [1501, 2510] };

    private static GameData RealData()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "servers.json"))) return GameData.Load(Path.Combine(dir.FullName, "data"));
        Assert.Fail("data/servers.json not found");
        return GameData.Empty;
    }

    [Fact]
    public void Resolution_order_is_settings_login_last_character_fallback()
    {
        var picked = ServerContext.Resolve("eu", 2303, 1303, 1101, Names);
        Assert.Equal((2303, ServerSource.Settings, "Triniel", "eu"), (picked.ServerId, picked.Source, picked.ServerName, picked.Region));
        var login = ServerContext.Resolve("eu", 0, 1303, 1101, Names);
        Assert.Equal((1303, ServerSource.Login), (login.ServerId, login.Source));
        var last = ServerContext.Resolve("eu", 0, 0, 1101, Names);
        Assert.Equal((1101, ServerSource.LastCharacter, "nae"), (last.ServerId, last.Source, last.Region));   // the region follows the server
        var none = ServerContext.Resolve("eu", 0, 0, 0, Names);
        Assert.Equal((1303, ServerSource.Fallback, "Vaizel", "eu"), (none.ServerId, none.Source, none.ServerName, none.Region));
    }

    [Fact]
    public void Fallback_uses_the_chosen_region_only_when_nothing_is_known()
    {
        Assert.Equal(1101, ServerContext.Resolve("nae", 0, 0, 0, Names, ByRegion).ServerId);
        Assert.Equal("nae", ServerContext.Resolve("nae", 0, 0, 0, Names, ByRegion).Region);
        Assert.Equal(1303, ServerContext.Resolve("eu", 0, 0, 0, Names, ByRegion).ServerId);
        Assert.Equal(1303, ServerContext.Resolve("naw", 0, 0, 0, Names, ByRegion).ServerId);   // a shard without a list: the clan default
        Assert.Equal(1303, ServerContext.Resolve("nae", 0, 1303, 0, Names, ByRegion).ServerId);  // a login beats the region setting
        Assert.Equal("eu", ServerContext.Resolve("XX", 0, 0, 0, Names).Region);
        Assert.Equal("as", ServerContext.NormalizeRegion("AS"));
        Assert.Equal("4242", ServerContext.Resolve("eu", 4242, 0, 0, Names).ServerName);          // unknown id: shown as the number
    }

    [Fact]
    public void Eu_vaizel_keys_are_unchanged()
    {
        var c = ServerContext.Resolve("eu", 0, 1303, 0, Names);
        Assert.Equal(11303, c.ExchangeKey);   // PriceWatchStore's old default
        Assert.Equal("eu", c.AbyssLogsRegion);
        Assert.Equal("eu", c.LeaderboardRegion);
        Assert.Equal("eu", c.ArmoryRegion);
        Assert.Equal("1303", c.WarehouseKey);  // characters/1303-Akunita → AccountWarehouse "1303"
        Assert.Equal("Europe/Berlin", c.TimeZoneId);
    }

    [Theory]
    [InlineData(1101, "nae", 11101, "na", "America/New_York")]
    [InlineData(2203, "naw", 12203, "na", "America/Los_Angeles")]
    [InlineData(2323, "eu", 12323, "eu", "Europe/Berlin")]
    [InlineData(1405, "la", 11405, "sa", "America/Sao_Paulo")]
    [InlineData(2510, "as", 12510, "asia", "Asia/Seoul")]
    public void Keys_per_shard(int server, string region, int exchange, string abyss, string zone)
    {
        var c = ServerContext.Resolve("eu", server, 0, 0, Names);
        Assert.Equal((region, exchange, abyss, zone, server.ToString()), (c.Region, c.ExchangeKey, c.AbyssLogsRegion, c.TimeZoneId, c.WarehouseKey));
        Assert.Equal(abyss, AbyssLogs.RegionOf(server));   // the fight-side mapping agrees with the context's
    }

    [Fact]
    public void Character_folder_names_give_their_server()
    {
        Assert.Equal(1303, ServerContext.ServerOfKey("1303-Akunita"));
        Assert.Equal(2303, ServerContext.ServerOfKey("2303-Some-Name"));
        Assert.Equal(0, ServerContext.ServerOfKey("Akunita"));
        Assert.Equal(0, ServerContext.ServerOfKey("-1303"));
        Assert.Equal(0, ServerContext.ServerOfKey("x1-a"));
    }

    [Fact]
    public void Official_server_table_has_every_shard()
    {
        var d = RealData();
        Assert.Equal(106, d.Servers.Count);   // tools/servers.py, official gameinfo/servers 2026-10-13
        Assert.Equal(46, d.ServersByRegion["eu"].Length);
        Assert.Equal(18, d.ServersByRegion["nae"].Length);
        Assert.Equal(10, d.ServersByRegion["naw"].Length);
        Assert.Equal(12, d.ServersByRegion["la"].Length);
        Assert.Equal(20, d.ServersByRegion["as"].Length);
        Assert.Equal("Vaizel", d.ServerName(1303));
        foreach (var (region, ids) in d.ServersByRegion)
            foreach (var id in ids) Assert.Equal(region, ServerContext.RegionOfServer(id));   // id digits and the official shard agree
    }

    [Fact]
    public void Events_move_to_the_shard_clock_and_resets_stay()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var seoul = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        IReadOnlyList<ScheduledEvent> eu =
        [
            new ScheduledEvent("siege", "Siege", berlin, [new TimeOnly(21, 0)], 60, 15),
            new ScheduledEvent("reset", "Reset", seoul, [new TimeOnly(16, 0)], 1, 0),
        ];
        Assert.Same(eu, ScheduledEvent.ForZone(eu, berlin));   // EU: the very same list
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nae = ScheduledEvent.ForZone(eu, ny);
        Assert.Equal(ny.Id, nae[0].Zone.Id);
        Assert.Same(eu[1], nae[1]);
        // 2026-10-13 12:00 UTC: next siege 21:00 New York = 01:00 UTC on the 14th (EDT), not 19:00 UTC as in Berlin
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 1, 0, 0, TimeSpan.Zero), nae[0].Next(now).Start.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 19, 0, 0, TimeSpan.Zero), eu[0].Next(now).Start.ToUniversalTime());
        Assert.Equal(eu[1].Next(now).Start, nae[1].Next(now).Start);
    }

    // A2M-27: under Wine every IANA lookup throws (no ICU); the Windows id must be tried, or events.json loads no event.
    private static TimeZoneInfo WineLookup(string id) => id.Contains('/')
        ? throw new TimeZoneNotFoundException(id)
        : TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(1), id, id);

    [Theory]
    [InlineData("Europe/Berlin", "W. Europe Standard Time")]
    [InlineData("Asia/Seoul", "Korea Standard Time")]
    [InlineData("America/New_York", "Eastern Standard Time")]
    [InlineData("America/Los_Angeles", "Pacific Standard Time")]
    [InlineData("America/Sao_Paulo", "E. South America Standard Time")]
    public void TimeZones_FallBackToTheWindowsId_WhenIanaIdsAreUnknown(string iana, string windows)
    {
        Assert.Equal(windows, TimeZones.Find(iana, WineLookup).Id);
        Assert.Equal(iana, TimeZones.Find(iana).Id);   // where IANA works it is used as is
        // the Windows name is a real zone with the same clock (ICU maps it on Linux too)
        var now = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeZoneInfo.FindSystemTimeZoneById(iana).GetUtcOffset(now), TimeZoneInfo.FindSystemTimeZoneById(windows).GetUtcOffset(now));
    }

    [Fact]
    public void TimeZones_UnknownIdStillThrows() =>
        Assert.Throws<TimeZoneNotFoundException>(() => TimeZones.Find("Mars/Olympus", WineLookup));

    [Fact]
    public void TimeZones_EveryRegionAndEventZoneHasAWindowsId()
    {
        foreach (var region in (string[])["nae", "naw", "eu", "la", "as"]) Assert.NotNull(TimeZones.WindowsId(ServerContext.TimeZoneOf(region)));
        Assert.NotNull(TimeZones.WindowsId(ScheduledEvent.EuZoneId));
    }
}
