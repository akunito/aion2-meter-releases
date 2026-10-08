using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Market;

/// <summary>One broker listing as the game sent it: Price is the listed price, Current the reduced one (level 0–3 → ×0.9/0.8/0.7/0.5).</summary>
public sealed record Listing(long SeenMs, int Page, int Item, int Quantity, int Level, long Price, long Current);

/// <summary>Every listing seen on the broker (Mercado) pages, appended with the time; a price history grows as the player browses.</summary>
public sealed class BrokerStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly List<Listing> _listings = new();
    private readonly string? _path;
    private bool _dirty;

    public BrokerStore(string? path)
    {
        _path = path;
        Load();
    }

    public event Action? Changed;

    public int Count { get { lock (_gate) return _listings.Count; } }

    public List<Listing> All() { lock (_gate) return _listings.ToList(); }

    /// <summary>Lowest listed price of an item over the last <paramref name="hours"/>, or null when unseen.</summary>
    public long? Lowest(int item, long nowMs, int hours = 24)
    {
        lock (_gate)
        {
            long? best = null;
            foreach (var l in _listings)
                if (l.Item == item && nowMs - l.SeenMs <= hours * 3_600_000L && (best is null || l.Price < best)) best = l.Price;
            return best;
        }
    }

    public void OnEvent(GameEvent e)
    {
        if (e is not BrokerPageEvent p || p.Listings.Count == 0) return;
        lock (_gate)
        {
            // The same page re-sent within a minute (scrolling back) is not new information.
            var cut = p.TimeMs - 60_000;
            foreach (var l in p.Listings)
            {
                var dup = false;
                for (var i = _listings.Count - 1; i >= 0 && _listings[i].SeenMs >= cut; i--)
                    if (_listings[i].Item == l.Item && _listings[i].Price == l.Price && _listings[i].Quantity == l.Quantity) { dup = true; break; }
                if (!dup) _listings.Add(l);
            }
            _dirty = true;
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try { _listings.AddRange(JsonSerializer.Deserialize<List<Listing>>(File.ReadAllText(_path), Json) ?? []); }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    public void Save()
    {
        if (_path is null) return;
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            text = JsonSerializer.Serialize(_listings, Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", text);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_gate) _dirty = true; }
    }
}
