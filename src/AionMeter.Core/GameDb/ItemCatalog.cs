using System.Net;
using System.Text.Json;

namespace AionMeter.Core.GameDb;

public sealed record ItemInfo(int Id, string Name, string? Grade, string? Category, string? Icon);

/// <summary>
/// Item names, grades and icons by id from aion2hub.me's public JSON API (<c>/api/entities/item/&lt;gameId&gt;</c>, allowed by its
/// robots.txt, 13k items of the KR/TW/Global clients). GET only, one request per 400 ms, every answer cached for good in
/// <c>items.json</c>; ids the site does not know are cached as unknown so they are asked once.
/// </summary>
public sealed class ItemCatalog : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly Dictionary<int, ItemInfo?> _items = new();
    private readonly string? _path;
    private long _lastRequestMs;
    private bool _dirty;

    public ItemCatalog(string? path)
    {
        _path = path;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.1 (private build; reads aion2hub.me item entries, cached)");
        Load();
    }

    public int Requests { get; private set; }

    public ItemInfo? Cached(int id) { lock (_cacheGate) return _items.GetValueOrDefault(id); }

    public bool Known(int id) { lock (_cacheGate) return _items.ContainsKey(id); }

    public string NameOf(int id) => Cached(id)?.Name ?? $"Item {id}";

    /// <summary>The entry (fetched once), or null when the site has no such item.</summary>
    public async Task<ItemInfo?> GetAsync(int id, CancellationToken ct = default)
    {
        lock (_cacheGate) if (_items.TryGetValue(id, out var c)) return c;
        ItemInfo? info = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = 400 - (Environment.TickCount64 - _lastRequestMs);
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastRequestMs = Environment.TickCount64;
            Requests++;
            using var resp = await _http.GetAsync($"https://aion2hub.me/api/entities/item/{id}", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var r = doc.RootElement;
                var stats = r.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
                info = new ItemInfo(id, Str(r, "name") ?? $"Item {id}", Str(stats, "grade"), Str(stats, "category") ?? Str(stats, "type"), Str(r, "icon"));
            }
            else if (resp.StatusCode != HttpStatusCode.NotFound) return null; // transient: ask again later
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
        finally { _gate.Release(); }
        lock (_cacheGate) { _items[id] = info; _dirty = true; }
        return info;
    }

    /// <summary>Fetches every id not yet known, in order; stops early on a network failure.</summary>
    public async Task<int> ResolveAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var id in ids.Distinct())
        {
            if (Known(id)) continue;
            if (await GetAsync(id, ct).ConfigureAwait(false) is null && !Known(id)) break;
            n++;
        }
        Save();
        return n;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<int, ItemInfo?>>(File.ReadAllText(_path), Json);
            if (all is not null) foreach (var (k, v) in all) _items[k] = v;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    public void Save()
    {
        if (_path is null) return;
        string text;
        lock (_cacheGate)
        {
            if (!_dirty) return;
            text = JsonSerializer.Serialize(_items, Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", text);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_cacheGate) _dirty = true; }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
