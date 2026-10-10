using AionMeter.Core.Events;

namespace AionMeter.Core.Combat;

/// <summary>Immutable copies handed to the UI and written to history; safe to read off the capture thread.</summary>
/// <param name="MaxHpKnown">False when the meter never saw the boss appear: <paramref name="MaxHp"/> is then only the
/// highest HP it saw, and the percentage means little.</param>
/// <param name="Uncounted">HP the boss had lost before the meter began watching it: a fight joined late, or the meter
/// started mid-fight. (Fights saved before 0.2.6 took it from the damage, which in the open world is 4.5× off.)</param>
public sealed record BossSnapshot(uint ActorId, int NpcCode, string Name, long Hp, long MaxHp, bool MaxHpKnown = true, long Uncounted = 0)
{
    public double HpFraction => MaxHp > 0 && Hp >= 0 ? Math.Clamp((double)Hp / MaxHp, 0, 1) : 1;

    /// <summary>The boss's HP (fraction of max) when the meter's count began, when a sizeable part went uncounted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double? CountedFrom => MaxHpKnown && MaxHp > 0 && Uncounted >= MaxHp * 0.05 ? 1 - (double)Uncounted / MaxHp : null;
}

public sealed record CombatantSnapshot(
    uint ActorId,
    string Name,
    GameClass Class,
    bool IsSelf,
    long Damage,
    double Dps,
    double Share,
    int Hits,
    double CritRate,
    long MaxHit,
    long DamageTaken,
    int ServerId = 0,
    int GearScore = 0,
    long CombatPower = 0)
{
    /// <summary>The "summons, owner unknown" pseudo-row (see <see cref="Combatant.UnknownSummonsId"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUnknownSummons => ActorId == Combatant.UnknownSummonsId;
}

public sealed record EncounterSnapshot(
    Guid Id,
    string Title,
    string? Zone,
    DateTimeOffset StartedAt,
    long ClockMs,
    long CombatMs,
    bool IsActive,
    EncounterEndReason Reason,
    long TotalDamage,
    double PartyDps,
    BossSnapshot? Boss,
    IReadOnlyList<CombatantSnapshot> Combatants,
    int TargetCode = 0,
    int Attackers = 0)
{
    /// <summary>Players who took part (the unknown-summons row is not one).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int PlayerCount => Combatants.Count(c => !c.IsUnknownSummons);

    /// <summary>Players whose hits — with their damage — reached this client.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int DamageSenders => Combatants.Count(c => !c.IsUnknownSummons && c.Hits > 0);

    /// <summary>Everyone seen fighting: the players listed, and those whose hit notices came without any damage.
    /// Fights saved before the meter read hit notices know only the players listed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Fighting => Math.Max(Attackers, PlayerCount);

    /// <summary>
    /// The server kept most of the fight from this client: on a world boss with a crowd it sends the damage of a few
    /// players (you among them) and, for the rest, hit notices without numbers or damage over time — so their numbers
    /// and every share are far from the real ones. Fights saved before the hit notices tell it by their rows: almost
    /// everyone dealt damage over time only.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MostDamageUnsent
    {
        get
        {
            if (Boss is null) return false;
            int withHits = 0, dotOnly = 0;
            foreach (var c in Combatants)
            {
                if (c.IsUnknownSummons || c.Damage <= 0) continue;
                if (c.Hits > 0) withHits++;
                else dotOnly++;
            }
            if (withHits == 0) return false; // no hit at all came: not a crowd, a fight seen in part
            return Attackers > 0
                ? Attackers >= 20 && withHits * 4 <= Attackers
                : dotOnly >= 5 && withHits * 4 <= dotOnly;
        }
    }
}

public sealed record SkillRow(
    int SkillCode,
    string Name,
    int Hits,
    int DotTicks,
    long Damage,
    double Dps,
    double Share,
    long Average,
    long Min,
    long Max,
    double? CritRate,
    double? BackRate,
    double? FrontRate,
    double? PerfectRate,
    double? HeavyRate,
    double? MultiRate);

public sealed record HitQuality(
    int Hits,
    int DotTicks,
    double? CritRate,
    double? BackRate,
    double? FrontRate,
    double? PerfectRate,
    double? HeavyRate,
    double? MultiRate,
    long MaxHit,
    string? MaxHitSkill);

public sealed record CombatantDetail(
    uint ActorId,
    string Name,
    GameClass Class,
    bool IsSelf,
    long Damage,
    double Dps,
    double Share,
    long CombatMs,
    long DamageTaken,
    int HitsTaken,
    HitQuality Quality,
    IReadOnlyList<SkillRow> Skills,
    IReadOnlyList<long> PerSecond,
    IReadOnlyList<CastRecord> Casts,
    int ServerId = 0,
    int GearScore = 0,
    long CombatPower = 0);

public sealed record SegmentInfo(Guid Id, string Title, DateTimeOffset StartedAt, long CombatMs, bool IsActive, EncounterEndReason Reason, long TotalDamage);

public enum BossNoticeKind
{
    /// <summary>Came into view alive (a spawn, or walking into its area).</summary>
    Alive,
    /// <summary>Died while in view: the respawn countdown starts now.</summary>
    Killed,
    /// <summary>Came into view already dead: killed earlier, time unknown.</summary>
    SeenDead,
}

/// <summary>A world boss sighting for the respawn timers; the position is where it was seen (0 when unknown); the server is
/// the local character's (every server runs its own bosses).</summary>
public sealed record BossNotice(int NpcCode, int MapId, string? Zone, BossNoticeKind Kind, long TimeMs, float X = 0, float Y = 0, float Z = 0,
    int ServerId = 0);

/// <summary>A whole fight as saved to disk.</summary>
public sealed record FightRecord(int Version, EncounterSnapshot Summary, IReadOnlyList<CombatantDetail> Details);
