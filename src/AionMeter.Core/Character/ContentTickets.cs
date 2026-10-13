namespace AionMeter.Core.Character;

/// <summary>
/// One <c>ContentsTickets_NT</c> record (<c>0b 61</c> snapshot / <c>0c 61</c> delta). Field widths by mask bit after Waffle Meter (MIT,
/// github.com/Waffle-ens/waffle_meter, dotnet/src/WaffleMeter.Capture/AbyssCorridorParser.cs): 0x01 u64, 0x02 u64, 0x04 varint (base pool),
/// 0x08 varint (bonus pool). A field the mask leaves out is ZERO, not "unchanged" (a spent ticket arrives with mask 0x00).
/// <paramref name="Time"/> = the 0x01 field: remaining ms of a time ticket (Abyss 25,200,000 = 7 h, key 6 = 1 h).
/// </summary>
public sealed record ContentTicket(int Key, byte Mask, long Time, long Time2, int Base, int Bonus, long UpdatedMs)
{
    public bool IsTime => (Mask & 0x03) != 0 || TicketBook.Of(Key).Time;
    public int Total => Base + Bonus;
}

/// <summary><paramref name="Max"/> &gt; 0 = the game shows the value as "x/Max". <paramref name="Stars"/> = difficulty stars in the Expedition
/// window (0 = not in it); <paramref name="Chest"/> = clears that fill its guaranteed-reward bar (Diego's screenshots 2026-10-13).</summary>
public sealed record TicketInfo(string En, string Es, string Group, bool Time = false, bool Inferred = false, string Mode = "", int Max = 0, int Stars = 0, int Chest = 0)
{
    public string Name(string lang) => lang == "es" && Es.Length > 0 ? Es : En;
}

/// <summary>
/// Names of the 73 ticket keys our EU captures carry (docs/27, table "Tickets de contenido: nombres"). The client table <c>ContentsTicket</c> that names them is not public, so:
/// Waffle's ids (KR, confirmed on our captures by value behaviour) for Odyle, Shugo keys, Abyss corridors and sanctuary raids; the party dungeons
/// from the id numbering (6000N101/201/301 ↔ questlog dungeon ids 6000N1x, N = family; 101/201 match the expedition explore/conquest pair,
/// 301 the transcendence-only ones) — the meaning of their numbers is not verified; key 6 from session-20261011-1402 (a 1 h budget that only
/// ran down inside the 320xxx seal dungeons and 142xxx instance layers); stars, chest sizes and the Odyle cap from Diego's Expedition window
/// screenshots of 2026-10-13. Anything else is kept by key in character.json and left off the Today card.
/// </summary>
public static class TicketBook
{
    public const string Odyle = "odyle", Keys = "keys", Raid = "raid", Dungeon = "dungeon", Corridor = "corridor", Time = "time", Other = "other";
    public const string Explore = "explore", Conquest = "conquest", Transcendence = "transcendence";

    private static readonly Dictionary<int, TicketInfo> Known = Build();

    private static Dictionary<int, TicketInfo> Build()
    {
        var m = new Dictionary<int, TicketInfo>
        {
            // 75 (+1110)/840 in the Expedition window header = base 75, bonus 1110 of this key at 09:21 on 2026-10-13: the 840 cap is the base's
            [60_000_001] = new("Odyle energy", "Energía Odyle", Odyle, Max: 840),
            // value matches only (constant on every character and capture so far, never seen spent): the Conquest tab's "cube rewards 10/10"
            // and "number of uses: 28", the Exploration tab's "number of uses: 3" (which of 70000001/80000001 is not known)
            [60_000_009] = new("Conquest cube rewards", "Recompensas de cubo (Conquista)", Dungeon, Inferred: true, Mode: Conquest, Max: 10),
            [60_000_008] = new("Conquest reward uses", "Usos con recompensa (Conquista)", Dungeon, Inferred: true, Mode: Conquest),
            [70_000_001] = new("Exploration reward uses", "Usos con recompensa (Exploración)", Dungeon, Inferred: true, Mode: Explore),
            [80_000_001] = new("Exploration reward uses (2)", "Usos con recompensa (Exploración, 2)", Dungeon, Inferred: true, Mode: Explore),
            [1] = new("Shugo keys", "Llaves Shugo", Keys),
            [6] = new("Seal dungeons time (1 h)", "Tiempo de mazmorras selladas (1 h)", Time, Time: true, Inferred: true),
            [90_000_001] = new("Abyssal Forge: Ludra — entries", "Forja Abismal: Ludra — entradas", Raid),
            [90_000_002] = new("Abyssal Forge: Ludra — boss clears", "Forja Abismal: Ludra — jefe final", Raid),
            [90_000_003] = new("Corroded Decontamination Facility — entries", "Purificatorio de Erosión — entradas", Raid),
            [90_000_004] = new("Corroded Decontamination Facility — boss clears", "Purificatorio de Erosión — jefe final", Raid),
            [90_000_005] = new("Chalice of Muspel — entries", "Cáliz de Muspel — entradas", Raid),
            [90_000_006] = new("Chalice of Muspel — boss clears", "Cáliz de Muspel — jefe final", Raid),
            [90_000_007] = new("Snowfield of Sorrow — entries", "Snowfield of Sorrow — entradas", Raid),
            [90_000_008] = new("Snowfield of Sorrow — boss clears", "Snowfield of Sorrow — jefe final", Raid),
        };
        // Abyss corridors 10000001–6 (Waffle AbyssCorridorCatalog; names translated from its Korean, not the official EN), 7–12 unused stubs
        string[] corridors = ["Ereshranta's Root", "Sulfur Tree Island", "Siel's Wing Archipelago", "Eroded Central Island", "Contaminated Swamp", "Twisted Old-Tree Forest"];
        for (var i = 0; i < corridors.Length; i++)
            m[10_000_001 + i] = new("Abyss corridor: " + corridors[i], "Corredor del Abismo: " + corridors[i], Corridor, Time: true);
        // Stars/chest: Conquest tab of the Expedition window (Diego, 2026-10-13); the window lists exactly N = 0,1,2,3,7,9 in both tabs, so 4 and 12
        // have conquest keys but no window entry. What the key's number counts (7 or 14, 4 in 301) is not verified: it never moved in 3 days of captures.
        (int N, string En, string Es, int Stars)[] dungeons =
        [
            (0, "Krao Cave", "Cueva de Krao", 1), (1, "Urugugu Canyon", "Cañón de Urugugu", 2), (2, "Fire Temple", "Templo de Fuego", 3), (3, "Draupnir", "Draupnir", 1),
            (4, "Dying Dramata's Nest", "Nido del Dramata Muerto", 0), (5, "Deus Research Base", "Base de Investigación Deus", 0), (6, "Shattered Arkanis", "Arkanis Fragmentado", 0),
            (7, "Vakron Sky Island", "Isla Aérea de Vakron", 2), (9, "Ferocious Horn Den", "Caverna del Cuerno Feroz", 3), (12, "Cradle of Nihility", "Cuna de la Nada", 0),
            (13, "Hall of Illusion", "Corredor de la Ilusión", 0), (14, "Azure Breath Island", "Isla del Aliento Azul", 0), (15, "Citadel of the Fallen Daeva", "Ciudadela del Daeva Caído", 0),
        ];
        foreach (var (n, en, es, stars) in dungeons)
        {
            var b = 60_000_000 + n * 1000;
            m[b + 101] = new(en + " (exploration)", es + " (exploración)", Dungeon, Inferred: true, Mode: Explore);
            m[b + 201] = new(en + " (conquest)", es + " (conquista)", Dungeon, Inferred: true, Mode: Conquest, Stars: stars, Chest: stars == 0 ? 0 : 7 + 7 * stars);
            m[b + 301] = new(en + " (transcendence)", es + " (trascendencia)", Dungeon, Inferred: true, Mode: Transcendence);
        }
        return m;
    }

