using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionMeter.Core.Armory;

/// <summary>
/// GET-only client for NCSoft's public character pages (search + info + equipment + daevanion boards), one request at a
/// time with a 400 ms gap, every profile cached as JSON under <c>cacheDir</c> for <see cref="MaxAge"/>. Nothing is ever
/// posted and no credentials are sent (audit rule of this build).
/// </summary>
public sealed class ArmoryClient : IDisposable
{
    private const string SearchUrl = "https://api-search.plaync.com/aion2global/search/v2/character";
    private const string ApiUrl = "https://aion2.plaync.com/api/character/";
    private static readonly int[] Boards = [21, 22, 23, 24, 26];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _cacheDir;
    private readonly string _lang;
    private long _lastRequestMs;

    public ArmoryClient(string cacheDir, string region = "eu", string lang = "en-US")
    {
        _cacheDir = cacheDir;
        Region = region;
        _lang = lang;
        Directory.CreateDirectory(cacheDir);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.1 (private build; reads aion2.plaync.com character pages)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://aion2.plaync.com/en-us/characters/index");
    }

    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Default search shard (A2M-40: the effective server's, ServerContext.ArmoryRegion); a known server id searches its own shard.</summary>
    public string Region { get; set; }

    /// <summary>The shard of <paramref name="serverId"/>, else <see cref="Region"/>: the search only returns its own shard's characters.</summary>
    public string RegionFor(int serverId) => Game.ServerContext.RegionOfServer(serverId) is { Length: > 0 } r ? r : Region;

    public int Requests { get; private set; }

    public async Task<List<CharacterRef>> SearchAsync(string name, int page = 1, int size = 50, CancellationToken ct = default, string? region = null)
    {
        var url = $"{SearchUrl}?keyword={Uri.EscapeDataString(name)}&region={region ?? Region}&localeInfo={_lang}&page={page}&size={size}";
        return ArmoryParser.ParseSearch(await GetAsync(url, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The character with exactly this name on this server. The search is a substring match over every server ("Baby" has
    /// thousands of hits), so up to 4 pages of 50 are read until the exact name on the wanted server shows up.
    /// <paramref name="serverId"/> 0 = match on <paramref name="serverName"/> instead (the region's names are unique).
    /// </summary>
    public async Task<CharacterRef?> FindAsync(string name, int serverId, string? serverName = null, CancellationToken ct = default)
    {
        for (var page = 1; page <= 4; page++)
        {
            var list = await SearchAsync(name, page, 50, ct, RegionFor(serverId)).ConfigureAwait(false);
            if (Match(list, name, serverId, serverName) is { } r) return r;
            if (list.Count < 50) break;
        }
        return null;
    }

    /// <summary>
    /// The search hit for this character: the exact name (an exact-case hit first: "ВАСИЛИЙ" and "Василий" are different
    /// characters) on <paramref name="serverId"/>, or on the server called <paramref name="serverName"/> when the id is 0.
    /// </summary>
    public static CharacterRef? Match(List<CharacterRef> list, string name, int serverId, string? serverName)
    {
        CharacterRef? folded = null;
        for (var i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (serverId != 0 ? r.ServerId != serverId
                : string.IsNullOrEmpty(serverName) || !string.Equals(r.ServerName, serverName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(r.Name, name, StringComparison.Ordinal)) return r;
            if (folded is null && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) folded = r;
        }
        return folded;
    }

    /// <summary>
    /// The id of the server called <paramref name="name"/>. Every region reuses the names (Vaizel is 1003, 1103, … 1303, 2003 …;
    /// EU is 13xx/23xx), so the id in the same region as <paramref name="sameRegionAs"/> (the hundreds digit) wins, then the only
    /// id with that name; 0 when still ambiguous. Taking the first match picked 1003 for EU's Vaizel and every ranked player
    /// came back "not found" (A2M-38, 2026-10-13).
    /// </summary>
    public static int ServerIdFor(IReadOnlyDictionary<int, string> servers, string name, int sameRegionAs)
    {
        var region = sameRegionAs / 100 % 10;
        int only = 0, count = 0;
        foreach (var (id, n) in servers)
        {
            if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (sameRegionAs != 0 && id / 100 % 10 == region) return id;
            only = id;
            count++;
        }
        return count == 1 ? only : 0;
    }

    public CharacterProfile? Cached(string name, int serverId)
    {
        var path = CachePath(name, serverId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>Cached profile when fresh, otherwise search + fetch; null when the character is not found.</summary>
    public async Task<CharacterProfile?> LookupAsync(string name, int serverId, bool withDaevanion = true, bool force = false, string? serverName = null, CancellationToken ct = default)
    {
        if (!force && serverId != 0 && Cached(name, serverId) is { } c && DateTimeOffset.UtcNow - c.FetchedAt < MaxAge && (!withDaevanion || c.Daevanion.Count > 0)) return c;
        var r = await FindAsync(name, serverId, serverName, ct).ConfigureAwait(false);
        if (r is null) return null;
        var p = await FetchAsync(r, withDaevanion, ct).ConfigureAwait(false);
        Store(p);
        return p;
    }

    public async Task<CharacterProfile> FetchAsync(CharacterRef r, bool withDaevanion, CancellationToken ct = default)
    {
        var q = $"characterId={r.CharacterId}&serverId={r.ServerId}&region={RegionFor(r.ServerId)}&lang={_lang}";
        var info = await GetAsync(ApiUrl + "info?" + q, ct).ConfigureAwait(false);
        var equipment = await GetAsync(ApiUrl + "equipment?" + q, ct).ConfigureAwait(false);
        List<string>? boards = null;
        if (withDaevanion)
        {
            // Board ids are per class (Templar 21–26, Ranger 31–36, …): take them from the info page's board list, not from a fixed set
            // (the fixed set returned empty templates for Irin, every node "open": 0, 2026-10-09).
            var ids = new List<int>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(info);
                void Walk(System.Text.Json.JsonElement e)
                {
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (e.TryGetProperty("openNodeCount", out _) && e.TryGetProperty("id", out var id) && id.TryGetInt32(out var v)) ids.Add(v);
                        foreach (var pr in e.EnumerateObject()) Walk(pr.Value);
                    }
                    else if (e.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
                }
                Walk(doc.RootElement);
            }
            catch (System.Text.Json.JsonException) { }
            var boardIds = ids.Count > 0 ? ids.Select(i => i.ToString()).ToArray() : Boards.Select(i => i.ToString()).ToArray();
            boards = new List<string>(boardIds.Length);
            foreach (var b in boardIds)
            {
                try { boards.Add(await GetAsync(ApiUrl + "daevanion/detail?" + q + "&boardId=" + b, ct).ConfigureAwait(false)); }
                catch (HttpRequestException) { } // a board the character has not unlocked answers 4xx on some accounts
            }
        }
        return ArmoryParser.Parse(r, info, equipment, boards, DateTimeOffset.UtcNow);
    }

    private void Store(CharacterProfile p)
    {
        try
        {
            var path = CachePath(p.Ref.Name, p.Ref.ServerId);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(p, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string CachePath(string name, int serverId)
    {
        var safe = string.Concat(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        return Path.Combine(_cacheDir, $"{serverId}-{safe.ToLowerInvariant()}.json");
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = 400 - (Environment.TickCount64 - _lastRequestMs);
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastRequestMs = Environment.TickCount64;
            Requests++;
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
