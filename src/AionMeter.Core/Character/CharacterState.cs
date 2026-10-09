using System.Text.Json;
using AionMeter.Core.Events;

namespace AionMeter.Core.Character;

public sealed record PetOwned(int Id, int Level);
public sealed record AchievementRow(int Id, int State, long CompletedMs);

/// <summary>
/// The local character's collections as the server lists them at login (EU, 2026-10-08): pets with levels (<c>00 90</c>),
/// owned titles (<c>79 56</c>) and the achievement/collection rows (<c>83 56</c>). Saved as one JSON so the AI side can read it.
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

    public void OnEvent(GameEvent e)
    {
        lock (_gate)
        {
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
                default:
                    return;
            }
            UpdatedMs = e.TimeMs;
            _lastListMs = Environment.TickCount64;
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
            if (keep) _dirty = true;
            else { EquippedPet = 0; Pets = new(); Titles = new(); Achievements = new(); UpdatedMs = 0; Load(); _dirty = true; }
        }
        Save();
        Changed?.Invoke();
    }

    private sealed record Snapshot(int EquippedPet, Dictionary<int, int> Pets, List<int> Titles, List<AchievementRow> Achievements, long UpdatedMs);

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), Json);
            if (s is null) return;
            EquippedPet = s.EquippedPet; Pets = s.Pets; Titles = new HashSet<int>(s.Titles); Achievements = s.Achievements.ToDictionary(r => r.Id, r => r); UpdatedMs = s.UpdatedMs;
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
            text = JsonSerializer.Serialize(new Snapshot(EquippedPet, Pets, Titles.ToList(), Achievements.Values.ToList(), UpdatedMs), Json);
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
