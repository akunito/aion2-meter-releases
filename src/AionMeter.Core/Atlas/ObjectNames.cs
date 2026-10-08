using System.Text.Json;

namespace AionMeter.Core.Atlas;

/// <summary>Names the player gave to world object codes (gathering nodes), on top of data/objects.json. One JSON file.</summary>
public sealed class ObjectNames(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Dictionary<int, (string Name, string Category)> Load()
    {
        var result = new Dictionary<int, (string, string)>();
        try
        {
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (int.TryParse(p.Name, out var code) && p.Value.ValueKind == JsonValueKind.Object)
                    result[code] = (p.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", p.Value.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return result;
    }

    public void Save(Dictionary<int, (string Name, string Category)> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(names.OrderBy(k => k.Key).ToDictionary(k => k.Key.ToString(), k => new { name = k.Value.Name, category = k.Value.Category }), Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
