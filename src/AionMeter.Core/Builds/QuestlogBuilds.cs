using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AionMeter.Core.Net;

namespace AionMeter.Core.Builds;

public sealed record BuildSummary(long Id, string Name, string ClassId, string? Author, string? AuthorSlug, int LikeCount, DateTimeOffset? UpdatedAt,
                                  IReadOnlyList<string> Tags, bool HasDescription, Build Build);

public sealed record BuildSearchPage(List<BuildSummary> Builds, int Page, int PageCount, int TotalHits);

/// <summary>
/// Parsers for questlog.gg skill-builder JSON (tRPC GETs, no login). searchSkillBuilds returns result.data.pageData[] with
/// skills[] where specializations is only a COUNT; a single build (getSkillBuilderBySlug builds[], or {"build":…}) has skills as an
/// object keyed by skill id with the spec id list. Priority comes as ["id",…] in search results and [{"id":…},…] in saved builds.
/// </summary>
public static partial class QuestlogBuilds
{
    public const string Base = "https://questlog.gg/aion-2";

    public static string SearchUrl(string term, int page = 1) =>
        Base + "/api/trpc/skillBuilder.searchSkillBuilds?input=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { searchTerm = term ?? "", page = Math.Max(1, page) }));

    public static string BuildUrl(string userSlug, long buildId, string lang = "en") => $"{Base}/{lang}/skill-builder/{userSlug}?build-id={buildId}";

    [GeneratedRegex(@"questlog\.gg/aion-2/(?:[a-z]{2}(?:-[a-z]{2})?/)?skill-builder/([A-Za-z0-9_-]+)\?(?:[^#]*&)?build-id=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    /// <summary>(user slug, build id) from https://questlog.gg/aion-2/&lt;lang&gt;/skill-builder/&lt;slug&gt;?build-id=&lt;id&gt;; null when it is not one.</summary>
    public static (string Slug, long Id)? ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = UrlRx().Match(url.Trim());
        return m.Success && long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? (m.Groups[1].Value, id) : null;
    }

    public static List<BuildSummary> ParseSearch(string json) => ParseSearchPage(json).Builds;

    /// <summary>A searchSkillBuilds page, or a getSkillBuilderBySlug answer (user + builds[]); unknown shapes give an empty page.</summary>
    public static BuildSearchPage ParseSearchPage(string json)
    {
        var list = new List<BuildSummary>();
        int page = 1, pages = 0, hits = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var d = Unwrap(doc.RootElement);
            if (d.ValueKind == JsonValueKind.Object)
            {
                page = Int(d, "currentPage", 1); pages = Int(d, "pageCount", 0); hits = Int(d, "totalHits", 0);
                JsonElement? user = d.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : null;
                var arr = d.TryGetProperty("pageData", out var pd) ? pd : d.TryGetProperty("builds", out var bs) ? bs : default;
                if (arr.ValueKind == JsonValueKind.Array)
                    foreach (var b in arr.EnumerateArray())
                        if (Summary(b, user) is { } s) list.Add(s);
                if (pages == 0) { pages = 1; hits = list.Count; }
            }
        }
        catch (JsonException) { }
        return new BuildSearchPage(list, page, pages, hits);
    }

    /// <summary>One build: {"build":{…}}, a tRPC wrapper around it, or the bare object.</summary>
    public static Build? ParseBuild(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var d = Unwrap(doc.RootElement);
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.Object) d = b;
            return d.ValueKind == JsonValueKind.Object && d.TryGetProperty("skills", out _) ? ToBuild(d, null) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The build with this id inside a getSkillBuilderBySlug answer (what a pasted build URL resolves to).</summary>
    public static Build? FindBuild(string userBuildsJson, long buildId)
    {
        foreach (var s in ParseSearch(userBuildsJson)) if (s.Id == buildId) return s.Build;
        return null;
    }

