using AionMeter.Core.Armory;

namespace AionMeter.Core.Builds;

public enum SkillState { AtTarget, Near, Far, NotLearned }

/// <summary>Current = level on the official profile, null when not acquired. InInventory = not acquired but the item is held (stigma, phase 3).</summary>
public sealed record SkillProgress(int SkillId, int Target, int? Current, SkillState State, bool InInventory);

public sealed record BoardProgress(int BoardId, int Planned, int Open, int StillToOpen);

/// <summary>MissingSkills = skills below target (not learned included); MissingLevels = Σ(target − current), a skill not learned counting from 0.</summary>
public sealed record BuildProgress(List<SkillProgress> Skills, int MissingSkills, int TotalSkills, int MissingLevels, int NotLearned, List<BoardProgress> Boards);

public static class Progress
{
    /// <summary>Up to this many levels short is "Near".</summary>
    public const int NearLevels = 4;

    public static BuildProgress Compare(Build build, CharacterProfile? profile, Func<int, bool>? hasItem = null)
    {
        var have = new Dictionary<int, SkillLine>();
        if (profile is not null) foreach (var s in profile.Skills) have[s.Id] = s;
        var list = new List<SkillProgress>(build.Skills.Count);
        int missing = 0, levels = 0, notLearned = 0;
        foreach (var b in build.Skills)
        {
            int? cur = have.TryGetValue(b.SkillId, out var line) && line.Acquired ? line.Level : null;
            var gap = b.Level - (cur ?? 0);
            var state = cur is null ? SkillState.NotLearned : gap <= 0 ? SkillState.AtTarget : gap <= NearLevels ? SkillState.Near : SkillState.Far;
            if (state != SkillState.AtTarget) { missing++; levels += gap; }
            if (cur is null) notLearned++;
            list.Add(new SkillProgress(b.SkillId, b.Level, cur, state, cur is null && hasItem?.Invoke(b.SkillId) == true));
        }
        var open = new HashSet<(int, int)>();
        if (profile is not null) foreach (var n in profile.Daevanion) if (n.Open) open.Add((n.BoardId, n.NodeId));
        var boards = new List<BoardProgress>(build.Daevanion.Count);
        foreach (var (board, nodes) in build.Daevanion)
        {
            var o = 0;
            foreach (var n in nodes) if (open.Contains((board, n))) o++;
            boards.Add(new BoardProgress(board, nodes.Count, o, nodes.Count - o));
        }
        boards.Sort((a, b) => a.BoardId.CompareTo(b.BoardId));
        return new BuildProgress(list, missing, build.Skills.Count, levels, notLearned, boards);
    }
}
