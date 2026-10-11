using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Inventory;

/// <summary>One stack in a container slot. Uid is the server's per-instance id (changes when stacks merge, renumbered per login), Item the catalogue id.</summary>
public sealed record ItemStack(uint Uid, int Item, int Count, byte Container, byte Slot, int Enchant);

/// <summary>When a container was last seen (Unix ms, 0 = never): SeenMs = any slot or list of it, FullMs = its last whole list (login list or window re-send).</summary>
public readonly record struct ContainerSeen(long SeenMs, long FullMs);

/// <summary>A character's inventory.json read without binding a store to it (Items tab: the other characters).</summary>
public sealed record InventoryFile(List<ItemStack> Stacks, Dictionary<byte, ContainerSeen> Seen, DateTime WrittenUtc)
{
    public List<ItemStack> Items(byte container) { var l = new List<ItemStack>(); foreach (var s in Stacks) if (s.Container == container) l.Add(s); l.Sort(static (a, b) => a.Slot.CompareTo(b.Slot)); return l; }
    public ContainerSeen SeenOf(byte container) => Seen.GetValueOrDefault(container);
}

/// <summary>Containers as the game numbers them. The numbers are what the files store; only the names changed (A2M-32).</summary>
public static class Containers
{
    // Verified against the game's windows 2026-10-09/11 (Akunita, Irin, Arielka): 1 = Cubo (bag); 2 = Almacén del servidor, shared by every
    // character of the account on that server (Irin's and Akunita's copies hold the same item in the same slot, 65 of 65); 15 = Almacén del
    // personaje; 11 = equipped; 4 = cosmetic (a weapon skin on Akunita, a title item on Arielka — was guessed "pet soul"); 17 = unknown
    // (one Wisdom Breastplate in slot 32 on Akunita).
    public const byte Bag = 1, AccountWarehouse = 2, Cosmetic = 4, Equipped = 11, CharacterWarehouse = 15, Unknown17 = 17;
    public static string Name(byte c) => c switch { Bag => "Bag", AccountWarehouse => "Account warehouse", Cosmetic => "Cosmetic", Equipped => "Equipped", CharacterWarehouse => "Character warehouse", _ => $"Container {c}" };
    /// <summary>Character-bound containers the login list always carries: missing from it = empty. The warehouses are not always in it
    /// (Arielka's and Irin's logins of 2026-10-10/11 left them out), so a login list without them must not wipe them.</summary>
    public static bool AlwaysInLoginList(byte c) => c is Bag or Equipped or Cosmetic;
}

/// <summary>
/// What the character holds, from the login list (<c>11 56</c>) and the refreshes (<c>1b 56</c>: a container re-sent when its window
/// opens, or the slots a gather / loot / move / deposit changed). Saved as JSON; names and icons come from the game database on demand,
/// the store keeps ids only.
/// </summary>
public sealed class InventoryStore
{
    /// <summary>Currencies are ordinary bag stacks with item ids 9301xxxxx: Kina 930100031 (confirmed 2026-10-09: +9,733 then +72,900 on a broker settlement arrived as two 1B56 updates of this stack), Enhance Stone 930100023, Abyss Points 930100003, Kina (Bound) 930100001, Legion Coin 930100002.</summary>
    public const int Kinah = 930100031, KinahBound = 930100001, AbyssPoints = 930100003, EnhanceStone = 930100023, CurrencyMin = 930_100_000, CurrencyMax = 930_200_000;

    public static bool IsCurrency(int item) => item is >= CurrencyMin and < CurrencyMax;

    /// <summary>Packets of one login list closer than this belong to the same list (a later one must not wipe what the first replaced).</summary>
    private const long LoginBurstMs = 5_000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly Dictionary<byte, Dictionary<byte, ItemStack>> _containers = new();
    private readonly Dictionary<byte, ContainerSeen> _seen = new();
    // when each slot was last written this session (count 0 too): what the shared account warehouse merges by. Not persisted.
    private readonly Dictionary<byte, Dictionary<byte, long>> _slotSeen = new();
    private readonly HashSet<byte> _loginReplaced = new(), _wholeReplaced = new();
    private string? _path, _mirror;
    private bool _dirty;
    private long _lastFullMs, _loginBurstMs = -1;
    // read-only view of another character's file (A2M-28 view-while-playing): never takes packets, never writes
    private readonly bool _readOnly;
    private DateTime _loadedWritten;
    private long _lastReloadCheck = -1;

    public InventoryStore(string? path) : this(path, false) { }

    private InventoryStore(string? path, bool readOnly)
    {
        _path = path;
        _readOnly = readOnly;
        Load();
    }

    /// <summary>A character's inventory.json shown while the live packets belong to someone else: ignores events, Save and Bind write nothing; <see cref="ReloadIfChanged"/> follows the file.</summary>
    public static InventoryStore ReadOnly(string path) => new(path, true);

    public bool IsReadOnly => _readOnly;

