using System.Text;
using System.Text.Json;

namespace AionMeter.Core.Market;

/// <summary>One hourly sample: the site's bucket start (unix seconds), its close (lowest listing then), its low and listing count.</summary>
public readonly record struct PriceSample(long Ts, long Price, long Low, int Listings);

public enum PriceRange { Day, Week, Month }

/// <summary>
/// Rolling 30-day series per watched item, merged from every <c>/prices?window=7d</c> read (the site returns the whole 7-day hourly
/// series each time, so a gap shorter than 7 days between meter runs backfills itself). Bucket start is the key: a re-read of the
/// same hour replaces the sample. Compact JSON <c>{"v":1,"items":{"id":[[ts,price,low,listings],…]}}</c>, ≤ 720 samples per item.
/// </summary>
public sealed class PriceHistory
{
    public const int MaxDays = 30;
    public const int MaxPoints = MaxDays * 24;
    public const int ChartPoints = 200;
    private readonly object _gate = new();
    private readonly Dictionary<int, List<PriceSample>> _series = new();
    private readonly string? _path;

    public PriceHistory(string? path) { _path = path; Load(); }

    public static long SpanSeconds(PriceRange r) => r switch { PriceRange.Day => 86_400, PriceRange.Week => 7 * 86_400, _ => MaxDays * 86_400L };
    public static PriceRange ParseRange(string? s) => s switch { "24h" => PriceRange.Day, "30d" => PriceRange.Month, _ => PriceRange.Week };
    public static string RangeKey(PriceRange r) => r switch { PriceRange.Day => "24h", PriceRange.Month => "30d", _ => "7d" };

    public int Count(int item) { lock (_gate) return _series.TryGetValue(item, out var l) ? l.Count : 0; }
    public DateTimeOffset? First(int item) { lock (_gate) return _series.TryGetValue(item, out var l) && l.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(l[0].Ts) : null; }

