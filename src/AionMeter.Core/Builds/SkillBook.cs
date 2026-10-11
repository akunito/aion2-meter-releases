using System.Text;
using System.Text.Json;

namespace AionMeter.Core.Builds;

public sealed record SkillSpec(int Id, int Slot, int RequiredLevel, string NameEn, string NameEs, string? Icon)
{
    public string Name(string lang) => lang == "es" && NameEs.Length > 0 ? NameEs : NameEn;
}

/// <summary>
/// One class skill from data/gamedb/skills/&lt;class&gt;.json (tools/skill-catalog.py, questlog). Desc* keep &lt;b&gt;…&lt;/b&gt; and {i} markers;
/// Values[i][level-1] is the number for marker i (a short array holds its last value to MaxLevel; a null array = questlog has no number).
/// </summary>
public sealed record SkillInfo(int Id, string ClassId, string Sub, int Grade, string? DamageType, bool Basic, string Icon,
                               string NameEn, string NameEs, string DescEn, string DescEs, string?[]?[] Values, SkillSpec[] Specs, int MaxLevel)
{
    public string Name(string lang) => lang == "es" && NameEs.Length > 0 ? NameEs : NameEn;
    public string Template(string lang) => lang == "es" && DescEs.Length > 0 ? DescEs : DescEn;

    public string? Value(int index, int level)
    {
        if ((uint)index >= (uint)Values.Length || Values[index] is not { Length: > 0 } v) return null;
        var i = Math.Clamp(level, 1, MaxLevel) - 1;
        return v[i < v.Length ? i : v.Length - 1];
    }
}

/// <summary>Skill catalogue per class, loaded on first use of the class. Missing or broken files give an empty class, never an exception.</summary>
public sealed class SkillBook
{
    public const int DefaultMaxLevel = 40;
    public const string Cdn = "https://cdn.questlog.gg/aion-2/assets/Game/UI/Resource/Texture/";
    // Skill ids carry the class in their first two digits (11 = gladiator … 18 = chanter), the same order as this array.
    public static readonly string[] Classes = ["gladiator", "templar", "assassin", "ranger", "sorcerer", "elementalist", "cleric", "chanter"];

    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, SkillInfo[]> _byClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SkillInfo> _byId = new();

    public SkillBook(string directory) { _dir = directory; }

    /// <summary>Questlog's icon paths end in "NAME.NAME"; the CDN serves "NAME.webp" (the .png and the doubled form 404).</summary>
    public static string IconUrl(string stem) => Cdn + "Skill/" + stem + ".webp";
    public static string SpecIconUrl(string stem) => Cdn + "Item/ETC/" + stem + ".webp";

    public static string? ClassOf(int skillId)
    {
        var d = skillId / 1_000_000 - 11;
        return (uint)d < (uint)Classes.Length ? Classes[d] : null;
    }

    public IReadOnlyList<SkillInfo> ForClass(string classId)
    {
        lock (_lock)
        {
            if (_byClass.TryGetValue(classId, out var s)) return s;
            var list = LoadClass(classId);
            _byClass[classId] = list;
            foreach (var x in list) _byId[x.Id] = x;
            return list;
        }
    }

    public SkillInfo? Get(int skillId)
    {
        lock (_lock) if (_byId.TryGetValue(skillId, out var s)) return s;
        if (ClassOf(skillId) is not { } c) return null;
        ForClass(c);
        lock (_lock) return _byId.GetValueOrDefault(skillId);
    }

    public string? Name(int skillId, string lang) => Get(skillId)?.Name(lang);

    /// <summary>Description at <paramref name="level"/> (clamped to 1..MaxLevel) with the numbers filled in; &lt;b&gt; kept unless <paramref name="plain"/>.</summary>
    public string? Resolve(int skillId, int level, string lang, bool plain = false) => Get(skillId) is { } s ? Resolve(s, level, lang, plain) : null;

    public static string Resolve(SkillInfo s, int level, string lang, bool plain = false)
    {
        var t = s.Template(lang);
        var sb = new StringBuilder(t.Length + 16);
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '{')
            {
                var j = i + 1; var n = 0;
                while (j < t.Length && char.IsAsciiDigit(t[j])) n = n * 10 + (t[j++] - '0');
                if (j > i + 1 && j < t.Length && t[j] == '}') { sb.Append(s.Value(n, level) ?? "?"); i = j; continue; }
            }
            else if (plain && c == '<' && (string.CompareOrdinal(t, i, "<b>", 0, 3) == 0 || string.CompareOrdinal(t, i, "</b>", 0, 4) == 0))
            {
                i += t[i + 1] == '/' ? 3 : 2;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private SkillInfo[] LoadClass(string classId)
    {
        var path = Path.Combine(_dir, classId.ToLowerInvariant() + ".json");
        if (!File.Exists(path)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var list = new List<SkillInfo>(40);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var (nameEn, nameEs) = Pair(r, "name");
                var (descEn, descEs) = Pair(r, "desc");
                string?[]?[] vals = [];
                if (r.TryGetProperty("vals", out var va) && va.ValueKind == JsonValueKind.Array)
                {
                    vals = new string?[]?[va.GetArrayLength()];
                    var k = 0;
                    foreach (var v in va.EnumerateArray())
                    {
                        if (v.ValueKind == JsonValueKind.Array)
                        {
                            var a = new string?[v.GetArrayLength()]; var m = 0;
                            foreach (var x in v.EnumerateArray()) a[m++] = Scalar(x);
                            vals[k] = a;
                        }
                        k++;
                    }
                }
                SkillSpec[] specs = [];
                if (r.TryGetProperty("specs", out var sa) && sa.ValueKind == JsonValueKind.Array)
                {
                    specs = new SkillSpec[sa.GetArrayLength()]; var k = 0;
                    foreach (var p in sa.EnumerateArray())
                    {
                        var (en, es) = Pair(p, "name");
                        specs[k++] = new SkillSpec(p.GetProperty("id").GetInt32(), Int(p, "slot"), Int(p, "req"), en, es, Str(p, "icon"));
                    }
                }
                list.Add(new SkillInfo(r.GetProperty("id").GetInt32(), classId.ToLowerInvariant(), Str(r, "sub") ?? "", Int(r, "grade"), Str(r, "dmg"),
                                       r.TryGetProperty("basic", out var b) && b.ValueKind == JsonValueKind.True, Str(r, "icon") ?? "",
                                       nameEn, nameEs, descEn, descEs, vals, specs, Int(r, "max") is > 0 and var mx ? mx : DefaultMaxLevel));
            }
            return list.ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return []; }
    }

    private static string? Scalar(JsonElement x) => x.ValueKind switch
    {
        JsonValueKind.String => x.GetString(),
        JsonValueKind.Number => x.GetRawText(),
        _ => null,
    };

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static (string En, string Es) Pair(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object) return ("", "");
        return (Str(o, "en") ?? "", Str(o, "es") ?? "");
    }
}
