using System.Text.Json;
using System.Text.Json.Serialization;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;

namespace AionMeter.Core.Storage;

/// <summary>One line of the history index: everything the pickers show without opening the fight file.</summary>
public sealed record HistoryEntry(
    string FileName,
    string Title,
    DateTimeOffset StartedAt,
    long CombatMs,
    EncounterEndReason Reason,
    double PartyDps,
    long TotalDamage,
    string? Zone,
    int BossCode,
    int TargetCode,
    int Players,
    string? SelfName,
    GameClass SelfClass,
    double SelfDps,
    int SelfPlace)
{
    [JsonIgnore] public string Path { get; init; } = "";
    public bool IsBoss => BossCode != 0;
}

/// <summary>
/// Every finished fight is one JSON file, so closing the app never loses a kill. A small <c>index.jsonl</c> beside
/// them keeps the summaries, so listing hundreds of fights never parses the (large) fight files themselves.
/// </summary>
public sealed class HistoryStore
{
    private const string IndexName = "index.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The fight files' format (one line, enums as text): the daemon's `fight-detail` socket reply uses it too (A2M-48).</summary>
    public static JsonSerializerOptions JsonOptions => Json;

    private readonly object _gate = new();
    private readonly List<HistoryEntry> _entries = new();

    public HistoryStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        LoadIndex();
    }

    public string Directory { get; }

    /// <summary>Raised after a fight is saved or deleted.</summary>
    public event Action? Changed;

    public string Save(FightRecord record)
    {
        var s = record.Summary;
        var name = $"{s.StartedAt:yyyyMMdd_HHmmss}_{Sanitize(s.Title)}.json";
        var path = System.IO.Path.Combine(Directory, name);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
        File.Move(tmp, path, overwrite: true);

        var entry = EntryFor(name, record) with { Path = path };
        lock (_gate)
        {
            _entries.RemoveAll(e => e.FileName == name);
            _entries.Add(entry);
            File.AppendAllText(IndexPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
        }
        Changed?.Invoke();
        return path;
    }

    public FightRecord? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<FightRecord>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryEntry> List(int max = int.MaxValue)
    {
        lock (_gate)
            return _entries.OrderByDescending(e => e.StartedAt).Take(max).ToList();
    }

    public void Delete(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetDirectoryName(full) != System.IO.Path.GetFullPath(Directory).TrimEnd('\\')) return;
        if (File.Exists(full)) File.Delete(full);
        lock (_gate)
        {
            _entries.RemoveAll(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
            WriteIndex();
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ index

    private string IndexPath => System.IO.Path.Combine(Directory, IndexName);

    private void LoadIndex()
    {
        var byName = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(IndexPath))
        {
            foreach (var line in File.ReadLines(IndexPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<HistoryEntry>(line, Json) is { } e) byName[e.FileName] = e;
                }
                catch (JsonException)
                {
                    // A torn last line after a crash: the reconcile below re-reads that file.
                }
            }
        }

        var dirty = false;
        var files = new DirectoryInfo(Directory).EnumerateFiles("*.json").ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in byName.Keys.Where(n => !files.ContainsKey(n)).ToList())
        {
            byName.Remove(gone);
            dirty = true;
        }
        foreach (var (name, file) in files)
        {
            if (byName.ContainsKey(name)) continue;
            if (Load(file.FullName) is not { } record) continue; // older fights saved before the index existed
            byName[name] = EntryFor(name, record);
            dirty = true;
        }

        _entries.AddRange(byName.Values.Select(e => e with { Path = System.IO.Path.Combine(Directory, e.FileName) }));
        if (dirty) WriteIndex();
    }

    private void WriteIndex()
    {
        var tmp = IndexPath + ".tmp";
        File.WriteAllLines(tmp, _entries.OrderBy(e => e.StartedAt).Select(e => JsonSerializer.Serialize(e, Json)));
        File.Move(tmp, IndexPath, overwrite: true);
    }

    private static HistoryEntry EntryFor(string fileName, FightRecord record)
    {
        var s = record.Summary;
        var selfIndex = -1;
        for (var i = 0; i < s.Combatants.Count; i++)
            if (s.Combatants[i].IsSelf) { selfIndex = i; break; }
        var self = selfIndex >= 0 ? s.Combatants[selfIndex] : null;
        return new HistoryEntry(
            fileName, s.Title, s.StartedAt, s.CombatMs, s.Reason, s.PartyDps, s.TotalDamage, s.Zone,
            s.Boss?.NpcCode ?? 0, s.TargetCode, s.PlayerCount,
            self?.Name, self?.Class ?? GameClass.Unknown, self?.Dps ?? 0, selfIndex + 1);
    }

    private static string Sanitize(string title)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = title.Select(ch => invalid.Contains(ch) || ch == ' ' ? '_' : ch).Take(48).ToArray();
        return chars.Length == 0 ? "fight" : new string(chars);
    }
}
