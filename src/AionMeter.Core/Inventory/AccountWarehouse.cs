using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionMeter.Core.Inventory;

/// <summary>One slot of the shared warehouse: who wrote it last and when (Unix ms). Count 0 = emptied at that time (kept so an older copy cannot bring it back).</summary>
public sealed record AccountSlot(byte Slot, int Item, int Count, int Enchant, string By, long AtMs);

/// <summary>The shared warehouse of one server as the merge of every character's sightings: live slots by slot, last full list.</summary>
public sealed record AccountWarehouseView(string Server, string FullBy, long FullMs, IReadOnlyList<AccountSlot> Slots)
{
    /// <summary>Slots written after the last full list, grouped by who wrote them: (who, newest time, slot count).</summary>
    public List<(string By, long AtMs, int Slots)> NewerThanFull()
    {
        var d = new Dictionary<string, (long, int)>();
        foreach (var s in Slots)
            if (s.AtMs > FullMs) { var (at, n) = d.GetValueOrDefault(s.By); d[s.By] = (Math.Max(at, s.AtMs), n + 1); }
        var l = new List<(string, long, int)>(d.Count);
        foreach (var (k, (at, n)) in d) l.Add((k, at, n));
        l.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));
        return l;
    }

    /// <summary>The most recent write of any slot (or the full list): "updated by X at HH:mm".</summary>
    public (string By, long AtMs) LastUpdate()
    {
        var by = FullBy; var at = FullMs;
        foreach (var s in Slots) if (s.AtMs > at) { at = s.AtMs; by = s.By; }
        return (by, at);
    }
}

/// <summary>
/// Container 2 is the account's warehouse on a server (A2M-32): every character sees the same slots, so each character's copy is only
/// as fresh as that character's last look. This merges them by slot (uids are renumbered per login, slot is the key): the newest write
/// per slot wins, a full list counts as a write of every slot at its time (absent = empty). Saved as AppData\account-warehouse.json keyed
/// by server id; the per-character inventory.json keeps its own container 2 for compatibility.
/// </summary>
public sealed class AccountWarehouse
{
    /// <summary>Old inventory.json files carry no container times: a container 2 of at least this many stacks is taken as a full list (the
    /// warehouse holds 65–67 on the main account; deposit-only copies of 2026-10-11 had 2–10).</summary>
    public const int LegacyFullStacks = 40;

    private sealed class ServerState
    {
        public string FullBy { get; set; } = "";
        public long FullMs { get; set; }
        public Dictionary<byte, AccountSlot> Slots { get; set; } = new();
    }

    private sealed class FileShape
    {
        [JsonPropertyName("servers")] public Dictionary<string, ServerShape> Servers { get; set; } = new();
    }

