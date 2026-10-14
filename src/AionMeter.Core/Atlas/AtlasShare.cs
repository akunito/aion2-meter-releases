using System.Text;
using System.Text.Json;

namespace AionMeter.Core.Atlas;

/// <summary>
/// A2M-45: the object names the player answers, shared with Diego's server as telemetry kind "atlas" (Settings → Privacy, "Share the node names I
/// answer"). What the server acknowledged lives in atlas/objects-shared.json (code → category + name), written only by the process that sends (the
/// App on Windows, the daemon on Linux): a rename differs from it and goes again, and the first send after the switch is on carries every name
/// answered so far in one batch.
/// </summary>
public static class AtlasShare
{
    /// <summary>Category of an object the player said is not a gathering node (a door, a chest…): never drawn on the map, never asked again.</summary>
    public const string OtherCategory = "other";
    /// <summary>n8n's body limit is far above this; a backlog larger than one post goes on the next flush.</summary>
    public const int MaxPerPost = 500;

    public static bool IsOther(string category) => string.Equals(category, OtherCategory, StringComparison.OrdinalIgnoreCase);

    public static string Stamp(string name, string category) => category + "\u001f" + name;

    /// <summary>Codes whose current name differs from what the server last acknowledged, lowest first, at most <paramref name="max"/>.</summary>
    public static List<int> Pending(IReadOnlyDictionary<int, (string Name, string Category)> names, IReadOnlyDictionary<int, string> sent, int max = MaxPerPost)
    {
        var codes = new List<int>(names.Count);
        foreach (var (code, (name, category)) in names)
            if (name.Length > 0 && (!sent.TryGetValue(code, out var was) || was != Stamp(name, category))) codes.Add(code);
        codes.Sort();
        if (codes.Count > max) codes.RemoveRange(max, codes.Count - max);
        return codes;
    }

    public readonly record struct Node(int Code, string Name, string Category, int[] Maps);

    /// <summary>{"kind":"atlas","install","ts","nodes":[{"code","category","name","maps":[map ids]}]} — nothing else.</summary>
    public static string Payload(string install, DateTimeOffset now, IReadOnlyList<Node> nodes)
    {
        using var ms = new MemoryStream(64 + nodes.Count * 64);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("kind", "atlas");
            w.WriteString("install", install);
            w.WriteString("ts", now.UtcDateTime.ToString("o"));
            w.WriteStartArray("nodes");
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                w.WriteStartObject();
                w.WriteNumber("code", n.Code);
                w.WriteString("category", n.Category);
                w.WriteString("name", n.Name);
                w.WriteStartArray("maps");
                foreach (var m in n.Maps) w.WriteNumberValue(m);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static Dictionary<int, string> LoadSent(string path)
    {
        var result = new Dictionary<int, string>();
        try
        {
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                if (int.TryParse(p.Name, out var code) && p.Value.ValueKind == JsonValueKind.String) result[code] = p.Value.GetString() ?? "";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    public static void SaveSent(string path, Dictionary<int, string> sent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var f = File.Create(tmp))
        using (var w = new Utf8JsonWriter(f))
        {
            w.WriteStartObject();
            foreach (var (code, stamp) in sent) w.WriteString(code.ToString(System.Globalization.CultureInfo.InvariantCulture), stamp);
            w.WriteEndObject();
        }
        File.Move(tmp, path, overwrite: true);
    }
}