    /// <summary>Read-only stores: re-read the file when its write time moved, checked at most every 5 s; raises Changed on a reload.</summary>
    public bool ReloadIfChanged(long nowMs)
    {
        if (!_readOnly || _path is null || (_lastReloadCheck >= 0 && nowMs - _lastReloadCheck < 5_000)) return false;
        _lastReloadCheck = nowMs;
        DateTime written;
        try { written = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        lock (_gate)
        {
            if (written == _loadedWritten) return false;
            _containers.Clear(); _seen.Clear(); _slotSeen.Clear();
            Load();
            _loadedWritten = written;   // a rewrite between the stat and the read costs one extra reload, never a missed one
        }
        Changed?.Invoke();
        return true;
    }

    public string? FilePath => _path;

    /// <summary>
    /// Switch to another character's file (characters/&lt;server&gt;-&lt;name&gt;/inventory.json). The login list (Full) usually lands a
    /// moment before the identity packet, so a full list seen in the last 60 s is carried into the new file instead of being
    /// written to the previous character. <paramref name="mirror"/> is also written on every save (the legacy inventory.json = current character).
    /// </summary>
    public void Bind(string? path, string? mirror = null)
    {
        if (_readOnly)
        {
            lock (_gate) { _path = path; _containers.Clear(); _seen.Clear(); _slotSeen.Clear(); _loadedWritten = default; Load(); }
            Changed?.Invoke();
            return;
        }
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) { lock (_gate) { _mirror = mirror; _lastFullMs = 0; } Save(); return; }   // same character again: release the hold
        bool keep;
        lock (_gate) { keep = LoginListPending; _lastFullMs = 0; }
        if (!keep) Save();
        lock (_gate)
        {
            _path = path; _mirror = mirror;
            if (keep) _dirty = true;
            else { _containers.Clear(); _seen.Clear(); _slotSeen.Clear(); UpdatedMs = 0; Load(); _dirty = true; }
        }
        Save();
        Changed?.Invoke();
    }

    public event Action? Changed;

    public long UpdatedMs { get; private set; }

    /// <summary>A login list waits for its character (see <see cref="Bind"/>): nothing derived from it may be attributed yet.</summary>
    public bool Pending { get { lock (_gate) return LoginListPending; } }

    public IReadOnlyList<byte> ContainerIds { get { lock (_gate) return _containers.Keys.OrderBy(k => k).ToList(); } }

    public List<ItemStack> Items(byte container)
    {
        lock (_gate) return _containers.TryGetValue(container, out var c) ? c.Values.OrderBy(s => s.Slot).ToList() : [];
    }

    public List<ItemStack> All() { lock (_gate) return _containers.Values.SelectMany(c => c.Values).ToList(); }

    public ContainerSeen Seen(byte container) { lock (_gate) return _seen.GetValueOrDefault(container); }

    public Dictionary<byte, ContainerSeen> SeenAll() { lock (_gate) return new Dictionary<byte, ContainerSeen>(_seen); }

