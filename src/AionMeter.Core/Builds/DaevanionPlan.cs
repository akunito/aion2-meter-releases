using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AionMeter.Core.Armory;

namespace AionMeter.Core.Builds;

public enum DaevToggle { Added, Removed, Open, NotConnected, Unknown }

public enum DaevGroup { Resource, Critical, Attack, Defense, PvP, Skill }

/// <summary>Key = the English effect name (+ "%"), the same in both languages; Name = shown text in the requested language.</summary>
public sealed record EffectSum(string Key, string Name, double Value, string Suffix, DaevGroup Group)
{
    public string Amount => "+" + Value.ToString("0.##", CultureInfo.InvariantCulture) + Suffix;
}

/// <summary>A questlog daevanion-planner build: selectedNodes per board (the start is never listed). The planner has no likes.</summary>
public sealed record QuestlogDaevPlan(long Id, string Name, string ClassId, Dictionary<int, List<int>> Boards, DateTimeOffset? UpdatedAt = null, IReadOnlyList<string>? Tags = null);

/// <summary>
/// The Daevanion planner's rules as pure functions over one board. A plan list never holds the start node; what is shown and saved
/// is plan ∪ open, because the character cannot close a node. Rule verified on 200 real boards: a node can be open only when it is
/// connected to the start through open nodes (4-neighbours), and the API reports the start itself closed, so it always counts as open.
/// </summary>
public static partial class DaevanionPlan
{
    /// <summary>Open node ids of the profile plus every board's start.</summary>
    public static HashSet<int> OpenSet(CharacterProfile? profile, DaevClass c)
    {
        var set = new HashSet<int>(256);
        foreach (var b in c.Boards) set.Add(b.Start);
        if (profile is not null) foreach (var n in profile.Daevanion) if (n.Open) set.Add(n.NodeId);
        return set;
    }

    /// <summary>plan ∪ open of this board, start excluded, in catalogue order.</summary>
    public static List<int> Effective(DaevBoard b, IReadOnlyCollection<int>? plan, IReadOnlySet<int> open)
    {
        var p = plan is null ? null : plan as HashSet<int> ?? new HashSet<int>(plan);
        var list = new List<int>(b.Nodes.Length);
        foreach (var n in b.Nodes)
            if (!n.Start && (open.Contains(n.Id) || p?.Contains(n.Id) == true)) list.Add(n.Id);
        return list;
    }

    /// <summary>Nodes of <paramref name="set"/> reachable from the start through nodes of the set (the start always included).</summary>
    public static HashSet<int> Reachable(DaevBoard b, IReadOnlySet<int> set)
    {
        var seen = new HashSet<int>(set.Count + 1) { b.Start };
        var stack = new Stack<int>(32);
        stack.Push(b.Start);
        while (stack.Count > 0)
            foreach (var m in b.Neighbours(stack.Pop()))
                if (set.Contains(m) && seen.Add(m)) stack.Push(m);
        return seen;
    }

    /// <summary>Every node is connected to the start through nodes of the same set.</summary>
    public static bool Connected(DaevBoard b, IEnumerable<int> nodes)
    {
        var set = new HashSet<int>(nodes);
        var r = Reachable(b, set);
        foreach (var n in set) if (!r.Contains(n)) return false;
        return true;
    }

    /// <summary>
    /// Adds a node that touches plan ∪ open ∪ start, or removes a planned one together with the planned nodes that lose their link
    /// to the start. Open nodes and the start never change. Returns plan ∪ open after the edit (also when refused).
    /// </summary>
    public static List<int> Toggle(DaevBoard b, IReadOnlyCollection<int>? plan, int nodeId, IReadOnlySet<int> open, out DaevToggle result)
    {
        var eff = new HashSet<int>(Effective(b, plan, open)) { b.Start };
        if (nodeId == b.Start || !b.Adj.ContainsKey(nodeId)) { result = DaevToggle.Unknown; return Ordered(b, eff); }
        if (open.Contains(nodeId)) { result = DaevToggle.Open; return Ordered(b, eff); }
        if (eff.Remove(nodeId))
        {
            var r = Reachable(b, eff);
            eff.RemoveWhere(n => !r.Contains(n) && !open.Contains(n));
            result = DaevToggle.Removed;
            return Ordered(b, eff);
        }
        var reach = Reachable(b, eff);
        foreach (var m in b.Neighbours(nodeId))
            if (reach.Contains(m)) { eff.Add(nodeId); result = DaevToggle.Added; return Ordered(b, eff); }
        result = DaevToggle.NotConnected;
        return Ordered(b, eff);
    }

