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
