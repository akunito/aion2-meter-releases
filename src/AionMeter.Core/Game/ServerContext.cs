using System.Globalization;

namespace AionMeter.Core.Game;

/// <summary>Where <see cref="ServerContext.ServerId"/> came from, in resolution order.</summary>
public enum ServerSource
{
    /// <summary>Picked in Settings → Game.</summary>
    Settings,
    /// <summary>The logged-in character's identity packet.</summary>
    Login,
    /// <summary>The newest characters/&lt;server&gt;-&lt;name&gt; folder (meter started before the game).</summary>
    LastCharacter,
    /// <summary>Nothing known: Vaizel (1303) on EU, else the shard's first server.</summary>
    Fallback,
}

/// <summary>
/// A2M-40: the server the user plays on and every key the server-dependent features derive from it (aion2.exchange server,
/// Abyss Logs and aion2index regions, official search region, account-warehouse key, event clock). One instance per change,
/// built by <see cref="Resolve"/>; consumers re-read it on MeterService.ServerChanged.
/// </summary>
public sealed record ServerContext(string Region, int ServerId, string ServerName, ServerSource Source)
{
    public const string DefaultRegion = "eu";
    public const int FallbackServer = 1303;
    /// <summary>The official shard keys (gameinfo/servers ?region=), in the order the Settings dropdown lists them.</summary>
    public static readonly string[] Regions = ["eu", "nae", "naw", "la", "as"];

    /// <summary>aion2.exchange server ids are 10000 + the game's server id (EU Vaizel 1303 → 11303).</summary>
    public int ExchangeKey => 10000 + ServerId;
    /// <summary>AccountWarehouse / characters folder prefix: the server id as text.</summary>
    public string WarehouseKey => ServerId.ToString(CultureInfo.InvariantCulture);
    public string AbyssLogsRegion => AbyssLogsRegionOf(Region);
    /// <summary>aion2index.com /rankings?region= takes the official keys (eu nae naw la as; its region links, 2026-10-13).</summary>
    public string LeaderboardRegion => Region;
    /// <summary>api-search.plaync.com ?region= takes the official keys too (eu nae naw la as answer; "na" is empty, 2026-10-13).</summary>
    public string ArmoryRegion => Region;
    public string TimeZoneId => TimeZoneOf(Region);

    /// <summary>Global server ids are race×1000 + shard×100 + n; "" for ids outside the five shards.</summary>
    public static string RegionOfServer(int serverId) => (serverId / 100 % 10) switch
    {
        1 => "nae",
        2 => "naw",
        3 => "eu",
        4 => "la",
        5 => "as",
        _ => "",
    };

    /// <summary>api.abysslogs.com regions: asia, eu, na, sa (kr/tw are other builds). Unknown → eu.</summary>
    public static string AbyssLogsRegionOf(string region) => region switch
    {
        "nae" or "naw" => "na",
        "la" => "sa",
        "as" => "asia",
        _ => "eu",
    };

    /// <summary>
    /// The shard's server clock. shugo.gg timers.json (2026-10-09): every Global shard runs the same local event hours on its own
    /// zone — NA East New York (not the "PDT" of the official notices, which is NA West), NA West Los Angeles, Europe Berlin,
    /// South America Sao Paulo, Asia Seoul; the daily/weekly reset is one moment everywhere (16:00 KST).
    /// </summary>
    public static string TimeZoneOf(string region) => region switch
    {
        "nae" => "America/New_York",
        "naw" => "America/Los_Angeles",
        "la" => "America/Sao_Paulo",
        "as" => "Asia/Seoul",
        _ => "Europe/Berlin",
    };

    public static string NormalizeRegion(string? region)
    {
        if (string.IsNullOrEmpty(region)) return DefaultRegion;
        for (var i = 0; i < Regions.Length; i++)
            if (string.Equals(Regions[i], region, StringComparison.OrdinalIgnoreCase)) return Regions[i];
        return DefaultRegion;
    }

    /// <summary>
    /// The effective server: <paramref name="chosen"/> (Settings, 0 = auto), else <paramref name="login"/>, else
    /// <paramref name="lastCharacter"/>, else 1303 (EU) or the first server of <paramref name="region"/>'s shard. The region
    /// follows the server; the setting only counts when nothing is known.
    /// </summary>
    public static ServerContext Resolve(string? region, int chosen, int login, int lastCharacter,
        IReadOnlyDictionary<int, string> names, IReadOnlyDictionary<string, int[]>? byRegion = null)
    {
        var reg = NormalizeRegion(region);
        int id; ServerSource src;
        if (chosen > 0) { id = chosen; src = ServerSource.Settings; }
        else if (login > 0) { id = login; src = ServerSource.Login; }
        else if (lastCharacter > 0) { id = lastCharacter; src = ServerSource.LastCharacter; }
        else
        {
            src = ServerSource.Fallback;
            id = reg != DefaultRegion && byRegion is not null && byRegion.TryGetValue(reg, out var ids) && ids.Length > 0 ? ids[0] : FallbackServer;
        }
        var r = RegionOfServer(id);
        return new ServerContext(r.Length > 0 ? r : reg, id, names.TryGetValue(id, out var n) ? n : id.ToString(CultureInfo.InvariantCulture), src);
    }

    /// <summary>The server of the newest character folder name ("1303-Akunita" → 1303); 0 when none parses.</summary>
    public static int ServerOfKey(string key)
    {
        var dash = key.IndexOf('-');
        return dash > 0 && int.TryParse(key.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    public override string ToString() => $"{ServerName} ({ServerId}, {Region}) from {Source}";
}
