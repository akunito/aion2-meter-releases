using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>The few strings the card composes itself (everything else arrives final from OverlayBuilder). Pure, tested without a display.</summary>
public static class CardText
{
    /// <summary>WPF has no letter-spacing, so the WPF/Avalonia cards space the caps with blanks; GTK CSS has letter-spacing, the caps stay plain.</summary>
    public static string HeaderLabel(string label, string? when) => when is null ? label.ToUpperInvariant() : label.ToUpperInvariant() + "   " + when;

    /// <summary>Rank badge text: pets of unknown owners have no place.</summary>
    public static string Rank(int rank) => rank > 0 ? rank.ToString(System.Globalization.CultureInfo.InvariantCulture) : "·";

    /// <summary>The title line: the fight title, or the detail line while there is none ("Waiting for combat…").</summary>
    public static string Title(OverlayFight f) => f.Title.Length > 0 ? f.Title : f.Detail;

    /// <summary>One entry of the Fights menu, as the Avalonia/WPF menus write it: "▸ ✔ 19:04  Marsh Targon  ·  1:23  ·  30.5K/s #1".</summary>
    public static string FightItem(OverlayFightItem it, string? shownId, UiText t, TimeZoneInfo? zone = null)
    {
        var mark = it.Active ? "●" : it.Reason switch { EncounterEndReason.Kill => "✔", EncounterEndReason.Wipe => "✖", _ => "•" };
        var dps = it.SelfDps > 0 ? $"  ·  {Format.Compact(it.SelfDps)}/s{(it.Place > 0 ? $" #{it.Place}" : "")}"
            : it.PartyDps > 0 ? $"  ·  {t.Party} {Format.Compact(it.PartyDps)}/s" : "";
        var at = TimeZoneInfo.ConvertTime(it.StartedAt, zone ?? TimeZoneInfo.Local);
        return $"{(it.Id == shownId ? "▸ " : "")}{mark} {at:HH:mm}  {it.Title}  ·  {Format.Clock(it.CombatMs)}{dps}";
    }

    /// <summary>The "No daemon" card's body line.</summary>
    public static string NoDaemon(string socket) => "aionmeter-daemon is not running.\nWaiting for " + socket;

    /// <summary>CSS class of a footer pill's colour step.</summary>
    public static string PillClass(PillTone tone) => tone switch { PillTone.Soon => "p1", PillTone.Close => "p2", PillTone.Now => "p3", _ => "p0" };

    /// <summary>CSS class of a rank badge: gold/silver/bronze, ringed number below.</summary>
    public static string RankClass(int rank) => rank switch { 1 => "r1", 2 => "r2", 3 => "r3", _ => "rn" };

    /// <summary>Every "Npx" in the stylesheet times <paramref name="scale"/> (--scale): the card grows as a whole, hairlines stay ≥ 1 px.</summary>
    public static string ScaleCss(string css, double scale)
    {
        if (Math.Abs(scale - 1) < 0.001) return css;
        return System.Text.RegularExpressions.Regex.Replace(css, @"(?<![\w.#-])(\d+(?:\.\d+)?)px", m =>
        {
            var v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * scale;
            if (v > 0 && v < 1) v = 1;
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";
        });
    }
}
