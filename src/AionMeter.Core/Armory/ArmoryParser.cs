using System.Text.Json;
using System.Text.RegularExpressions;

namespace AionMeter.Core.Armory;

/// <summary>
/// Reads the official character API's JSON (see docs/08-player-databases.md in the project repo). Field names as the site
/// sends them on 2026-10-08; missing fields read as empty, never throw, so a schema drift degrades instead of breaking.
/// </summary>
public static class ArmoryParser
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    public static List<CharacterRef> ParseSearch(string json)
    {
        var list = new List<CharacterRef>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
            list.Add(new CharacterRef(Str(e, "characterId"), Tags.Replace(Str(e, "name"), ""), Int(e, "pcId"), Int(e, "level"),
                Int(e, "serverId"), Str(e, "serverName"), Str(e, "region")));
        return list;
    }

    /// <summary>info + equipment (+ optional daevanion board details) → one profile.</summary>
    public static CharacterProfile Parse(CharacterRef r, string infoJson, string equipmentJson, IEnumerable<string>? daevanionJsons, DateTimeOffset fetchedAt)
    {
        using var info = JsonDocument.Parse(infoJson);
        using var equip = JsonDocument.Parse(equipmentJson);
        var root = info.RootElement;
        var profile = Prop(root, "profile");
        var stats = new List<StatLine>();
        var itemLevel = 0;
        if (Prop(Prop(root, "stat"), "statList") is { ValueKind: JsonValueKind.Array } sl)
            foreach (var s in sl.EnumerateArray())
            {
                var type = Str(s, "type");
                var value = s.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? "") : "";
                var effects = new List<string>();
                if (s.TryGetProperty("statSecondList", out var sec) && sec.ValueKind == JsonValueKind.Array)
                    foreach (var x in sec.EnumerateArray()) effects.Add(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : Str(x, "desc"));
                if (type == "ItemLevel" && int.TryParse(value, out var il)) itemLevel = il;
                stats.Add(new StatLine(type, Str(s, "name"), value, effects));
            }
        var boards = new List<BoardSummary>();
        if (Prop(Prop(root, "daevanion"), "boardList") is { ValueKind: JsonValueKind.Array } bl)
            foreach (var b in bl.EnumerateArray()) boards.Add(new BoardSummary(Int(b, "id"), Str(b, "name"), Int(b, "openNodeCount"), Int(b, "totalNodeCount")));

        var eroot = equip.RootElement;
        var items = new List<EquipItem>();
        if (Prop(Prop(eroot, "equipment"), "equipmentList") is { ValueKind: JsonValueKind.Array } el)
            foreach (var q in el.EnumerateArray())
                items.Add(new EquipItem(Int(q, "slotPos"), Str(q, "slotPosName"), Str(q, "name"), Str(q, "grade"), Int(q, "enchantLevel"), Int(q, "exceedLevel"), Long(q, "id"), NullableStr(q, "icon")));
        var skills = new List<SkillLine>();
        if (Prop(Prop(eroot, "skill"), "skillList") is { ValueKind: JsonValueKind.Array } kl)
            foreach (var k in kl.EnumerateArray())
                skills.Add(new SkillLine(Str(k, "category"), Str(k, "name"), Int(k, "skillLevel"), Int(k, "equip") != 0, Int(k, "acquired") != 0, Int(k, "id")));
        var petwing = Prop(eroot, "petwing");
        var pet = Prop(petwing, "pet") is { ValueKind: JsonValueKind.Object } p ? $"{Str(p, "name")} lv{Int(p, "level")}" : null;
        var wing = Prop(petwing, "wing") is { ValueKind: JsonValueKind.Object } w ? $"{Str(w, "name")} +{Int(w, "enchantLevel")}" : null;

        var nodes = new List<DaevanionNode>();
        var boardEffects = new List<string>(); // the server's own sum of every open node: "Judgment +1", "HP +700", …
        if (daevanionJsons is not null)
            foreach (var dj in daevanionJsons)
            {
                using var d = JsonDocument.Parse(dj);
                foreach (var listName in (string[])["openSkillEffectList", "openStatEffectList"])
                    if (d.RootElement.TryGetProperty(listName, out var ol) && ol.ValueKind == JsonValueKind.Array)
                        foreach (var x in ol.EnumerateArray()) boardEffects.Add(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : Str(x, "desc"));
                if (!d.RootElement.TryGetProperty("nodeList", out var nl) || nl.ValueKind != JsonValueKind.Array) continue;
                foreach (var n in nl.EnumerateArray())
                {
                    if (Str(n, "type") == "None") continue; // empty grid cell (136 of 225 on a board)
                    var effects = new List<string>();
                    if (n.TryGetProperty("effectList", out var efl) && efl.ValueKind == JsonValueKind.Array)
                        foreach (var x in efl.EnumerateArray()) effects.Add(x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : Str(x, "desc"));
                    nodes.Add(new DaevanionNode(Int(n, "boardId"), Int(n, "row"), Int(n, "col"), Int(n, "nodeId"), Int(n, "open") != 0, Str(n, "grade"), Str(n, "type"), Str(n, "name"), effects));
                }
            }

        return new CharacterProfile(r, Str(profile, "className"), Int(profile, "characterLevel"), Long(profile, "combatPower"),
            NullableStr(profile, "titleName"), NullableStr(profile, "regionName"), NullableStr(profile, "raceName"),
            stats, itemLevel, boards, items, pet, wing, skills, nodes, boardEffects, fetchedAt);
    }

    private static JsonElement Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string? NullableStr(JsonElement e, string name) { var s = Str(e, name); return s.Length == 0 ? null : s; }
    private static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    private static long Long(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;
}
