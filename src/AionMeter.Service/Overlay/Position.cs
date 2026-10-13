using AionMeter.Core.Atlas;

namespace AionMeter.Service.Overlay;

/// <summary>
/// A2M-48 `position` socket message (≤ 2 Hz): where the atlas draws the player and which spots are in view now.
/// </summary>
/// <param name="Map">The zone (atlas map id) the daemon is in; 0 before the first zone load / last-map.txt.</param>
/// <param name="X">World cm of the point to draw (0,0 = nothing to draw).</param>
/// <param name="Exact">True: a fix from the player's own actions (cast, gather, state record); false: the centroid of what is in view (hollow ring).</param>
/// <param name="AgeS">Seconds since the fix, on the stream's clock (a replay's own); the label "25s" past 10 s.</param>
/// <param name="Samples">Positions behind the centroid (the "~N" label when not exact).</param>
/// <param name="Heading">Direction of the last move of ≥ 1 m, degrees in world axes (0 = +x, 90 = +y); null until the player has moved.</param>
/// <param name="Live">The current map's spots in view with their units taken.</param>
public sealed record PositionModel(int Map, float X, float Y, float Z, bool Exact, long AgeS, int Samples, float? Heading, uint SelfId, LiveSpot[] Live);

/// <summary>
/// Picks the drawn position the way the WPF AtlasView does and adds the heading. An action's fix holds while the surroundings agree
/// with it; standing still, the centroid of what is in view wanders as mobs and players move (Diego saw the dot drift while idle,
/// 2026-10-09), so the centroid only takes over when it has stayed more than 40 m away from the last fix for 15 s running.
/// One instance per daemon (it keeps the drift timer and the last point).
/// </summary>
public sealed class PositionBuilder
{
    private long _driftSinceMs;
    private float _lastX, _lastY;
    private bool _hasLast;
    private float? _heading;
    private readonly List<LiveSpot> _live = new(64);

    /// <param name="nowMs">The stream's clock: UtcNow live, <see cref="NodeStore.LastEventMs"/> for a replay.</param>
    public PositionModel Build(NodeStore atlas, long nowMs)
    {
        _live.Clear();
        var map = atlas.LiveSpots(_live);
        var selfAt = atlas.SelfAtMs;
        var age = selfAt > 0 ? Math.Max(0, (nowMs - selfAt) / 1000) : long.MaxValue;
        var (ex, ey, en) = atlas.Estimate(nowMs);
        float sx = atlas.SelfX, sy = atlas.SelfY;
        var farNow = en >= 3 && (Math.Abs(ex - sx) > 4_000 || Math.Abs(ey - sy) > 4_000);
        if (!farNow) _driftSinceMs = 0; else if (_driftSinceMs == 0) _driftSinceMs = nowMs;
        var drifted = farNow && nowMs - _driftSinceMs >= 15_000;
        float x, y; bool exact;
        if (age <= 20 || !drifted) { x = sx; y = sy; exact = true; }
        else if (en >= 3) { x = ex; y = ey; exact = false; }
        else if (age < 30 * 60) { x = sx; y = sy; exact = true; }
        else { x = y = 0; exact = false; }
        if (exact && selfAt == 0) { x = y = 0; exact = false; }   // no fix at all yet: nothing exact to draw
        if (x != 0 || y != 0)
        {
            if (!_hasLast) { _lastX = x; _lastY = y; _hasLast = true; }
            else
            {
                var dx = x - _lastX; var dy = y - _lastY;
                if (dx * dx + dy * dy >= 100f * 100f)   // 1 m: smaller steps are rounding and centroid noise
                {
                    _heading = MathF.Atan2(dy, dx) * (180f / MathF.PI);
                    _lastX = x; _lastY = y;
                }
            }
        }
        return new PositionModel(map, x, y, atlas.SelfZ, exact, age == long.MaxValue ? -1 : age, en, _heading, atlas.SelfId, _live.ToArray());
    }

    /// <summary>Same position, same spots: nothing new to send (the daemon still sends one every 5 s so a late UI catches up).</summary>
    public static bool Same(PositionModel? a, PositionModel b)
    {
        if (a is null || a.Map != b.Map || a.X != b.X || a.Y != b.Y || a.Exact != b.Exact || a.Heading != b.Heading || a.Live.Length != b.Live.Length) return false;
        if (a.AgeS / 10 != b.AgeS / 10 && b.AgeS > 10) return false;   // the age label moves in 10 s steps at most
        for (var i = 0; i < a.Live.Length; i++) if (a.Live[i] != b.Live[i]) return false;
        return true;
    }
}
