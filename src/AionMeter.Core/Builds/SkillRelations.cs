using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AionMeter.Core.Builds;

/// <summary>
/// Declaration order = display order inside one skill's list (rank and chain first, then what the skill takes, then what it gives).
/// Grants/Consumes = buff (A grants it, B is stronger with it); Applies/Needs = target state (A inflicts it, B needs or profits from it);
/// Shortens/ShortenedBy, Boosts/BoostedBy, ChainSkill = from spec names ("-1s [Deadshot] cooldown on hit", "x1.5 [Focused Eye] effect", "Adds [X] Chain Skill");
/// KeyChain = skills on one key in row order; Priority = rank in the build's priority chain of the skill's category.
/// </summary>
public enum RelationKind { Priority, KeyChain, Consumes, Needs, ShortenedBy, BoostedBy, Grants, Applies, Shortens, Boosts, ChainSkill }

/// <summary>From = A (the giver), To = B (the taker; 0 when the spec names a skill outside the catalogue). Text is phrased for <see cref="Owner"/>.</summary>
public sealed record SkillRelation(RelationKind Kind, int FromSkill, int ToSkill, string Text)
{
    public int Owner => Kind is RelationKind.Consumes or RelationKind.Needs or RelationKind.ShortenedBy or RelationKind.BoostedBy ? ToSkill : FromSkill;
}

/// <summary>What one skill's English texts say: buffs it grants/uses, states it inflicts/needs, spec links. Spec-sourced states carry the spec id.</summary>
public sealed record SkillFacts(int SkillId, string[] Grants, string[] Uses, (string State, int Spec)[] Inflicts, string[] Needs, bool NeedsIsBonus,
                                (string Target, string Amount, int Spec)[] Cooldowns, (string Target, string Amount, int Spec)[] Boosts, (string Target, int Spec)[] Chains);

public sealed class SkillRelationSet
{
    public static readonly SkillRelationSet Empty = new([]);
    private static readonly SkillRelation[] None = [];
    private readonly Dictionary<int, SkillRelation[]> _byOwner;
    public IReadOnlyList<SkillRelation> All { get; }

    public SkillRelationSet(List<SkillRelation> all)
    {
        All = all;
        var tmp = new Dictionary<int, List<SkillRelation>>();
        foreach (var r in all)
        {
            if (!tmp.TryGetValue(r.Owner, out var l)) tmp[r.Owner] = l = new List<SkillRelation>(4);
            l.Add(r);
        }
        _byOwner = new Dictionary<int, SkillRelation[]>(tmp.Count);
        foreach (var (k, l) in tmp)
        {
            var a = l.ToArray();
            // insertion sort by kind keeps the discovery order inside a kind (Array.Sort is unstable); lists hold a handful of entries
            for (var i = 1; i < a.Length; i++)
            {
                var x = a[i]; var j = i - 1;
                while (j >= 0 && a[j].Kind > x.Kind) { a[j + 1] = a[j]; j--; }
                a[j + 1] = x;
            }
            _byOwner[k] = a;
        }
    }

    public IReadOnlyList<SkillRelation> For(int skillId) => _byOwner.TryGetValue(skillId, out var a) ? a : None;
}

/// <summary>
/// Relations derived from the catalogue's ENGLISH description templates and spec names with a few regexes. The Spanish texts word
/// the same effects differently ("otorga Precisión", "objetivo afectado por Lentitud" …), so parsing stays English-only: skill names
/// are shown in the user's skill language, buff and state words as the English catalogue writes them.
/// </summary>
public static partial class SkillRelations
{
    // Names are Title Case words ("Precision", "Crimson Flames", "Mother Nature's Embrace"); the verbs around them are matched case-insensitively.
    private const string Name = @"[A-Z][\w']*(?: [A-Z][\w']*)*";

    [GeneratedRegex(@"(?i:\bgrants?) (?<n>" + Name + @") (?i:for|to the caster)")]
    private static partial Regex GrantsRx();

    [GeneratedRegex(@"(?i:\bif the caster has) (?<n>" + Name + @")|(?i:\bwhile) (?<n>" + Name + @") (?i:is active)")]
    private static partial Regex UsesRx();

    [GeneratedRegex(@"(?i:\binflicts?) (?<n>" + Name + ")")]
    private static partial Regex InflictsRx();

    [GeneratedRegex(@"(?i:\btargets? afflicted with) (?<n>" + Name + @"(?:(?:, or |, | or )" + Name + ")*)")]
    private static partial Regex NeedsRx();

    [GeneratedRegex(@"(?<a>[+-]\d+(?:\.\d+)?s) \[(?<n>[^\]]+)\] (?i:cooldown)")]
    private static partial Regex CooldownRx();

    [GeneratedRegex(@"(?<a>x\d+(?:\.\d+)?) \[(?<n>[^\]]+)\] (?i:effect)")]
    private static partial Regex BoostRx();

