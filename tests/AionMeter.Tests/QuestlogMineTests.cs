using AionMeter.Core.Builds;

namespace AionMeter.Tests;

/// <summary>A2M-21 "My questlog builds": user slug from links, skill build ↔ planner build pairing, the newer-version check.</summary>
public class QuestlogMineTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "questlog", name));

    [Fact]
    public void User_slug_comes_from_both_link_kinds_and_a_bare_slug()
    {
        Assert.Equal("AXIIyLxFWMU0", QuestlogMine.ParseUserSlug("https://questlog.gg/aion-2/en/skill-builder/AXIIyLxFWMU0?build-id=15969"));
        Assert.Equal("AXIIyLxFWMU0", QuestlogMine.ParseUserSlug("https://questlog.gg/aion-2/es/daevanion-planner/AXIIyLxFWMU0?build-id=8602"));
        Assert.Equal("AXIIyLxFWMU0", QuestlogMine.ParseUserSlug("questlog.gg/aion-2/skill-builder/AXIIyLxFWMU0"));        // the user's page, no build id
        Assert.Equal("a_b-C9", QuestlogMine.ParseUserSlug("HTTPS://WWW.QUESTLOG.GG/AION-2/PT-BR/DAEVANION-PLANNER/a_b-C9?x=1&build-id=3"));
        Assert.Equal("AXIIyLxFWMU0", QuestlogMine.ParseUserSlug("  AXIIyLxFWMU0 "));
        Assert.Null(QuestlogMine.ParseUserSlug("https://questlog.gg/aion-2/en/talent-builder/AXIIyLxFWMU0?build-id=1"));
        Assert.Null(QuestlogMine.ParseUserSlug("https://example.com/AXIIyLxFWMU0"));
        Assert.Null(QuestlogMine.ParseUserSlug("not a slug"));
        Assert.Null(QuestlogMine.ParseUserSlug(""));
        Assert.Null(QuestlogMine.ParseUserSlug(null));
    }

    [Fact]
    public void Planner_list_parses_every_build_with_date_and_tags()
    {
        var plans = DaevanionPlan.ParsePlannerList(Fixture("user-planner.json"));   // getDaevanionPlannerBySlug {"slug":"AXIIyLxFWMU0"}, captured 2026-10-11
        Assert.Equal([6035L, 6046, 8602, 8661, 8664, 8670], plans.ConvertAll(p => p.Id));
        var irin = plans[2];
        Assert.Equal("ranger", irin.ClassId);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 21, 15, 1, 80, TimeSpan.Zero), irin.UpdatedAt);
        Assert.Equal(["pve", "dps"], irin.Tags!);
        Assert.Empty(plans[0].Tags!);
        Assert.Equal(DaevanionPlan.ParsePlanner(Fixture("user-planner.json"), 8602)!.Boards.Keys.Order(), irin.Boards.Keys.Order());
        Assert.Empty(DaevanionPlan.ParsePlannerList("""{"result":{"data":null}}"""));
        Assert.Empty(DaevanionPlan.ParsePlannerList("not json"));
    }

    [Fact]
    public void Pairing_takes_the_planner_build_of_the_same_character_and_class()
    {
        Assert.Equal("Irin", QuestlogMine.PairPrefix("Irin — Ranger PvE 45 (ref. whelps 10.08 + top Vaizel)"));
        Assert.Equal("Arielka", QuestlogMine.PairPrefix("Arielka - Chanter PvE"));
        Assert.Equal("Mia", QuestlogMine.PairPrefix("Mia – PvP"));
        Assert.Null(QuestlogMine.PairPrefix("P1 Farmeo solo (Leonis Midgame + aguante)"));
        Assert.Null(QuestlogMine.PairPrefix("Akunita"));
        Assert.Null(QuestlogMine.PairPrefix("Lvl 1-45"));   // dash without a space before it
        Assert.Null(QuestlogMine.PairPrefix(" - nameless"));

        var skills = QuestlogBuilds.ParseSearch(Fixture("user-builds.json"));
        var plans = DaevanionPlan.ParsePlannerList(Fixture("user-planner.json"));
        long? Pair(long skillId) { var s = skills.Find(x => x.Id == skillId)!; return QuestlogMine.PairPlan(s.Name, s.ClassId, plans)?.Id; }
        Assert.Equal(8602, Pair(15969));    // Irin — Ranger PvE 45 ↔ Irin — Daevanion Ranger (top Vaizel)
        Assert.Equal(8661, Pair(16049));    // Arielka - Chanter ↔ Arielka - Daevanion Chanter
        Assert.Equal(8664, Pair(16051));    // Akunita - Templar ↔ Akunita - Daevanion Templar, not "Akunita" / "Akunita (Aligned)" (no dash)
        Assert.Equal(8670, Pair(16056));
        Assert.Null(Pair(13277));           // "P1 Farmeo solo …": no character prefix

        // same prefix, other class: no pair; several of the class: the newest
        Assert.Null(QuestlogMine.PairPlan("Irin — Templar alt", "templar", plans));
        var older = new QuestlogDaevPlan(1, "irin — old plan", "ranger", new(), new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = new QuestlogDaevPlan(2, "IRIN - new plan", "Ranger", new(), new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, QuestlogMine.PairPlan("Irin — Ranger", "ranger", [older, newer])!.Id);
        Assert.Equal(2, QuestlogMine.PairPlan("Irin — Ranger", "ranger", [newer, older])!.Id);
    }

    [Fact]
    public void Newer_check_compares_questlog_date_with_the_imported_one()
    {
        var at = new DateTimeOffset(2026, 10, 9, 20, 57, 11, 318, TimeSpan.Zero);
        Assert.False(QuestlogMine.IsNewer(at, at));
        Assert.False(QuestlogMine.IsNewer(at.AddMilliseconds(500), at));   // round-trip slack
        Assert.True(QuestlogMine.IsNewer(at.AddMinutes(5), at));
        Assert.False(QuestlogMine.IsNewer(at, at.AddMinutes(5)));          // local copy newer than questlog's
        Assert.True(QuestlogMine.IsNewer(at, null));                       // imported before dates were kept: offer the update
        Assert.False(QuestlogMine.IsNewer(null, at));

        // an import records questlog's updatedAt; a re-import keeps notes, plan and where the plan came from
        var b = QuestlogBuilds.FindBuild(Fixture("user-builds.json"), 15969)!;
        var fresh = QuestlogImport.Prepare(b, []);
        Assert.Equal(b.UpdatedAt, fresh.SourceUpdatedAt);
        Assert.False(QuestlogMine.IsNewer(b.UpdatedAt, fresh.SourceUpdatedAt));
        var mine = new Build { Id = "abc123", Source = b.Source, PlanSource = new BuildSource(8602, "AXIIyLxFWMU0", null), PlanName = "Irin — Daevanion", PlanSourceUpdatedAt = at, Daevanion = { [31] = [310001] } };
        var again = QuestlogImport.Prepare(b with { UpdatedAt = at.AddDays(1) }, [mine]);
        Assert.Equal(at.AddDays(1), again.SourceUpdatedAt);
        Assert.Equal(mine.PlanSource, again.PlanSource);
        Assert.Equal("Irin — Daevanion", again.PlanName);
        Assert.Equal(at, again.PlanSourceUpdatedAt);
        Assert.Equal([310001], again.Daevanion[31]);
    }

    [Fact]
    public void Class_filter_keeps_order_and_limit()
    {
        var skills = QuestlogBuilds.ParseSearch(Fixture("user-builds.json"));
        var templar = QuestlogMine.OfClass(skills, static s => s.ClassId, "templar");
        Assert.Equal([13277L, 13278, 13279, 16051], templar.ConvertAll(s => s.Id));
        Assert.Equal(2, QuestlogMine.OfClass(skills, static s => s.ClassId, "TEMPLAR", 2).Count);
        Assert.Equal(7, QuestlogMine.OfClass(skills, static s => s.ClassId, null).Count);
    }
}
