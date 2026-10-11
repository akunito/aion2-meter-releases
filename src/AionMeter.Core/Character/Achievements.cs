using System.Text.Json;

namespace AionMeter.Core.Character;

/// <summary>
/// One row of <c>AchievementList_NT</c> (<c>83 56</c>) / <c>AchievementCountUpdate_NT</c> (<c>84 56</c>): <paramref name="Count"/> is the progress
/// counter (cumulative across tiers), <paramref name="CompletedMs"/> when the counter last moved (0 = never), <paramref name="Step"/> how many
/// tiers have been claimed (<c>_rewarded_step</c>). Measured 2026-10-11 (docs/27): the in-game window's "completed" counter is the sum of Step over
/// the achievement rows (464 → 466 → 467 → 468 across four logins, the 467 seen in the window on 10-10 22:32).
/// </summary>
public sealed record AchievementRow(int Id, int Count, long CompletedMs, int Step);

/// <summary>A tab/sub-tab reward track of the achievement window (<c>_group_reward_states</c>): group 3000001xx…3000005xx, one flag per phase.</summary>
public sealed record AchievementGroup(int Id, bool[] Claimed);

public sealed record AchievementTier(int Goal, int Points, (int Item, int Amount)[] Rewards);

/// <summary>Catalogue entry (questlog, tools/achievements.py). Ids are the wire ids. Main: achievement, seasonmission, battlepass, attendance, unlock.</summary>
public sealed record AchievementInfo(int Id, string NameEn, string NameEs, string Main, string Sub, string Group, string DescEn, string DescEs, AchievementTier[] Tiers)
{
    public string Name(string lang) => lang == "es" && NameEs.Length > 0 ? NameEs : NameEn;
    public string Desc(string lang) => lang == "es" && DescEs.Length > 0 ? DescEs : DescEn;
}

public enum AchievementState { NotStarted, InProgress, Claimable, Done, Unknown }

/// <summary>Where a row stands against its catalogue tiers. NextTier is 1-based (0 when every tier is claimed); Goal is that tier's goal.</summary>
public readonly record struct AchievementProgress(AchievementRow Row, AchievementInfo? Info, AchievementState State, int Tiers, int NextTier, int Goal, int ClaimableTiers)
{
    public double Percent => Goal <= 0 ? 0 : Math.Min(1.0, (double)Row.Count / Goal);
}

public sealed record AchievementSummary(int Achievements, int TiersTotal, int TiersClaimed, int Completed, int Claimable, int InProgress, int Unmatched, List<AchievementProgress> ClaimableList);

/// <summary>data/gamedb/achievements.json (tools/achievements.py) by id.</summary>
public sealed class AchievementCatalog
{
    private readonly Dictionary<int, AchievementInfo> _byId;

    public AchievementCatalog(Dictionary<int, AchievementInfo> byId) => _byId = byId;

    public static readonly AchievementCatalog Empty = new(new());

    public int Count => _byId.Count;
    public AchievementInfo? Get(int id) => _byId.GetValueOrDefault(id);

    public static AchievementCatalog Load(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllBytes(path)) : Empty; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException) { return Empty; }
    }

    /// <summary><c>[{"i":id,"n":{en,es},"m":main,"c":sub,"g":group,"d":{en,es},"t":[[goal,points,[[item,amount],…]],…]},…]</c></summary>
    public static AchievementCatalog Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var map = new Dictionary<int, AchievementInfo>(doc.RootElement.GetArrayLength());
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("i").GetInt32();
            var n = e.GetProperty("n"); var d = e.TryGetProperty("d", out var dd) ? dd : default;
            var tiersEl = e.GetProperty("t");
            var tiers = new AchievementTier[tiersEl.GetArrayLength()];
            var k = 0;
            foreach (var t in tiersEl.EnumerateArray())
            {
                var rw = t[2];
                var rewards = new (int, int)[rw.GetArrayLength()];
                var j = 0;
                foreach (var r in rw.EnumerateArray()) rewards[j++] = (r[0].GetInt32(), r[1].GetInt32());
                tiers[k++] = new AchievementTier(t[0].GetInt32(), t[1].GetInt32(), rewards);
            }
            map[id] = new AchievementInfo(id, Str(n, "en"), Str(n, "es"), Str(e, "m"), Str(e, "c"), Str(e, "g"),
                d.ValueKind == JsonValueKind.Object ? Str(d, "en") : "", d.ValueKind == JsonValueKind.Object ? Str(d, "es") : "", tiers);
        }
        return new AchievementCatalog(map);

        static string Str(JsonElement o, string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    public static AchievementProgress Progress(AchievementRow r, AchievementInfo? info)
    {
        if (info is null || info.Tiers.Length == 0) return new(r, info, AchievementState.Unknown, 0, 0, 0, 0);
        var n = info.Tiers.Length;
        var step = Math.Clamp(r.Step, 0, n);
        if (step >= n) return new(r, info, AchievementState.Done, n, 0, info.Tiers[n - 1].Goal, 0);
        var claimable = 0;
        for (var i = step; i < n && info.Tiers[i].Goal <= r.Count; i++) claimable++;
        var goal = info.Tiers[step].Goal;
        var state = claimable > 0 ? AchievementState.Claimable : r.Count > 0 || step > 0 ? AchievementState.InProgress : AchievementState.NotStarted;
        return new(r, info, state, n, step + 1, goal, claimable);
    }

    /// <summary>The achievement window's view (catalogue main = "achievement"): tiers claimed/total and per-achievement states; claimable sorted by id.</summary>
    public AchievementSummary Summarise(IEnumerable<AchievementRow> rows)
    {
        int count = 0, total = 0, claimed = 0, done = 0, claimable = 0, progress = 0, unmatched = 0;
        var list = new List<AchievementProgress>();
        foreach (var r in rows)
        {
            var info = Get(r.Id);
            if (info is null) { if (r.Id is >= 300_000_000 and < 500_000_000) unmatched++; continue; }
            if (info.Main != "achievement") continue;
            var p = Progress(r, info);
            count++; total += p.Tiers; claimed += Math.Min(r.Step, p.Tiers);
            switch (p.State)
            {
                case AchievementState.Done: done++; break;
                case AchievementState.Claimable: claimable++; list.Add(p); break;
                case AchievementState.InProgress: progress++; break;
            }
        }
        list.Sort((a, b) => a.Row.Id.CompareTo(b.Row.Id));
        return new AchievementSummary(count, total, claimed, done, claimable, progress, unmatched, list);
    }
}

