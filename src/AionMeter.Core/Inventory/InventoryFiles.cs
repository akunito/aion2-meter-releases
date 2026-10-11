namespace AionMeter.Core.Inventory;

/// <summary>Other characters' inventory.json, read once and again only when the file's write time changes (Items tab reloads often).</summary>
public sealed class InventoryFiles
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Written, InventoryFile? File)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public InventoryFile? Get(string path)
    {
        DateTime written;
        try { if (!File.Exists(path)) return null; written = File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        lock (_gate) if (_cache.TryGetValue(path, out var c) && c.Written == written) return c.File;
        var f = InventoryStore.ReadFile(path);
        lock (_gate) _cache[path] = (written, f);
        return f;
    }
}