    /// <summary>Merges the site's buckets (sorted or not) into the item's series and prunes it; returns how many hours were new.</summary>
    public int Merge(int item, IReadOnlyList<PricePoint> points, DateTimeOffset now)
    {
        if (points.Count == 0) return 0;
        var incoming = new List<PriceSample>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var price = p.Close > 0 ? p.Close : p.Low;
            if (price > 0) incoming.Add(new PriceSample(p.Ts.ToUnixTimeSeconds(), price, p.Low > 0 ? p.Low : price, p.Listings));
        }
        return MergeSamples(item, incoming, now);
    }

    public int MergeSamples(int item, List<PriceSample> incoming, DateTimeOffset now)
    {
        incoming.Sort(static (a, b) => a.Ts.CompareTo(b.Ts));
        lock (_gate)
        {
            if (!_series.TryGetValue(item, out var old)) _series[item] = old = new List<PriceSample>();
            var merged = new List<PriceSample>(Math.Min(old.Count + incoming.Count, MaxPoints + incoming.Count));
            int i = 0, j = 0, added = 0;
            while (i < old.Count || j < incoming.Count)
            {
                if (j >= incoming.Count) merged.Add(old[i++]);
                else if (i >= old.Count) { Push(merged, incoming[j++], ref added, true); }
                else if (old[i].Ts < incoming[j].Ts) merged.Add(old[i++]);
                else if (old[i].Ts > incoming[j].Ts) Push(merged, incoming[j++], ref added, true);
                else { Push(merged, incoming[j++], ref added, false); i++; }   // same bucket: the newer read wins
            }
            Prune(merged, now);
            _series[item] = merged;
            return added;
        }
    }

    private static void Push(List<PriceSample> into, PriceSample s, ref int added, bool isNew)
    {
        if (into.Count > 0 && into[^1].Ts == s.Ts) { into[^1] = s; return; }   // duplicate ts inside one response
        into.Add(s);
        if (isNew) added++;
    }

    private static void Prune(List<PriceSample> l, DateTimeOffset now)
    {
        var cutoff = now.ToUnixTimeSeconds() - MaxDays * 86_400L;
        var drop = 0;
        while (drop < l.Count && l[drop].Ts < cutoff) drop++;
        if (l.Count - drop > MaxPoints) drop = l.Count - MaxPoints;
        if (drop > 0) l.RemoveRange(0, drop);
    }

    /// <summary>Drops the series of items no longer watched.</summary>
    public void RetainOnly(IEnumerable<int> items)
    {
        var keep = new HashSet<int>(items);
        lock (_gate)
        {
            List<int>? gone = null;
            foreach (var k in _series.Keys) if (!keep.Contains(k)) (gone ??= new()).Add(k);
            if (gone is not null) foreach (var k in gone) _series.Remove(k);
        }
    }

    /// <summary>The samples inside [now − range, now], oldest first (a copy: the poll may merge meanwhile).</summary>
    public PriceSample[] Window(int item, PriceRange range, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_series.TryGetValue(item, out var l) || l.Count == 0) return [];
            var from = now.ToUnixTimeSeconds() - SpanSeconds(range);
            var start = LowerBound(l, from);
            return start >= l.Count ? [] : l.GetRange(start, l.Count - start).ToArray();
        }
    }

    private static int LowerBound(List<PriceSample> l, long ts)
    {
        int lo = 0, hi = l.Count;
        while (lo < hi) { var mid = (lo + hi) >>> 1; if (l[mid].Ts < ts) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>
    /// At most <paramref name="max"/> points for the chart: per time bucket the lowest and the highest sample in time order (spikes
    /// survive, a plain stride would drop them), plus the newest sample always last.
    /// </summary>
    public static PriceSample[] Downsample(PriceSample[] src, int max = ChartPoints)
    {
        if (src.Length <= max || max < 3) return src;
        var buckets = (max - 1) / 2;
        var body = src.Length - 1;
        var outp = new PriceSample[buckets * 2 + 1];
        var n = 0;
        for (var b = 0; b < buckets; b++)
        {
            int s = (int)((long)b * body / buckets), e = (int)((long)(b + 1) * body / buckets);
            if (e <= s) continue;
            int lo = s, hi = s;
            for (var k = s + 1; k < e; k++) { if (src[k].Price < src[lo].Price) lo = k; if (src[k].Price > src[hi].Price) hi = k; }
            if (lo == hi) outp[n++] = src[lo];
            else if (lo < hi) { outp[n++] = src[lo]; outp[n++] = src[hi]; }
            else { outp[n++] = src[hi]; outp[n++] = src[lo]; }
        }
        outp[n++] = src[^1];
        return n == outp.Length ? outp : outp.AsSpan(0, n).ToArray();
    }

    public static (long Min, long Median, long Max) Stats(PriceSample[] s)
    {
        if (s.Length == 0) return (0, 0, 0);
        var prices = new long[s.Length];
        for (var i = 0; i < s.Length; i++) prices[i] = s[i].Price;
        Array.Sort(prices);
        var mid = prices.Length / 2;
        var median = prices.Length % 2 == 1 ? prices[mid] : (prices[mid - 1] + prices[mid]) / 2;
        return (prices[0], median, prices[^1]);
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object) return;
            foreach (var it in items.EnumerateObject())
            {
                if (!int.TryParse(it.Name, out var id) || it.Value.ValueKind != JsonValueKind.Array) continue;
                var list = new List<PriceSample>(it.Value.GetArrayLength());
                foreach (var a in it.Value.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 4)
                        list.Add(new PriceSample(a[0].GetInt64(), a[1].GetInt64(), a[2].GetInt64(), a[3].GetInt32()));
                list.Sort(static (x, y) => x.Ts.CompareTo(y.Ts));
                _series[id] = list;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException) { }
    }

    public string Serialize()
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("{\"v\":1,\"items\":{");
        lock (_gate)
        {
            var firstItem = true;
            foreach (var (id, l) in _series)
            {
                if (l.Count == 0) continue;
                if (!firstItem) sb.Append(',');
                firstItem = false;
                sb.Append('"').Append(id).Append("\":[");
                for (var i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var s = l[i];
                    sb.Append('[').Append(s.Ts).Append(',').Append(s.Price).Append(',').Append(s.Low).Append(',').Append(s.Listings).Append(']');
                }
                sb.Append(']');
            }
        }
        return sb.Append("}}").ToString();
    }

    public void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, Serialize());
            File.Move(tmp, _path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
