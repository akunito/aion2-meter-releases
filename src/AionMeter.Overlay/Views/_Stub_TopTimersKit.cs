// TEMP A2M-50 stub, deleted at merge (the real interface is in Shell/TopTimersKit.cs on branch a2m50-top-timers)
using AionMeter.Core.Armory;

namespace AionMeter.Overlay.Shell;

internal interface IFightTabProfile
{
    PlayerCard? Card { get; }
    void UpdateCard(PlayerCard card, string? status);
    void RefreshProfile();
}
