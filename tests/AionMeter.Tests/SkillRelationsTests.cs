using AionMeter.Core.Builds;

namespace AionMeter.Tests;

public class SkillRelationsTests
{
    private const int Deadshot = 14010000, Snipe = 14020000, Griffon = 14060000, Suppressing = 14070000, Burst = 14080000, Marking = 14090000,
        Snare = 14130000, Illusory = 14150000, Vaizel = 14310000, Bow = 14220000, Support = 14380000, Explosive = 14360000, FocusedEye = 14740000;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "questlog", name));

    private static string SkillsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "data", "gamedb", "skills");
            if (File.Exists(Path.Combine(p, "ranger.json"))) return p;
        }
        Assert.Fail("data/gamedb/skills not found: run build/build.sh (it copies data/skills from the project root)");
        return "";
    }

    private static (IReadOnlyList<SkillInfo> Cat, Build Build) Ranger()
    {
        var book = new SkillBook(SkillsDir());
        return (book.ForClass("ranger"), QuestlogBuilds.ParseBuild(Fixture("irin-build.json"))!);
    }

    private static bool Has(SkillRelationSet set, RelationKind k, int from, int to) => set.All.Any(r => r.Kind == k && r.FromSkill == from && r.ToSkill == to);

    [Fact]
    public void Facts_read_buffs_states_and_spec_links_from_the_english_texts()
    {
        var (cat, _) = Ranger();
        SkillFacts F(int id) => SkillRelations.Facts(cat.First(s => s.Id == id));
        Assert.Equal(new[] { "Precision" }, F(Marking).Grants);
        Assert.Equal(new[] { "Precision" }, F(Deadshot).Uses);
        Assert.Equal(new[] { "Precision" }, F(Suppressing).Uses);
        Assert.Contains(("Crimson Flames", 0), F(Griffon).Inflicts);
        Assert.Contains(("Slow", 0), F(Snare).Inflicts);
        Assert.Contains(("Slow", 14150030), F(Illusory).Inflicts);           // spec "Inflicts Slow for 5s on hit"
        Assert.Equal(new[] { "Slow", "Root" }, F(Burst).Needs);
        Assert.False(F(Burst).NeedsIsBonus);
        Assert.True(F(Explosive).NeedsIsBonus);                               // "Increases damage by 20% to targets afflicted with …"
        Assert.Contains(("Deadshot", "-1s", 14020040), F(Snipe).Cooldowns);
        Assert.Contains(("Tempest Arrow", 14020050), F(Snipe).Chains);
        Assert.Contains(("Focused Eye", "x1.5", 14310020), F(Vaizel).Boosts);
    }

    [Fact]
    public void Ranger_relations_for_irins_build()
    {
        var (cat, b) = Ranger();
        var set = SkillRelations.Compute(cat, b, "en");
        // (a) Marking Shot grants Precision → Deadshot and Suppressing Arrow
        Assert.True(Has(set, RelationKind.Grants, Marking, Deadshot));
        Assert.True(Has(set, RelationKind.Grants, Marking, Suppressing));
        Assert.Contains(set.For(Deadshot), r => r.Kind == RelationKind.Consumes && r.Text == "Stronger with Precision from Marking Shot");
        Assert.Contains(set.For(Marking), r => r.Text == "Enables Deadshot: grants Precision");
        // (b) Snare Shot inflicts Slow → Burst Arrow needs Slow or Root
        Assert.True(Has(set, RelationKind.Applies, Snare, Burst));
        Assert.Contains(set.For(Burst), r => r.Kind == RelationKind.Needs && r.Text == "Needs Slow or Root: Snare Shot applies Slow");
        Assert.Contains(set.For(Explosive), r => r.Kind == RelationKind.Needs && r.Text.StartsWith("Bonus vs Slow or Root: Snare Shot"));
        // a spec-only state counts only when chosen: Illusory Arrow is not in Irin's build
        Assert.False(Has(set, RelationKind.Applies, Illusory, Burst));
        var withSpec = b with { Skills = [.. b.Skills, new BuildSkill(Illusory, 15, "4.0", [14150030])] };
        Assert.True(Has(SkillRelations.Compute(cat, withSpec, "en"), RelationKind.Applies, Illusory, Burst));
        // (c) Snipe's spec shortens Deadshot; chain skill outside the catalogue
        Assert.True(Has(set, RelationKind.Shortens, Snipe, Deadshot));
        Assert.Contains(set.For(Deadshot), r => r.Kind == RelationKind.ShortenedBy && r.FromSkill == Snipe);
        Assert.Contains(set.For(Snipe), r => r.Kind == RelationKind.ChainSkill && r.Text == "Chain skill Tempest Arrow (spec)" && r.ToSkill == 0);
        Assert.True(Has(set, RelationKind.Boosts, Vaizel, FocusedEye));
        // (d) the E chain in row order, on each of its skills
        const string e = "E chain: Supporting Fire → Vaizel's Authority → Bow of Blessing → Marking Shot";
        foreach (var id in new[] { Support, Vaizel, Bow, Marking })
            Assert.Contains(set.For(id), r => r.Kind == RelationKind.KeyChain && r.Text == e);
        // (e) priority rank inside the category chain
        Assert.Contains(set.For(Snipe), r => r.Kind == RelationKind.Priority && r.Text == "#3 in your priority");
        Assert.Contains(set.For(Vaizel), r => r.Kind == RelationKind.Priority && r.Text == "#1 in your priority");   // first stigma
        // no duplicates, nothing points at itself except chain/priority lines
        Assert.Equal(set.All.Count, set.All.Select(r => (r.Kind, r.FromSkill, r.ToSkill)).Distinct().Count());
        Assert.Equal(set.All.Count, set.All.Select(r => (r.Owner, r.Text)).Distinct().Count());
        Assert.All(set.All, r => Assert.True(r.FromSkill != r.ToSkill || r.Kind is RelationKind.KeyChain or RelationKind.Priority, r.Text));
    }

    [Fact]
    public void Relations_name_skills_in_the_skill_language_and_cache_per_build_edit()
    {
        var book = new SkillBook(SkillsDir());
        var b = QuestlogBuilds.ParseBuild(Fixture("irin-build.json"))!;
        var cache = new SkillRelationCache(book);
        var es = cache.Get("ranger", b, "es");
        var marking = book.Get(Marking)!;
        Assert.Contains(es.For(Deadshot), r => r.Kind == RelationKind.Consumes && r.Text == "Stronger with Precision from " + marking.Name("es"));
        Assert.Same(es, cache.Get("ranger", b, "es"));
        Assert.NotSame(es, cache.Get("ranger", b with { Priority = [] }, "es"));
        Assert.Empty(cache.Get("", b, "en").All);
        Assert.Empty(SkillRelations.Compute(book.ForClass("ranger"), null, "en").All.Where(r => r.Kind is RelationKind.KeyChain or RelationKind.Priority));
    }
}
