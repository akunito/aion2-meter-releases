using System.Globalization;
using System.Text;
using AionMeter.Core;
using AionMeter.Core.Events;
using AionMeter.Core.Leaderboard;
using AionMeter.Core.Ranking;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>The display-free part of the Top DPS tab (A2M-50 port of the Avalonia TopDpsView): row texts, the rotation summary, the class pick.</summary>
internal static class TopDpsLogic
{
    public static readonly GameClass[] AllClasses =
        [GameClass.Gladiator, GameClass.Templar, GameClass.Ranger, GameClass.Assassin, GameClass.Sorcerer, GameClass.Elementalist, GameClass.Cleric, GameClass.Chanter];

    /// <summary>The class shown: the one picked, else the character's, else the first listed; Unknown when the list is empty.</summary>
    public static GameClass PickClass(GameClass current, GameClass self, IReadOnlyList<GameClass> classes)
    {
        if (current == GameClass.Unknown && self != GameClass.Unknown) current = self;
        if (current != GameClass.Unknown)
            for (var i = 0; i < classes.Count; i++) if (classes[i] == current) return current;
        return classes.Count > 0 ? classes[0] : GameClass.Unknown;
    }

    /// <summary>"Boss · Zone · 2026-10-13 21:30 · 1.2M dmg".</summary>
    public static string FightMeta(TopEntry e) =>
        e.Title + (string.IsNullOrEmpty(e.Zone) ? "" : " · " + e.Zone) + " · " + e.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        + " · " + Format.Compact(e.Damage) + " dmg";

    public static string Dps(TopEntry e) => Format.Compact(e.Dps) + "/s";

    public static string CombatTime(TopEntry e) => TimeSpan.FromMilliseconds(e.CombatMs).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>"Server · lv45 · #12 in NAE · seen 10-13 21:30".</summary>
    public static string BoardMeta(LeaderboardRow b) =>
        $"{b.ServerName} · lv{b.Level} · #{b.Rank} in {b.Region.ToUpperInvariant()}" + (b.LastInfoAt is { } t ? " · seen " + t.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : "");

    public static string ClassTab(string className, int count, bool own) => own ? className + " (" + count.ToString(CultureInfo.InvariantCulture) + ")" : className;

    /// <summary>The twelve biggest skill shares: "Name 34% ×12   Other 20% ×8".</summary>
    public static string Skills(TopEntry e, int max = 12)
    {
        var n = e.Skills.Count;
        if (n == 0) return "";
        var order = new TopSkill[n];
        for (var i = 0; i < n; i++) order[i] = e.Skills[i];
        // stable descending by share, as OrderByDescending
        var idx = new int[n];
        for (var i = 0; i < n; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => { var c = order[b].Share.CompareTo(order[a].Share); return c != 0 ? c : a.CompareTo(b); });
        var sb = new StringBuilder();
        for (var k = 0; k < n && k < max; k++)
        {
            var s = order[idx[k]];
            if (sb.Length > 0) sb.Append("   ");
            sb.Append(s.Name).Append(' ').Append((s.Share * 100).ToString("0", CultureInfo.InvariantCulture)).Append("% ×").Append(s.Hits);
        }
        return sb.ToString();
    }

    /// <summary>The opener: the first <paramref name="n"/> casts joined by arrows.</summary>
    public static string Opening(TopEntry e, Func<int, string> skillName, int n = 16)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < e.Casts.Count && i < n; i++)
        {
            if (i > 0) sb.Append(" → ");
            sb.Append(skillName(e.Casts[i].S));
        }
        return sb.ToString();
    }

    /// <summary>The most repeated three-cast sequences, one per line with their count; "—" when nothing repeats.</summary>
    public static string Repeated(TopEntry e, Func<int, string> skillName)
    {
        var sb = new StringBuilder();
        foreach (var (skills, count) in TopDpsStore.Frequent(e, 3, 5))
        {
            if (sb.Length > 0) sb.Append('\n');
            for (var i = 0; i < skills.Length; i++) { if (i > 0) sb.Append(" → "); sb.Append(skillName(skills[i])); }
            sb.Append("   ×").Append(count);
        }
        return sb.Length > 0 ? sb.ToString() : "—";
    }

    /// <summary>Casts per minute of the ten most cast skills: "Name 6.5/min   Other 3.0/min".</summary>
    public static string Cadence(TopEntry e, Func<int, string> skillName)
    {
        var sb = new StringBuilder();
        var list = TopDpsStore.Cadence(e);
        for (var i = 0; i < list.Count && i < 10; i++)
        {
            if (sb.Length > 0) sb.Append("   ");
            sb.Append(skillName(list[i].Skill)).Append(' ').Append(list[i].PerMinute.ToString("0.0", CultureInfo.InvariantCulture)).Append("/min");
        }
        return sb.ToString();
    }
}
