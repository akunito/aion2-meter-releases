using System.Net;
using System.Text;
using System.Text.Json;

namespace AionMeter.Core.GameDb;

/// <summary>A name the shipped catalogue lacks, as stored in item-names-extra.json and merged by <see cref="ItemNames.AddExtras"/>.</summary>
public sealed record ExtraItem(int Id, string En, string Es, Grade Grade, bool Bound, string? Icon)
{
    public string? IconUrl => Icon is null ? null : ItemNameResolver.IconUrl(Icon);
}

/// <summary>One getItem answer: the fields the meter keeps.</summary>
public sealed record QuestlogItem(string Name, int GradeCode, string? Icon, bool Bound);

/// <summary>
/// Names for item ids missing from data/gamedb/item-names.json (aion2hub's 13,018 items lack Global-only tickets, bound variants and new
/// gear), asked from questlog.gg's public tRPC once per id from the player's own PC and cached forever in
/// %AppData%\AionMeter\item-names-extra.json. questlog's ToS forbids scrapers: never walk ids, only ask for what the player owns.
/// One request at a time, ≥400 ms apart; an id that failed or is unknown is not asked again in the session.
/// </summary>
public sealed class ItemNameResolver : IDisposable
{
    // GET <Endpoint><urlencoded {"id":"600590001","language":"en"}> (id as a string, raw input, no {"json":…} wrapper) →
    // {"result":{"data":{id,name,icon,grade,mainCategory,subCategory,exchangeType,…}}}. An id questlog does not know answers
    // HTTP 500 {"error":{"message":"Cannot read properties of null (reading 'equipment_info')"…}} (2026-10-13), not data null.
    public const string Endpoint = "https://questlog.gg/aion-2/api/trpc/database.getItem?input=";
    // icon "/assets/Game/UI/Resource/Texture/Item/ETC/Icon_X.Icon_X" → cdn + "/assets/…/Icon_X.webp" (verified 2026-10-13: .png and bare 404)
    public const string IconCdn = "https://cdn.questlog.gg/aion-2";

    private static readonly JsonSerializerOptions WriteJson = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly ItemNames _names;
    private readonly string? _path;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly Queue<int> _queue = new();
    private readonly HashSet<int> _asked = new();
    private readonly Dictionary<int, ExtraItem> _file = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private long _lastRequestMs = long.MinValue / 2;

    /// <summary>Off = no network (render tool); cached extras still load.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Gap between requests; tests set 0.</summary>
    public int PaceMs { get; init; } = 400;
    public int Requests { get; private set; }

    public ItemNameResolver(ItemNames names, string? path, HttpMessageHandler? handler = null)
    {
        _names = names;
        _path = path;
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.3 (private build; one questlog lookup per unknown item id, cached)");
        var loaded = Load(path);
        foreach (var x in loaded) _file[x.Id] = x;
        if (loaded.Count > 0) _names.AddExtras(loaded);
    }

    /// <summary>Queues a lookup when no name is known for <paramref name="id"/>. Cheap enough for a render path: a binary search and two set probes.</summary>
    public void Want(int id)
    {
        if (id <= 0 || !Enabled || _names.Known(id)) return;
        lock (_gate)
        {
            if (!_asked.Add(id)) return;
            _queue.Enqueue(id);
            if (_worker is { IsCompleted: false }) return;
            _worker = Task.Run(WorkAsync);
        }
    }

    /// <summary>Completes when the queue is empty (tests).</summary>
    public Task IdleAsync() { lock (_gate) return _worker ?? Task.CompletedTask; }

    private async Task WorkAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            int id;
            lock (_gate)
            {
                if (_queue.Count == 0) { _worker = null; return; }
                id = _queue.Dequeue();
            }
            var en = await FetchAsync(id, "en").ConfigureAwait(false);
            if (en is null) continue;
            var es = await FetchAsync(id, "es").ConfigureAwait(false);
            var x = new ExtraItem(id, en.Name, es?.Name ?? "", GradeOf(en.GradeCode), en.Bound, en.Icon);
            lock (_gate) _file[id] = x;
            Save();
            _names.AddExtras(new[] { x });
        }
    }

    private async Task<QuestlogItem?> FetchAsync(int id, string lang)
    {
        try
        {
            var wait = PaceMs - (Environment.TickCount64 - _lastRequestMs);
            if (wait > 0) await Task.Delay((int)wait, _cts.Token).ConfigureAwait(false);
            _lastRequestMs = Environment.TickCount64;
            Requests++;
            using var resp = await _http.GetAsync(Url(id, lang), _cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return Parse(await resp.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException) { return null; }
    }

    public static string Url(int id, string lang) =>
        Endpoint + Uri.EscapeDataString("{\"id\":\"" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\",\"language\":\"" + lang + "\"}");

    /// <summary>The item in a getItem answer, or null (data null, no name, an error body, broken JSON).</summary>
    public static QuestlogItem? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var r) || !r.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
            if (!d.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() is not { Length: > 0 } name) return null;
            var grade = d.TryGetProperty("grade", out var g) && g.ValueKind == JsonValueKind.Number && g.TryGetInt32(out var gi) ? gi : 0;
            var icon = d.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
            var ex = d.TryGetProperty("exchangeType", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            // "soulbind" = binds on equip (the hub calls those tradable); "soulbind_acquire" and "nonexchangeable" are what item-grades.json marks " b"
            return new QuestlogItem(name, grade, icon, ex is "nonexchangeable" or "soulbind_acquire");
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// questlog grade codes, measured 2026-10-13 against item-grades.json on 2,977 ids of the cached questlog dumps with no conflict:
    /// 11 common (876), 21 rare (327), 31 legendary (578), 41 unique (1,186), 71 special (10). No epic item was found on questlog
    /// (ten epic ids all answer 500), so epic's code is unknown and anything else maps to Unknown.
    /// </summary>
    public static Grade GradeOf(int code) => code switch { 11 => Grade.Common, 21 => Grade.Rare, 31 => Grade.Legendary, 41 => Grade.Unique, 71 => Grade.Special, _ => Grade.Unknown };

    public static string IconUrl(string icon)
    {
        var slash = icon.LastIndexOf('/');
        var dot = icon.IndexOf('.', slash + 1);
        return IconCdn + (dot < 0 ? icon : icon[..dot]) + ".webp";
    }

    /// <summary>item-names-extra.json: {"id": ["en", "es", "grade", "b"|"", "icon path"]}.</summary>
    public static List<ExtraItem> Load(string? path)
    {
        var list = new List<ExtraItem>();
        if (path is null || !File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var id) || p.Value.ValueKind != JsonValueKind.Array) continue;
                var a = p.Value;
                var len = a.GetArrayLength();
                string S(int i) => i < len && a[i].ValueKind == JsonValueKind.String ? a[i].GetString() ?? "" : "";
                var icon = S(4);
                list.Add(new ExtraItem(id, S(0), S(1), ItemGrades.ParseGrade(S(2)), S(3) == "b", icon.Length > 0 ? icon : null));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return list;
    }

    public static string Serialize(IEnumerable<ExtraItem> items)
    {
        var d = new SortedDictionary<int, string[]>();
        foreach (var x in items) d[x.Id] = new[] { x.En, x.Es, ItemGrades.Name(x.Grade), x.Bound ? "b" : "", x.Icon ?? "" };
        return JsonSerializer.Serialize(d, WriteJson);
    }

    private void Save()
    {
        if (_path is null) return;
        string text;
        lock (_gate) text = Serialize(_file.Values);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", text, new UTF8Encoding(false));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
