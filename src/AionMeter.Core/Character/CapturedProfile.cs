using AionMeter.Core.Armory;

namespace AionMeter.Core.Character;

/// <summary>One board of <c>26 e2</c>: the open node ids (board×10000 + (row−1)×15 + col), the start node included.</summary>
public sealed record DaevanionBoard(int BoardId, int[] Nodes);

/// <summary>One record of <c>00 51</c>. Id is either a base skill (…0000) or its chosen-spec variant (base + spec slot digits, e.g.
/// 12010340 = specs 3 and 4 of 12010000); both carry the same levels. Ids below 1,000,000 are emotes and basic actions.</summary>
public sealed record LearnedSkill(int Id, int Level, int BaseLevel);

/// <summary>One record of <c>00 39</c>: Specs[i] = spec id chosen in slot i+1 (0 = empty). Five slots = a stigma (Dp skill).</summary>
public sealed record EquippedSkill(int SkillId, int[] Specs)
{
    public bool IsStigma => Specs.Length == 5;
}

/// <summary>A filled key of the skill bar (<c>8c e2</c>): macro page 1–3, key slot (0–7, 20–23) and its skills (main first).</summary>
public sealed record BarKey(int Page, int Slot, int[] Skills);

/// <summary>
/// What the game itself told us about the character at its last login (A2M-35), with the time of each family. Fresher than the
/// official API (which lags by hours to days) and exact: docs/27 matched every Daevanion node of Akunita and Irin, 26/26 of Irin's
/// skill levels and 18/23 of Akunita's (the 5 others disagree with the API on two days running; the wire is taken as truth).
/// </summary>
public sealed record CapturedProfile(
    long SkillsMs, List<LearnedSkill> Skills,
    long EquippedMs, List<EquippedSkill> Equipped,
    long BarMs, List<BarKey> Bar,
    long DaevanionMs, List<DaevanionBoard> Boards,
    long AttributesMs, Dictionary<int, int> Attributes)
{
    public static readonly CapturedProfile Empty = new(0, [], 0, [], 0, [], 0, [], 0, []);

    public long LatestMs => Math.Max(Math.Max(Math.Max(SkillsMs, EquippedMs), Math.Max(BarMs, DaevanionMs)), AttributesMs);

    /// <summary><c>49 36</c> stat id → the official profile's stat Type. Id 3 (INT) and 12 (no deity on the site; Azphel's board is
    /// 26) are absent from the sheet when 0. Measured on Akunita and Irin: all 16 listed values equal the profile's.</summary>
    public static readonly (int Id, string Type)[] StatTypes =
    [
        (1, "STR"), (2, "DEX"), (3, "INT"), (4, "CON"), (5, "AGI"), (6, "WIS"), (7, "Justice"), (8, "Freedom"), (9, "Illusion"),
        (10, "Life"), (11, "Time"), (13, "Destruction"), (14, "Death"), (15, "Wisdom"), (16, "Destiny"), (17, "Space"),
    ];

    /// <summary>Highest level per base skill id (class skills only, id ≥ 10,000,000).</summary>
    public Dictionary<int, int> LevelsByBase()
    {
        var map = new Dictionary<int, int>(Skills.Count);
        foreach (var s in Skills)
        {
            if (s.Id < 10_000_000) continue;
            var b = s.Id / 10_000 * 10_000;
            if (!map.TryGetValue(b, out var l) || s.Level > l) map[b] = s.Level;
        }
        return map;
    }

    /// <summary>
    /// The official profile with every family the capture holds newer than <see cref="CharacterProfile.FetchedAt"/> replaced by the
    /// captured values: skill levels and acquisition (00 51), equipped flags (bar page 1 for Active, 5-slot 00 39 entries for Dp),
    /// open Daevanion nodes and board counts (26 e2; start nodes stay closed as on the site) and the 16 attributes (49 36).
    /// FetchedAt becomes the newest skill/node family applied, so the Build tab's "learned as of" shows the login. Returns
    /// <paramref name="official"/> itself when nothing is newer.
    /// </summary>
    public CharacterProfile Apply(CharacterProfile official)
    {
        var fetched = official.FetchedAt.ToUnixTimeMilliseconds();
        long applied = 0;
        var skills = official.Skills;
        if (SkillsMs > fetched && Skills.Count > 0)
        {
            var levels = LevelsByBase();
            HashSet<int>? onBar = BarMs > fetched && Bar.Count > 0 ? new() : null;
            if (onBar is not null)
                foreach (var k in Bar)
                    if (k.Page == 1)
                        foreach (var id in k.Skills) onBar.Add(id / 10_000 * 10_000);
            HashSet<int>? stigmas = EquippedMs > fetched && Equipped.Count > 0 ? new() : null;
            if (stigmas is not null) foreach (var e in Equipped) if (e.IsStigma) stigmas.Add(e.SkillId);
            var list = new List<SkillLine>(official.Skills.Count);
            foreach (var s in official.Skills)
            {
                var line = levels.TryGetValue(s.Id, out var lv) ? s with { Level = lv, Acquired = true } : s;
                if (line.Category == "Dp" && stigmas is not null) line = line with { Equipped = stigmas.Contains(s.Id) };
                else if (line.Category == "Active" && onBar is not null) line = line with { Equipped = onBar.Contains(s.Id) };
                list.Add(line);
            }
            skills = list;
            applied = Math.Max(applied, SkillsMs);
        }
        var nodes = official.Daevanion;
        var boards = official.Boards;
        if (DaevanionMs > fetched && Boards.Count > 0)
        {
            var open = new Dictionary<int, HashSet<int>>(Boards.Count);
            foreach (var b in Boards) open[b.BoardId] = new HashSet<int>(b.Nodes);
            var list = new List<DaevanionNode>(official.Daevanion.Count);
            var counts = new Dictionary<int, int>();
            foreach (var n in official.Daevanion)
            {
                var o = n.Open;
                if (open.TryGetValue(n.BoardId, out var set)) o = n.Type != "Start" && set.Contains(n.NodeId);
                if (o) counts[n.BoardId] = counts.GetValueOrDefault(n.BoardId) + 1;
                list.Add(o == n.Open ? n : n with { Open = o });
            }
            nodes = list;
            var bl = new List<BoardSummary>(official.Boards.Count);
            foreach (var b in official.Boards) bl.Add(open.ContainsKey(b.Id) ? b with { Open = counts.GetValueOrDefault(b.Id) } : b);
            boards = bl;
            applied = Math.Max(applied, DaevanionMs);
        }
        var stats = official.Stats;
        if (AttributesMs > fetched && Attributes.Count > 0)
        {
            var list = new List<StatLine>(official.Stats.Count);
            foreach (var st in official.Stats)
            {
                var v = st;
                foreach (var (id, type) in StatTypes)
                    if (type == st.Type) { v = st with { Value = Attributes.GetValueOrDefault(id).ToString(System.Globalization.CultureInfo.InvariantCulture) }; break; }
                list.Add(v);
            }
            stats = list;
        }
        if (ReferenceEquals(skills, official.Skills) && ReferenceEquals(nodes, official.Daevanion) && ReferenceEquals(stats, official.Stats)) return official;
        // attributes arrive on every zone load and never move "learned as of": that line is about skills and nodes
        var at = applied > fetched ? DateTimeOffset.FromUnixTimeMilliseconds(applied) : official.FetchedAt;
        return official with { Skills = skills, Daevanion = nodes, Boards = boards, Stats = stats, FetchedAt = at };
    }
}
