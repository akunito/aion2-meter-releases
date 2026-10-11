using System.Globalization;
using AionMeter.Core.Armory;

namespace AionMeter.Core.Builds;

public enum SpecToggle { Added, Removed, Locked, Full, Unknown }

/// <summary>
/// The Build tab's edits as pure functions: every call returns a new <see cref="Build"/> (lists copied), the input is never changed,
/// and a refused edit returns the input itself (callers compare references to know whether anything happened).
/// </summary>
public static class BuildEditor
{
    public const int Keys = 12, Rows = 4;
    // questlog's planner (observed 2026-10-11): actives and passives carry at most 3 spec diamonds at level 20, stigmas 4
    public const int MaxSpecs = 3, MaxSpecsStigma = 4;

    public static int SpecCap(SkillInfo s) => s.Sub == "stigma" ? MaxSpecsStigma : MaxSpecs;

    public static string Slot(int key, int row) => key.ToString(CultureInfo.InvariantCulture) + "." + row.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseSlot(string? slot, out int key, out int row)
    {
        key = row = 0;
        if (slot is null) return false;
        var dot = slot.IndexOf('.');
        return dot > 0 && int.TryParse(slot.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out key) &&
               int.TryParse(slot.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out row) &&
               key is >= 1 and <= Keys && row is >= 0 and < Rows;
    }

    public static string KeyName(int key) => key switch { 9 => "Q", 10 => "E", 11 => "LMB", 12 => "RMB", _ => key.ToString(CultureInfo.InvariantCulture) };

    public static int KeyOf(string? slot) => TryParseSlot(slot, out var k, out _) ? k : 0;

    public static int? SkillAt(Build b, int key, int row)
    {
        foreach (var s in b.Skills)
            if (TryParseSlot(s.Slot, out var k, out var r) && k == key && r == row) return s.SkillId;
        return null;
    }

    /// <summary>Lowest empty row of <paramref name="key"/>, -1 when all four are taken.</summary>
    public static int FirstFreeRow(Build b, int key)
    {
        var used = 0;
        foreach (var s in b.Skills)
            if (TryParseSlot(s.Slot, out var k, out var r) && k == key) used |= 1 << r;
        for (var r = 0; r < Rows; r++) if ((used & (1 << r)) == 0) return r;
        return -1;
    }

    /// <summary>Into the first free row of <paramref name="key"/> (added at level 1 when not in the build). Passives never go on the bar.</summary>
    public static Build Equip(Build b, SkillInfo s, int key)
    {
        if (s.Sub == "passive" || key is < 1 or > Keys) return b;
        if (b.Skill(s.Id) is { } cur && KeyOf(cur.Slot) == key) return b;
        var row = FirstFreeRow(b, key);
        return row < 0 ? b : Place(b, s.Id, Slot(key, row));
    }

    /// <summary>Onto one empty cell; refused when the cell is taken.</summary>
    public static Build MoveTo(Build b, SkillInfo s, int key, int row)
    {
        if (s.Sub == "passive" || key is < 1 or > Keys || row is < 0 or >= Rows || SkillAt(b, key, row) is not null) return b;
        return Place(b, s.Id, Slot(key, row));
    }

    private static Build Place(Build b, int skillId, string slot)
    {
        var withSkill = b.Skill(skillId) is null ? AddSkill(b, skillId) : b;
        return Map(withSkill, skillId, x => x with { Slot = slot });
    }

    public static Build Unequip(Build b, int skillId) => b.Skill(skillId) is { Slot: not null } ? Map(b, skillId, x => x with { Slot = null }) : b;

    public static Build AddSkill(Build b, int skillId, int level = 1)
    {
        if (b.Skill(skillId) is not null) return b;
        var skills = new List<BuildSkill>(b.Skills.Count + 1);
        skills.AddRange(b.Skills);
        skills.Add(new BuildSkill(skillId, Math.Max(1, level), null, []));
        return b with { Skills = skills };
    }

    /// <summary>Out of the build, the bar, the priority, every macro and the notes.</summary>
    public static Build RemoveSkill(Build b, int skillId)
    {
        if (b.Skill(skillId) is null) return b;
        var skills = new List<BuildSkill>(b.Skills.Count);
        foreach (var s in b.Skills) if (s.SkillId != skillId) skills.Add(s);
        var prio = new List<int>(b.Priority.Count);
        foreach (var p in b.Priority) if (p != skillId) prio.Add(p);
        var macros = new List<BuildMacro>(b.Macros.Count);
        foreach (var m in b.Macros) macros.Add(m.Steps.Contains(skillId) ? m with { Steps = m.Steps.FindAll(x => x != skillId) } : m);
        var notes = b.Notes;
        if (notes.ContainsKey(skillId)) { notes = new Dictionary<int, string>(notes); notes.Remove(skillId); }
        return b with { Skills = skills, Priority = prio, Macros = macros, Notes = notes };
    }

