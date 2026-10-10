using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Net;

namespace AionMeter.Core.Market;

/// <summary>One item the player wants to buy at or below MaxPrice; the hourly poll fills the Last* fields.</summary>
public sealed class PriceWatch
{
    public int Item { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Threshold: buy at or below it (Sell = false) or sell at or above it (Sell = true).</summary>
    public long MaxPrice { get; set; }
    public bool Sell { get; set; }
    /// <summary>Units held and what each cost, for the value / gain column; 0 = unknown.</summary>
    public int Quantity { get; set; }
    public long PaidEach { get; set; }
    public long? LastPrice { get; set; }
    public int? LastListings { get; set; }
    public DateTimeOffset? LastCheck { get; set; }
    public string? LastError { get; set; }
    /// <summary>Lowest listing about a day before the last point, for the trend arrow.</summary>
    public long? DayAgoPrice { get; set; }
    /// <summary>Start of the newest hourly bucket the site returned = when it last sampled this item (the site samples every item about once an hour, 455/455 rows shared one captured_at on 2026-10-08; "Check now" cannot force a new sample).</summary>
    public DateTimeOffset? SampledAt { get; set; }
    [JsonIgnore] public bool Hit => LastPrice is long p && p > 0 && MaxPrice > 0 && (Sell ? p >= MaxPrice : p <= MaxPrice);
}

public sealed record PricePoint(DateTimeOffset Ts, long Low, long High, long Avg, long Close, int Listings);

/// <summary>
/// Items to buy below a price, checked against aion2.exchange's hourly price buckets (<c>/api/items/{key}/prices</c>, the same
/// call the site's own page makes) once an hour per item, 400 ms apart. Every poll is appended to a .history.jsonl next to the
/// store so the trend outlives the site's window. Exchange server ids are 10000 + the game's server key (EU Vaizel 1303 → 11303,
/// observed 2026-10-08; other servers inferred from that one case).
/// </summary>
public sealed class PriceWatchStore : IDisposable
{
    public const string SiteUrl = "https://aion2.exchange";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly List<PriceWatch> _watches = new();
    private readonly string? _path;
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
    private long _lastPollMs = -1;

    public PriceWatchStore(string? path)
    {
        _path = path;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AION2DpsMeter/0.1 (private build; one price read per hour per watched item)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Load();
    }

    public event Action? Changed;

    public int ExchangeServerId { get; set; } = 11303;
    public int IntervalMinutes { get; set; } = 60;
    public DateTimeOffset? LastPollAt { get; private set; }
    /// <summary>Summary of the last poll for the log: items read, through the solver, failed, elapsed ms.</summary>
    public (int Items, int Solver, int Errors, long Ms) LastPollStats { get; private set; }

    public List<PriceWatch> All() { lock (_gate) return _watches.ToList(); }
    public int Count { get { lock (_gate) return _watches.Count; } }
    public bool AnyHit { get { lock (_gate) { foreach (var w in _watches) if (w.Hit) return true; return false; } } }
    public string ItemUrl(int item) => $"{SiteUrl}/item/{item}?server={ExchangeServerId}";

    public void Add(int item, string name, long maxPrice)
    {
        lock (_gate)
        {
            var w = _watches.Find(x => x.Item == item);
            if (w is null) _watches.Add(w = new PriceWatch { Item = item });
            w.Name = name;
            w.MaxPrice = maxPrice;
        }
        Save();
        Changed?.Invoke();
    }

    public void SetMax(int item, long maxPrice) { lock (_gate) { var w = _watches.Find(x => x.Item == item); if (w is null || w.MaxPrice == maxPrice) return; w.MaxPrice = maxPrice; } Save(); Changed?.Invoke(); }
    public void SetSell(int item, bool sell) { lock (_gate) { var w = _watches.Find(x => x.Item == item); if (w is null || w.Sell == sell) return; w.Sell = sell; } Save(); Changed?.Invoke(); }
    public void SetHolding(int item, int quantity, long paidEach) { lock (_gate) { var w = _watches.Find(x => x.Item == item); if (w is null || (w.Quantity == quantity && w.PaidEach == paidEach)) return; w.Quantity = quantity; w.PaidEach = paidEach; } Save(); Changed?.Invoke(); }
    public void Rename(int item, string name) { lock (_gate) { var w = _watches.Find(x => x.Item == item); if (w is null || w.Name == name) return; w.Name = name; } Save(); Changed?.Invoke(); }
    public void Remove(int item) { lock (_gate) if (_watches.RemoveAll(x => x.Item == item) == 0) return; Save(); Changed?.Invoke(); }

