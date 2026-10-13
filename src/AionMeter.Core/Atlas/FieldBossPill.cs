namespace AionMeter.Core.Atlas;

/// <summary>One world-boss timer as the footer pill sees it.</summary>
public readonly record struct BossSpawn(int NpcCode, int MapId, DateTimeOffset? NextSpawn, bool AliveNow);

/// <summary>
/// The footer's field-boss pill: the next boss to spawn on the player's map (any map of the server when that map has none
/// pending). Also the respawn/alive rules of a timer, shared with the App's BossTimer so the tests run on the same formula.
/// </summary>
public static class FieldBossPill
{
    /// <summary>
    /// Index of the soonest pending spawn (dead, respawn time still ahead, boss known), on <paramref name="currentMap"/> when it
    /// has one; -1 when none. No preference for watched bosses: that pool was mostly Rotten Kutar (30-minute respawn, watched
    /// after one nearby kill), so the pill read "Kutar" all day (Diego, A2M-38, 2026-10-13).
    /// </summary>
    public static int Pick(IReadOnlyList<BossSpawn> timers, int currentMap, DateTimeOffset now)
    {
        int best = -1, bestHere = -1;
        for (var i = 0; i < timers.Count; i++)
        {
            var t = timers[i];
            if (t.NpcCode <= 0 || t.AliveNow || t.NextSpawn is not { } at || at <= now) continue;
            if (best < 0 || at < timers[best].NextSpawn!.Value) best = i;
            if (currentMap != 0 && t.MapId == currentMap && (bestHere < 0 || at < timers[bestHere].NextSpawn!.Value)) bestHere = i;
        }
        return bestHere >= 0 ? bestHere : best;
    }

    /// <summary>The game's own respawn time while the list is the newest word on it; else the last kill + the interval.</summary>
    public static DateTimeOffset? NextSpawn(DateTimeOffset? listedAt, bool listedAlive, DateTimeOffset? listedTime, DateTimeOffset? lastKill, int respawnMinutes) =>
        listedAt is { } listed && !listedAlive && listedTime is { } back && (lastKill is not { } k || k <= listed) ? back
        : lastKill is { } kill && respawnMinutes > 0 ? kill.AddMinutes(respawnMinutes)
        : null;

    /// <summary>Alive when the latest sign of life (seen, or listed alive) is newer than the latest sign of death (kill, corpse, listed down).</summary>
    public static bool AliveNow(DateTimeOffset? lastKill, DateTimeOffset? lastSeenAlive, DateTimeOffset? lastSeenDead, DateTimeOffset? listedAt, bool listedAlive)
    {
        var alive = Max(lastSeenAlive, listedAlive ? listedAt : null);
        if (alive is null) return false;
        var dead = Max(Max(lastKill, lastSeenDead), listedAlive ? null : listedAt);
        return dead is null || alive > dead;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null || a >= b ? a : b;
}
