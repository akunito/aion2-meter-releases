using AionMeter.Core.Combat;

namespace AionMeter.Service.Overlay;

/// <summary>
/// A2M-48 `fight-detail`: the whole fight behind a breakdown (summary + every combatant's detail, the shape of a fights/*.json file) for an
/// overlay fight id, read through the same <see cref="IFightView"/> the WPF BreakdownView uses. Call it on the thread that ticks the engine.
/// </summary>
public static class FightDetail
{
    /// <param name="id">"live" (the fight the card shows: the selected one, else the newest segment), "s:&lt;guid&gt;" or "h:&lt;history file&gt;".</param>
    /// <returns>The record, whether it is still running (the UI asks again each second), and the concrete id ("s:…"/"h:…"); null when the id names nothing.</returns>
    public static (FightRecord Record, bool Live, string Id)? Resolve(MeterEngine meter, OverlaySelection selection, string id)
    {
        if (id is "live" or "")
        {
            if (selection.Saved is { } saved) return (saved.Record, false, "h:" + saved.Entry.FileName);
            Guid? seg = selection.Segment;
            if (seg is null && meter.Tracker.Segments() is [var newest, ..]) seg = newest.Id;
            if (seg is not { } g) return null;
            return FromView(new LiveFightView(meter.Tracker, g), "s:" + g.ToString("N"));
        }
        if (id.StartsWith("s:", StringComparison.Ordinal) && Guid.TryParse(id.AsSpan(2), out var sg))
            return FromView(new LiveFightView(meter.Tracker, sg), "s:" + sg.ToString("N"));
        if (!id.StartsWith("h:", StringComparison.Ordinal)) return null;
        var file = id[2..];
        foreach (var h in meter.History.List())
            if (h.FileName == file)
                return meter.History.Load(h.Path) is { } record ? (record, false, id) : null;
        return null;
    }

    public static (FightRecord Record, bool Live, string Id)? FromView(IFightView view, string id)
    {
        var summary = view.Summary();
        if (summary is null) return null;
        var details = view.AllDetails();
        return (new FightRecord(1, summary, details), summary.IsActive, id);
    }
}
