using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Events;

namespace AionMeter.Core.Ranking;

/// <summary>
/// One cell of Abyss Logs' <c>/v1/statistics</c>: the DPS spread of one class on one fight, over all combat power
/// (<see cref="BracketFloor"/> = <see cref="BracketCeil"/> = 0) or one 50K CP bracket. top75 is the 25th percentile
/// (75 % of logs are above it), top10 the 90th. <see cref="Lowest"/> is often junk (0.02 DPS seen on 2026-10-11).
/// </summary>
public sealed record AbyssLogsCell(
    [property: JsonPropertyName("classHint")] string ClassHint,
    [property: JsonPropertyName("bracketFloor")] long BracketFloor,
    [property: JsonPropertyName("bracketCeil")] long BracketCeil,
    [property: JsonPropertyName("samples")] int Samples,
    [property: JsonPropertyName("characters")] int Characters,
    [property: JsonPropertyName("top10")] double Top10,
    [property: JsonPropertyName("top25")] double Top25,
    [property: JsonPropertyName("median")] double Median,
    [property: JsonPropertyName("top75")] double Top75,
    [property: JsonPropertyName("lowest")] double Lowest,
    [property: JsonPropertyName("highest")] double Highest,
    [property: JsonPropertyName("builtAt")] DateTimeOffset? BuiltAt)
{
    [JsonIgnore] public bool AllPower => BracketFloor == 0 && BracketCeil == 0;
}

/// <summary>The statistics of one fight as served (plus when we fetched them, for the 6 h cache).</summary>
public sealed record AbyssLogsStats(
    [property: JsonPropertyName("fightKey")] int FightKey,
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("cells")] List<AbyssLogsCell> Cells)
{
    [JsonPropertyName("fetchedAt")] public DateTimeOffset FetchedAt { get; init; }
}

/// <summary>Where the player's DPS sits in one cell.</summary>
/// <param name="Percentile">0–100: share of the class's logs below the player's DPS, interpolated between the cell's points.</param>
/// <param name="AboveBest">Higher than every log of the class (and bracket) this period.</param>
public readonly record struct AbyssLogsPercentile(int Percentile, double Median, bool AboveBest, AbyssLogsCell Cell);

public static class AbyssLogs
{
    /// <summary>A CP bracket with fewer logs than this is too thin to rank against: the all-CP cell is used instead.</summary>
    public const int MinBracketSamples = 50;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static AbyssLogsStats? ParseStats(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AbyssLogsStats>(json, Json);
            return s is null ? null : s with { Cells = s.Cells ?? [] };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The fight keys that have statistics (<c>/v1/statistics/filters</c>): only these are worth a request.</summary>
    public static HashSet<int> ParseFightKeys(string json)
    {
        var keys = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("dungeons", out var dungeons) || dungeons.ValueKind != JsonValueKind.Array) return keys;
            foreach (var d in dungeons.EnumerateArray())
            {
                if (!d.TryGetProperty("fights", out var fights) || fights.ValueKind != JsonValueKind.Array) continue;
                foreach (var f in fights.EnumerateArray())
                    if (f.TryGetProperty("key", out var k) && k.TryGetInt32(out var key)) keys.Add(key);
            }
        }
        catch (JsonException) { }
        return keys;
    }

    /// <summary>Abyss Logs' class name; null for classes it does not log (Brawler had no cell on 2026-10-11, it just finds nothing).</summary>
    public static string? ClassHint(GameClass c) => c == GameClass.Unknown ? null : c.ToString();

    /// <summary>
    /// Abyss Logs region of a Global server id (race×1000 + region×100 + n, see GameData.RegionOf). The numbers differ per
    /// region (eu and na medians on Nuakum 15.7K vs 14.4K on 2026-10-11). Unknown ids fall back to eu, the clan's region.
    /// </summary>
    public static string RegionOf(int serverId) => Game.ServerContext.AbyssLogsRegionOf(Game.ServerContext.RegionOfServer(serverId));

    /// <summary>
    /// The class's cell for the player's combat power when that bracket has enough logs, else the class's all-CP cell.
    /// CP 0 (no party roster seen) always takes the all-CP cell.
    /// </summary>
    public static AbyssLogsCell? Pick(AbyssLogsStats stats, GameClass cls, long combatPower)
    {
        var hint = ClassHint(cls);
        if (hint is null) return null;
        AbyssLogsCell? all = null, bracket = null;
        foreach (var c in stats.Cells)
        {
            if (!string.Equals(c.ClassHint, hint, StringComparison.OrdinalIgnoreCase)) continue;
            if (c.AllPower) all = c;
            else if (combatPower > 0 && combatPower >= c.BracketFloor && combatPower < c.BracketCeil) bracket = c;
        }
        return bracket is { Samples: >= MinBracketSamples } ? bracket : all;
    }

    /// <summary>
    /// Percentile of <paramref name="dps"/> by straight-line interpolation through (lowest, 0) (top75, 25) (median, 50)
    /// (top25, 75) (top10, 90) (highest, 100). A junk or missing lowest is taken as 0 DPS.
    /// </summary>
    public static AbyssLogsPercentile Estimate(AbyssLogsCell c, double dps)
    {
        var low = c.Lowest > 0 && c.Lowest < c.Top75 ? c.Lowest : 0;
        Span<double> x = [low, c.Top75, c.Median, c.Top25, c.Top10, c.Highest];
        Span<double> y = [0, 25, 50, 75, 90, 100];
        if (c.Highest > 0 && dps >= c.Highest) return new(100, c.Median, true, c);
        double p = 0;
        if (dps > low)
        {
            p = 100;
            for (var i = 1; i < x.Length; i++)
            {
                if (dps >= x[i]) continue;
                var span = x[i] - x[i - 1];
                p = span > 0 ? y[i - 1] + (dps - x[i - 1]) / span * (y[i] - y[i - 1]) : y[i];
                break;
            }
        }
        // p99 at most below the best log: "p100" is kept for a new record
        return new((int)Math.Clamp(Math.Floor(p), 0, 99), c.Median, false, c);
    }
}