    /// <summary>True once per interval while there is something to watch (TickCount64 clock).</summary>
    public bool Due(long nowMs) => Count > 0 && (_lastPollMs < 0 || nowMs - _lastPollMs >= IntervalMinutes * 60_000L);

    /// <summary>Reads every watched item once; returns the watches whose lowest listing is at or under their maximum.</summary>
    public async Task<List<PriceWatch>> PollAsync(long nowMs, CancellationToken ct = default)
    {
        _lastPollMs = nowMs;
        LastPollAt = DateTimeOffset.Now;
        var hits = new List<PriceWatch>();
        var first = true;
        var solverBefore = Net.CloudflareHttp.SolverUses; var errors = 0; var items = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var w in All())
        {
            if (!first) await Task.Delay(400, ct).ConfigureAwait(false);
            first = false;
            try
            {
                // Through the Cloudflare solver when the site walls off a plain client; expectJson peels its <pre> wrapper.
                var json = await CloudflareHttp.GetStringAsync(_http, $"{SiteUrl}/api/items/{w.Item}/prices?server={ExchangeServerId}&window=7d", ct, expectJson: true).ConfigureAwait(false);
                Apply(w, ParsePrices(json), DateTimeOffset.Now);
                AppendHistory(w);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                w.LastError = e.Message;
                w.LastCheck = DateTimeOffset.Now;
                errors++;
            }
            items++;
            if (w.Hit) hits.Add(w);
        }
        LastPollStats = (items, Net.CloudflareHttp.SolverUses - solverBefore, errors, sw.ElapsedMilliseconds);
        Save();
        Changed?.Invoke();
        return hits;
    }

    /// <summary>The last bucket's close is the newest lowest listing; the point ≥ 23 h older gives the trend.</summary>
    public static void Apply(PriceWatch w, List<PricePoint> points, DateTimeOffset now)
    {
        w.LastCheck = now;
        w.LastError = null;
        if (points.Count == 0) { w.LastError = "no data"; return; }
        var last = points[^1];
        w.SampledAt = last.Ts;
        w.LastPrice = last.Close > 0 ? last.Close : last.Low;
        w.LastListings = last.Listings;
        PricePoint? ago = null;
        foreach (var p in points) if (last.Ts - p.Ts >= TimeSpan.FromHours(23)) ago = p;
        w.DayAgoPrice = ago is null ? null : (ago.Close > 0 ? ago.Close : ago.Low);
    }

    public static List<PricePoint> ParsePrices(string json)
    {
        var list = new List<PricePoint>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in points.EnumerateArray())
        {
            if (!p.TryGetProperty("ts", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)) continue;
            list.Add(new PricePoint(when, Num(p, "low"), Num(p, "high"), Num(p, "avg"), Num(p, "close"), (int)Num(p, "listings")));
        }
        return list;
    }

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDouble()) : 0;

    private void AppendHistory(PriceWatch w)
    {
        if (_path is null || w.LastPrice is null) return;
        try { File.AppendAllText(_path + ".history.jsonl", $"{{\"item\":{w.Item},\"ts\":\"{w.LastCheck:O}\",\"price\":{w.LastPrice},\"listings\":{w.LastListings ?? 0}}}\n"); }
        catch (IOException) { }
    }

    private void Load()
    {
        try
        {
            if (_path is not null && File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<PriceWatch>>(File.ReadAllText(_path), Json);
                if (list is not null) _watches.AddRange(list);
            }
        }
        catch (Exception) { }
    }

    // No seeded watches: the exe is shared with the clan (2026-10-10); Diego's own list lives in his state/price-watch.json.

    public void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json; lock (_gate) json = JsonSerializer.Serialize(_watches, Json);
            File.WriteAllText(_path, json);
        }
        catch (IOException) { }
    }

    public void Dispose() => _http.Dispose();
}
