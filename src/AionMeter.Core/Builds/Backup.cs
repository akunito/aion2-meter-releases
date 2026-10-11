using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AionMeter.Core.Market;

namespace AionMeter.Core.Builds;

/// <summary>
/// A2M-30 backup without accounts: the install id (32 hex) is the backup code. The server only ever sees
/// <see cref="CharHash"/>, never a character name; a restore adopts the code and recomputes the hashes of the local characters.
/// </summary>
public static class BackupCode
{
    /// <summary>"3F9C-0A12-…": 8 groups of 4, upper case; anything that is not 32 hex comes back unchanged.</summary>
    public static string Format(string installId)
    {
        if (Parse(installId) is not { } id) return installId;
        return string.Create(39, id, static (span, src) =>
        {
            var j = 0;
            for (var i = 0; i < 32; i++)
            {
                if (i > 0 && (i & 3) == 0) span[j++] = '-';
                span[j++] = char.ToUpperInvariant(src[i]);
            }
        });
    }

    /// <summary>Lower-case 32 hex from what the user typed or pasted (dashes, spaces and case ignored); null when it is not a code.</summary>
    public static string? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        Span<char> buf = stackalloc char[32];
        var n = 0;
        foreach (var c in text)
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            if (!char.IsAsciiHexDigit(c) || n == 32) return null;
            buf[n++] = char.ToLowerInvariant(c);
        }
        return n == 32 ? new string(buf) : null;
    }

    /// <summary>First 16 hex (lower case) of SHA-256(installId + ":" + characterKey); "" for the shared folder (no character key).</summary>
    public static string CharHash(string installId, string characterKey)
    {
        if (characterKey.Length == 0) return "";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(installId + ":" + characterKey), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }
}

public sealed record RestoredBuild(Build Build, string CharHash, DateTimeOffset Ts);

public sealed record RestoredState(string Kind, JsonElement Data, DateTimeOffset Ts);

public sealed record RestorePayload(List<RestoredBuild> Builds, List<RestoredState> State);

