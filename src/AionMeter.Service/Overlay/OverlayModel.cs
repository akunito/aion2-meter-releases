using System.Text.Json.Serialization;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Storage;

namespace AionMeter.Service.Overlay;

// A2M-48 phase 3: what the overlay card shows, built once by OverlayBuilder for the WPF overlay and for the daemon's socket (Linux UI).
// Every string is final text in the UI language; colours are tones the UI maps to its own brushes.

/// <summary>Footer event pill colour step (EventPill.Paint): grey-blue, grey-yellow under 20 min, orange under 10, red under 5 or running.</summary>
public enum PillTone : byte { Calm, Soon, Close, Now }

/// <summary>The status dot: green capturing, amber waiting/starting, red error, grey otherwise.</summary>
public enum StatusTone : byte { Good, Warn, Error, Mute }

/// <summary>What the card is showing: Waiting/Live/Last follow the live tracker, Session = an older in-memory segment, Saved = a fight file.</summary>
public enum OverlayView : byte { Waiting, Live, Last, Session, Saved }

/// <param name="Fill">0..1 width of the class-coloured bar (relative to the top damage or the share, Settings.BarsRelativeToTop).</param>
public sealed record OverlayRow(
    uint ActorId, int Rank, string Name, GameClass Class, bool IsSelf, bool IsUnknownSummons,
    string GearScore, string CombatPower, string Dps, string Damage, string Share, double Fill, string Tooltip);

/// <param name="HpText">"135,382,562 HP" (· "counted from 82 %" when part of the fight went uncounted), or "Defeated".</param>
/// <param name="Percent">"58.9%", or "?" when the max HP is a guess.</param>
public sealed record OverlayBoss(string Name, int NpcCode, long Hp, long MaxHp, double Fraction, string HpText, string Percent);

/// <summary>The fight part of the card: title, clock, boss bar, ranking (OverlayViewModel.Apply before 2026-10-13).</summary>
public sealed record OverlayFight(
    Guid? SegmentId, bool HasData, bool IsActive, string Title, string TitleTip, string Detail, string Clock,
    string PartyDps, string TotalDamage, string Footer, bool MostDamageUnsent, string TopHit, string TopHitBy,
    OverlayBoss? Boss, OverlayRow[] Rows, bool HasGear, int SelfRank, string SelfPlace, int PlayerCount);

/// <param name="Text">"Shugo 39m", "Rift ● 42m" while it runs.</param>
public sealed record OverlayPill(string Key, string Name, string Text, PillTone Tone, bool Running, bool CanSync, long SecondsLeft);

/// <param name="Text">"54:12" left of the instance's countdown (upstream 0.2.8). <paramref name="Urgent"/>: last minute or last tenth → red.</param>
public sealed record OverlayLimit(string Text, string Tip, bool Urgent, int Kind);

/// <summary>One snapshot of the whole overlay card.</summary>
/// <param name="FightId">"live", "s:&lt;segment guid&gt;" or "h:&lt;history file&gt;" — the id `show-fight` takes.</param>
/// <param name="HeaderLabel">"LIVE FIGHT", "SAVED"… in caps; WPF letter-spaces it, <paramref name="HeaderWhen"/> follows ("05.10 19:04").</param>
/// <param name="Status">Status-line text (also the dot's tooltip). <paramref name="FooterText"/> is the caption shown next to the dot.</param>
/// <param name="TimersLabel">The gold boss-timer pill: "Gartua 12:34" or "Timers".</param>
/// <param name="Announcement">A2M-48: the last boss-respawn alert for 60 s ("Gartua respawns 14:05"), a line in the Linux card (no sound yet); the daemon sets it.</param>
public sealed record OverlayModel(
    OverlayFight Fight, OverlayView View, string HeaderLabel, string? HeaderWhen, string FightId,
    OverlayLimit? Limit, CaptureState CaptureState, string CaptureMessage, string Status, StatusTone StatusTone, string FooterText,
    string? AbyssLogs, string TimersLabel, OverlayPill[] Pills, string? UpdateBanner,
    string ModeLabel, TargetMode Mode, string Language, string Version, string Character, long Ts, string? Announcement = null);

/// <summary>One pickable fight (newest first): a segment of this session, then saved fights not already in it.</summary>
/// <param name="Id">"s:&lt;guid&gt;" or "h:&lt;file&gt;", for `show-fight`.</param>
/// <param name="Place">Your place in it (0 = not in it).</param>
public sealed record OverlayFightItem(
    string Id, string Title, DateTimeOffset StartedAt, long CombatMs, EncounterEndReason Reason, bool Active,
    double SelfDps, int Place, double PartyDps, bool Saved)
{
    [JsonIgnore] public Guid? Segment { get; init; }
    [JsonIgnore] public HistoryEntry? Entry { get; init; }
    [JsonIgnore] public EncounterSnapshot? Snapshot { get; init; }
}

/// <summary>`status` message: the capture state and who is logged in, sent on connect and whenever it changes.</summary>
public sealed record OverlayStatus(CaptureState State, string Message, string Character, string Version, string DataDir, bool Replay);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OverlayModel))]
[JsonSerializable(typeof(OverlayFightItem[]))]
[JsonSerializable(typeof(OverlayStatus))]
[JsonSerializable(typeof(PositionModel))]
public sealed partial class OverlayJson : JsonSerializerContext;
