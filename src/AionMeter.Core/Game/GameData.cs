using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Game;

public sealed record NpcDef(int Code, string Name, bool IsBoss, bool IsDummy);

/// <summary>
/// Static lookup tables loaded from the <c>data</c> folder (see <c>data/NOTICE.md</c> for formats and sources).
/// Names are per language (<c>skills/ru.json</c> …) with English as the fallback. Every file is optional — without
/// them the meter still works and shows raw ids ("Skill 11020000", "NPC 2000002").
/// </summary>
public sealed class GameData
{
    public string Language { get; private set; } = "en";
    public Dictionary<int, string> Skills { get; private set; } = new();
    public Dictionary<int, NpcDef> Npcs { get; private set; } = new();
    public HashSet<int> DotSkills { get; } = new();
    public HashSet<int> HealSkills { get; } = new();
    public Dictionary<int, string> Maps { get; private set; } = new();
    public Dictionary<int, string> Servers { get; private set; } = new();

    public static GameData Empty => new();

    /// <summary>
    /// Switches name tables to another language in place. Each table is replaced by a fully built new one, so the
    /// capture thread always sees a complete dictionary (old or new) while the switch happens.
    /// </summary>
    public void ReloadNames(string directory, string language)
    {
        var fresh = Load(directory, language);
        Skills = fresh.Skills;
        Npcs = fresh.Npcs;
        Maps = fresh.Maps;
        Servers = fresh.Servers;
        Language = language;
    }

    /// <summary>Languages that have a skill table, e.g. ["de", "en", "ru", …].</summary>
    public static IReadOnlyList<string> AvailableLanguages(string directory)
    {
        var dir = Path.Combine(directory, "skills");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToList()
            : [];
    }

    public static GameData Load(string directory, string language = "en")
    {
        var data = new GameData { Language = language };
        if (!Directory.Exists(directory)) return data;

        // English first, then the chosen language on top: anything it lacks keeps the English name.
        string[] layers = language == "en" ? ["en"] : ["en", language];
        foreach (var lang in layers)
        {
            LoadSkills(data, Path.Combine(directory, "skills", lang + ".json"));
            LoadNpcs(data, Path.Combine(directory, "npcs", lang + ".json"));
            LoadMaps(data, Path.Combine(directory, "dungeons", lang + ".json"));
        }
        // Single-file overrides (user-supplied corrections) win over everything.
        LoadSkills(data, Path.Combine(directory, "skills.json"));
        LoadNpcs(data, Path.Combine(directory, "npcs.json"));
        LoadMaps(data, Path.Combine(directory, "maps.json"));

        foreach (var (key, value) in ReadObject(Path.Combine(directory, "servers.json")))
        {
            if (key != "servers" || value.ValueKind != JsonValueKind.Object) continue;
            foreach (var server in value.EnumerateObject())
            {
                if (!int.TryParse(server.Name, out var id) || server.Value.ValueKind != JsonValueKind.Object) continue;
                var name = server.Value.TryGetProperty(language, out var n) && n.GetString() is { Length: > 0 } local ? local
                    : server.Value.TryGetProperty("en", out var en) ? en.GetString() : null;
                if (name is not null) data.Servers[id] = name;
            }
        }

        ReadIntArray(Path.Combine(directory, "dot_skill_ids.json"), data.DotSkills);
        ReadIntArray(Path.Combine(directory, "healing_skill_ids.json"), data.HealSkills);
        return data;
    }

