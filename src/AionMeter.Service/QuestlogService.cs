using System.IO;
using System.Net.Http;
using AionMeter.Core.Builds;

namespace AionMeter.Service;

/// <summary>
/// The app's one questlog client (one request at a time, 400 ms apart) and its session caches: search pages per (term, page, class)
/// for the session, suggestions per class for 6 h. Everything here runs off the UI thread; callers await it.
/// </summary>
public static class QuestlogService
{
    public static readonly QuestlogClient Client = new();
    public static readonly TimeSpan SuggestionsTtl = TimeSpan.FromHours(6);
    private const int SuggestPages = 3;

    private static readonly Dictionary<(string Term, int Page, string Class), BuildSearchPage> Pages = new();
    private static readonly Dictionary<string, (DateTimeOffset At, List<BuildSummary> List)> Suggestions = new();
    private static readonly Dictionary<string, Task<List<BuildSummary>>> SuggestionsRunning = new();

    public static async Task<BuildSearchPage> SearchAsync(string term, int page, string? classId)
    {
        var key = (term.Trim(), Math.Max(1, page), classId ?? "");
        lock (Pages) if (Pages.TryGetValue(key, out var hit)) return hit;
        var p = await Task.Run(() => Client.SearchAsync(key.Item1, key.Item2, classId)).ConfigureAwait(false);
        lock (Pages) Pages[key] = p;
        return p;
    }

    /// <summary>Cached suggestions of a class, or null when they still have to be fetched (<see cref="SuggestionsAsync"/>).</summary>
    public static List<BuildSummary>? CachedSuggestions(string classId)
    {
        lock (Suggestions) return Suggestions.TryGetValue(classId, out var c) && DateTimeOffset.UtcNow - c.At < SuggestionsTtl ? c.List : null;
    }

    /// <summary>
    /// The 5 most-liked public builds of the class: empty-term pages 1..3 (server-side class filter, sorted by likes) ranked again
    /// client-side, stopping once a page holds fewer likes than the 5th best. One fetch per class at a time; errors propagate.
    /// </summary>
    public static Task<List<BuildSummary>> SuggestionsAsync(string classId)
    {
        if (CachedSuggestions(classId) is { } hit) return Task.FromResult(hit);
        lock (SuggestionsRunning)
        {
            if (SuggestionsRunning.TryGetValue(classId, out var running)) return running;
            var t = Task.Run(async () =>
            {
                try
                {
                    var all = new List<BuildSummary>(64);
                    for (var page = 1; page <= SuggestPages; page++)
                    {
                        var p = await SearchAsync("", page, classId).ConfigureAwait(false);
                        all.AddRange(p.Builds);
                        var ranked = BuildSuggestions.Rank(all, classId);
                        if (page >= p.PageCount || (ranked.Count == BuildSuggestions.Take && MaxLikes(p.Builds) <= ranked[^1].LikeCount)) break;
                    }
                    var list = BuildSuggestions.Rank(all, classId);
                    lock (Suggestions) Suggestions[classId] = (DateTimeOffset.UtcNow, list);
                    return list;
                }
                finally { lock (SuggestionsRunning) SuggestionsRunning.Remove(classId); }
            });
            SuggestionsRunning[classId] = t;
            return t;
        }
    }

    private static int MaxLikes(List<BuildSummary> list)
    {
        var m = 0;
        foreach (var b in list) if (b.LikeCount > m) m = b.LikeCount;
        return m;
    }

    /// <summary>The user's own questlog builds: skill builds (full, specs included) and planner builds, fetched together.</summary>
    public sealed record MyBuilds(List<BuildSummary> Skills, List<QuestlogDaevPlan> Plans, DateTimeOffset At);

    private static readonly Dictionary<string, MyBuilds> Mine = new(StringComparer.Ordinal);

    /// <summary>Cached lists of this slug for the session, or null when they still have to be fetched.</summary>
    public static MyBuilds? CachedMine(string slug)
    {
        lock (Mine) return Mine.GetValueOrDefault(slug);
    }

    /// <summary>getSkillBuilderBySlug + getDaevanionPlannerBySlug of one user (two GETs on the shared client), cached per slug for the session; <paramref name="refresh"/> refetches. Errors propagate.</summary>
    public static async Task<MyBuilds> MineAsync(string slug, bool refresh = false)
    {
        if (!refresh && CachedMine(slug) is { } hit) return hit;
        var r = await Task.Run(async () =>
        {
            var skills = QuestlogBuilds.ParseSearch(await Client.GetAsync(QuestlogBuilds.UserBuildsUrl(slug)).ConfigureAwait(false));
            var plans = DaevanionPlan.ParsePlannerList(await Client.GetAsync(DaevanionPlan.PlannerApiUrl(slug)).ConfigureAwait(false));
            return new MyBuilds(skills, plans, DateTimeOffset.UtcNow);
        }).ConfigureAwait(false);
        lock (Mine) Mine[slug] = r;
        return r;
    }

    /// <summary>A questlog link was imported: its author becomes the user's slug when none is set yet. True when the setting changed.</summary>
    public static bool LearnSlug(AppSettings settings, string? slug)
    {
        if (!string.IsNullOrEmpty(settings.QuestlogSlug) || string.IsNullOrEmpty(slug)) return false;
        settings.QuestlogSlug = slug;
        try { settings.Save(); } catch (IOException) { }
        Log.Info($"questlog user learnt from a link: {slug}");
        return true;
    }

    /// <summary>The full build (specs included) of a search result or a pasted link.</summary>
    public static Task<Build?> FetchAsync(string slug, long id) => Task.Run(() => Client.FetchBuildAsync(slug, id));

    /// <summary>Short reason for the status line.</summary>
    public static string Reason(Exception ex) => ex switch
    {
        TaskCanceledException => "timeout",
        HttpRequestException h when h.StatusCode is { } c => ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => ex.Message,
    };
}