    [GeneratedRegex(@"(?i:adds) \[(?<n>[^\]]+)\] (?i:chain skill)")]
    private static partial Regex ChainRx();

    [GeneratedRegex(@", or |, | or ")]
    private static partial Regex ListSepRx();

    public static SkillFacts Facts(SkillInfo s)
    {
        var text = SkillBook.Resolve(s, 1, "en", plain: true);
        var grants = Names(GrantsRx(), text);
        var uses = Names(UsesRx(), text);
        var inflicts = new List<(string, int)>();
        foreach (var n in Names(InflictsRx(), text)) inflicts.Add((n, 0));
        var needs = new List<string>();
        var bonus = false;
        foreach (Match m in NeedsRx().Matches(text))
        {
            // "Selects / Deals … to a target afflicted with X" = needs X; "Increases damage … / Has a N% chance … afflicted with X" = bonus vs X
            if (needs.Count == 0) bonus = SentenceStart(text, m.Index) is var st && (st.StartsWith("Increases", StringComparison.OrdinalIgnoreCase) || st.StartsWith("Has a", StringComparison.OrdinalIgnoreCase));
            foreach (var part in ListSepRx().Split(m.Groups["n"].Value)) AddOnce(needs, part);
        }
        var cds = new List<(string, string, int)>();
        var boosts = new List<(string, string, int)>();
        var chains = new List<(string, int)>();
        foreach (var sp in s.Specs)
        {
            var n = sp.NameEn;
            foreach (Match m in InflictsRx().Matches(n)) inflicts.Add((m.Groups["n"].Value, sp.Id));
            foreach (Match m in CooldownRx().Matches(n)) cds.Add((m.Groups["n"].Value, m.Groups["a"].Value, sp.Id));
            foreach (Match m in BoostRx().Matches(n)) boosts.Add((m.Groups["n"].Value, m.Groups["a"].Value, sp.Id));
            foreach (Match m in ChainRx().Matches(n)) chains.Add((m.Groups["n"].Value, sp.Id));
        }
        return new SkillFacts(s.Id, grants, uses, inflicts.ToArray(), needs.ToArray(), bonus, cds.ToArray(), boosts.ToArray(), chains.ToArray());
    }

    public static SkillFacts[] Facts(IReadOnlyList<SkillInfo> catalogue)
    {
        var a = new SkillFacts[catalogue.Count];
        for (var i = 0; i < a.Length; i++) a[i] = Facts(catalogue[i]);
        return a;
    }

