using System.Text.Json;

namespace AionMeter.Core.GameDb;

/// <summary><see cref="DropperIds"/> runs parallel to <see cref="Droppers"/> (0 where pets.json has no npc id).</summary>
public sealed record PetInfo(int Id, string Name, string Category, int NeedSoul, int[] Souls, string[] Droppers, Dictionary<string, int> Zones, int[] DropperIds);

/// <summary>Pets with the monsters that drop their souls, from data/gamedb/pets.json (questlog pets + aion2hub NPC drops, tools/pets.py).</summary>
public sealed class PetBook
{
    public static readonly PetBook Empty = new(Array.Empty<PetInfo>());
    private readonly PetInfo[] _pets;
    private PetBook(PetInfo[] pets) { _pets = pets; }
    public IReadOnlyList<PetInfo> All => _pets;
    public int Count => _pets.Length;

    public static PetBook Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var list = new List<PetInfo>(220);
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var souls = new List<int>();
                if (p.TryGetProperty("souls", out var ss)) foreach (var sv in ss.EnumerateArray()) souls.Add(sv.GetInt32());
                var droppers = new List<string>(); var dropperIds = new List<int>();
                if (p.TryGetProperty("droppers", out var ds))
                    foreach (var d in ds.EnumerateArray())
                    {
                        droppers.Add(d.GetProperty("name").GetString() ?? "");
                        dropperIds.Add(d.TryGetProperty("npc", out var npc) && npc.ValueKind == JsonValueKind.Number ? npc.GetInt32() : 0);
                    }
                var zones = new Dictionary<string, int>();
                if (p.TryGetProperty("zones", out var zs)) foreach (var z in zs.EnumerateObject()) zones[z.Name] = z.Value.GetInt32();
                list.Add(new PetInfo(p.GetProperty("id").GetInt32(), p.GetProperty("name").GetString() ?? "", p.TryGetProperty("cat", out var c) ? c.GetString() ?? "" : "", p.TryGetProperty("needSoul", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0, souls.ToArray(), droppers.ToArray(), zones, dropperIds.ToArray()));
            }
            return new PetBook(list.ToArray());
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { return Empty; }
    }

    /// <summary>Pets not in <paramref name="owned"/>; the atlas matches their species to the monster templates.</summary>
    public List<PetInfo> Missing(IReadOnlyCollection<int> owned)
    {
        var set = owned as ISet<int> ?? new HashSet<int>(owned);
        var list = new List<PetInfo>();
        foreach (var p in _pets) if (!set.Contains(p.Id)) list.Add(p);
        return list;
    }
}
