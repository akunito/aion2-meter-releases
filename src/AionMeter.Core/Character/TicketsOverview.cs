using System.Globalization;

namespace AionMeter.Core.Character;

/// <summary>
/// One character's row of the Items tab "Tickets" table. Cells are display strings ("—" = the character has no such record); the tips name
/// each raid/corridor behind the compact cells. <see cref="SeenMs"/> = newest ticket update, else the file's last login list (0 = never).
/// </summary>
public sealed record TicketsRow(bool Any, string Odyle, string Keys, string DaevaPass, string EventPass, string Seal, string Raids, string Corridors,
    string PassTip, string RaidsTip, string CorridorsTip, long SeenMs);

public static class TicketsOverview
{
    public const string None = "—";
    public const int DaevaPassId = 810_433_000, EventPassId = 810_435_000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly int[] PassIds = [DaevaPassId, EventPassId];

    /// <summary>Raids as (entries key, boss-clears key, short name); Snowfield (7/8) is named by Waffle but not yet seen on EU.</summary>
    private static readonly (int Entries, int Boss, string Short)[] RaidKeys =
        [(90_000_001, 90_000_002, "Ludra"), (90_000_003, 90_000_004, "Corroded"), (90_000_005, 90_000_006, "Muspel"), (90_000_007, 90_000_008, "Snowfield")];

    public static readonly TicketsRow Empty = new(false, None, None, None, None, None, None, None, "", "", "", 0);

    /// <summary>"0:42 h" (minutes truncated, as the game's own countdowns).</summary>
    public static string Hours(long ms)
    {
        if (ms < 0) ms = 0;
        var min = ms / 60_000;
        return (min / 60).ToString(Inv) + ":" + (min % 60).ToString("00", Inv) + " h";
    }

    /// <summary>Pass level = catalogue tiers whose EXP goal the counter reached (matches the claimed step on Arielka 22/16); claimed step without a catalogue.</summary>
    public static int PassLevel(AchievementRow r, AchievementInfo? info)
    {
        if (info is null || info.Tiers.Length == 0) return r.Step;
        var n = 0;
        var tiers = info.Tiers;
        for (var i = 0; i < tiers.Length && tiers[i].Goal <= r.Count; i++) n++;
        return n;
    }

    public static TicketsRow Of(CharacterProgress? p, AchievementCatalog catalog, string lang = "en")
    {
        if (p is null) return Empty;
        var t = p.Tickets;
        var a = p.Achievements;
        long seen = 0;
        foreach (var x in t.Values) if (x.UpdatedMs > seen) seen = x.UpdatedMs;

        var odyle = t.TryGetValue(60_000_001, out var o) ? o.Base.ToString(Inv) + "+" + o.Bonus.ToString(Inv) : None;
        var keys = t.TryGetValue(1, out var k) ? k.Total.ToString(Inv) : None;
        var seal = t.TryGetValue(6, out var s) ? Hours(s.Time) : None;

        string passTip = "", daeva = None, ev = None;
        foreach (var id in PassIds)
        {
            if (!a.TryGetValue(id, out var r) || r.Count == 0 && r.Step == 0) continue;
            var info = catalog.Get(id);
            var lv = PassLevel(r, info).ToString(Inv);
            var line = (id == DaevaPassId ? "Daeva Pass" : "Event pass") + ": level " + lv + (info is { Tiers.Length: > 0 } ? "/" + info.Tiers.Length.ToString(Inv) : "")
                       + " · " + r.Count.ToString("N0", Inv) + " EXP · rewards claimed " + r.Step.ToString(Inv);
            passTip = passTip.Length == 0 ? line : passTip + "\n" + line;
            if (id == DaevaPassId) daeva = lv; else ev = lv;
        }

        var raids = new List<string>(4);
        var raidTip = new List<string>(4);
        foreach (var (ek, bk, shortName) in RaidKeys)
        {
            var he = t.TryGetValue(ek, out var e); var hb = t.TryGetValue(bk, out var b);
            if (!he && !hb) continue;
            var en = he ? e!.Total.ToString(Inv) : "?"; var bn = hb ? b!.Total.ToString(Inv) : "?";
            raids.Add(en + "/" + bn);
            raidTip.Add(RaidName(ek, lang, shortName) + ": " + en + " entries, " + bn + " boss clears");
        }

        // corridors 10000001–6 carry time only while the faction holds them; 7–12 are empty stubs
        var held = new List<string>(6);
        var corrTip = new List<string>(6);
        var anyCorridor = false;
        for (var key = 10_000_001; key <= 10_000_006; key++)
        {
            if (!t.TryGetValue(key, out var c)) continue;
            anyCorridor = true;
            if (c.Time <= 0) continue;
            held.Add(Hours(c.Time));
            corrTip.Add(TicketBook.Of(key).Name(lang) + ": " + Hours(c.Time));
        }
        var corridors = !anyCorridor ? None : held.Count == 0 ? "0" : held.Count <= 2 ? string.Join(" · ", held) : held.Count.ToString(Inv) + " held";

        var any = t.Count > 0 || daeva != None || ev != None;
        if (seen == 0 && any) seen = p.UpdatedMs;
        return new TicketsRow(any, odyle, keys, daeva, ev, seal, raids.Count == 0 ? None : string.Join(" · ", raids), corridors,
            passTip, string.Join("\n", raidTip), string.Join("\n", corrTip), seen);
    }

    private static string RaidName(int entriesKey, string lang, string fallback)
    {
        var n = TicketBook.Of(entriesKey).Name(lang);
        var dash = n.IndexOf(" — ", StringComparison.Ordinal);
        return dash > 0 ? n[..dash] : fallback;
    }
}
