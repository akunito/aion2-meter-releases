using System.Text.Json;

namespace AionMeter.Core.Builds;

/// <summary>Slot = "key.row" (keys 1–8, 9 = Q, 10 = E, 11 = LMB, 12 = RMB; row 0 = first chain link) or null when not on the bar.</summary>
public sealed record BuildSkill(int SkillId, int Level, string? Slot, List<int> Specs);

public sealed record BuildMacro(string Name, List<int> Steps);

/// <summary>Where an imported build came from: questlog build id, the author's user slug (part of the build URL) and display name.</summary>
public sealed record BuildSource(long Id, string? Slug, string? Author);

/// <summary>The meter's own build of one character. Notes = per-skill "when to use / combos"; Daevanion = boardId → node ids planned open.</summary>
public sealed record Build
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ClassId { get; init; } = "";
    public BuildSource? Source { get; init; }
    /// <summary>The questlog daevanion-planner build the plan was imported from (skills and plans are separate tools on questlog; the meter keeps both in one build per character).</summary>
    public BuildSource? PlanSource { get; init; }
    public string? PlanName { get; init; }
    public List<BuildSkill> Skills { get; init; } = [];
    public List<int> Priority { get; init; } = [];
    public List<BuildMacro> Macros { get; init; } = [];
    public Dictionary<int, string> Notes { get; init; } = new();
    public Dictionary<int, List<int>> Daevanion { get; init; } = new();
    public string? Description { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public BuildSkill? Skill(int skillId)
    {
        foreach (var s in Skills) if (s.SkillId == skillId) return s;
        return null;
    }
}

/// <summary>
/// One JSON file per build in <c>directory</c> (&lt;id&gt;.json) plus the active build id in <c>activeFile</c>
/// (default: build-active.txt next to the directory, i.e. characters/&lt;key&gt;/build-active.txt for characters/&lt;key&gt;/builds).
/// </summary>
public sealed class BuildStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;
    private readonly string _activeFile;

    public BuildStore(string directory, string? activeFile = null)
    {
        _dir = directory;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))) ?? directory;
        _activeFile = activeFile ?? Path.Combine(parent, "build-active.txt");
    }

    public string Directory => _dir;

    public List<Build> List()
    {
        var list = new List<Build>();
        if (!System.IO.Directory.Exists(_dir)) return list;
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "*.json"))
            if (Read(f) is { } b) list.Add(b);
        list.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return list;
    }

    public Build? Load(string id) => ValidId(id) ? Read(Path.Combine(_dir, id + ".json")) : null;

    /// <summary>Writes the build (new id when it has none, UpdatedAt = now) and returns what was written.</summary>
    public Build Save(Build build)
    {
        var b = build with { Id = ValidId(build.Id) ? build.Id : Guid.NewGuid().ToString("N")[..12], UpdatedAt = DateTimeOffset.UtcNow };
        System.IO.Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, b.Id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(b, Json));
        File.Move(path + ".tmp", path, overwrite: true);
        return b;
    }

    /// <summary>Writes the build as it is (id and UpdatedAt kept): the backup restore puts back what the server holds.</summary>
    public bool Put(Build b)
    {
        if (!ValidId(b.Id)) return false;
        System.IO.Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, b.Id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(b, Json));
        File.Move(path + ".tmp", path, overwrite: true);
        return true;
    }

    public bool Delete(string id)
    {
        if (!ValidId(id)) return false;
        var path = Path.Combine(_dir, id + ".json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        if (ActiveId == id) ActiveId = null;
        return true;
    }

    public string? ActiveId
    {
        get
        {
            try { return File.Exists(_activeFile) && File.ReadAllText(_activeFile).Trim() is { Length: > 0 } s && ValidId(s) ? s : null; }
            catch (IOException) { return null; }
        }
        set
        {
            if (value is null) { if (File.Exists(_activeFile)) File.Delete(_activeFile); return; }
            if (!ValidId(value)) throw new ArgumentException("invalid build id", nameof(value));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_activeFile))!);
            File.WriteAllText(_activeFile, value);
        }
    }

    public Build? Active => ActiveId is { } id ? Load(id) : null;

    // ids become file names: letters, digits, '-' and '_' only
    public static bool ValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (var c in id) if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }

    public static Build? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Build>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
