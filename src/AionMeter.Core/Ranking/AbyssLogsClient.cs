using System.Net;
using System.Text.Json;

namespace AionMeter.Core.Ranking;

/// <summary>
/// GET-only client for Abyss Logs' open API (<c>api.abysslogs.com/v1</c>, no key; robots.txt 404 on the API host and no
/// terms found, re-checked 2026-10-11). One request at a time with a 400 ms gap; the fight list is cached for a day and
/// each (region, period, fight) for <see cref="MaxAge"/> under <c>cacheDir</c>. Offline or on an error the last cached
/// copy is used, however old; nothing about the player is ever sent (the query is region + period + boss code).
/// </summary>
public sealed class AbyssLogsClient : IDisposable
{
    private const string Api = "https://api.abysslogs.com/v1/";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDir;
    private long _lastRequestMs;
    private readonly Dictionary<string, (HashSet<int> Keys, DateTimeOffset At)> _keys = new();

    public AbyssLogsClient(string cacheDir, HttpMessageHandler? handler = null)
    {
        _cacheDir = cacheDir;
        _http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AION2DpsMeter/0.3 (private build; one request per 400 ms)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>Statistics are rebuilt about hourly (builtAt); 6 h keeps a week's percentile fresh enough at a few requests a day.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan KeysMaxAge { get; set; } = TimeSpan.FromHours(24);

    public int Requests { get; private set; }

    /// <summary>
    /// Statistics of one fight, or null when Abyss Logs has none for it (most open-world bosses: only the fight keys of
    /// <c>statistics/filters</c> have cells) or nothing is reachable nor cached.
    /// </summary>
    public async Task<AbyssLogsStats?> StatsAsync(string region, int fight, string period = "week", CancellationToken ct = default)
    {
        var keys = await FightKeysAsync(region, ct).ConfigureAwait(false);
        if (keys is not null && !keys.Contains(fight)) return null;

        var path = Path.Combine(_cacheDir, $"stats-{region}-{period}-{fight}.json");
        var cached = Read<AbyssLogsStats>(path);
        if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < MaxAge) return cached.Cells.Count > 0 ? cached : null;
        var body = await TryGetAsync($"{Api}statistics?region={region}&period={period}&fight={fight}", ct).ConfigureAwait(false);
        var fresh = body is null ? null : AbyssLogs.ParseStats(body);
        if (fresh is null) return cached is { Cells.Count: > 0 } ? cached : null;
        fresh = fresh with { FetchedAt = DateTimeOffset.UtcNow };
        Write(path, fresh);
        return fresh.Cells.Count > 0 ? fresh : null;
    }

    /// <summary>Fight keys with statistics in a region; null when unknown (offline, never fetched): the caller then just asks.</summary>
    public async Task<HashSet<int>?> FightKeysAsync(string region, CancellationToken ct = default)
    {
        lock (_keys)
            if (_keys.TryGetValue(region, out var mem) && DateTimeOffset.UtcNow - mem.At < KeysMaxAge) return mem.Keys;
        var path = Path.Combine(_cacheDir, $"fights-{region}.json");
        var cached = Read<KeysFile>(path);
        if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < KeysMaxAge)
        {
            var set = cached.Keys.ToHashSet();
            lock (_keys) _keys[region] = (set, cached.FetchedAt);
            return set;
        }
        var body = await TryGetAsync($"{Api}statistics/filters?region={region}", ct).ConfigureAwait(false);
        var keys = body is null ? null : AbyssLogs.ParseFightKeys(body);
        if (keys is not { Count: > 0 }) return cached?.Keys.ToHashSet();
        var now = DateTimeOffset.UtcNow;
        lock (_keys) _keys[region] = (keys, now);
        Write(path, new KeysFile(now, [.. keys]));
        return keys;
    }

    private sealed record KeysFile(DateTimeOffset FetchedAt, List<int> Keys);

    private static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<string?> TryGetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = 400 - (Environment.TickCount64 - _lastRequestMs);
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastRequestMs = Environment.TickCount64;
            Requests++;
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
