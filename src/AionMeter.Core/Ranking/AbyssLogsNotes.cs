using System.Text.Json;

namespace AionMeter.Core.Ranking;

/// <summary>
/// The Abyss Logs line of each fight by its id, kept on disk (newest <see cref="Keep"/>) so a saved fight's breakdown
/// still shows it after a restart: the fight file is written before the percentile arrives, so it cannot carry it.
/// </summary>
public sealed class AbyssLogsNotes(string? path)
{
    public const int Keep = 300;
    private readonly Dictionary<Guid, string> _notes = Load(path);
    private readonly List<Guid> _order = [];

    public string? Get(Guid fight)
    {
        lock (_notes) return _notes.TryGetValue(fight, out var n) ? n : null;
    }

    public void Set(Guid fight, string note)
    {
        Dictionary<Guid, string>? copy = null;
        lock (_notes)
        {
            if (_order.Count == 0 && _notes.Count > 0) _order.AddRange(_notes.Keys);
            if (!_notes.ContainsKey(fight)) _order.Add(fight);
            _notes[fight] = note;
            while (_order.Count > Keep) { _notes.Remove(_order[0]); _order.RemoveAt(0); }
            if (path is not null) copy = new Dictionary<Guid, string>(_notes);
        }
        if (copy is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(copy));
            File.Move(path + ".tmp", path!, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static Dictionary<Guid, string> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<Guid, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return [];
    }
}
