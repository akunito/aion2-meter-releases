using System.Text.Json;

namespace AionMeter.Core.GameDb;

/// <summary>Item id → icon path under https://aion2hub.me/media/icons/ (data/gamedb/item-icons.json, tools/item-icons.py; 12,816 items, png or webp).</summary>
public sealed class ItemIcons
{
    public static readonly ItemIcons Empty = new(new Dictionary<int, string>());
    private readonly Dictionary<int, string> _paths;
    private ItemIcons(Dictionary<int, string> paths) { _paths = paths; }
    public int Count => _paths.Count;
    public string? Path(int item) => _paths.GetValueOrDefault(item);

    public static ItemIcons Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var d = new Dictionary<int, string>(13_000);
            foreach (var p in doc.RootElement.EnumerateObject()) if (int.TryParse(p.Name, out var id)) d[id] = p.Value.GetString() ?? "";
            return new ItemIcons(d);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return Empty; }
    }
}
