using System.Text;
using AionMeter.Core.Combat;

namespace AionMeter.Core;

/// <summary>The words of a chat summary, in the interface language (English by default).</summary>
public sealed record ChatWords
{
    public static ChatWords English { get; } = new();

    public string Kill { get; init; } = "KILL";
    public string Wipe { get; init; } = "WIPE";
    /// <summary>"(boss 29%)" after a wipe or while the fight goes on.</summary>
    public string BossLeft { get; init; } = "boss {0}";
    /// <summary>A world boss with a crowd: "(444 attacking, damage of 53 sent)" — the server kept most damage from us
    /// (see <see cref="Combat.EncounterSnapshot.MostDamageUnsent"/>), so the numbers are not the fight's.</summary>
    public string Fighting { get; init; } = "{0} attacking";
    public string DamageSentBy { get; init; } = "damage of {0} sent";
    public string Party { get; init; } = "party {0}";
    /// <summary>"#3 of 5": the player's place among the players.</summary>
    public string Place { get; init; } = "#{0} of {1}";
    public string Damage { get; init; } = "{0} dmg";
    public string Crit { get; init; } = "crit {0}";
    public string TopHit { get; init; } = "top hit {0}";
    /// <summary>"+3 more": players that did not fit in one chat message.</summary>
    public string More { get; init; } = "+{0} more";
    public string ColPlayer { get; init; } = "Player";
    public string ColDps { get; init; } = "DPS";
    public string ColDamage { get; init; } = "Damage";
    public string ColShare { get; init; } = "Share";
    /// <summary>Gear score and combat power after a name: "(GS 2859 / CP 59.07K)".</summary>
    public string GearScore { get; init; } = "GS";
    public string CombatPower { get; init; } = "CP";
}

/// <summary>
/// Fight summaries to paste elsewhere: one line sized for the in-game chat box (plain ASCII separators, numbers with
/// their unit, so they read without the meter), or a table for Discord.
/// </summary>
public static class ChatLine
{
    public const int MaxLength = 180;

    /// <summary>
    /// <c>Balhash 2:41 KILL | Talwyn (GS 2859 / CP 59.07K) #3 of 5: 5.05K/s, 813.2K dmg (20%), crit 15%, top hit 34.52K |
    /// party 24.79K/s</c> — gear score and combat power when the party roster gave them.
    /// </summary>
    /// <param name="name">How a player is called (e.g. "Templar #10388" for one whose name the server never sent).</param>
    public static string Player(EncounterSnapshot s, uint actorId, ChatWords? words = null, Func<CombatantSnapshot, string>? name = null)
    {
        var w = words ?? ChatWords.English;
        name ??= c => c.Name;
        var players = Players(s);
        var c = players.FirstOrDefault(x => x.ActorId == actorId);
        if (c is null) return Party(s, w, name);
        var stats = new List<string>
        {
            $"{Format.Compact(c.Dps)}/s",
            $"{string.Format(w.Damage, Format.Compact(c.Damage))} ({Pct(c.Share)})",
        };
        if (c.Hits > 0) stats.Add(string.Format(w.Crit, Pct(c.CritRate)));
        if (c.MaxHit > 0) stats.Add(string.Format(w.TopHit, Format.Compact(c.MaxHit)));
        var place = string.Format(w.Place, players.IndexOf(c) + 1, players.Count);
        return Trim($"{Header(s, w)} | {name(c)}{Gear(c, w)} {place}: {string.Join(", ", stats)} | {PartyDps(s, w)}");
    }

    /// <summary>
    /// <c>Balhash 2:41 KILL | 1.Sylvaen 10.18K/s 41% | 2.Borgrim 7.09K/s 29% | … | party 24.79K/s</c> — gear score and
    /// combat power after the names when the whole party fits in one message with them; otherwise as many players as fit,
    /// then "+3 more".
    /// </summary>
    public static string Party(EncounterSnapshot s, ChatWords? words = null, Func<CombatantSnapshot, string>? name = null)
    {
        var w = words ?? ChatWords.English;
        name ??= c => c.Name;
        var players = Players(s);
        var head = Header(s, w);
        var tail = " | " + PartyDps(s, w);

        var geared = string.Concat(players.Select((c, i) => Part(c, i, Gear(c, w))));
        if (geared.Length > 0 && head.Length + geared.Length + tail.Length <= MaxLength && players.Any(c => Gear(c, w).Length > 0))
            return head + geared + tail;

        var parts = players.Select((c, i) => Part(c, i, "")).ToList();
        for (var shown = parts.Count; shown >= 0; shown--)
        {
            var more = shown < parts.Count ? " | " + string.Format(w.More, parts.Count - shown) : "";
            var line = head + string.Concat(parts.Take(shown)) + more + tail;
            if (line.Length <= MaxLength || shown == 0) return Trim(line);
        }
        return Trim(head + tail);

        string Part(CombatantSnapshot c, int i, string gear) => $" | {i + 1}.{name(c)}{gear} {Format.Compact(c.Dps)}/s {Pct(c.Share)}";
    }

