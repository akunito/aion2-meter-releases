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
    private readonly string? _path;
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
            _dirty = true;
        }
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

    public void Save()
    {
        if (_path is null) return;
        string text;
        lock (_gate)
        {
            if (!_dirty) return;
            text = JsonSerializer.Serialize(new Snapshot(EquippedPet, Pets, Titles.ToList(), Achievements.Values.ToList(), UpdatedMs), Json);
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