    /// <summary>
    /// Every relation among the class's skills, each (kind, from, to) once. Buff, state and spec-name links come from the catalogue alone,
    /// except a state that only a spec inflicts ("Inflicts Slow for 5s on hit"): it counts only when the build has that spec chosen.
    /// Key chains and priority ranks need <paramref name="build"/>.
    /// </summary>
    public static SkillRelationSet Compute(IReadOnlyList<SkillInfo> catalogue, Build? build, string lang, SkillFacts[]? facts = null)
    {
        facts ??= Facts(catalogue);
        var byName = new Dictionary<string, int>(catalogue.Count, StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<int, SkillInfo>(catalogue.Count);
        foreach (var s in catalogue) { byName.TryAdd(s.NameEn, s.Id); byId[s.Id] = s; }
        string Nm(int id) => byId.TryGetValue(id, out var s) ? s.Name(lang) : id.ToString(CultureInfo.InvariantCulture);
        string Target(string en) => byName.TryGetValue(en, out var id) ? Nm(id) : en;

        var list = new List<SkillRelation>(32);
        var seen = new HashSet<(RelationKind, int, int)>();
        void Add(RelationKind k, int from, int to, string text) { if (seen.Add((k, from, to))) list.Add(new SkillRelation(k, from, to, text)); }

        foreach (var a in facts)
        {
            foreach (var buff in a.Grants)
                foreach (var b in facts)
                    if (b.SkillId != a.SkillId && Contains(b.Uses, buff))
                    {
                        Add(RelationKind.Grants, a.SkillId, b.SkillId, "Enables " + Nm(b.SkillId) + ": grants " + buff);
                        Add(RelationKind.Consumes, a.SkillId, b.SkillId, "Stronger with " + buff + " from " + Nm(a.SkillId));
                    }
            foreach (var (state, spec) in a.Inflicts)
            {
                if (spec != 0 && !(build?.Skill(a.SkillId)?.Specs.Contains(spec) ?? false)) continue;
                var how = spec != 0 ? " (spec)" : "";
                foreach (var b in facts)
                    if (b.SkillId != a.SkillId && Contains(b.Needs, state))
                    {
                        Add(RelationKind.Applies, a.SkillId, b.SkillId, "Applies " + state + " for " + Nm(b.SkillId) + how);
                        Add(RelationKind.Needs, a.SkillId, b.SkillId, (b.NeedsIsBonus ? "Bonus vs " : "Needs ") + string.Join(" or ", b.Needs) + ": " + Nm(a.SkillId) + " applies " + state + how);
                    }
            }
            foreach (var (t, amount, _) in a.Cooldowns)
                if (byName.TryGetValue(t, out var to) && to != a.SkillId)
                {
                    Add(RelationKind.Shortens, a.SkillId, to, "Shortens " + Nm(to) + " cooldown " + amount + " (spec)");
                    Add(RelationKind.ShortenedBy, a.SkillId, to, "Cooldown " + amount + " from " + Nm(a.SkillId) + " (spec)");
                }
            foreach (var (t, amount, _) in a.Boosts)
                if (byName.TryGetValue(t, out var to) && to != a.SkillId)
                {
                    Add(RelationKind.Boosts, a.SkillId, to, "Boosts " + Nm(to) + " " + amount + " (spec)");
                    Add(RelationKind.BoostedBy, a.SkillId, to, Nm(a.SkillId) + " makes it " + amount + " (spec)");
                }
            foreach (var (t, _) in a.Chains)
                Add(RelationKind.ChainSkill, a.SkillId, byName.GetValueOrDefault(t), "Chain skill " + Target(t) + " (spec)");
        }

        if (build is not null)
        {
            // key chains: the skills of one key, first link (row 0) first
            var rows = new int[BuildEditor.Keys + 1, BuildEditor.Rows];
            foreach (var s in build.Skills)
                if (BuildEditor.TryParseSlot(s.Slot, out var k, out var r) && byId.ContainsKey(s.SkillId)) rows[k, r] = s.SkillId;
            var sb = new StringBuilder(96);
            for (var k = 1; k <= BuildEditor.Keys; k++)
            {
                var n = 0;
                for (var r = 0; r < BuildEditor.Rows; r++) if (rows[k, r] != 0) n++;
                if (n < 2) continue;
                sb.Clear().Append(BuildEditor.KeyName(k)).Append(" chain: ");
                var first = true;
                for (var r = 0; r < BuildEditor.Rows; r++)
                {
                    if (rows[k, r] == 0) continue;
                    if (!first) sb.Append(" → ");
                    sb.Append(Nm(rows[k, r])); first = false;
                }
                var text = sb.ToString();
                for (var r = 0; r < BuildEditor.Rows; r++) if (rows[k, r] != 0) Add(RelationKind.KeyChain, rows[k, r], rows[k, r], text);
            }
            // rank inside the skill's category chain, as the tab shows one chain per category
            var rank = new Dictionary<string, int>(3);
            foreach (var id in build.Priority)
            {
                if (!byId.TryGetValue(id, out var s)) continue;
                var n = rank[s.Sub] = rank.GetValueOrDefault(s.Sub) + 1;
                Add(RelationKind.Priority, id, id, "#" + n.ToString(CultureInfo.InvariantCulture) + " in your priority");
            }
        }
        return new SkillRelationSet(list);
    }

    private static string[] Names(Regex rx, string text)
    {
        var l = new List<string>(2);
        foreach (Match m in rx.Matches(text)) AddOnce(l, m.Groups["n"].Value);
        return l.ToArray();
    }

    private static void AddOnce(List<string> l, string v)
    {
        foreach (var x in l) if (x.Equals(v, StringComparison.OrdinalIgnoreCase)) return;
        l.Add(v);
    }

    private static bool Contains(string[] a, string v)
    {
        foreach (var x in a) if (x.Equals(v, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string SentenceStart(string text, int at)
    {
        var i = at;
        while (i > 0 && text[i - 1] is not ('.' or '\n')) i--;
        while (i < at && text[i] == ' ') i++;
        return text.Substring(i, at - i);
    }
}

/// <summary>The catalogue facts per class (parsed once) and the last relation set per (class, build edit, language): every edit makes a new Build.</summary>
public sealed class SkillRelationCache(SkillBook book)
{
    private readonly Dictionary<string, SkillFacts[]> _facts = new(StringComparer.OrdinalIgnoreCase);
    private (string Class, string Lang, Build? Build, SkillRelationSet Set) _last = ("", "", null, SkillRelationSet.Empty);

    public SkillRelationSet Get(string classId, Build? build, string lang)
    {
        if (classId.Length == 0) return SkillRelationSet.Empty;
        if (_last.Class == classId && _last.Lang == lang && ReferenceEquals(_last.Build, build)) return _last.Set;
        var cat = book.ForClass(classId);
        if (!_facts.TryGetValue(classId, out var f)) _facts[classId] = f = SkillRelations.Facts(cat);
        var set = SkillRelations.Compute(cat, build, lang, f);
        _last = (classId, lang, build, set);
        return set;
    }
}