/// <summary>
/// characters/&lt;key&gt;/achievements.json for the AI side: the window summary, every claimable achievement with its rewards, the ones nearest to
/// their next tier, the tab reward tracks and one line per row (id, name, progress, claimed step, tiers, next goal, state, last change).
/// </summary>
public static class AchievementStore
{
    private static readonly JsonWriterOptions Indent = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Write(string path, IReadOnlyDictionary<int, AchievementRow> rows, IReadOnlyList<AchievementGroup> groups, AchievementCatalog catalog,
        Func<int, string?> itemName, DateTimeOffset now)
    {
        var sorted = new List<AchievementRow>(rows.Values);
        sorted.Sort((a, b) => a.Id.CompareTo(b.Id));
        var sum = catalog.Summarise(sorted);
        var near = new List<AchievementProgress>(64);
        foreach (var r in sorted)
        {
            var p = AchievementCatalog.Progress(r, catalog.Get(r.Id));
            if (p.State == AchievementState.InProgress && p.Info!.Main == "achievement") near.Add(p);
        }
        near.Sort((a, b) => b.Percent.CompareTo(a.Percent));
        if (near.Count > 40) near.RemoveRange(40, near.Count - 40);

        using var ms = new MemoryStream(256 * 1024);
        using (var w = new Utf8JsonWriter(ms, Indent))
        {
            w.WriteStartObject();
            w.WriteString("at", now.ToString("O"));
            w.WriteString("note", "count = progress counter (cumulative over tiers); claimedStep = tiers claimed; the game's completed counter = tiersClaimed. Ids = questlog achievement ids.");
            w.WriteStartObject("summary");
            w.WriteNumber("achievements", sum.Achievements); w.WriteNumber("tiersTotal", sum.TiersTotal); w.WriteNumber("tiersClaimed", sum.TiersClaimed);
            w.WriteNumber("completed", sum.Completed); w.WriteNumber("claimable", sum.Claimable); w.WriteNumber("inProgress", sum.InProgress);
            w.WriteNumber("rowsWithoutCatalogue", sum.Unmatched);
            w.WriteEndObject();
            w.WriteStartArray("claimable");
            foreach (var p in sum.ClaimableList) WriteProgress(w, p, itemName, rewards: true);
            w.WriteEndArray();
            w.WriteStartArray("nearest");
            foreach (var p in near) WriteProgress(w, p, itemName, rewards: true);
            w.WriteEndArray();
            w.WriteStartArray("groups");
            foreach (var g in groups)
            {
                w.WriteStartObject(); w.WriteNumber("id", g.Id);
                w.WriteStartArray("claimed"); foreach (var f in g.Claimed) w.WriteBooleanValue(f); w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("rows");
            foreach (var r in sorted)
            {
                var info = catalog.Get(r.Id);
                if (info is null && r.Count == 0 && r.Step == 0) continue;
                WriteProgress(w, AchievementCatalog.Progress(r, info), itemName, rewards: false);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", ms.ToArray());
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static void WriteProgress(Utf8JsonWriter w, AchievementProgress p, Func<int, string?> itemName, bool rewards)
    {
        var r = p.Row;
        w.WriteStartObject();
        w.WriteNumber("id", r.Id);
        if (p.Info is { } i) { w.WriteString("name", i.NameEn); if (i.NameEs.Length > 0) w.WriteString("nameEs", i.NameEs); w.WriteString("cat", i.Main + "/" + i.Sub); }
        w.WriteNumber("count", r.Count);
        w.WriteNumber("claimedStep", r.Step);
        if (p.Tiers > 0) { w.WriteNumber("tiers", p.Tiers); if (p.NextTier > 0) { w.WriteNumber("nextTier", p.NextTier); w.WriteNumber("goal", p.Goal); } }
        w.WriteString("state", p.State.ToString());
        if (p.ClaimableTiers > 0) w.WriteNumber("claimableTiers", p.ClaimableTiers);
        if (r.CompletedMs > 0) w.WriteString("updatedAt", DateTimeOffset.FromUnixTimeMilliseconds(r.CompletedMs).ToLocalTime().ToString("O"));
        if (rewards && p.Info is { } info && p.NextTier > 0)
        {
            if (info.DescEn.Length > 0) w.WriteString("desc", info.DescEn);
            w.WriteStartArray("rewards");
            foreach (var (item, amount) in info.Tiers[p.NextTier - 1].Rewards)
            {
                w.WriteStartObject(); w.WriteNumber("item", item); w.WriteNumber("amount", amount);
                if (itemName(item) is { } n) w.WriteString("name", n);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
}
