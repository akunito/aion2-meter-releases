using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Inventory;

/// <summary>One stack in a container slot. Uid is the server's per-instance id (changes when stacks merge), Item the catalogue id.</summary>
public sealed record ItemStack(uint Uid, int Item, int Count, byte Container, byte Slot, int Enchant);

/// <summary>Containers as the game numbers them (measured 2026-10-08, Akunita): 1 bag, 2 second bag (materials), 11 equipped, 15 personal warehouse.</summary>
public static class Containers
{
    // Verified against the game's windows 2026-10-09 (Akunita): 1 = Cubo (bag), 2 = Almacén del servidor (account warehouse, 43/70),
    // 15 = Almacén del personaje (character warehouse, 39/75), 11 = equipped, 4 = equipped pet soul.
    public const byte Bag = 1, Bag2 = 2, ServerWarehouse = 2, PetSoul = 4, Equipped = 11, Warehouse = 15;
    public static string Name(byte c) => c switch { Bag => "Bag", ServerWarehouse => "Server warehouse", PetSoul => "Pet soul", Equipped => "Equipped", Warehouse => "Character warehouse", _ => $"Container {c}" };
}

/// <summary>
/// What the character holds, from the login list (<c>11 56</c>, every container) and the refreshes (<c>1b 56</c>: a container
/// re-sent when its window opens, or single slots after a gather / loot / move). Saved as JSON; names and icons come from the
/// game database on demand (ItemCatalog), the store keeps ids only.
/// </summary>
public sealed class InventoryStore
{
    /// <summary>Currencies are ordinary bag stacks with item ids 9301xxxxx: Kina 930100031 (confirmed 2026-10-09: +9,733 then +72,900 on a broker settlement arrived as two 1B56 updates of this stack), Enhance Stone 930100023, Abyss Points 930100003, Kina (Bound) 930100001, Legion Coin 930100002.</summary>
    public const int Kinah = 930100031, KinahBound = 930100001, CurrencyMin = 930_100_000, CurrencyMax = 930_200_000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly Dictionary<byte, Dictionary<byte, ItemStack>> _containers = new();
    private string? _path, _mirror;
    private bool _dirty;
    private long _lastFullMs;

    public InventoryStore(string? path)
    {
        _path = path;
        Load();
    }

    public string? FilePath => _path;

    /// <summary>
    /// Switch to another character's file (characters/&lt;server&gt;-&lt;name&gt;/inventory.json). The login list (Full) usually lands a
    /// moment before the identity packet, so a full list seen in the last 60 s is carried into the new file instead of being
    /// written to the previous character. <paramref name="mirror"/> is also written on every save (the legacy inventory.json = current character).
    /// </summary>
    public void Bind(string? path, string? mirror = null)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) { _mirror = mirror; return; }
        bool keep;
        lock (_gate) keep = _lastFullMs > 0 && Environment.TickCount64 - _lastFullMs < 60_000;
        if (!keep) Save();
        lock (_gate)
        {
            _path = path; _mirror = mirror;
            if (keep) _dirty = true;
            else { _containers.Clear(); UpdatedMs = 0; Load(); _dirty = true; }
        }
        Changed?.Invoke();
    }

    public event Action? Changed;

    public long UpdatedMs { get; private set; }

    public IReadOnlyList<byte> ContainerIds { get { lock (_gate) return _containers.Keys.OrderBy(k => k).ToList(); } }

    public List<ItemStack> Items(byte container)
    {
        lock (_gate) return _containers.TryGetValue(container, out var c) ? c.Values.OrderBy(s => s.Slot).ToList() : [];
    }

    public List<ItemStack> All() { lock (_gate) return _containers.Values.SelectMany(c => c.Values).ToList(); }

    /// <summary>Total of one item over every container but the equipped set.</summary>
    public int CountOf(int item)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var (cid, c) in _containers)
                if (cid != Containers.Equipped) foreach (var s in c.Values) if (s.Item == item) n += s.Count;
            return n;
        }
    }

    public void OnEvent(GameEvent e)
    {
        if (e is not InventoryEvent inv || inv.Stacks.Count == 0) return;
        lock (_gate)
        {
            if (inv.Full) { _containers.Clear(); _lastFullMs = Environment.TickCount64; }
            else if (inv.Stacks.Count >= 8)
            {
                // A whole container re-sent (its window opened): replace it, slots that vanished are gone.
                foreach (var cid in inv.Stacks.Select(s => s.Container).Distinct()) _containers.Remove(cid);
            }
            foreach (var s in inv.Stacks)
            {
                // Shape-scan false positives read misaligned words as ids (855638016 = 0x33000000, 899461376 = 0x359C0000, container 0): real item ids are 9 digits with a non-zero low half.
                if (!Plausible(s)) continue;
                // A uid lives in exactly one slot: a move, a split or a partial refresh must not leave the old copy behind.
                foreach (var (cid, cont) in _containers)
                    foreach (var (slot, st) in cont)
                        if (st.Uid == s.Uid && (cid != s.Container || slot != s.Slot)) { cont.Remove(slot); goto next; }
                next:
                if (s.Count <= 0) { if (_containers.TryGetValue(s.Container, out var gone)) gone.Remove(s.Slot); continue; }   // emptied slot
                if (!_containers.TryGetValue(s.Container, out var c)) _containers[s.Container] = c = new();
                c[s.Slot] = s;
            }
            UpdatedMs = inv.TimeMs;
            _dirty = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Shape-scan false positives: uid 0, container 0, ids below 100M or with a zero low half (855638016 = 0x33000000, 899461376 = 0x359C0000, 812236803 in container 0).</summary>
    private static bool Plausible(ItemStack s) => s.Uid != 0 && s.Container != 0 && s.Item >= 100_000_000 && (s.Item & 0xFFFF) != 0;

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var all = JsonSerializer.Deserialize<List<ItemStack>>(File.ReadAllText(_path), Json);
            if (all is null) return;
            foreach (var s in all)
            {
                if (!Plausible(s)) { _dirty = true; continue; }
                if (!_containers.TryGetValue(s.Container, out var c)) _containers[s.Container] = c = new();
                c[s.Slot] = s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    public void Save()
    {
        if (_path is null) return;
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            text = JsonSerializer.Serialize(_containers.Values.SelectMany(c => c.Values), Json);
            _dirty = false;
        }
        try
        {
            foreach (var target in new[] { _path, _mirror })
            {
                if (target is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target + ".tmp", text);
                File.Move(target + ".tmp", target, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_gate) _dirty = true; }
    }
}
