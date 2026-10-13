using AionMeter.Core.Combat;

namespace AionMeter.Service;

/// <summary>Where a breakdown reads its numbers from: a live in-memory segment or a saved fight (WPF BreakdownView, the daemon's `fight-detail`).</summary>
public interface IFightView
{
    bool IsLive { get; }
    EncounterSnapshot? Summary();
    CombatantDetail? Detail(uint actorId);
    IReadOnlyList<CombatantDetail> AllDetails();
}

public sealed class LiveFightView(CombatTracker tracker, Guid segmentId) : IFightView
{
    public bool IsLive => Summary() is { IsActive: true };

    public EncounterSnapshot? Summary() => tracker.Snapshot(segmentId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public CombatantDetail? Detail(uint actorId) => tracker.Detail(segmentId, actorId);

    public IReadOnlyList<CombatantDetail> AllDetails() =>
        Summary()?.Combatants.Select(c => tracker.Detail(segmentId, c.ActorId)).OfType<CombatantDetail>().ToList()
        ?? (IReadOnlyList<CombatantDetail>)[];
}

public sealed class RecordFightView(FightRecord record) : IFightView
{
    public bool IsLive => false;
    public EncounterSnapshot? Summary() => record.Summary;
    public CombatantDetail? Detail(uint actorId) => record.Details.FirstOrDefault(d => d.ActorId == actorId);
    public IReadOnlyList<CombatantDetail> AllDetails() => record.Details;
}
