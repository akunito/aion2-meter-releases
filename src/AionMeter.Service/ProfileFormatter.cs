using System.Text;
using System.Text.RegularExpressions;
using AionMeter.Core.Armory;
using AionMeter.Core.Game;
using AionMeter.Core.Ranking;

namespace AionMeter.Service;

/// <summary>Plain-text block of an official profile next to a ranking entry; the join with the rotation is by skill code.</summary>
public static class ProfileFormatter
{
    private static readonly Regex Bonus = new(@"^(.+?) \+(\d+)$", RegexOptions.Compiled);

    public static string Text(CharacterProfile p, TopEntry? entry, GameData data)
    {
        var sb = new StringBuilder(2048);
        sb.Append(p.ClassName).Append(" lv").Append(p.Level).Append("  ·  combat power ").Append(p.CombatPower.ToString("N0"))
          .Append("  ·  item level ").Append(p.ItemLevel);
        if (p.Legion is not null) sb.Append("  ·  ").Append(p.Legion);
        if (p.Title is not null) sb.Append("  ·  \"").Append(p.Title).Append('"');
        sb.Append("  ·  fetched ").Append(p.FetchedAt.ToLocalTime().ToString("MM-dd HH:mm")).Append('\n');

        // Deity stats, the ones that move DPS first.
        sb.Append("stats: ");
        foreach (var s in p.Stats)
            if (s.Type is not ("INT" or "CON" or "ItemLevel") && s.Value != "0") sb.Append(s.Name.Replace(" [", "/").Replace("]", "")).Append(' ').Append(s.Value).Append("  ");
        sb.Append('\n');

        sb.Append("boards: ");
        foreach (var b in p.Boards) sb.Append(b.Name).Append(' ').Append(b.Open).Append('/').Append(b.Total).Append("  ");
        sb.Append('\n');

        sb.Append("Dp: ").Append(string.Join(", ", p.EquippedDp.Select(s => $"{s.Name} lv{s.Level}"))).Append('\n');

        // Daevanion skill bonuses summed over the boards ("Judgment +1" ×3 → +3), joined to the active skill levels.
        var bonus = new Dictionary<string, int>();
        foreach (var eff in p.DaevanionEffects)
        {
            var m = Bonus.Match(eff);
            if (m.Success) bonus[m.Groups[1].Value] = bonus.GetValueOrDefault(m.Groups[1].Value) + int.Parse(m.Groups[2].Value);
        }
        sb.Append("active skills: ");
        foreach (var s in p.Skills.Where(s => s.Category == "Active" && s.Equipped))
            sb.Append(s.Name).Append(" lv").Append(s.Level).Append(bonus.TryGetValue(s.Name, out var b) ? $"(+{b})" : "").Append("  ");
        sb.Append('\n');
        var statBonus = bonus.Where(kv => !p.Skills.Any(s => s.Name == kv.Key)).Select(kv => $"{kv.Key} +{kv.Value}");
        sb.Append("daevanion stats: ").Append(string.Join(", ", statBonus)).Append('\n');

        sb.Append("gear: ");
        foreach (var g in p.Equipment.OrderBy(g => g.SlotPos))
            sb.Append(g.Slot).Append(' ').Append(g.Name).Append(" (").Append(g.Grade).Append(" +").Append(g.Enchant).Append(g.Exceed > 0 ? $" x{g.Exceed}" : "").Append(")  ");
        sb.Append('\n');
        if (p.Pet is not null || p.Wing is not null) sb.Append("pet ").Append(p.Pet).Append("  ·  wing ").Append(p.Wing).Append('\n');

        if (entry is not null)
        {
            // The rotation's skills with the level this character has for each (same code family: base code = code - code % 10000).
            var byBase = p.Skills.ToDictionary(s => GameData.BaseSkill(s.Id), s => s);
            sb.Append("rotation skills: ");
            foreach (var code in entry.Skills.OrderByDescending(s => s.Share).Select(s => GameData.BaseSkill(s.Code)).Distinct().Take(10))
                sb.Append(data.SkillName(code)).Append(byBase.TryGetValue(code, out var s) ? $" lv{s.Level}" : " lv?").Append("  ");
        }
        return sb.ToString();
    }
}