    public static List<int> SelectAll(DaevBoard b)
    {
        var list = new List<int>(b.Nodes.Length);
        foreach (var n in b.Nodes) if (!n.Start) list.Add(n.Id);
        return list;
    }

    public static List<int> Reset(DaevBoard b, IReadOnlySet<int> open) => Effective(b, null, open);

    public static int Cost(DaevClass c, IEnumerable<int> nodes)
    {
        var sum = 0;
        foreach (var id in nodes) if (c.Node(id) is { } n) sum += n.Cost;
        return sum;
    }

    /// <summary>"HP +400" → (HP, 400, ""), "Combat Speed +1.5%" → (Combat Speed, 1.5, "%"); false when the text has no " +number" tail.</summary>
    public static bool TryParseEffect(string text, out string name, out double value, out string suffix)
    {
        name = text; value = 0; suffix = "";
        var i = text.LastIndexOf(" +", StringComparison.Ordinal);
        if (i <= 0) return false;
        var tail = text.AsSpan(i + 2);
        if (tail.Length > 0 && tail[^1] == '%') { suffix = "%"; tail = tail[..^1]; }
        if (!double.TryParse(tail, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) { suffix = ""; return false; }
        name = text[..i];
        return true;
    }

    /// <summary>Group from the English name (the Spanish one varies): PvP first, then HP/MP, critical, defensive, the rest is attack.</summary>
    public static DaevGroup GroupOf(string enName, bool skill)
    {
        if (skill) return DaevGroup.Skill;
        if (enName.StartsWith("PvP", StringComparison.Ordinal)) return DaevGroup.PvP;
        if (enName is "HP" or "MP" || enName.EndsWith(" HP", StringComparison.Ordinal) || enName.EndsWith(" MP", StringComparison.Ordinal)) return DaevGroup.Resource;
        if (enName.Contains("Critical", StringComparison.Ordinal)) return DaevGroup.Critical;
        if (enName.Contains("Resist", StringComparison.Ordinal) || enName.Contains("Defense", StringComparison.Ordinal) || enName.Contains("Tolerance", StringComparison.Ordinal))
            return DaevGroup.Defense;
        return DaevGroup.Attack;
    }

    /// <summary>Effects of <paramref name="nodes"/> summed per effect name, ordered by group then first appearance; texts without a number are skipped.</summary>
    public static List<EffectSum> Summarize(DaevClass c, IEnumerable<int> nodes, string lang)
    {
        var idx = new Dictionary<string, int>(64);
        var acc = new List<EffectSum>(48);
        foreach (var id in nodes)
        {
            if (c.Node(id) is not { } n) continue;
            var shown = n.Effects(lang);
            for (var i = 0; i < n.EffectsEn.Length; i++)
            {
                if (!TryParseEffect(n.EffectsEn[i], out var en, out var v, out var suf)) continue;
                var key = en + suf;
                if (idx.TryGetValue(key, out var k)) { acc[k] = acc[k] with { Value = acc[k].Value + v }; continue; }
                var name = en;
                if (!ReferenceEquals(shown, n.EffectsEn) && TryParseEffect(shown[i], out var local, out _, out _)) name = local;
                idx[key] = acc.Count;
                acc.Add(new EffectSum(key, name, v, suf, GroupOf(en, n.IsSkill)));
            }
        }
        // stable: List.Sort is not, so sort by (group, first index)
        var order = new int[acc.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (x, y) => acc[x].Group != acc[y].Group ? acc[x].Group.CompareTo(acc[y].Group) : x.CompareTo(y));
        var sorted = new List<EffectSum>(acc.Count);
        foreach (var i in order) sorted.Add(acc[i]);
        return sorted;
    }

    // ------------------------------------------------------------------ questlog daevanion planner

    public const string QuestlogBase = "https://questlog.gg/aion-2";

    [GeneratedRegex(@"questlog\.gg/aion-2/(?:[a-z]{2}(?:-[a-z]{2})?/)?daevanion-planner/([A-Za-z0-9_-]+)\?(?:[^#]*&)?build-id=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlannerRx();

    /// <summary>(user slug, build id) from https://questlog.gg/aion-2/&lt;lang&gt;/daevanion-planner/&lt;slug&gt;?build-id=&lt;id&gt;.</summary>
    public static (string Slug, long Id)? ParsePlannerUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = PlannerRx().Match(url.Trim());
        return m.Success && long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (m.Groups[1].Value, id) : null;
    }

    public static string PlannerUrl(string slug, long id, string lang = "en") => $"{QuestlogBase}/{lang}/daevanion-planner/{slug}?build-id={id}";

    /// <summary>Public tRPC read (verified 2026-10-11, no login): result.data = {user, builds[{id, name, classId, selectedNodes{board:["nodeId"…]}, …}]}.</summary>
    public static string PlannerApiUrl(string slug) =>
        QuestlogBase + "/api/trpc/daevanionPlanner.getDaevanionPlannerBySlug?input=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { slug }));

    /// <summary>Build <paramref name="id"/> of a getDaevanionPlannerBySlug answer; null when absent or the JSON is not that shape.</summary>
    public static QuestlogDaevPlan? ParsePlanner(string json, long id)
    {
        foreach (var p in ParsePlannerList(json)) if (p.Id == id) return p;
        return null;
    }

    /// <summary>Every planner build of a getDaevanionPlannerBySlug answer, in questlog's order; empty when the JSON is not that shape.</summary>
    public static List<QuestlogDaevPlan> ParsePlannerList(string json)
    {
        var list = new List<QuestlogDaevPlan>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var d = doc.RootElement;
            if (d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0) d = d[0];   // batched tRPC
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("data", out var data)) d = data;
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("json", out var j)) d = j;   // superjson wrapper
            if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("builds", out var builds) || builds.ValueKind != JsonValueKind.Array) return list;
            foreach (var b in builds.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("id", out var bid) || !bid.TryGetInt64(out var v)) continue;
                var boards = new Dictionary<int, List<int>>();
                if (b.TryGetProperty("selectedNodes", out var sel) && sel.ValueKind == JsonValueKind.Object)
                    foreach (var p in sel.EnumerateObject())
                    {
                        if (!int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var board) || p.Value.ValueKind != JsonValueKind.Array) continue;
                        var nodes = new List<int>(p.Value.GetArrayLength());
                        foreach (var x in p.Value.EnumerateArray())
                        {
                            int n;
                            var okId = x.ValueKind == JsonValueKind.String ? int.TryParse(x.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) : x.TryGetInt32(out n);
                            if (okId) nodes.Add(n);
                        }
                        boards[board] = nodes;
                    }
                List<string>? tags = null;
                if (b.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                {
                    tags = new List<string>(t.GetArrayLength());
                    foreach (var x in t.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && x.GetString() is { } tag) tags.Add(tag);
                }
                DateTimeOffset? updated = b.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ud) ? ud : null;
                list.Add(new QuestlogDaevPlan(v, b.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "" : "",
                    b.TryGetProperty("classId", out var c) && c.ValueKind == JsonValueKind.String ? (c.GetString() ?? "").ToLowerInvariant() : "", boards, updated, tags ?? []));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>The questlog plan as this class's plan: only nodes of the catalogue that sit on their own board, starts dropped. <paramref name="count"/> = nodes kept.</summary>
    public static Dictionary<int, List<int>> FromQuestlog(DaevClass cls, QuestlogDaevPlan plan, out int count)
    {
        var next = new Dictionary<int, List<int>>();
        count = 0;
        foreach (var b in cls.Boards)
        {
            if (!plan.Boards.TryGetValue(b.Id, out var ids)) continue;
            var list = new List<int>(ids.Count);
            foreach (var id in ids) if (cls.Node(id) is { Start: false } n && n.Board == b.Id) list.Add(id);
            next[b.Id] = list;
            count += list.Count;
        }
        return next;
    }

    private static List<int> Ordered(DaevBoard b, HashSet<int> set)
    {
        var list = new List<int>(set.Count);
        foreach (var n in b.Nodes) if (!n.Start && set.Contains(n.Id)) list.Add(n.Id);
        return list;
    }
}