/// <summary>The build telemetry payload (Telemetry.BuildPayload) read back from the restore endpoint.</summary>
public static class BuildTelemetryJson
{
    /// <summary>
    /// {ok, builds:[{build_id, charhash, class, name, source, level, skills, priority, macros, daevanion, notes, ts}], state:[{kind, data, ts}]}.
    /// The JSON-valued fields arrive as the strings n8n stored or as objects; the newest row per build id / state kind wins.
    /// </summary>
    public static RestorePayload ParseRestore(string json)
    {
        var builds = new Dictionary<string, RestoredBuild>(StringComparer.Ordinal);
        var state = new Dictionary<string, RestoredState>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("builds", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var row in arr.EnumerateArray())
                if (FromRestore(row) is { } r && (!builds.TryGetValue(r.Build.Id, out var have) || have.Ts < r.Ts)) builds[r.Build.Id] = r;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var row in st.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(row, "kind");
                if (kind.Length == 0 || Value(row, "data") is not { ValueKind: JsonValueKind.Object } data) continue;
                var ts = Time(row);
                if (!state.TryGetValue(kind, out var prev) || prev.Ts < ts) state[kind] = new RestoredState(kind, data.Clone(), ts);
            }
        var list = new List<RestoredBuild>(builds.Values);
        list.Sort(static (a, b) => b.Ts.CompareTo(a.Ts));
        return new RestorePayload(list, [.. state.Values]);
    }

    /// <summary>One restore row → the meter's Build (Id = build_id, UpdatedAt = ts); null when it has no usable id.</summary>
    public static RestoredBuild? FromRestore(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var id = Str(row, "build_id");
        if (!BuildStore.ValidId(id)) return null;
        var ts = Time(row);
        var skills = new List<BuildSkill>();
        if (Value(row, "skills") is { ValueKind: JsonValueKind.Array } sk)
            foreach (var s in sk.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var skillId = Int(s, "skillId");
                if (skillId <= 0) continue;
                var slot = s.TryGetProperty("slot", out var sl) && sl.ValueKind == JsonValueKind.String ? sl.GetString() : null;
                skills.Add(new BuildSkill(skillId, Int(s, "level"), slot, Ints(s.TryGetProperty("specs", out var sp) ? sp : default)));
            }
        var macros = new List<BuildMacro>();
        if (Value(row, "macros") is { ValueKind: JsonValueKind.Array } mc)
            foreach (var m in mc.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object) macros.Add(new BuildMacro(Str(m, "name"), Ints(m.TryGetProperty("steps", out var steps) ? steps : default)));
        var notes = new Dictionary<int, string>();
        if (Value(row, "notes") is { ValueKind: JsonValueKind.Object } nt)
            foreach (var p in nt.EnumerateObject())
                if (int.TryParse(p.Name, out var k) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } text) notes[k] = text;
        var daev = new Dictionary<int, List<int>>();
        if (Value(row, "daevanion") is { ValueKind: JsonValueKind.Object } dv)
            foreach (var p in dv.EnumerateObject())
                if (int.TryParse(p.Name, out var board)) daev[board] = Ints(p.Value);
        BuildSource? source = null;
        var src = Str(row, "source");
        if (src.StartsWith("questlog:", StringComparison.Ordinal) && long.TryParse(src.AsSpan(9), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var qid) && qid > 0)
            source = new BuildSource(qid, null, null);
        var build = new Build
        {
            Id = id,
            Name = Str(row, "name"),
            ClassId = Str(row, "class"),
            Source = source,
            Skills = skills,
            Priority = Ints(Value(row, "priority")),
            Macros = macros,
            Notes = notes,
            Daevanion = daev,
            UpdatedAt = ts,
        };
        var hash = Str(row, "charhash");
        return new RestoredBuild(build, hash.Length == 16 && IsHex(hash) ? hash.ToLowerInvariant() : "", ts);
    }

    /// <summary>The server copy goes in unless a local build with the same id was changed after the copy was sent.</summary>
    public static bool RestoreWins(Build? local, DateTimeOffset ts) => local is null || local.UpdatedAt <= ts;

    // A field that is a JSON string holding JSON (what n8n stored) is parsed; an object/array is taken as it is.
    private static JsonElement Value(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v)) return default;
        if (v.ValueKind != JsonValueKind.String) return v;
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s)) return default;
        try { using var d = JsonDocument.Parse(s); return d.RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" } : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            _ => 0,
        } : 0;

    private static List<int> Ints(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return [];
        var list = new List<int>(e.GetArrayLength());
        foreach (var x in e.EnumerateArray()) if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var i)) list.Add(i);
        return list;
    }

    private static DateTimeOffset Time(JsonElement row) =>
        row.TryGetProperty("ts", out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;

    private static bool IsHex(string s) { foreach (var c in s) if (!char.IsAsciiHexDigit(c)) return false; return true; }
}

/// <summary>
/// kind "watch": the small per-user lists the game cannot give back — {v:1, watch:[{item, name, max, sell, qty, paid}], craftFavourites:[ids]}.
/// The poll fields of a watch (last price, listings…) are left out: the next poll refills them.
/// </summary>
public static class BackupState
{
    public const string Kind = "watch";
    public const int MaxBytes = 20 * 1024;

    public static string ToJson(IReadOnlyList<PriceWatch> watches, IReadOnlyList<int> craftFavourites)
    {
        for (var take = watches.Count; ; take = take * 3 / 4)
        {
            var json = Write(watches, take, craftFavourites);
            if (Encoding.UTF8.GetByteCount(json) <= MaxBytes || take == 0) return json;
        }
    }