    /// <summary>Clamped to 1..MaxLevel; chosen specs that need a higher level are dropped (the game would not allow them).</summary>
    public static Build SetLevel(Build b, SkillInfo s, int level)
    {
        if (b.Skill(s.Id) is not { } cur) return b;
        level = Math.Clamp(level, 1, s.MaxLevel);
        if (cur.Level == level) return b;
        var specs = cur.Specs;
        foreach (var id in cur.Specs)
            if (Spec(s, id) is { } sp && sp.RequiredLevel > level) { specs = specs.FindAll(x => Spec(s, x) is not { } y || y.RequiredLevel <= level); break; }
        return Map(b, s.Id, x => x with { Level = level, Specs = specs });
    }

    public static Build ToggleSpec(Build b, SkillInfo s, int specId, out SpecToggle result)
    {
        if (b.Skill(s.Id) is not { } cur || Spec(s, specId) is not { } sp) { result = SpecToggle.Unknown; return b; }
        if (cur.Specs.Contains(specId))
        {
            result = SpecToggle.Removed;
            return Map(b, s.Id, x => x with { Specs = x.Specs.FindAll(y => y != specId) });
        }
        if (sp.RequiredLevel > cur.Level) { result = SpecToggle.Locked; return b; }
        if (cur.Specs.Count >= SpecCap(s)) { result = SpecToggle.Full; return b; }
        result = SpecToggle.Added;
        return Map(b, s.Id, x => x with { Specs = [.. x.Specs, specId] });
    }

    public static Build AddPriority(Build b, int skillId) =>
        b.Skill(skillId) is null || b.Priority.Contains(skillId) ? b : b with { Priority = [.. b.Priority, skillId] };

    public static Build RemovePriority(Build b, int skillId) =>
        b.Priority.Contains(skillId) ? b with { Priority = b.Priority.FindAll(x => x != skillId) } : b;

    /// <summary>Swaps with the previous (<paramref name="direction"/> &lt; 0) or next entry of the same category: the tab shows one chain per category over a single list.</summary>
    public static Build MovePriority(Build b, int skillId, int direction, Func<int, string?> category)
    {
        var i = b.Priority.IndexOf(skillId);
        if (i < 0 || direction == 0) return b;
        var cat = category(skillId);
        var step = direction < 0 ? -1 : 1;
        for (var j = i + step; j >= 0 && j < b.Priority.Count; j += step)
        {
            if (category(b.Priority[j]) != cat) continue;
            var prio = new List<int>(b.Priority);
            (prio[i], prio[j]) = (prio[j], prio[i]);
            return b with { Priority = prio };
        }
        return b;
    }

    public static Build AddMacro(Build b, BuildMacro m) => m.Steps.Count == 0 ? b : b with { Macros = [.. b.Macros, m] };

    public static Build RemoveMacro(Build b, int index)
    {
        if ((uint)index >= (uint)b.Macros.Count) return b;
        var macros = new List<BuildMacro>(b.Macros);
        macros.RemoveAt(index);
        return b with { Macros = macros };
    }

    /// <summary>
    /// A build that is what the character has now: every acquired class skill at its current level. The official profile says
    /// nothing about bar slots, specs or priority, so those stay empty.
    /// </summary>
    public static Build FromProfile(CharacterProfile p, string name)
    {
        var cls = ClassIdOf(p.ClassName);
        if (cls is null) foreach (var s in p.Skills) if (s.Acquired && SkillBook.ClassOf(s.Id) is { } c) { cls = c; break; }
        var skills = new List<BuildSkill>(p.Skills.Count);
        foreach (var s in p.Skills)
            if (s.Acquired && s.Level > 0 && SkillBook.ClassOf(s.Id) is { } c && c == cls && !Contains(skills, s.Id))
                skills.Add(new BuildSkill(s.Id, s.Level, null, []));
        return new Build { Name = name, ClassId = cls ?? "", Skills = skills };
    }

    /// <summary>Catalogue class id for the official profile's class name ("Ranger", "Spiritmaster" …), null when unknown.</summary>
    public static string? ClassIdOf(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        var n = className.Trim();
        foreach (var c in SkillBook.Classes) if (n.Equals(c, StringComparison.OrdinalIgnoreCase)) return c;
        // the official site and older translations call the elementalist "Spiritmaster" / "Spirit Master"
        if (n.Equals("spiritmaster", StringComparison.OrdinalIgnoreCase) || n.Equals("spirit master", StringComparison.OrdinalIgnoreCase)) return "elementalist";
        return null;
    }

    public static SkillSpec? Spec(SkillInfo s, int specId)
    {
        foreach (var sp in s.Specs) if (sp.Id == specId) return sp;
        return null;
    }

    private static bool Contains(List<BuildSkill> skills, int id)
    {
        foreach (var s in skills) if (s.SkillId == id) return true;
        return false;
    }

    private static Build Map(Build b, int skillId, Func<BuildSkill, BuildSkill> f)
    {
        var skills = new List<BuildSkill>(b.Skills.Count);
        foreach (var s in b.Skills) skills.Add(s.SkillId == skillId ? f(s) : s);
        return b with { Skills = skills };
    }
}