    /// <summary>" (GS 2859 / CP 59.07K)", or as much of it as is known; empty when neither is.</summary>
    public static string Gear(CombatantSnapshot c, ChatWords? words = null)
    {
        var w = words ?? ChatWords.English;
        var parts = new List<string>(2);
        if (c.GearScore > 0) parts.Add($"{w.GearScore} {c.GearScore}");
        if (c.CombatPower > 0) parts.Add($"{w.CombatPower} {Format.Power(c.CombatPower)}");
        return parts.Count == 0 ? "" : $" ({string.Join(" / ", parts)})";
    }

    /// <summary>
    /// The whole ranking as a Discord message: a bold title line, then the players in a monospace block so the
    /// columns line up.
    /// </summary>
    public static string Table(EncounterSnapshot s, ChatWords? words = null, Func<CombatantSnapshot, string>? name = null)
    {
        var w = words ?? ChatWords.English;
        name ??= c => c.Name;
        var players = Players(s);
        // Gear score and combat power columns only when the party roster gave them for someone.
        var gear = players.Any(c => c.GearScore > 0 || c.CombatPower > 0);
        var rows = players
            .Select((c, i) =>
            {
                string[] row = [(i + 1).ToString(), Cut(name(c), 20), Format.Compact(c.Dps) + "/s", Format.Compact(c.Damage), Format.Share(c.Share)];
                return gear
                    ? [.. row, c.GearScore > 0 ? c.GearScore.ToString() : "-", c.CombatPower > 0 ? Format.Power(c.CombatPower) : "-"]
                    : row;
            })
            .ToList();
        string[] head = gear
            ? ["#", w.ColPlayer, w.ColDps, w.ColDamage, w.ColShare, w.GearScore, w.CombatPower]
            : ["#", w.ColPlayer, w.ColDps, w.ColDamage, w.ColShare];
        var widths = Enumerable.Range(0, head.Length).Select(col => rows.Append(head).Max(r => r[col].Length)).ToArray();

        var sb = new StringBuilder();
        sb.Append("**").Append(Header(s, w)).Append("** | ").AppendLine(PartyDps(s, w));
        sb.AppendLine("```");
        foreach (var row in rows.Prepend(head))
        {
            var cells = row.Select((cell, col) => col == 1 ? cell.PadRight(widths[col]) : cell.PadLeft(widths[col]));
            sb.AppendLine(string.Join("  ", cells).TrimEnd());
        }
        sb.Append("```");
        return sb.ToString();
    }

    /// <summary>"Balhash 2:41 KILL", "Balhash 3:56 WIPE (boss 29%)".</summary>
    private static string Header(EncounterSnapshot s, ChatWords w)
    {
        var t = TimeSpan.FromMilliseconds(s.CombatMs);
        var result = s.Reason switch
        {
            EncounterEndReason.Kill => " " + w.Kill,
            EncounterEndReason.Wipe => " " + w.Wipe,
            _ => "",
        };
        // How far the boss got, unless it died — and only when its max HP is known, else the percentage would be wrong.
        var boss = s.Boss is { MaxHpKnown: true, MaxHp: > 0, Hp: > 0 } b && s.Reason != EncounterEndReason.Kill
            ? $" ({string.Format(w.BossLeft, Pct(b.HpFraction))})"
            : "";
        // Whoever reads the line must not take a world boss's partial numbers for the fight's.
        var partial = s.MostDamageUnsent
            ? $" ({string.Format(w.Fighting, s.Fighting)}, {string.Format(w.DamageSentBy, s.DamageSenders)})"
            : "";
        return $"{s.Title} {(int)t.TotalMinutes}:{t.Seconds:00}{result}{boss}{partial}";
    }

    private static string PartyDps(EncounterSnapshot s, ChatWords w) => string.Format(w.Party, Format.Compact(s.PartyDps) + "/s");

    /// <summary>Players only: the pseudo-row of pets without a known owner is nobody's result.</summary>
    private static List<CombatantSnapshot> Players(EncounterSnapshot s) => s.Combatants.Where(c => !c.IsUnknownSummons).ToList();

    /// <summary>Whole percents read best in chat ("41%"); a token share is "&lt;1%", not a misleading "0%".</summary>
    private static string Pct(double fraction) => fraction is > 0 and < 0.005 ? "<1%" : Format.Percent(fraction, 0);

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Trim(string s) => s.Length <= MaxLength ? s : s[..MaxLength];
}