    private sealed class ServerShape
    {
        [JsonPropertyName("fullBy")] public string FullBy { get; set; } = "";
        [JsonPropertyName("fullAt")] public long FullAt { get; set; }
        // [slot, item, count, enchant, seenBy, seenAt]
        [JsonPropertyName("slots")] public List<JsonElement[]> Slots { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _gate = new();
    private readonly Dictionary<string, ServerState> _servers = new();
    private readonly string? _path;
    private bool _dirty;

    public AccountWarehouse(string? path)
    {
        _path = path;
        Load();
    }

    public event Action? Changed;

    public bool HasServer(string server) { lock (_gate) return _servers.ContainsKey(server); }

    public AccountWarehouseView? View(string server)
    {
        lock (_gate)
        {
            if (!_servers.TryGetValue(server, out var s)) return null;
            var live = new List<AccountSlot>(s.Slots.Count);
            foreach (var v in s.Slots.Values) if (v.Count > 0) live.Add(v);
            live.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
            return new AccountWarehouseView(server, s.FullBy, s.FullMs, live);
        }
    }

    /// <summary>
    /// One character's container 2 into the merge. <paramref name="fullMs"/> = its last full list (0 = none); <paramref name="slotStamps"/> =
    /// slot → time of the last write this session (emptied slots included). Slots without a stamp newer than what the merge holds are ignored.
    /// Returns true when the merge changed.
    /// </summary>
    public bool Absorb(string server, string who, long fullMs, IReadOnlyList<ItemStack> stacks, IReadOnlyDictionary<byte, long> slotStamps)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_servers.TryGetValue(server, out var s)) _servers[server] = s = new ServerState();
            Dictionary<byte, ItemStack>? bySlot = null;
            if (fullMs > s.FullMs)
            {
                bySlot = BySlot(stacks);
                // every slot older than the list takes the list's content (or is empty); slots written after it stay
                foreach (var slot in s.Slots.Keys.ToList())
                    if (s.Slots[slot].AtMs < fullMs && !bySlot.ContainsKey(slot)) s.Slots.Remove(slot);
                foreach (var (slot, st) in bySlot)
                    if (!s.Slots.TryGetValue(slot, out var cur) || cur.AtMs < fullMs) s.Slots[slot] = new AccountSlot(slot, st.Item, st.Count, st.Enchant, who, fullMs);
                s.FullMs = fullMs; s.FullBy = who;
                changed = true;
            }
            foreach (var (slot, at) in slotStamps)
            {
                if (at <= s.FullMs || (s.Slots.TryGetValue(slot, out var cur) && cur.AtMs >= at)) continue;
                bySlot ??= BySlot(stacks);
                s.Slots[slot] = bySlot.TryGetValue(slot, out var st) ? new AccountSlot(slot, st.Item, st.Count, st.Enchant, who, at) : new AccountSlot(slot, 0, 0, 0, who, at);
                changed = true;
            }
            if (changed) _dirty = true;
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>
    /// First run: the merge from the inventory.json files already on disk. A file with container times uses them; an old one counts as seen at
    /// its write time, as a full list when it holds at least <see cref="LegacyFullStacks"/> stacks, else as single slots.
    /// </summary>
    public void Seed(string server, IEnumerable<(string Who, InventoryFile File)> characters)
    {
        foreach (var (who, f) in characters.OrderBy(c => c.File.WrittenUtc))
        {
            var stacks = f.Items(Containers.AccountWarehouse);
            var seen = f.SeenOf(Containers.AccountWarehouse);
            var fileMs = new DateTimeOffset(DateTime.SpecifyKind(f.WrittenUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var full = seen.FullMs > 0 ? seen.FullMs : stacks.Count >= LegacyFullStacks ? fileMs : 0;
            var at = seen.SeenMs > 0 ? seen.SeenMs : fileMs;
            var stamps = new Dictionary<byte, long>(stacks.Count);
            if (full == 0 || at > full) foreach (var st in stacks) stamps[st.Slot] = at;
            Absorb(server, who, full, stacks, stamps);
        }
        lock (_gate) if (!_servers.ContainsKey(server)) { _servers[server] = new ServerState(); _dirty = true; }
    }

    private static Dictionary<byte, ItemStack> BySlot(IReadOnlyList<ItemStack> stacks)
    {
        var d = new Dictionary<byte, ItemStack>(stacks.Count);
        foreach (var st in stacks) if (st.Container == Containers.AccountWarehouse && st.Count > 0) d[st.Slot] = st;
        return d;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var f = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path), Json);
            if (f is null) return;
            foreach (var (server, sh) in f.Servers)
            {
                var s = new ServerState { FullBy = sh.FullBy, FullMs = sh.FullAt };
                foreach (var r in sh.Slots)
                {
                    if (r.Length < 6) continue;
                    var slot = (byte)r[0].GetInt32();
                    s.Slots[slot] = new AccountSlot(slot, r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32(), r[4].GetString() ?? "", r[5].GetInt64());
                }
                _servers[server] = s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException) { }
    }

    public void Save()
    {
        if (_path is null) return;
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            var f = new FileShape();
            foreach (var (server, s) in _servers)
            {
                var sh = new ServerShape { FullBy = s.FullBy, FullAt = s.FullMs };
                foreach (var v in s.Slots.Values.OrderBy(v => v.Slot))
                    sh.Slots.Add([Num(v.Slot), Num(v.Item), Num(v.Count), Num(v.Enchant), JsonSerializer.SerializeToElement(v.By), Num(v.AtMs)]);
                f.Servers[server] = sh;
            }
            text = JsonSerializer.Serialize(f, Json);
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

    private static JsonElement Num(long v) => JsonSerializer.SerializeToElement(v);
}