    private static JsonElement Unwrap(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0) e = e[0];      // batched tRPC
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("result", out var r)) e = r;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("data", out var d)) e = d;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("json", out var j)) e = j; // superjson
        return e;
    }

    private static BuildSummary? Summary(JsonElement b, JsonElement? owner)
    {
        if (b.ValueKind != JsonValueKind.Object) return null;
        JsonElement? user = b.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : owner;
        var build = ToBuild(b, user);
        return new BuildSummary(build.Source!.Id, build.Name, build.ClassId, build.Source.Author, build.Source.Slug, Int(b, "likeCount", 0), Date(b, "updatedAt"),
                                Tags(b), b.TryGetProperty("hasDescription", out var hd) ? hd.ValueKind == JsonValueKind.True : Str(b, "description") is { Length: > 0 }, build);
    }

    private static Build ToBuild(JsonElement b, JsonElement? user)
    {
        var skills = new List<BuildSkill>();
        if (b.TryGetProperty("skills", out var sk))
        {
            if (sk.ValueKind == JsonValueKind.Object) foreach (var p in sk.EnumerateObject()) AddSkill(skills, p.Value, p.Name);
            else if (sk.ValueKind == JsonValueKind.Array) foreach (var x in sk.EnumerateArray()) AddSkill(skills, x, null);
        }
        var prio = new List<int>();
        if (b.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Array)
            foreach (var x in pr.EnumerateArray())
                if (Id(x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out var xi) ? xi : x) is { } pid) prio.Add(pid);
        var macros = new List<BuildMacro>();
        if (b.TryGetProperty("macros", out var ma) && ma.ValueKind == JsonValueKind.Array)
            foreach (var m in ma.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                var steps = new List<int>();
                if (m.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array)
                    foreach (var s in st.EnumerateArray())
                        if (Id(s.ValueKind == JsonValueKind.Object && s.TryGetProperty("skillId", out var si) ? si : s) is { } stepId) steps.Add(stepId);
                macros.Add(new BuildMacro(Str(m, "name") ?? "", steps));
            }
        var buildId = b.TryGetProperty("id", out var ie) ? Long(ie) : 0;
        return new Build
        {
            Name = Str(b, "name") ?? "",
            ClassId = (Str(b, "classId") ?? "").ToLowerInvariant(),
            Source = new BuildSource(buildId, user is { } us ? Str(us, "slug") : null, user is { } un ? Str(un, "name") : null),
            Skills = skills,
            Priority = prio,
            Macros = macros,
            Description = Str(b, "description"),
            UpdatedAt = Date(b, "updatedAt") ?? default,
        };
    }

    private static void AddSkill(List<BuildSkill> skills, JsonElement x, string? key)
    {
        if (x.ValueKind != JsonValueKind.Object) return;
        var id = (x.TryGetProperty("id", out var ie) ? Id(ie) : null) ?? (key is not null && int.TryParse(key, out var k) ? k : null);
        if (id is null) return;
        var specs = new List<int>();
        // search results carry only the number of chosen specs here, not which ones
        if (x.TryGetProperty("specializations", out var sp) && sp.ValueKind == JsonValueKind.Array)
            foreach (var s in sp.EnumerateArray()) if (Id(s) is { } sid) specs.Add(sid);
        skills.Add(new BuildSkill(id.Value, Int(x, "lvl", 1), Str(x, "slotId"), specs));
    }

    private static IReadOnlyList<string> Tags(JsonElement b)
    {
        if (!b.TryGetProperty("tags", out var t)) return [];
        if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } s)
        {
            try { using var d = JsonDocument.Parse(s); t = d.RootElement.Clone(); }
            catch (JsonException) { return [s]; }
        }
        if (t.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>(t.GetArrayLength());
        foreach (var x in t.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && x.GetString() is { } v) list.Add(v);
        return list;
    }

    private static int? Id(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var i) => i,
        JsonValueKind.String when int.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    private static long Long(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var i) => i,
        JsonValueKind.String when long.TryParse(e.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => 0,
    };

    private static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>GET-only questlog client: one request at a time, 400 ms apart (as <see cref="Armory.ArmoryClient"/>), Cloudflare fallback via <see cref="CloudflareHttp"/>.</summary>
public sealed class QuestlogClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastRequestMs;

    public QuestlogClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<BuildSearchPage> SearchAsync(string term, int page = 1, CancellationToken ct = default) =>
        QuestlogBuilds.ParseSearchPage(await GetAsync(QuestlogBuilds.SearchUrl(term, page), ct).ConfigureAwait(false));

    public async Task<string> GetAsync(string url, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = 400 - (Environment.TickCount64 - _lastRequestMs);
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastRequestMs = Environment.TickCount64;
            return await CloudflareHttp.GetStringAsync(_http, url, ct, expectJson: true).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