    public static TicketInfo Of(int key) => Known.TryGetValue(key, out var t) ? t : new TicketInfo("Ticket " + key, "Ticket " + key, Other, Inferred: true);
    public static bool IsNamed(int key) => Known.ContainsKey(key);
    /// <summary>Corridor stubs 10000007–12 exist in every snapshot and never carry anything.</summary>
    public static bool IsStub(int key) => key is >= 10_000_007 and <= 10_000_012;

    private static readonly string[] TodayGroups = [Odyle, Keys, Raid, Dungeon, Corridor, Time];

    /// <summary>
    /// Items tab "Today" card rows, in display order: Odyle, Shugo keys, raids, then the Conquest dungeons of the Expedition window by stars (the
    /// shared conquest counters first) and transcendence, held corridors, the seal hour. Exploration keys, dungeons outside the window, corridor
    /// stubs and unnamed keys stay in character.json only.
    /// </summary>
    public static List<(ContentTicket T, TicketInfo Info)> Today(IReadOnlyDictionary<int, ContentTicket> tickets)
    {
        var list = new List<(ContentTicket T, TicketInfo Info)>(24);
        foreach (var t in tickets.Values)
        {
            if (!Known.TryGetValue(t.Key, out var info) || IsStub(t.Key)) continue;
            if (info.Mode == Explore) continue;
            if (info.Mode == Conquest && info.Stars == 0 && t.Key % 1000 == 201) continue;   // 60004201, 60012201: not in the window
            if (info.Group == Corridor && t.Time == 0) continue;   // only the corridors the faction holds carry time
            list.Add((t, info));
        }
        list.Sort(static (a, b) =>
        {
            var c = Array.IndexOf(TodayGroups, a.Info.Group).CompareTo(Array.IndexOf(TodayGroups, b.Info.Group));
            if (c != 0) return c;
            c = Rank(a.Info).CompareTo(Rank(b.Info));
            return c != 0 ? c : a.T.Key.CompareTo(b.T.Key);
        });
        return list;
    }

    // conquest counters (stars 0, key < 60001000) → conquest by stars → transcendence
    private static int Rank(TicketInfo i) => i.Mode switch { Conquest => i.Stars, Transcendence => 10, _ => 0 };

    /// <summary>"75/840 + 1110", "10/10", "14", "4 + 1".</summary>
    public static string Value(ContentTicket t, TicketInfo info)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var v = info.Max > 0 ? t.Base.ToString(inv) + "/" + info.Max.ToString(inv) : t.Base.ToString(inv);
        return t.Bonus > 0 ? v + " + " + t.Bonus.ToString(inv) : v;
    }
}

/// <summary>Reset boundaries for the Global EU servers: daily 16:00 Seoul (atlas/events.json "reset"); weekly assumed Wednesday at the same hour.</summary>
public static class GameReset
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    public const int Hour = 16;

    public static DateTimeOffset NextDaily(DateTimeOffset now)
    {
        var k = now.ToOffset(Kst);
        var at = new DateTimeOffset(k.Year, k.Month, k.Day, Hour, 0, 0, Kst);
        return at > k ? at : at.AddDays(1);
    }

    public static DateTimeOffset NextWeekly(DateTimeOffset now)
    {
        var at = NextDaily(now);
        while (at.ToOffset(Kst).DayOfWeek != DayOfWeek.Wednesday) at = at.AddDays(1);
        return at;
    }
}
