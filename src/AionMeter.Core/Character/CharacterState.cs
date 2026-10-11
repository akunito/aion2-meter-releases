using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Character;

public sealed record PetOwned(int Id, int Level);
public sealed record AchievementRow(int Id, int State, long CompletedMs);

/// <summary>
/// The local character's collections as the server lists them at login (EU, 2026-10-08): pets with levels (<c>00 90</c>),
/// owned titles (<c>79 56</c>) and the achievement/collection rows (<c>83 56</c>), plus (A2M-35) the build lists — skills
/// (<c>00 51</c>), equipped specs (<c>00 39</c>), skill bar (<c>8c e2</c>), Daevanion nodes (<c>26 e2</c>) — and the stat sheet
/// (<c>49 36</c>) as <see cref="Captured"/>. Saved as one JSON so the AI side can read it.
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
                    break;
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
            else { EquippedPet = 0; Pets = new(); Titles = new(); Achievements = new(); UpdatedMs = 0; Captured = CapturedProfile.Empty; Load(); _dirty = true; }
        }
        Save();
        Changed?.Invoke();
    }

    private sealed record Snapshot(int EquippedPet, Dictionary<int, int> Pets, List<int> Titles, List<AchievementRow> Achievements, long UpdatedMs,
        CapturedProfile? Captured = null);

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
            text = JsonSerializer.Serialize(new Snapshot(EquippedPet, Pets, Titles.ToList(), Achievements.Values.ToList(), UpdatedMs, Captured), Json);
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
