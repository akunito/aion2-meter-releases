using System.Text.Json;
using AionMeter.Core.GameDb;

namespace AionMeter.Core.Atlas;

/// <summary>One monster template of a map; <see cref="Ids"/> = client NPC ids of that name (SkeeveAN MapData, A2M-37), empty for shugo-only rows.</summary>
public sealed record MonsterTemplate(string Name, string Category, string Sub, (int X, int Y)[] Points, int[] Ids)
{
    public bool HasId(int npc) => Array.IndexOf(Ids, npc) >= 0;
}

/// <summary>Monster spawn points per map from data/atlas/&lt;mapId&gt;/monsters.json (shugo.gg markers converted to world cm by tools/monsters.py,
/// SkeeveAN spawn points and NPC ids merged by tools/atlas-import-skeevean.py).</summary>
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
                int[] ids = [];
                if (r.TryGetProperty("ids", out var ia))
                {
                    ids = new int[ia.GetArrayLength()];
                    var k = 0; foreach (var v in ia.EnumerateArray()) ids[k++] = v.GetInt32();
                }
                list.Add(new MonsterTemplate(r.GetProperty("name").GetString() ?? "", r.GetProperty("cat").GetString() ?? "", r.GetProperty("sub").GetString() ?? "", pts.ToArray(), ids));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        return list;
    }

    /// <summary>
    /// Does this template drop the pet's soul: by NPC id when both sides carry ids (aion2hub and the client name some mobs differently,
    /// e.g. 2110507 "Crimson Manduri Trebuchet Worker" vs "Crimson Manduri Slinger"), else by the aion2hub dropper name.
    /// Diego 2026-10-10: word matching ("Spider", "Slime"...) painted mobs that drop nothing; pets with a soul but no hub drop data
    /// (Coradon, Young Tog…) fall back to the species as the LAST word(s) of the template name.
    /// </summary>
    public static bool Drops(PetInfo pet, MonsterTemplate t)
    {
        if (pet.Droppers.Length == 0)
            return t.Name.EndsWith(pet.Name, StringComparison.OrdinalIgnoreCase) && (t.Name.Length == pet.Name.Length || t.Name[t.Name.Length - pet.Name.Length - 1] == ' ');
        for (var i = 0; i < pet.Droppers.Length; i++)
        {
            if (t.Ids.Length > 0 && i < pet.DropperIds.Length && pet.DropperIds[i] != 0 && t.HasId(pet.DropperIds[i])) return true;
            if (pet.Droppers[i].Equals(t.Name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
