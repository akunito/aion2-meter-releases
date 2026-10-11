namespace AionMeter.Core.Character;

/// <summary>What the Items tab needs from a character.json: achievement rows (pass levels), content tickets and the last login list time.</summary>
public sealed record CharacterProgress(IReadOnlyDictionary<int, AchievementRow> Achievements, IReadOnlyDictionary<int, ContentTicket> Tickets, long UpdatedMs);

/// <summary>Other characters' character.json, read-only, parsed again only when the file's write time changes (the Items tab reloads often).</summary>
public sealed class CharacterFiles
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Written, CharacterProgress? Progress)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CharacterProgress? Get(string path)
    {
        DateTime written;
        try { if (!File.Exists(path)) return null; written = File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        lock (_gate) if (_cache.TryGetValue(path, out var c) && c.Written == written) return c.Progress;
        var p = CharacterState.ReadProgress(path);
        lock (_gate) _cache[path] = (written, p);
        return p;
    }
}