    private static string Write(IReadOnlyList<PriceWatch> watches, int take, IReadOnlyList<int> favs)
    {
        using var ms = new MemoryStream(256 + take * 96 + favs.Count * 10);
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteStartArray("watch");
            for (var i = 0; i < take; i++)
            {
                var x = watches[i];
                w.WriteStartObject();
                w.WriteNumber("item", x.Item);
                w.WriteString("name", x.Name);
                w.WriteNumber("max", x.MaxPrice);
                if (x.Sell) w.WriteBoolean("sell", true);
                if (x.Quantity != 0) w.WriteNumber("qty", x.Quantity);
                if (x.PaidEach != 0) w.WriteNumber("paid", x.PaidEach);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("craftFavourites");
            for (var i = 0; i < favs.Count && i < 500; i++) w.WriteNumberValue(favs[i]);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static (List<PriceWatch> Watch, List<int> CraftFavourites) Parse(JsonElement data)
    {
        var watch = new List<PriceWatch>();
        var favs = new List<int>();
        if (data.ValueKind != JsonValueKind.Object) return (watch, favs);
        if (data.TryGetProperty("watch", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("item", out var it) || !it.TryGetInt32(out var item) || item <= 0) continue;
                watch.Add(new PriceWatch
                {
                    Item = item,
                    Name = x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
                    MaxPrice = x.TryGetProperty("max", out var m) && m.TryGetInt64(out var mv) ? mv : 0,
                    Sell = x.TryGetProperty("sell", out var s) && s.ValueKind == JsonValueKind.True,
                    Quantity = x.TryGetProperty("qty", out var q) && q.TryGetInt32(out var qv) ? qv : 0,
                    PaidEach = x.TryGetProperty("paid", out var p) && p.TryGetInt64(out var pv) ? pv : 0,
                });
            }
        if (data.TryGetProperty("craftFavourites", out var fa) && fa.ValueKind == JsonValueKind.Array)
            foreach (var f in fa.EnumerateArray()) if (f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var id) && !favs.Contains(id)) favs.Add(id);
        return (watch, favs);
    }

    /// <summary>A restored list goes in only where the local one is empty: never clobbers what the player has on this PC.</summary>
    public static bool Takes<T>(ICollection<T> local, ICollection<T> restored) => local.Count == 0 && restored.Count > 0;
}

/// <summary>Where restored builds land: the character whose hash matches, else builds-restored/&lt;charhash&gt;/ until it logs in.</summary>
public static class BackupPlacement
{
    public const string WaitingDir = "builds-restored";

    public readonly record struct Result(int Placed, int Waiting, int KeptLocal);

    /// <summary>appData = %AppData%\AionMeter (characters/&lt;key&gt;/builds, builds/ for the shared folder, builds-restored/).</summary>
    public static Result Place(IEnumerable<RestoredBuild> builds, string appData, string installId)
    {
        var byHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var chars = Path.Combine(appData, "characters");
        if (Directory.Exists(chars))
            foreach (var d in Directory.EnumerateDirectories(chars))
                if (Path.GetFileName(d) is { Length: > 0 } key) byHash[BackupCode.CharHash(installId, key)] = key;
        int placed = 0, waiting = 0, kept = 0;
        foreach (var r in builds)
        {
            string dir; var onChar = true;
            if (r.CharHash.Length == 0) dir = Path.Combine(appData, "builds");
            else if (byHash.TryGetValue(r.CharHash, out var key)) dir = Path.Combine(chars, key, "builds");
            else { dir = Path.Combine(appData, WaitingDir, r.CharHash); onChar = false; }
            var store = new BuildStore(dir);
            if (!BuildTelemetryJson.RestoreWins(store.Load(r.Build.Id), r.Ts)) { kept++; continue; }
            store.Put(r.Build);
            if (onChar) placed++; else waiting++;
        }
        return new Result(placed, waiting, kept);
    }

    /// <summary>Moves the builds waiting for this character into its builds folder (a newer local copy wins); returns how many moved.</summary>
    public static int Adopt(string appData, string installId, string characterKey)
    {
        if (installId.Length == 0 || characterKey.Length == 0) return 0;
        var waiting = Path.Combine(appData, WaitingDir);
        if (!Directory.Exists(waiting)) return 0;
        var src = Path.Combine(waiting, BackupCode.CharHash(installId, characterKey));
        if (!Directory.Exists(src)) return 0;
        var store = new BuildStore(Path.Combine(appData, "characters", characterKey, "builds"));
        var moved = 0;
        foreach (var f in Directory.EnumerateFiles(src, "*.json"))
        {
            if (BuildStore.Read(f) is { } b && BuildTelemetryJson.RestoreWins(store.Load(b.Id), b.UpdatedAt) && store.Put(b)) moved++;
            File.Delete(f);
        }
        try { Directory.Delete(src, recursive: true); if (!Directory.EnumerateFileSystemEntries(waiting).Any()) Directory.Delete(waiting); }
        catch (IOException) { }
        return moved;
    }
}
