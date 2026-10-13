using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AionMeter.Core.Net;

namespace AionMeter.Core.Market;

/// <summary>One broker listing: what the whole stack costs, how many units, the unit price, the seller when the source names one.</summary>
public sealed record MarketListing(long Price, int Quantity, long Unit, string? Seller)
{
    public static MarketListing FromTotal(long total, int qty, string? seller = null) => new(total, qty, qty > 0 ? total / qty : total, seller);
}

public sealed record ListingsResult(IReadOnlyList<MarketListing> Cheapest, DateTimeOffset? At, string? Status)
{
    public static readonly ListingsResult NotConfigured = new([], null, "not-configured");
}

/// <summary>
/// Where the expanded Prices row gets the cheapest current listings. aion2.exchange's API as far as our code and the export script
/// know it (<c>/servers</c>, <c>/items</c>, <c>/items/{key}/prices</c>, <c>/volume</c>; docs/13 also names <c>/depth</c> and
/// <c>/depth/ladder</c>, response shape never captured) has no per-listing call we can parse, so the default source says so instead
/// of guessing URLs against the live site. A source plugs in here once Diego captures the item page's request (docs/16).
/// </summary>
public interface IListingsSource
{
    bool Configured { get; }
    Task<ListingsResult> GetAsync(int item, int exchangeServer, CancellationToken ct = default);
}

public sealed class UnconfiguredListingsSource : IListingsSource
{
    public static readonly UnconfiguredListingsSource Instance = new();
    public bool Configured => false;
    public Task<ListingsResult> GetAsync(int item, int exchangeServer, CancellationToken ct = default) => Task.FromResult(ListingsResult.NotConfigured);
}

/// <summary>Fixed listings (render tool, tests).</summary>
public sealed class StaticListingsSource(IReadOnlyList<MarketListing> listings, DateTimeOffset at) : IListingsSource
{
    public bool Configured => true;
    public Task<ListingsResult> GetAsync(int item, int exchangeServer, CancellationToken ct = default) => Task.FromResult(new ListingsResult(Listings.Cheapest(listings), at, null));
}

public static class Listings
{
    /// <summary>The <paramref name="n"/> lowest unit prices, cheaper stack first on a tie.</summary>
    public static List<MarketListing> Cheapest(IReadOnlyList<MarketListing> all, int n = 5)
    {
        var copy = new List<MarketListing>(all.Count);
        for (var i = 0; i < all.Count; i++) if (all[i].Unit > 0) copy.Add(all[i]);
        copy.Sort(static (a, b) => a.Unit != b.Unit ? a.Unit.CompareTo(b.Unit) : a.Price.CompareTo(b.Price));
        if (copy.Count > n) copy.RemoveRange(n, copy.Count - n);
        return copy;
    }
}

/// <summary>
/// Cheapest current listings from aion2.exchange's price-ladder call — <c>/api/items/{key}/depth/ladder?server=…</c>, the one the
/// item page itself makes (endpoint + shape confirmed 2026-10-13: <c>levels</c> come cheapest first, each a <c>unit_price</c> with
/// its <c>units</c> and the <c>listings</c> count that sit at it; aggregated, no seller, so Seller stays null). It is a periodic
/// SITE capture, not live: the response carries <c>captured_at</c> (hours–days old) and many servers — Vaizel (11303) among them on
/// 2026-10-13 — have no capture at all, so levels is empty and the row shows "no listings". One read per expanded item, cached by
/// the view, through the Cloudflare solver when the site walls off a plain client.
/// </summary>
public sealed class ExchangeListingsSource : IListingsSource, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _siteUrl;

    public ExchangeListingsSource(string siteUrl = "https://aion2.exchange")
    {
        _siteUrl = siteUrl.TrimEnd('/');
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AION2DpsMeter/0.1 (private build; one listings read per expanded item)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public bool Configured => true;

    public async Task<ListingsResult> GetAsync(int item, int exchangeServer, CancellationToken ct = default)
    {
        var url = $"{_siteUrl}/api/items/{item}/depth/ladder?server={exchangeServer}";
        var json = await CloudflareHttp.GetStringAsync(_http, url, ct, expectJson: true).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Ladder JSON → the 5 cheapest levels as listings (unit price × units = stack cost), plus the site's capture time.</summary>
    public static ListingsResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        DateTimeOffset? at = root.TryGetProperty("captured_at", out var c) && c.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(c.GetString(), null, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        var list = new List<MarketListing>();
        if (root.TryGetProperty("levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
            foreach (var lv in levels.EnumerateArray())
            {
                var unit = Num(lv, "unit_price");
                var units = (int)Num(lv, "units");
                if (unit > 0 && units > 0) list.Add(new MarketListing(unit * units, units, unit, null));
            }
        return new ListingsResult(Listings.Cheapest(list, 5), at, null);
    }

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDouble()) : 0;

    public void Dispose() => _http.Dispose();
}