    private static void LoadSkills(GameData data, string path)
    {
        foreach (var (key, value) in ReadObject(path))
            if (int.TryParse(key, out var code) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } name)
                data.Skills[code] = name;
    }

    private static void LoadNpcs(GameData data, string path)
    {
        foreach (var (key, value) in ReadObject(path))
        {
            if (!int.TryParse(key, out var code)) continue;
            data.Npcs.TryGetValue(code, out var prev);
            if (value.ValueKind == JsonValueKind.String)
            {
                data.Npcs[code] = new NpcDef(code, value.GetString()!, prev?.IsBoss ?? false, prev?.IsDummy ?? false);
            }
            else if (value.ValueKind == JsonValueKind.Object)
            {
                var name = value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0 && prev is not null) name = prev.Name;
                var boss = value.TryGetProperty("isBoss", out var b) ? b.ValueKind == JsonValueKind.True : prev?.IsBoss ?? false;
                var dummy = value.TryGetProperty("isDummy", out var d) ? d.ValueKind == JsonValueKind.True : prev?.IsDummy ?? false;
                data.Npcs[code] = new NpcDef(code, name, boss, dummy);
            }
        }
    }

    private static void LoadMaps(GameData data, string path)
    {
        foreach (var (key, value) in ReadObject(path))
        {
            if (!int.TryParse(key, out var id)) continue;
            var name = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object when value.TryGetProperty("name", out var n) => n.GetString(),
                _ => null,
            };
            if (!string.IsNullOrEmpty(name)) data.Maps[id] = name;
        }
    }

    public string ServerName(int id) => Servers.TryGetValue(id, out var name) ? name : id == 0 ? "" : id.ToString();

    /// <summary>Global server ids are race×1000 + region×100 + n.</summary>
    public static string RegionOf(int serverId) => (serverId / 100 % 10) switch
    {
        1 => "NA-East",
        2 => "NA-West",
        3 => "EU",
        4 => "SA",
        5 => "Asia",
        _ => "",
    };

    private static void ReadIntArray(string path, HashSet<int> target)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.TryGetInt32(out var id)) target.Add(id);
        }
        catch (JsonException)
        {
        }
    }

    private static IEnumerable<(string Key, JsonElement Value)> ReadObject(string path)
    {
        if (!File.Exists(path)) yield break;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            yield break;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) yield break;
            foreach (var p in doc.RootElement.EnumerateObject()) yield return (p.Name, p.Value.Clone());
        }
    }

    // ------------------------------------------------------------------ skills

    /// <summary>Player skill codes are 8 digits; the last four carry level / specialisation and are dropped for grouping.</summary>
    public static bool IsClassSkill(int code) => code is >= 10_000_000 and <= 19_999_999;

    /// <summary>NPC skills are 7 digits.</summary>
    public static bool IsNpcSkill(int code) => code is >= 1_000_000 and <= 9_999_999;

    public static int BaseSkill(int code) => IsClassSkill(code) ? code - code % 10_000 : code;

    public string SkillName(int code)
    {
        if (Skills.TryGetValue(code, out var name)) return name;
        var b = BaseSkill(code);
        if (b != code && Skills.TryGetValue(b, out name)) return name;
        return $"Skill {code}";
    }

    public bool IsDamageDot(int rawDotSkill) => DotSkills.Count == 0 || DotSkills.Contains(rawDotSkill);

    /// <summary>
    /// Records that use the damage opcode but are not damage: Elementalist spirit links (1699xxxx, 1677xxxx) and
    /// known heal skills.
    /// </summary>
    public bool IsNonDamageSkill(int code) =>
        code is >= 16_990_000 and <= 16_999_999 or >= 16_770_000 and <= 16_779_999
        || HealSkills.Contains(code) || HealSkills.Contains(BaseSkill(code));

    /// <summary>Class from the skill code prefix (code / 1,000,000). Mob skills (xx00xxxx) map to nothing.</summary>
    public static GameClass ClassFromSkill(int code)
    {
        if (!IsClassSkill(code) || code / 10_000 % 100 == 0) return GameClass.Unknown;
        return (code / 1_000_000) switch
        {
            11 => GameClass.Gladiator,
            12 => GameClass.Templar,
            13 => GameClass.Assassin,
            14 => GameClass.Ranger,
            15 => GameClass.Sorcerer,
            16 => GameClass.Elementalist,
            17 => GameClass.Cleric,
            18 => GameClass.Chanter,
            19 => GameClass.Brawler,
            _ => GameClass.Unknown,
        };
    }

    /// <summary>Class id as sent in identity / party records: four consecutive values per class.</summary>
    public static GameClass ClassFromWire(uint id) => id switch
    {
        >= 5 and <= 8 => GameClass.Gladiator,
        >= 9 and <= 12 => GameClass.Templar,
        >= 13 and <= 16 => GameClass.Ranger,
        >= 17 and <= 20 => GameClass.Assassin,
        >= 21 and <= 24 => GameClass.Elementalist,
        >= 25 and <= 28 => GameClass.Sorcerer,
        >= 29 and <= 32 => GameClass.Cleric,
        >= 33 and <= 36 => GameClass.Chanter,
        >= 45 and <= 48 => GameClass.Brawler,
        _ => GameClass.Unknown,
    };

    // ------------------------------------------------------------------ npcs / maps

    public string NpcName(int code) => Npcs.TryGetValue(code, out var n) && n.Name.Length > 0 ? n.Name : $"NPC {code}";

    /// <summary>Dungeon name from the tables; maps the tables do not name are open-world fields and layers.</summary>
    public string MapName(int id) => Maps.TryGetValue(id, out var name) ? name : IsDungeonMap(id) ? $"Dungeon {id}" : "Open world";

    /// <summary>Instanced dungeons live in the 6xxxxx map range.</summary>
    public static bool IsDungeonMap(int id) => id is >= 600_000 and < 700_000;

    /// <summary>
    /// A map's field bosses share one block of a thousand NPC codes (Altgard: 2400xxx), and the in-game boss list
    /// (<see cref="Events.FieldBossListEvent"/>) shows them in code order. Returns that block's bosses, sorted.
    /// </summary>
    public IReadOnlyList<int> FieldBossesInBlock(int block) =>
        Npcs.Values.Where(n => n.IsBoss && !n.IsDummy && n.Code / 1000 == block).Select(n => n.Code).Order().ToList();

    /// <summary>The boss in a list slot (map × 100 + place), when the block holds exactly as many bosses as the list.</summary>
    public int FieldBossInSlot(int block, int mapId, int slotId, int slotCount) =>
        FieldBossInSlot(FieldBossesInBlock(block), mapId, slotId, slotCount);

    /// <summary>
    /// The boss in a list slot from an explicit slot-ordered list (data/field_boss_maps.json "bosses"): for maps whose
    /// field bosses span more than one code block, or whose block holds bosses the list leaves out.
    /// </summary>
    public static int FieldBossInSlot(IReadOnlyList<int> bosses, int mapId, int slotId, int slotCount)
    {
        var place = slotId - mapId * 100;
        return bosses.Count == slotCount && place >= 1 && place <= bosses.Count ? bosses[place - 1] : 0;
    }
}
