using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Inventory;

/// <summary>One stack in a container slot. Uid is the server's per-instance id (changes when stacks merge), Item the catalogue id.</summary>
public sealed record ItemStack(uint Uid, int Item, int Count, byte Container, byte Slot, int Enchant);

/// <summary>Containers as the game numbers them (measured 2026-10-08, Akunita): 1 bag, 2 second bag (materials), 11 equipped, 15 personal warehouse.</summary>
public static class Containers
{
    public const byte Bag = 1, Bag2 = 2, Equipped = 11, Warehouse = 15;
    public static string Name(byte c) => c switch { Bag => "Bag", Bag2 => "Bag 2", Equipped => "Equipped", Warehouse => "Warehouse", _ => $"Container {c}" };
}

/// <summary>
/// What the character holds, from the login list (<c>11 56</c>, every container) and the refreshes (<c>1b 56</c>: a container
/// re-sent when its window opens, or single slots after a gather / loot / move). Saved as JSON; names and icons come from the
/// game database on demand (ItemCatalog), the store keeps ids only.
/// </summary>
public sealed class InventoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly Dictionary<byte, Dictionary<byte, ItemStack>> _containers = new();
    private readonly string? _path;
    private bool _dirty;

    public InventoryStore(string? path)
    {
        _path = path;
        Load();
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
            if (inv.Full) _containers.Clear();
            else if (inv.Stacks.Count >= 8)
            {
                // A whole container re-sent (its window opened): replace it, slots that vanished are gone.
                foreach (var cid in inv.Stacks.Select(s => s.Container).Distinct()) _containers.Remove(cid);
            }
            foreach (var s in inv.Stacks)
            {
                if (!_containers.TryGetValue(s.Container, out var c)) _containers[s.Container] = c = new();
                c[s.Slot] = s;
            }
            UpdatedMs = inv.TimeMs;
            _dirty = true;
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var all = JsonSerializer.Deserialize<List<ItemStack>>(File.ReadAllText(_path), Json);
            if (all is null) return;
            foreach (var s in all)
            {
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
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", text);
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_gate) _dirty = true; }
    }
}
