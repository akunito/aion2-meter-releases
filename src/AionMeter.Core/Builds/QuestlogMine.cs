using System.Text.RegularExpressions;

namespace AionMeter.Core.Builds;

/// <summary>
/// "My questlog builds" (A2M-21): the user's questlog slug (no login, learnt from links), pairing a skill build with its planner
/// build by the character name before the dash, and the "questlog has a newer version" check.
/// </summary>
public static partial class QuestlogMine
{
    [GeneratedRegex(@"questlog\.gg/aion-2/(?:[a-z]{2}(?:-[a-z]{2})?/)?(?:skill-builder|daevanion-planner)/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UserUrlRx();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{3,64}$")]
    private static partial Regex BareSlugRx();

    /// <summary>The user slug of any skill-builder or daevanion-planner link (build-id optional), or a bare slug; null otherwise.</summary>
    public static string? ParseUserSlug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        var m = UserUrlRx().Match(t);
        if (m.Success) return m.Groups[1].Value;
        return t.Contains('/') || t.Contains('.') || !BareSlugRx().IsMatch(t) ? null : t;
    }

    /// <summary>Character-name prefix of a build name: text before the first " —", " –" or " -" (trimmed); null when there is none.</summary>
    public static string? PairPrefix(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var cut = -1;
        for (var i = 0; i + 1 < name.Length; i++)
            if (name[i] == ' ' && name[i + 1] is '—' or '–' or '-') { cut = i; break; }
        if (cut <= 0) return null;
        var p = name.AsSpan(0, cut).Trim();
        return p.Length > 0 ? p.ToString() : null;
    }

    /// <summary>
    /// The planner build that goes with this skill build: same class and the same name prefix (case-insensitive), the newest when
    /// several match ("Irin — Ranger PvE 45" ↔ "Irin — Daevanion Ranger (top Vaizel)"). Null when the skill build has no prefix.
    /// </summary>
    public static QuestlogDaevPlan? PairPlan(string skillName, string classId, IReadOnlyList<QuestlogDaevPlan> plans)
    {
        if (PairPrefix(skillName) is not { } prefix) return null;
        QuestlogDaevPlan? best = null;
        foreach (var p in plans)
        {
            if (!string.Equals(p.ClassId, classId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(PairPrefix(p.Name), prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (best is null || Nullable.Compare(p.UpdatedAt, best.UpdatedAt) > 0) best = p;
        }
        return best;
    }

    /// <summary>
    /// questlog's copy is newer than the imported one. An import without a recorded date (older meter) counts as older, so the
    /// user is offered the update; one second of slack absorbs the date round-trip.
    /// </summary>
    public static bool IsNewer(DateTimeOffset? remote, DateTimeOffset? imported) =>
        remote is { } r && (imported is not { } l || r - l > TimeSpan.FromSeconds(1));

    /// <summary>Items of one class (all when <paramref name="classId"/> is null or empty), questlog's order kept, at most <paramref name="take"/>.</summary>
    public static List<T> OfClass<T>(IReadOnlyList<T> items, Func<T, string> classOf, string? classId, int take = int.MaxValue)
    {
        var list = new List<T>(Math.Min(items.Count, take));
        foreach (var x in items)
        {
            if (list.Count >= take) break;
            if (string.IsNullOrEmpty(classId) || string.Equals(classOf(x), classId, StringComparison.OrdinalIgnoreCase)) list.Add(x);
        }
        return list;
    }
}
