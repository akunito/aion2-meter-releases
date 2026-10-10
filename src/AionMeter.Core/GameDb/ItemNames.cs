using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AionMeter.Core.GameDb;

/// <summary>
/// Offline item names by id and language from data/gamedb/item-names.json ({"id": ["en","es"]}, built from aion2hub.me's item list
/// and the official es-ES item endpoint). Search is accent- and case-insensitive on the chosen language; prefix matches first.
/// </summary>
public sealed class ItemNames
{
    public static readonly ItemNames Empty = new(Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>());
    private Dictionary<int, string> _sources = new();

    /// <summary>data/gamedb/item-sources.json ({"id": "gather; craft; drop 4% Draupnir (Hard)…"}, tools/item-sources.py from aion2hub).</summary>
    public void LoadSources(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var d = new Dictionary<int, string>(14_000);
            foreach (var p in doc.RootElement.EnumerateObject()) if (int.TryParse(p.Name, out var id)) d[id] = p.Value.GetString() ?? "";
            _sources = d;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    public string? Source(int id) => _sources.TryGetValue(id, out var s) ? s : null;

    private readonly int[] _ids;
    private readonly string[] _en, _es, _enKey, _esKey;

    private ItemNames(int[] ids, string[] en, string[] es)
    {
        _ids = ids; _en = en; _es = es;
        _enKey = new string[en.Length]; _esKey = new string[es.Length];
        for (var i = 0; i < en.Length; i++) { _enKey[i] = Normalize(en[i]); _esKey[i] = Normalize(es[i]); }
    }

    public int Count => _ids.Length;
    public int CountWith(string lang) { var a = lang == "es" ? _es : _en; var n = 0; for (var i = 0; i < a.Length; i++) if (a[i].Length > 0) n++; return n; }

    public static ItemNames Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rows = new List<(int Id, string En, string Es)>(13_500);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var id) || p.Value.ValueKind != JsonValueKind.Array) continue;
                var en = p.Value.GetArrayLength() > 0 ? p.Value[0].GetString() ?? "" : "";
                var es = p.Value.GetArrayLength() > 1 ? p.Value[1].GetString() ?? "" : "";
                rows.Add((id, en, es));
            }
            rows.Sort((a, b) => a.Id.CompareTo(b.Id));
            var ids = new int[rows.Count]; var ens = new string[rows.Count]; var ess = new string[rows.Count];
            for (var i = 0; i < rows.Count; i++) { ids[i] = rows[i].Id; ens[i] = rows[i].En; ess[i] = rows[i].Es; }
            return new ItemNames(ids, ens, ess);
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return Empty; }
    }

    /// <summary>Name in <paramref name="lang"/> ("en"/"es"), falling back to English, or null for an unknown id.</summary>
    public string? Name(int id, string lang = "en")
    {
        var i = Array.BinarySearch(_ids, id);
        if (i < 0) return null;
        if (lang == "es" && _es[i].Length > 0) return _es[i];
        return _en[i].Length > 0 ? _en[i] : null;
    }

    public (string? En, string? Es) Both(int id)
    {
        var i = Array.BinarySearch(_ids, id);
        return i < 0 ? (null, null) : (_en[i].Length > 0 ? _en[i] : null, _es[i].Length > 0 ? _es[i] : null);
    }

    /// <summary>Items whose name in <paramref name="lang"/> contains every word of the query; prefix hits first, then shorter names.</summary>
    public List<(int Id, string En, string Es)> Search(string query, string lang = "en", int max = 30)
    {
        var result = new List<(int Id, string En, string Es)>();
        var q = Normalize(query);
        if (q.Length < 2) return result;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keys = lang == "es" ? _esKey : _enKey;
        var scored = new List<(int Score, int Len, int Index)>();
        for (var i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            if (k.Length == 0) continue;
            var ok = true;
            foreach (var w in words) if (!k.Contains(w, StringComparison.Ordinal)) { ok = false; break; }
            if (!ok) continue;
            var score = k.StartsWith(words[0], StringComparison.Ordinal) ? 0 : k.Contains(" " + words[0], StringComparison.Ordinal) ? 1 : 2;
            scored.Add((score, k.Length, i));
        }
        scored.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Len != b.Len ? a.Len.CompareTo(b.Len) : a.Index.CompareTo(b.Index));
        for (var i = 0; i < scored.Count && i < max; i++) { var ix = scored[i].Index; result.Add((_ids[ix], _en[ix], _es[ix])); }
        return result;
    }

    /// <summary>Lower-case, accents stripped, punctuation to spaces, single spaces.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        var space = false;
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
            else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
        }
        return sb.ToString().TrimEnd();
    }
}