    /// <summary>Slot → Unix ms of its last write this session (emptied slots included).</summary>
    public Dictionary<byte, long> SlotStamps(byte container) { lock (_gate) return _slotSeen.TryGetValue(container, out var d) ? new Dictionary<byte, long>(d) : new(); }

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
        if (_readOnly || e is not InventoryEvent inv || inv.Stacks.Count == 0) return;
        var t = inv.TimeMs;
        lock (_gate)
        {
            if (inv.Full)
            {
                // A2M-32: the login list replaces the containers it carries plus the character-bound ones; warehouses it leaves out stay.
                if (_loginBurstMs < 0 || t - _loginBurstMs is < 0 or > LoginBurstMs) _loginReplaced.Clear();
                _loginBurstMs = t;
                foreach (var s in inv.Stacks) if (Plausible(s)) Replace(s.Container, t, _loginReplaced);
                Replace(Containers.Bag, t, _loginReplaced); Replace(Containers.Equipped, t, _loginReplaced); Replace(Containers.Cosmetic, t, _loginReplaced);
                _lastFullMs = Environment.TickCount64;
            }
            else if (inv.Whole)
            {
                // A whole container re-sent (its window opened, header flag 1): replace it, slots that vanished are gone.
                // Before A2M-32 any update of ≥ 8 stacks did this, and a 12-stack deposit (24 records) or a burst of potion uses wiped
                // the bag: Akunita's file was left with 8 stacks in slots 1, 9–15 on 2026-10-11.
                _wholeReplaced.Clear();
                foreach (var s in inv.Stacks) if (Plausible(s)) Replace(s.Container, t, _wholeReplaced);
            }
            foreach (var s in inv.Stacks)
            {
                // Shape-scan false positives read misaligned words as ids (855638016 = 0x33000000, 899461376 = 0x359C0000, container 0): real item ids are 9 digits with a non-zero low half.
                if (!Plausible(s)) continue;
                Stamp(s.Container, s.Slot, t);
                if (s.Count <= 0) { Empty(s, t); continue; }
                // A uid lives in exactly one slot: a move, a split or a partial refresh must not leave the old copy behind. The vacated
                // slot is stamped too: the game sends only the destination of a move (withdrawal 2026-10-11 15:08: bag slot 8 arrived, the
                // warehouse slot never did), and the shared account warehouse only drops a slot that carries a fresh stamp.
                foreach (var (cid, cont) in _containers)
                    foreach (var (slot, st) in cont)
                        if (st.Uid == s.Uid && (cid != s.Container || slot != s.Slot)) { cont.Remove(slot); Stamp(cid, slot, t); goto next; }
                next:
                if (!_containers.TryGetValue(s.Container, out var c)) _containers[s.Container] = c = new();
                c[s.Slot] = s;
            }
            UpdatedMs = t;
            _dirty = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Clears a container for a whole list (once per list) and marks it fully seen.</summary>
    private void Replace(byte container, long t, HashSet<byte> done)
    {
        if (!done.Add(container)) return;
        _containers.Remove(container);
        if (_slotSeen.TryGetValue(container, out var st)) foreach (var slot in st.Keys.ToList()) st[slot] = t;   // absent from the list = empty now
        _seen[container] = new ContainerSeen(t, t);
    }

    private void Stamp(byte container, byte slot, long t)
    {
        if (!_slotSeen.TryGetValue(container, out var st)) _slotSeen[container] = st = new();
        st[slot] = t;
        var old = _seen.GetValueOrDefault(container);
        _seen[container] = old with { SeenMs = Math.Max(old.SeenMs, t) };
    }

    /// <summary>Count 0: the stack left its slot. Its uid goes wherever it is; a different stack of the same item in that slot is a copy
    /// from an older login (uids are renumbered per login) and goes too; another item in the slot was written after and stays.</summary>
    private void Empty(ItemStack s, long t)
    {
        foreach (var (cid, cont) in _containers)
            foreach (var (slot, st) in cont)
                if (st.Uid == s.Uid) { cont.Remove(slot); Stamp(cid, slot, t); goto done; }
        done:
        if (_containers.TryGetValue(s.Container, out var c) && c.TryGetValue(s.Slot, out var cur) && cur.Item == s.Item) c.Remove(s.Slot);
    }

    /// <summary>Shape-scan false positives: uid 0, container 0, ids below 100M or with a zero low half (855638016 = 0x33000000, 899461376 = 0x359C0000, 812236803 in container 0).</summary>
    private static bool Plausible(ItemStack s) => s.Uid != 0 && s.Container != 0 && s.Item >= 100_000_000 && (s.Item & 0xFFFF) != 0;

    private void Load()
    {
        if (_path is null || ReadFile(_path) is not { } f) return;
        _loadedWritten = f.WrittenUtc;
        foreach (var s in f.Stacks)
        {
            if (!_containers.TryGetValue(s.Container, out var c)) _containers[s.Container] = c = new();
            c[s.Slot] = s;
        }
        foreach (var (k, v) in f.Seen) _seen[k] = v;
    }

    /// <summary>
    /// inventory.json is a JSON array of stacks; since A2M-32 a last element <c>{"Seen":{"1":[seenMs,fullMs],…}}</c> carries the container
    /// times. Older builds read that element as a stack of uid 0 and drop it (Plausible), so the file stays readable both ways.
    /// </summary>
    public static InventoryFile? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var written = File.GetLastWriteTimeUtc(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var stacks = new List<ItemStack>(doc.RootElement.GetArrayLength());
            var seen = new Dictionary<byte, ContainerSeen>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (el.TryGetProperty("Seen", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in meta.EnumerateObject())
                        if (byte.TryParse(p.Name, out var cid) && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 2)
                            seen[cid] = new ContainerSeen(p.Value[0].GetInt64(), p.Value[1].GetInt64());
                    continue;
                }
                var s = el.Deserialize<ItemStack>(Json);
                if (s is not null && Plausible(s) && s.Count > 0) stacks.Add(s);
            }
            return new InventoryFile(stacks, seen, written);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>A full list that arrived in the last 60 s may belong to a character whose identity has not been seen yet (Bind): do not write it over the previous character's file.</summary>
    private bool LoginListPending => _lastFullMs > 0 && Environment.TickCount64 - _lastFullMs < 60_000;

    public void Save()
    {
        if (_path is null || _readOnly) return;
        string text;
        lock (_gate)
        {
            if (!_dirty || LoginListPending) return;
            text = Serialize();
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

    private string Serialize()
    {
        var stacks = JsonSerializer.Serialize(_containers.Values.SelectMany(c => c.Values), Json);
        if (_seen.Count == 0) return stacks;
        var sb = new System.Text.StringBuilder(stacks.Length + 32 * _seen.Count);
        sb.Append(stacks, 0, stacks.Length - 1);
        if (stacks.Length > 2) sb.Append(',');
        sb.Append("{\"Seen\":{");
        var first = true;
        foreach (var (k, v) in _seen.OrderBy(kv => kv.Key))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(k).Append("\":[").Append(v.SeenMs).Append(',').Append(v.FullMs).Append(']');
        }
        sb.Append("}}]");
        return sb.ToString();
    }
}
