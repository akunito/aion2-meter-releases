using System.Text.Json;

namespace AionMeter.Core.Market;

public sealed record MarketRow(int Item, long Price, int Listings);

/// <summary>
/// One server's market as Diego's browser export wrote it (tools/aion2exchange-export.js: items keyed by item_key with the
/// cheapest listing, its count and the site's captured_at). Read-only input for the craft profit calculator; the meter never
/// fetches it itself.
/// </summary>
public sealed class MarketSnapshot
{
    public static readonly MarketSnapshot Empty = new(new Dictionary<int, MarketRow>(), null, "");
    private readonly Dictionary<int, MarketRow> _rows;
    private MarketSnapshot(Dictionary<int, MarketRow> rows, DateTimeOffset? capturedAt, string server) { _rows = rows; CapturedAt = capturedAt; Server = server; }
    public DateTimeOffset? CapturedAt { get; }
    public string Server { get; }
    public int Count => _rows.Count;
    public long? Price(int item) => _rows.TryGetValue(item, out var r) && r.Price > 0 ? r.Price : null;
    public MarketRow? Row(int item) => _rows.GetValueOrDefault(item);

    public static MarketSnapshot Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException) { return Empty; }
    }

    public static MarketSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rows = new Dictionary<int, MarketRow>(600);
        DateTimeOffset? captured = null;
        var server = root.TryGetProperty("server", out var sv) && sv.ValueKind == JsonValueKind.Object && sv.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
        if (root.TryGetProperty("items", out var items))
        {
            IEnumerable<JsonElement> list = items.ValueKind == JsonValueKind.Object ? items.EnumerateObject().Select(p => p.Value) : items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];
            foreach (var it in list)
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var key = it.TryGetProperty("item_key", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : 0;
                if (key == 0) continue;
                var price = it.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                var listings = it.TryGetProperty("listings", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                rows[key] = new MarketRow(key, price, listings);
                if (captured is null && it.TryGetProperty("captured_at", out var c) && DateTimeOffset.TryParse(c.GetString(), out var at)) captured = at;
            }
        }
        if (captured is null && root.TryGetProperty("exportedAt", out var e) && DateTimeOffset.TryParse(e.GetString(), out var ex)) captured = ex;
        return new MarketSnapshot(rows, captured, server);
    }
}
