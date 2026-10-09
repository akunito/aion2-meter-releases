using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AionMeter.Core.Events;
using AionMeter.Core.Net;

namespace AionMeter.Core.Leaderboard;

/// <summary>One row of aion2index.com's combat-power ranking (region-wide, 50 per page, refreshed by the site from the official pages).</summary>
public sealed record LeaderboardRow(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("serverName")] string ServerName,
    [property: JsonPropertyName("serverSlug")] string ServerSlug,
    [property: JsonPropertyName("classKey")] string ClassKey,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("combatPower")] long CombatPower,
    [property: JsonPropertyName("itemLevel")] int ItemLevel,
    [property: JsonPropertyName("lastInfoAt")] DateTimeOffset? LastInfoAt)
{
    public GameClass Class => ClassOf(ClassKey);

    public static GameClass ClassOf(string key) => key.ToUpperInvariant() switch
    {
        "GLADIATOR" => GameClass.Gladiator,
        "TEMPLAR" => GameClass.Templar,
        "RANGER" => GameClass.Ranger,
        "ASSASSIN" => GameClass.Assassin,
        "SORCERER" => GameClass.Sorcerer,
        "ELEMENTALIST" or "SPIRITMASTER" => GameClass.Elementalist,
        "CLERIC" => GameClass.Cleric,
        "CHANTER" => GameClass.Chanter,
        "BRAWLER" => GameClass.Brawler,
        _ => GameClass.Unknown,
    };
}

/// <summary>A day's snapshot of the pages read so far, so the ranking is paged through once per day at most.</summary>
public sealed record LeaderboardSnapshot(string Region, DateOnly Day, int Total, List<LeaderboardRow> Rows, int PagesRead);

/// <summary>
/// Reads the public ranking pages of aion2index.com (<c>/rankings?region=eu&amp;page=N</c>, allowed by its robots.txt; its <c>/api/</c>
/// is not, so the rows are taken from the page's embedded data). The site ranks by combat power, not DPS: it tells us who the
/// strongest characters are, the official character API then gives their gear, Daevanion and skill levels.
/// </summary>
public sealed class Aion2IndexClient : IDisposable
{
    private const int PageSize = 50;
    // RSC payload: the JSON sits inside a JS string, so quotes are escaped as \" (and backslashes doubled).
    private static readonly Regex Data = new("initialData\\\\\":\\{\\\\\"rows\\\\\":(\\[.*?\\]),\\\\\"total\\\\\":(\\d+)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex DataPlain = new("initialData\":\\{\"rows\":(\\[.*?\\]),\"total\":(\\d+)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDir;
    private long _lastRequestMs;

    public Aion2IndexClient(string cacheDir)
    {
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.1 (private build; reads aion2index.com ranking pages, 1 request per 400 ms, cached daily)");
    }

    public int Requests { get; private set; }

    /// <summary>Parses one ranking page; (rows, total characters in the region). Empty when the page layout changed.</summary>
    public static (List<LeaderboardRow> Rows, int Total) ParsePage(string html)
    {
        var m = Data.Match(html);
        string rowsJson;
        if (m.Success) rowsJson = m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        else
        {
            m = DataPlain.Match(html);
            if (!m.Success) return ([], 0);
            rowsJson = m.Groups[1].Value;
        }
        try
        {
            var rows = JsonSerializer.Deserialize<List<LeaderboardRow>>(rowsJson, Json) ?? [];
            return (rows, int.Parse(m.Groups[2].Value));
        }
        catch (JsonException) { return ([], 0); }
    }

    public LeaderboardSnapshot? Cached(string region)
    {
        var path = CachePath(region);
        if (!File.Exists(path)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<LeaderboardSnapshot>(File.ReadAllText(path), Json);
            return s is not null && s.Day == DateOnly.FromDateTime(DateTime.UtcNow) ? s : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>
    /// The best <paramref name="count"/> characters of a class (any class when Unknown), optionally on one server (slug or name),
    /// reading further pages of today's snapshot until found or <paramref name="maxPages"/> is reached.
    /// </summary>
    public async Task<List<LeaderboardRow>> TopAsync(string region, GameClass cls, string? server, int count = 10, int maxPages = 40, CancellationToken ct = default)
    {
        var snap = Cached(region) ?? new LeaderboardSnapshot(region, DateOnly.FromDateTime(DateTime.UtcNow), 0, [], 0);
        var result = Filter(snap.Rows, cls, server, count);
        while (result.Count < count && snap.PagesRead < maxPages && (snap.Total == 0 || snap.PagesRead * PageSize < snap.Total))
        {
            var page = snap.PagesRead + 1;
            var html = await GetAsync($"https://aion2index.com/rankings?region={region}&page={page}", ct).ConfigureAwait(false);
            var (rows, total) = ParsePage(html);
            if (rows.Count == 0) break; // layout changed or last page: stop, keep what we have
            snap.Rows.AddRange(rows);
            snap = snap with { Total = total, PagesRead = page };
            Store(snap);
            result = Filter(snap.Rows, cls, server, count);
        }
        return result;
    }

    private static List<LeaderboardRow> Filter(List<LeaderboardRow> rows, GameClass cls, string? server, int count)
    {
        var list = new List<LeaderboardRow>(count);
        foreach (var r in rows)
        {
            if (cls != GameClass.Unknown && r.Class != cls) continue;
            if (server is not null && !string.Equals(r.ServerSlug, server, StringComparison.OrdinalIgnoreCase) && !string.Equals(r.ServerName, server, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(r);
            if (list.Count >= count) break;
        }
        return list;
    }

    private void Store(LeaderboardSnapshot s)
    {
        try
        {
            var path = CachePath(s.Region);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(s, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string CachePath(string region) => Path.Combine(_cacheDir, $"rankings-{region}.json");

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = 400 - (Environment.TickCount64 - _lastRequestMs);
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastRequestMs = Environment.TickCount64;
            Requests++;
            // Ranking pages are HTML (the rows are parsed out of the embedded data), so no JSON unwrap here.
            return await CloudflareHttp.GetStringAsync(_http, url, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
