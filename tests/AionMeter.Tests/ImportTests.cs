using System.Text.Json;
using AionMeter.Core.Builds;

namespace AionMeter.Tests;

/// <summary>A2M-21 phase 3: questlog import (link → author's builds → one build), suggestions ranking, the "Copy for questlog" format.</summary>
public class ImportTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "questlog", name));

    [Fact]
    public void Build_links_parse_in_every_shape()
    {
        Assert.Equal<(string, long)?>(("AXIIyLxFWMU0", 15969L), QuestlogBuilds.ParseUrl("https://questlog.gg/aion-2/en/skill-builder/AXIIyLxFWMU0?build-id=15969"));
        Assert.Equal<(string, long)?>(("AXIIyLxFWMU0", 15969L), QuestlogBuilds.ParseUrl("questlog.gg/aion-2/skill-builder/AXIIyLxFWMU0?build-id=15969"));          // no scheme, no language
        Assert.Equal<(string, long)?>(("a_b-C9", 3L), QuestlogBuilds.ParseUrl("https://www.questlog.gg/aion-2/pt-br/skill-builder/a_b-C9?x=1&build-id=3"));
        Assert.Equal<(string, long)?>(("AXIIyLxFWMU0", 16051L), QuestlogBuilds.ParseUrl("HTTPS://QUESTLOG.GG/AION-2/ES/SKILL-BUILDER/AXIIyLxFWMU0?BUILD-ID=16051"));
        Assert.Null(QuestlogBuilds.ParseUrl("https://questlog.gg/aion-2/en/skill-builder/AXIIyLxFWMU0?build-id=abc"));
        Assert.Null(QuestlogBuilds.ParseUrl("https://questlog.gg/aion-2/en/talent-builder/AXIIyLxFWMU0?build-id=1"));
        Assert.Null(QuestlogBuilds.ParseUrl("   "));

        var url = QuestlogBuilds.UserBuildsUrl("AXIIyLxFWMU0");
        Assert.StartsWith("https://questlog.gg/aion-2/api/trpc/skillBuilder.getSkillBuilderBySlug?input=", url);
        Assert.Equal("""{"slug":"AXIIyLxFWMU0"}""", Uri.UnescapeDataString(url[(url.IndexOf("input=") + 6)..]));
        var search = QuestlogBuilds.SearchUrl("", 3, "cleric");
        Assert.Equal("""{"searchTerm":"","page":3,"classId":"cleric"}""", Uri.UnescapeDataString(search[(search.IndexOf("input=") + 6)..]));
    }

    [Fact]
    public void FindBuild_takes_one_build_out_of_the_authors_list()
    {
        var json = Fixture("user-builds.json");   // getSkillBuilderBySlug {"slug":"AXIIyLxFWMU0"}, captured 2026-10-11
        var all = QuestlogBuilds.ParseSearch(json);
        Assert.Equal(7, all.Count);
        Assert.All(all, s => Assert.Equal("AXIIyLxFWMU0", s.AuthorSlug));

        var b = QuestlogBuilds.FindBuild(json, 15969)!;
        Assert.Equal("ranger", b.ClassId);
        Assert.StartsWith("Irin", b.Name);
        Assert.Equal(new BuildSource(15969, "AXIIyLxFWMU0", "akunito88"), b.Source);
        Assert.True(b.Skills.Count >= 20, $"{b.Skills.Count} skills");
        Assert.Contains(b.Skills, s => s.Specs.Count > 0 && s.Specs.All(id => id / 10000 == s.SkillId / 10000));   // real spec ids, not a count
        Assert.Contains(b.Skills, s => s.Slot is not null);
        Assert.NotEmpty(b.Priority);

        Assert.Equal("templar", QuestlogBuilds.FindBuild(json, 13277)!.ClassId);
        Assert.Null(QuestlogBuilds.FindBuild(json, 1));
        Assert.Null(QuestlogBuilds.FindBuild("""{"result":{"data":null}}""", 15969));   // unknown author slug
    }

    [Fact]
    public void Questlog_export_round_trips_through_the_publish_format()
    {
        var b = QuestlogBuilds.FindBuild(Fixture("user-builds.json"), 15969)!;
        var json = QuestlogExport.ToJson(b);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal(b.Name, r.GetProperty("name").GetString());
        Assert.Equal("ranger", r.GetProperty("classId").GetString());
        Assert.Equal(JsonValueKind.Array, r.GetProperty("tags").ValueKind);
        Assert.Equal(b.Skills.Count, r.GetProperty("skills").GetArrayLength());

        // what tools/questlog-publish.js sends to saveBuild, rebuilt here: it must parse back into the same build
        var skills = new Dictionary<string, object?>();
        foreach (var s in r.GetProperty("skills").EnumerateArray())
        {
            var id = s.GetProperty("skillId").GetInt32().ToString();
            skills[id] = new Dictionary<string, object?>
            {
                ["id"] = id, ["lvl"] = s.GetProperty("level").GetInt32(),
                ["slotId"] = s.GetProperty("slot").ValueKind == JsonValueKind.Null ? null : s.GetProperty("slot").GetString(),
                ["specializations"] = s.GetProperty("specs").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            };
        }
        var saved = new Dictionary<string, object?>
        {
            ["id"] = 15969, ["name"] = r.GetProperty("name").GetString(), ["classId"] = r.GetProperty("classId").GetString(), ["skills"] = skills,
            ["priority"] = r.GetProperty("priority").EnumerateArray().Select(x => new { id = x.GetInt32().ToString() }).ToArray(),
            ["macros"] = r.GetProperty("macros").EnumerateArray().Select(m => new
            {
                name = m.GetProperty("name").GetString(),
                steps = m.GetProperty("steps").EnumerateArray().Select(x => new { delayMs = 0, skillId = x.GetInt32().ToString() }).ToArray(),
            }).ToArray(),
            ["description"] = r.GetProperty("description").GetString(),
        };
        var back = QuestlogBuilds.ParseBuild(JsonSerializer.Serialize(new { build = saved }))!;
        Assert.Equal(b.Name, back.Name);
        Assert.Equal(b.ClassId, back.ClassId);
        Assert.Equal(b.Priority, back.Priority);
        Assert.Equal(b.Description, back.Description);
        Assert.Equal(b.Skills.Count, back.Skills.Count);
        for (var i = 0; i < b.Skills.Count; i++)
        {
            Assert.Equal(b.Skills[i].SkillId, back.Skills[i].SkillId);
            Assert.Equal(b.Skills[i].Level, back.Skills[i].Level);
            Assert.Equal(b.Skills[i].Slot, back.Skills[i].Slot);
            Assert.Equal(b.Skills[i].Specs, back.Skills[i].Specs);
        }
        Assert.Equal(b.Macros.Count, back.Macros.Count);
        for (var i = 0; i < b.Macros.Count; i++) { Assert.Equal(b.Macros[i].Name, back.Macros[i].Name); Assert.Equal(b.Macros[i].Steps, back.Macros[i].Steps); }

        var empty = QuestlogExport.ToJson(new Build { Name = "x \"quoted\" ñ", ClassId = "cleric" });
        Assert.Equal("""{"name":"x \"quoted\" ñ","classId":"cleric","skills":[],"priority":[],"macros":[],"tags":[],"description":""}""", empty);
    }

    [Fact]
    public void Suggestions_keep_the_class_sort_by_likes_and_take_five()
    {
        static BuildSummary S(long id, string cls, int likes, int day = 1) =>
            new(id, "b" + id, cls, "a", "slug", likes, new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), [], false, new Build());
        var list = new List<BuildSummary>
        {
            S(1, "ranger", 3), S(2, "cleric", 99), S(3, "ranger", 71), S(4, "Ranger", 10), S(5, "ranger", 0), S(6, "ranger", 10, 5),
            S(7, "ranger", 25), S(3, "ranger", 71), S(8, "ranger", 1),
        };
        var top = BuildSuggestions.Rank(list, "ranger");
        Assert.Equal(new long[] { 3, 7, 6, 4, 1 }, top.Select(b => b.Id));   // 6 and 4 tie on likes: the newer first; duplicate 3 once
        Assert.Empty(BuildSuggestions.Rank(list, "gladiator"));
        Assert.Single(BuildSuggestions.Rank(list, "cleric"));

        // a real search page: every row of the class, most liked first
        var page = QuestlogBuilds.ParseSearchPage(Fixture("search.json"));
        var ranked = BuildSuggestions.Rank(page.Builds, "ranger");
        Assert.InRange(ranked.Count, 1, 5);
        for (var i = 1; i < ranked.Count; i++) Assert.True(ranked[i - 1].LikeCount >= ranked[i].LikeCount);
    }

    [Fact]
    public void Import_again_replaces_the_copy_and_keeps_notes_and_daevanion()
    {
        var b = QuestlogBuilds.FindBuild(Fixture("user-builds.json"), 15969)!;
        var fresh = QuestlogImport.Prepare(b, []);
        Assert.Equal("", fresh.Id);
        var mine = new Build { Id = "abc123", Name = "old", ClassId = "ranger", Source = b.Source, Notes = { [14010000] = "opener" }, Daevanion = { [1] = [2, 3] } };
        var again = QuestlogImport.Prepare(b, [new Build { Id = "other", Source = new BuildSource(1, null, null) }, mine]);
        Assert.Equal("abc123", again.Id);
        Assert.Equal(b.Name, again.Name);
        Assert.Equal("opener", again.Notes[14010000]);
        Assert.Equal(new[] { 2, 3 }, again.Daevanion[1]);
        Assert.Equal("ranger", QuestlogImport.Prepare(b with { ClassId = "" }, []).ClassId);   // class from the skill ids
    }
}
