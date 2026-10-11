using System.Text.Json;

namespace AionMeter.Core.GameDb;

/// <summary>Item grades in the game's ladder; Unknown = not in the catalogue.</summary>
public enum Grade : byte { Unknown, Common, Rare, Legendary, Unique, Epic, Special }

/// <summary>
/// Grade and bound flag per item id from data/gamedb/item-grades.json (<c>{"id": "unique"}</c> or <c>"unique b"</c> when not tradable),
/// written by tools/item-grades.py from aion2hub's bulk item listing (gamedb/hub-items.json, the one tools/item-icons.py walks).
/// </summary>
public sealed class ItemGrades
{
    public static readonly ItemGrades Empty = new(new Dictionary<int, byte>());
    private const byte BoundBit = 0x80;
    private readonly Dictionary<int, byte> _g;
    private ItemGrades(Dictionary<int, byte> g) { _g = g; }

    public int Count => _g.Count;
    public Grade Of(int item) => _g.TryGetValue(item, out var v) ? (Grade)(v & 0x0F) : Grade.Unknown;
    public bool Bound(int item) => _g.TryGetValue(item, out var v) && (v & BoundBit) != 0;
    public bool Known(int item) => _g.ContainsKey(item);

    /// <summary>"Grade" sort: special, epic, unique, legendary, rare, common, unknown (the approved mock's order).</summary>
    public static int Rank(Grade g) => g switch { Grade.Special => 0, Grade.Epic => 1, Grade.Unique => 2, Grade.Legendary => 3, Grade.Rare => 4, Grade.Common => 5, _ => 9 };

    public static string Name(Grade g) => g switch { Grade.Common => "common", Grade.Rare => "rare", Grade.Legendary => "legendary", Grade.Unique => "unique", Grade.Epic => "epic", Grade.Special => "special", _ => "" };

    public static Grade ParseGrade(string? s) => s switch { "common" => Grade.Common, "rare" => Grade.Rare, "legendary" => Grade.Legendary, "unique" => Grade.Unique, "epic" => Grade.Epic, "special" => Grade.Special, _ => Grade.Unknown };

    public static ItemGrades Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try { return Parse(File.ReadAllText(path)); }
        catch (IOException) { return Empty; }
    }

    public static ItemGrades Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var d = new Dictionary<int, byte>(14_000);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var id) || p.Value.GetString() is not { } v) continue;
                var sp = v.IndexOf(' ');
                var g = (byte)ParseGrade(sp < 0 ? v : v[..sp]);
                if (sp >= 0 && v.AsSpan(sp + 1).Contains('b')) g |= BoundBit;
                d[id] = g;
            }
            return new ItemGrades(d);
        }
        catch (JsonException) { return Empty; }
    }
}
