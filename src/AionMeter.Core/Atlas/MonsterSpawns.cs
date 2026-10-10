using System.Text.Json;

namespace AionMeter.Core.Atlas;

public sealed record MonsterTemplate(string Name, string Category, string Sub, (int X, int Y)[] Points);

/// <summary>Monster spawn points per map from data/atlas/&lt;mapId&gt;/monsters.json (shugo.gg markers converted to world cm by tools/monsters.py).</summary>
public static class MonsterSpawns
{
    public static List<MonsterTemplate> Load(string atlasDirectory, int mapId)
    {
        var path = Path.Combine(atlasDirectory, mapId.ToString(), "monsters.json");
        var list = new List<MonsterTemplate>();
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var pts = new List<(int, int)>();
                foreach (var p in r.GetProperty("pts").EnumerateArray()) pts.Add((p[0].GetInt32(), p[1].GetInt32()));
                list.Add(new MonsterTemplate(r.GetProperty("name").GetString() ?? "", r.GetProperty("cat").GetString() ?? "", r.GetProperty("sub").GetString() ?? "", pts.ToArray()));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        return list;
    }
}
