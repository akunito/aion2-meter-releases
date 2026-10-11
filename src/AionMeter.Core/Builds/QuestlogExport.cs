using System.Text;
using System.Text.Json;

namespace AionMeter.Core.Builds;

/// <summary>
/// The JSON the Build tab's "Copy for questlog" puts on the clipboard and tools/questlog-publish.js reads in the user's own questlog
/// session: {name, classId, skills:[{skillId, level, slot, specs}], priority:[ids], macros:[{name, steps:[ids]}], tags:[], description}.
/// </summary>
public static class QuestlogExport
{
    public static string ToJson(Build b)
    {
        using var ms = new MemoryStream(1024 + b.Skills.Count * 96);
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("name", b.Name);
            w.WriteString("classId", b.ClassId);
            w.WriteStartArray("skills");
            foreach (var s in b.Skills)
            {
                w.WriteStartObject();
                w.WriteNumber("skillId", s.SkillId);
                w.WriteNumber("level", s.Level);
                if (s.Slot is null) w.WriteNull("slot"); else w.WriteString("slot", s.Slot);
                w.WriteStartArray("specs");
                foreach (var sp in s.Specs) w.WriteNumberValue(sp);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("priority");
            foreach (var p in b.Priority) w.WriteNumberValue(p);
            w.WriteEndArray();
            w.WriteStartArray("macros");
            foreach (var m in b.Macros)
            {
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                w.WriteStartArray("steps");
                foreach (var st in m.Steps) w.WriteNumberValue(st);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("tags");
            w.WriteEndArray();
            w.WriteString("description", b.Description ?? "");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}

public static class BuildSuggestions
{
    public const int Take = 5;

    /// <summary>The most-liked builds of one class (newest first among equal likes), at most <paramref name="take"/>, each questlog id once.</summary>
    public static List<BuildSummary> Rank(IEnumerable<BuildSummary> builds, string classId, int take = Take)
    {
        var list = new List<BuildSummary>();
        var seen = new HashSet<long>();
        foreach (var b in builds)
            if (string.Equals(b.ClassId, classId, StringComparison.OrdinalIgnoreCase) && seen.Add(b.Id)) list.Add(b);
        list.Sort(static (a, b) => b.LikeCount != a.LikeCount ? b.LikeCount.CompareTo(a.LikeCount) : Nullable.Compare(b.UpdatedAt, a.UpdatedAt));
        if (list.Count > take) list.RemoveRange(take, list.Count - take);
        return list;
    }
}

public static class QuestlogImport
{
    /// <summary>
    /// A questlog build ready for <see cref="BuildStore.Save"/>: imported again, it replaces the earlier copy (same questlog id) and keeps
    /// what only the meter has (notes, Daevanion plan and where it came from); a new one gets a fresh id. Class falls back to the skills' class when questlog has none.
    /// </summary>
    public static Build Prepare(Build imported, IReadOnlyList<Build> existing)
    {
        var cls = imported.ClassId.Length > 0 ? imported.ClassId : imported.Skills.Count > 0 ? SkillBook.ClassOf(imported.Skills[0].SkillId) ?? "" : "";
        // the parsed UpdatedAt is questlog's; BuildStore.Save overwrites UpdatedAt, so it is kept as SourceUpdatedAt
        var b = imported with { Id = "", ClassId = cls, SourceUpdatedAt = imported.UpdatedAt != default ? imported.UpdatedAt : imported.SourceUpdatedAt };
        if (imported.Source is { Id: > 0 } src)
            foreach (var e in existing)
                if (e.Source?.Id == src.Id)
                    return b with { Id = e.Id, Notes = e.Notes, Daevanion = e.Daevanion, PlanSource = e.PlanSource, PlanName = e.PlanName, PlanSourceUpdatedAt = e.PlanSourceUpdatedAt };
        return b;
    }
}
