using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Character;

public sealed record PetOwned(int Id, int Level);

/// <summary>
/// The local character's collections as the server lists them at login (EU, 2026-10-08): pets with levels (<c>00 90</c>),
/// owned titles (<c>79 56</c>) and the achievement/collection rows (<c>83 56</c>), plus (A2M-35) the build lists — skills
/// (<c>00 51</c>), equipped specs (<c>00 39</c>), skill bar (<c>8c e2</c>), Daevanion nodes (<c>26 e2</c>) — and the stat sheet
/// (<c>49 36</c>) as <see cref="Captured"/>; (A2M-15) live achievement counters/claims (<c>84 56</c>, <c>8a 56</c>) and the content tickets
/// (<c>0b 61</c>/<c>0c 61</c>). Saved as one JSON so the AI side can read it.
/// </summary>
public sealed class CharacterState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private string? _path, _mirror;
    private long _lastListMs;
    private bool _dirty;

    public CharacterState(string? path)
    {
        _path = path;
        Load();
    }

    public event Action? Changed;

    public int EquippedPet { get; private set; }
    public Dictionary<int, int> Pets { get; private set; } = new();           // pet id → level
    public HashSet<int> Titles { get; private set; } = new();
    public Dictionary<int, AchievementRow> Achievements { get; private set; } = new();
    /// <summary>Achievement window tab reward tracks from the last <c>83 56</c>.</summary>
    public List<AchievementGroup> AchievementGroups { get; private set; } = new();
    /// <summary>Content tickets by key (<c>0b 61</c> at login, <c>0c 61</c> deltas).</summary>
    public Dictionary<int, ContentTicket> Tickets { get; private set; } = new();
    /// <summary>Expedition guaranteed-chest bars by dungeon id (<c>12 57</c>); null = none received for this character yet.</summary>
    public Dictionary<int, ExpeditionChest>? Chests { get; private set; }
    /// <summary>Bumped on every achievement or ticket change, live ones included (achievements.json / the Items cards refresh on it).</summary>
    public int ProgressVersion { get; private set; }
    public long UpdatedMs { get; private set; }
    /// <summary>The build lists of the last login and the latest stat sheet, each with its packet time.</summary>
    public CapturedProfile Captured { get; private set; } = CapturedProfile.Empty;

    public void OnEvent(GameEvent e)
    {
        lock (_gate)
        {
            var loginList = true;
            switch (e)
            {
                case PetListEvent p:
                    EquippedPet = p.EquippedPet;
                    Pets = p.Pets.ToDictionary(x => x.Id, x => x.Level);
                    break;
                case TitleListEvent t:
                    Titles = new HashSet<int>(t.Titles);
                    break;
                case AchievementListEvent a:
                    Achievements = a.Rows.ToDictionary(r => r.Id, r => r);
                    if (a.Groups is not null) AchievementGroups = a.Groups.ToList();
                    ProgressVersion++;
                    break;
                // Live changes about the character already bound: like 49 36, not login lists (no carry-over, no UpdatedMs bump)
                // copy-on-write: readers on other threads enumerate the dictionaries without the lock (2,327 rows, ~1 copy a minute)
                case AchievementUpdateEvent u:
                {
                    var d = new Dictionary<int, AchievementRow>(Achievements);
                    foreach (var r in u.Rows) d[r.Id] = r;
                    Achievements = d; ProgressVersion++; loginList = false;
                    break;
                }
                case AchievementClaimEvent c:
                {
                    var d = new Dictionary<int, AchievementRow>(Achievements);
                    foreach (var (_, id, step) in c.Claims)
                        d[id] = d.TryGetValue(id, out var old) ? old with { Step = step } : new AchievementRow(id, 0, c.TimeMs, step);
                    Achievements = d; ProgressVersion++; loginList = false;
                    break;
                }
                case ContentTicketsEvent t when t.Snapshot:
                    Tickets = t.Tickets.ToDictionary(x => x.Key, x => x);
                    ProgressVersion++;
                    break;
                case ExpeditionChestsEvent c:
                    Chests = c.Chests.ToDictionary(x => x.Dungeon, x => x);
                    ProgressVersion++;
                    break;
                case ContentTicketsEvent t:
                {
                    var d = new Dictionary<int, ContentTicket>(Tickets);
                    foreach (var x in t.Tickets) d[x.Key] = x;
                    Tickets = d; ProgressVersion++; loginList = false;
                    break;
                }
                case SkillListEvent s:
                    Captured = Captured with { SkillsMs = s.TimeMs, Skills = s.Skills.ToList() };
                    break;
                case EquippedSkillsEvent q:
                    Captured = Captured with { EquippedMs = q.TimeMs, Equipped = q.Skills.ToList() };
                    break;
                case SkillBarEvent k:
                    Captured = Captured with { BarMs = k.TimeMs, Bar = k.Keys.ToList() };
                    break;
                case DaevanionBoardsEvent d:
                    Captured = Captured with { DaevanionMs = d.TimeMs, Boards = d.Boards.ToList() };
                    break;
                case AttributesEvent at:
                    // Sent after every identity record (each zone load), always about the character already bound: it neither
                    // bumps UpdatedMs (titles/summary refresh on that) nor counts as a pending login list (it would carry the old
                    // character's lists over to the next one on a switch within 60 s).
                    Captured = Captured with { AttributesMs = at.TimeMs, Attributes = new Dictionary<int, int>(at.Stats) };
                    loginList = false;
                    break;
                default:
                    return;
            }
            if (loginList)
            {
                UpdatedMs = e.TimeMs;
                _lastListMs = Environment.TickCount64;
            }
            _dirty = true;
        }
        Changed?.Invoke();
    }

    public string? FilePath => _path;

    /// <summary>Switch to another character's file; login lists seen in the last 60 s belong to the new character and are carried over (see InventoryStore.Bind).</summary>
    public void Bind(string? path, string? mirror = null)
    {
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) { lock (_gate) { _mirror = mirror; _lastListMs = 0; } Save(); return; }
        bool keep;
        lock (_gate) { keep = ListsPending; _lastListMs = 0; }
        if (!keep) Save();
        lock (_gate)
        {
            _path = path; _mirror = mirror;
            // the stat sheet in memory is the previous character's (49 36 always follows the identity record, the new one comes next)
            if (keep) { _dirty = true; Captured = Captured with { AttributesMs = 0, Attributes = new() }; }
            else { EquippedPet = 0; Pets = new(); Titles = new(); Achievements = new(); AchievementGroups = new(); Tickets = new(); Chests = null; UpdatedMs = 0; Captured = CapturedProfile.Empty; Load(); _dirty = true; }
            ProgressVersion++;
        }
        Save();
        Changed?.Invoke();
    }

    private sealed record Snapshot(int EquippedPet, Dictionary<int, int> Pets, List<int> Titles, List<AchievementRow> Achievements, long UpdatedMs,
        CapturedProfile? Captured = null, List<AchievementGroup>? AchievementGroups = null, List<ContentTicket>? Tickets = null, List<ExpeditionChest>? Chests = null);

    // Only the fields ReadProgress needs: the serializer skips Captured/Pets/Titles (~150 KB) without building them.
    private sealed record ProgressSnapshot(List<AchievementRow>? Achievements, long UpdatedMs, List<ContentTicket>? Tickets, List<ExpeditionChest>? Chests = null);

    /// <summary>Achievements, tickets and UpdatedMs of another character's character.json (Items cards and the Tickets table); null when missing or unreadable.</summary>
    public static CharacterProgress? ReadProgress(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<ProgressSnapshot>(File.ReadAllBytes(path), Json);
            if (s is null) return null;
            var a = new Dictionary<int, AchievementRow>(s.Achievements?.Count ?? 0);
            if (s.Achievements is not null) foreach (var r in s.Achievements) a[r.Id] = r;
            var t = new Dictionary<int, ContentTicket>(s.Tickets?.Count ?? 0);
            if (s.Tickets is not null) foreach (var x in s.Tickets) t[x.Key] = x;
            return new CharacterProgress(a, t, s.UpdatedMs, s.Chests?.ToDictionary(x => x.Dungeon, x => x));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The captured part of a character.json (another character's file, for the selector view); Empty when missing.</summary>
    public static CapturedProfile ReadCaptured(string path)
    {
        try
        {
            if (!File.Exists(path)) return CapturedProfile.Empty;
            return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), Json)?.Captured ?? CapturedProfile.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return CapturedProfile.Empty; }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), Json);
            if (s is null) return;
            EquippedPet = s.EquippedPet; Pets = s.Pets; Titles = new HashSet<int>(s.Titles); Achievements = s.Achievements.ToDictionary(r => r.Id, r => r); UpdatedMs = s.UpdatedMs;
            Captured = s.Captured ?? CapturedProfile.Empty;
            AchievementGroups = s.AchievementGroups ?? new();
            Tickets = s.Tickets?.ToDictionary(x => x.Key, x => x) ?? new();
            Chests = s.Chests?.ToDictionary(x => x.Dungeon, x => x);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    private bool ListsPending => _lastListMs > 0 && Environment.TickCount64 - _lastListMs < 60_000;

    public void Save()
    {
        if (_path is null) return;
        lock (_gate) if (ListsPending) return;   // login lists not yet attributed to a character (see InventoryStore.LoginListPending)
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            text = JsonSerializer.Serialize(new Snapshot(EquippedPet, Pets, Titles.ToList(), Achievements.Values.ToList(), UpdatedMs, Captured, AchievementGroups, Tickets.Values.ToList(), Chests?.Values.ToList()), Json);
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
