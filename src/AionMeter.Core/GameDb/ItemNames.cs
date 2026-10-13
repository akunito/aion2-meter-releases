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

    // ── extras: ids the shipped catalogue lacks, looked up on questlog by ItemNameResolver ───────────────────────────────────
    // Copy-on-write: a batch builds a new dictionary and swaps the reference, so lookups (UI thread) never lock and never allocate.
    private readonly record struct Extra(string En, string Es, string EnKey, string EsKey, Grade Grade, bool Bound, string? IconUrl);
    private Dictionary<int, Extra> _extra = new();

    /// <summary>Raised (on the resolver's thread) after <see cref="AddExtras"/> added names; views coalesce it like any other change.</summary>
    public event Action? Changed;

    /// <summary>Adds names for ids missing from the catalogue (catalogue ids are left as they are) and raises <see cref="Changed"/>.</summary>
    public void AddExtras(IReadOnlyList<ExtraItem> items)
    {
        if (ReferenceEquals(this, Empty)) return;   // shared instance
        var d = new Dictionary<int, Extra>(_extra);
        var added = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var x = items[i];
            if (x.En.Length == 0 && x.Es.Length == 0 || Array.BinarySearch(_ids, x.Id) >= 0) continue;
            d[x.Id] = new Extra(x.En, x.Es, Normalize(x.En), Normalize(x.Es), x.Grade, x.Bound, x.IconUrl);
            added++;
        }
        if (added == 0) return;
        Volatile.Write(ref _extra, d);
        Changed?.Invoke();
    }

    public int ExtraCount => _extra.Count;

    /// <summary>True when the catalogue or the extras name the id.</summary>
    public bool Known(int id) => Array.BinarySearch(_ids, id) >= 0 || _extra.ContainsKey(id);

    /// <summary>Grade of an extra (Unknown otherwise): <see cref="ItemGrades.Of"/> falls back to it for ids item-grades.json lacks.</summary>
    public Grade ExtraGrade(int id) => _extra.TryGetValue(id, out var x) ? x.Grade : Grade.Unknown;
    public bool ExtraBound(int id) => _extra.TryGetValue(id, out var x) && x.Bound;
    /// <summary>questlog CDN icon of an extra, for ids item-icons.json lacks.</summary>
    public string? ExtraIconUrl(int id) => _extra.TryGetValue(id, out var x) ? x.IconUrl : null;

    public int Count => _ids.Length;
    public int CountWith(string lang) { var a = lang == "es" ? _es : _en; var n = 0; for (var i = 0; i < a.Length; i++) if (a[i].Length > 0) n++; return n; }

    public static ItemNames Load(string path)
    {
        // a fresh empty instance, not Empty: extras may still be added to it
        if (!File.Exists(path)) return new ItemNames(Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>());
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
        catch (Exception ex) when (ex is IOException or JsonException) { return new ItemNames(Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string>()); }
    }

    /// <summary>Name in <paramref name="lang"/> ("en"/"es"), falling back to English, or null for an unknown id.</summary>
    public string? Name(int id, string lang = "en")
    {
        var i = Array.BinarySearch(_ids, id);
        if (i < 0)
        {
            if (!_extra.TryGetValue(id, out var x)) return null;
            if (lang == "es" && x.Es.Length > 0) return x.Es;
            return x.En.Length > 0 ? x.En : null;
        }
        if (lang == "es" && _es[i].Length > 0) return _es[i];
        return _en[i].Length > 0 ? _en[i] : null;
    }

    public (string? En, string? Es) Both(int id)
    {
        var i = Array.BinarySearch(_ids, id);
        if (i >= 0) return (_en[i].Length > 0 ? _en[i] : null, _es[i].Length > 0 ? _es[i] : null);
        return _extra.TryGetValue(id, out var x) ? (x.En.Length > 0 ? x.En : null, x.Es.Length > 0 ? x.Es : null) : (null, null);
    }

    /// <summary>
    /// Items whose name in <paramref name="lang"/> contains every word of the query, or whose grade a word names ("legendary stone",
    /// "piedra legendaria"); ranked by the first word that hit the name: prefix, then word start, then anywhere; then shorter names.
    /// </summary>
    public List<(int Id, string En, string Es)> Search(string query, string lang = "en", int max = 30)
    {
        var result = new List<(int Id, string En, string Es)>();
        var q = ItemQuery.Parse(query);
        if (q.Text.Length < 2) return result;
        var words = q.Words;
        var keys = lang == "es" ? _esKey : _enKey;
        var grades = _grade;
        var scored = new List<(int Score, int Len, int Index)>();
        for (var i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            if (k.Length == 0) continue;
            var gb = grades.Length > i ? (byte)(1 << grades[i]) : (byte)1;
            string? lead = null;
            var ok = true;
            for (var w = 0; w < words.Length; w++)
            {
                if (k.Contains(words[w], StringComparison.Ordinal)) { lead ??= words[w]; continue; }
                if ((q.Masks[w] & gb) == 0) { ok = false; break; }
            }
            if (!ok) continue;
            var score = lead is null ? 3 : k.StartsWith(lead, StringComparison.Ordinal) ? 0 : k.Contains(" " + lead, StringComparison.Ordinal) ? 1 : 2;
            scored.Add((score, k.Length, i));
        }
        // extras: index past the catalogue arrays into exIds
        var ex = _extra;
        int[]? exIds = null;
        if (ex.Count > 0)
        {
            exIds = new int[ex.Count];
            var e = 0;
            foreach (var (id, x) in ex)
            {
                exIds[e] = id;
                var k = lang == "es" && x.EsKey.Length > 0 ? x.EsKey : x.EnKey;
                if (k.Length > 0)
                {
                    var gb = (byte)(1 << (int)x.Grade);
                    string? lead = null;
                    var ok = true;
                    for (var w = 0; w < words.Length; w++)
                    {
                        if (k.Contains(words[w], StringComparison.Ordinal)) { lead ??= words[w]; continue; }
                        if ((q.Masks[w] & gb) == 0) { ok = false; break; }
                    }
                    if (ok) scored.Add((lead is null ? 3 : k.StartsWith(lead, StringComparison.Ordinal) ? 0 : k.Contains(" " + lead, StringComparison.Ordinal) ? 1 : 2, k.Length, keys.Length + e));
                }
                e++;
            }
        }
        scored.Sort((a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Len != b.Len ? a.Len.CompareTo(b.Len) : a.Index.CompareTo(b.Index));
        for (var i = 0; i < scored.Count && i < max; i++)
        {
            var ix = scored[i].Index;
            if (ix < keys.Length) { result.Add((_ids[ix], _en[ix], _es[ix])); continue; }
            var id = exIds![ix - keys.Length];
            var x = ex[id];
            result.Add((id, x.En, x.Es));
        }
        return result;
    }

    // ── grades (A2M-29 data) ─────────────────────────────────────────────────────────────────────
    // Measured 2026-10-13 on 13,018 ids: 3,234 EN names are shared by 7,051 ids, 195 of them across grades (711 ids, e.g. "Artisan's
    // Refining Stone" rare 610530005 / legendary 610530008); ES 2,350 shared, 32 across grades (113 ids; the game's es-ES names split the
    // stones: "de experto" rare / "de artesano" legendary / "suprema" unique). The hub disambiguates with "(Grade: X)", "(Tradable: …)",
    // "(ID: n)" suffixes that item-names.py strips, so names stay as the game shows them and the grade goes next to them as a tag.
    private byte[] _grade = Array.Empty<byte>(), _amb = Array.Empty<byte>();

    /// <summary>Grade per id for tags and grade-word search, plus which names (EN bit 1, ES bit 2) another id of a different grade shares.</summary>
    public void AttachGrades(ItemGrades grades)
    {
        var gr = new byte[_ids.Length];
        for (var i = 0; i < gr.Length; i++) gr[i] = (byte)grades.Of(_ids[i]);
        var amb = new byte[_ids.Length];
        Mark(_en, null, 1);
        Mark(_es, _en, 2);
        _grade = gr; _amb = amb;
        if (!ReferenceEquals(grades, ItemGrades.Empty)) grades.Extras = this;

        void Mark(string[] names, string[]? fallback, byte bit)
        {
            var first = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
            var mixed = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < names.Length; i++)
            {
                var n = names[i].Length > 0 ? names[i] : fallback?[i] ?? "";
                if (n.Length == 0) continue;
                if (first.TryGetValue(n, out var j)) { if (gr[j] != gr[i]) mixed.Add(n); }
                else first[n] = i;
            }
            if (mixed.Count == 0) return;
            for (var i = 0; i < names.Length; i++)
            {
                var n = names[i].Length > 0 ? names[i] : fallback?[i] ?? "";
                if (n.Length > 0 && mixed.Contains(n)) amb[i] |= bit;
            }
        }
    }

    public Grade GradeOf(int id)
    {
        var i = Array.BinarySearch(_ids, id);
        if (i < 0) return ExtraGrade(id);
        return i < _grade.Length ? (Grade)_grade[i] : Grade.Unknown;
    }

    /// <summary>True when the name <see cref="Name"/> returns for <paramref name="lang"/> also belongs to an item of another grade.</summary>
    public bool Ambiguous(int id, string lang = "en") { var i = Array.BinarySearch(_ids, id); return i >= 0 && i < _amb.Length && (_amb[i] & (lang == "es" ? 2 : 1)) != 0; }

    /// <summary>Name as the game shows it plus the grade for the tag next to it; Name null for an unknown id.</summary>
    public (string? Name, Grade Grade) Display(int id, string lang = "en") => (Name(id, lang), GradeOf(id));

    /// <summary>Plain-text form for lists inside a line: "Artisan's Refining Stone (legendary)" only when the name alone is ambiguous.</summary>
    public string? Label(int id, string lang = "en")
    {
        var n = Name(id, lang);
        if (n is null || !Ambiguous(id, lang)) return n;
        var g = GradeOf(id);
        return g == Grade.Unknown ? n : n + " (" + ItemGrades.Word(g, lang) + ")";
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

/// <summary>
/// A normalised item query: every word must be in one of the names, or name the item's grade (<see cref="ItemGrades.WordMask"/>), so
/// "legendary stone" and "piedra legendaria" find the legendary stones whose name carries no grade. Shared by Prices, Craft and Items.
/// </summary>
public sealed class ItemQuery
{
    public static readonly ItemQuery Empty = new("", Array.Empty<string>(), Array.Empty<byte>());
    public string Text { get; }
    public string[] Words { get; }
    public byte[] Masks { get; }
    private ItemQuery(string text, string[] words, byte[] masks) { Text = text; Words = words; Masks = masks; }
    public bool IsEmpty => Words.Length == 0;

    public static ItemQuery Parse(string? text)
    {
        var q = ItemNames.Normalize(text ?? "");
        if (q.Length == 0) return Empty;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var masks = new byte[words.Length];
        for (var i = 0; i < words.Length; i++) masks[i] = ItemGrades.WordMask(words[i]);
        return new ItemQuery(q, words, masks);
    }

    /// <summary>Keys are already normalised (<see cref="ItemNames.Normalize"/>); empty keys are skipped.</summary>
    public bool Matches(Grade grade, string k1, string k2 = "", string k3 = "", string k4 = "")
    {
        var gb = (byte)(1 << (int)grade);
        for (var w = 0; w < Words.Length; w++)
        {
            var word = Words[w];
            if (k1.Contains(word, StringComparison.Ordinal) || (k2.Length > 0 && k2.Contains(word, StringComparison.Ordinal)) ||
                (k3.Length > 0 && k3.Contains(word, StringComparison.Ordinal)) || (k4.Length > 0 && k4.Contains(word, StringComparison.Ordinal))) continue;
            if ((Masks[w] & gb) == 0) return false;
        }
        return true;
    }
}
