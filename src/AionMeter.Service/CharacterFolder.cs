using System.Text.Json;
using AionMeter.Core.Builds;

namespace AionMeter.Service;

/// <summary>
/// A2M-48 phase 3c: a character's folder as the Linux UI reads it without touching the viewer engine's selection. MeterEngine.SelectCharacter
/// would rebind the live stores and Save() character.json / inventory.json over the daemon's files; the UI only needs the builds folder
/// (its own writes) and the profile (read through MeterEngine.KnownProfile).
/// </summary>
public static class CharacterFolder
{
    /// <summary>The character the daemon last saw logged in: "characterKey" of state/summary.json; null when missing or unreadable.</summary>
    public static string? CurrentKey(string appDataDir)
    {
        var path = Path.Combine(appDataDir, "state", "summary.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.TryGetProperty("characterKey", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() is { Length: > 0 } s ? s : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>"&lt;server&gt;-&lt;name&gt;" → (name, server); null for the shared folder ("") or anything else.</summary>
    public static (string Name, int Server)? ParseKey(string? key)
    {
        if (key is null || key.IndexOf('-') is not (> 0 and var dash) || dash == key.Length - 1) return null;
        return int.TryParse(key.AsSpan(0, dash), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var server) && server > 0
            ? (key[(dash + 1)..], server) : null;
    }

    /// <summary>characters/&lt;key&gt;/builds (the WPF BuildStore layout), or the shared builds/ folder for "".</summary>
    public static BuildStore Builds(string appDataDir, string key) =>
        new(key.Length == 0 ? Path.Combine(appDataDir, "builds") : Path.Combine(appDataDir, "characters", key, "builds"));

    /// <summary>The newest write time of the files a character's profile is made of (profile.json, character.json): a change means re-read.</summary>
    public static DateTime ProfileStamp(string appDataDir, string key)
    {
        if (key.Length == 0) return default;
        var dir = Path.Combine(appDataDir, "characters", key);
        var a = Stamp(Path.Combine(dir, "profile.json"));
        var b = Stamp(Path.Combine(dir, "character.json"));
        return a > b ? a : b;
    }

    private static DateTime Stamp(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }
}
