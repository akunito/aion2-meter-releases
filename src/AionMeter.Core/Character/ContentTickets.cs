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

public sealed record TicketInfo(string En, string Es, string Group, bool Time = false, bool Inferred = false)
{
    public string Name(string lang) => lang == "es" && Es.Length > 0 ? Es : En;
}

/// <summary>
/// Names of the 73 ticket keys our EU captures carry (docs/27). The client table <c>ContentsTicket</c> that names them is not public, so:
/// Waffle's ids (KR, confirmed on our captures by value behaviour) for Odyle, Shugo keys, Abyss corridors and sanctuary raids; the party dungeons
/// from the id numbering (6000N101/201/301 ↔ questlog dungeon ids 6000N1x, N = family; 101/201 match the expedition explore/conquest pair,
/// 301 the transcendence-only ones) — the meaning of their numbers is not verified; key 6 from session-20261011-1402 (a 1 h budget that only
/// ran down inside the 320xxx seal dungeons and 142xxx instance layers). Anything else is shown by key.
/// </summary>
public static class TicketBook
{
    public const string Odyle = "odyle", Keys = "keys", Raid = "raid", Dungeon = "dungeon", Corridor = "corridor", Time = "time", Other = "other";

    private static readonly Dictionary<int, TicketInfo> Known = Build();

    private static Dictionary<int, TicketInfo> Build()
    {
        var m = new Dictionary<int, TicketInfo>
        {
            [60_000_001] = new("Odyle energy", "Energía Odyle", Odyle),
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
        (int N, string En, string Es)[] dungeons =
        [
            (0, "Krao Cave", "Cueva de Krao"), (1, "Urugugu Canyon", "Cañón de Urugugu"), (2, "Fire Temple", "Templo de Fuego"), (3, "Draupnir", "Draupnir"),
            (4, "Dying Dramata's Nest", "Nido del Dramata Muerto"), (5, "Deus Research Base", "Base de Investigación Deus"), (6, "Shattered Arkanis", "Arkanis Fragmentado"),
            (7, "Vakron Sky Island", "Isla Aérea de Vakron"), (9, "Ferocious Horn Den", "Caverna del Cuerno Feroz"), (12, "Cradle of Nihility", "Cuna de la Nada"),
            (13, "Hall of Illusion", "Corredor de la Ilusión"), (14, "Azure Breath Island", "Isla del Aliento Azul"), (15, "Citadel of the Fallen Daeva", "Ciudadela del Daeva Caído"),
        ];
        foreach (var (n, en, es) in dungeons)
        {
            var b = 60_000_000 + n * 1000;
            m[b + 101] = new(en, es, Dungeon, Inferred: true);
            m[b + 201] = new(en + " (conquest)", es + " (conquista)", Dungeon, Inferred: true);
            m[b + 301] = new(en + " (transcendence)", es + " (trascendencia)", Dungeon, Inferred: true);
        }
        return m;
    }

    public static TicketInfo Of(int key) => Known.TryGetValue(key, out var t) ? t : new TicketInfo("Ticket " + key, "Ticket " + key, Other, Inferred: true);
    public static bool IsNamed(int key) => Known.ContainsKey(key);
    /// <summary>Corridor stubs 10000007–12 exist in every snapshot and never carry anything.</summary>
    public static bool IsStub(int key) => key is >= 10_000_007 and <= 10_000_012;
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
