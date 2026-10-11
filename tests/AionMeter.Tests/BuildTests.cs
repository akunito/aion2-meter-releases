using AionMeter.Core.Armory;
using AionMeter.Core.Builds;

namespace AionMeter.Tests;

public class BuildTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "questlog", name));

    // build.sh copies data/skills/*.json (tools/skill-catalog.py) into upstream/data/gamedb/skills before the tests
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

    [Fact]
    public void Catalogue_loads_every_class()
    {
        var book = new SkillBook(SkillsDir());
        foreach (var c in SkillBook.Classes)
        {
            var list = book.ForClass(c);
            Assert.True(list.Count >= 30, $"{c}: {list.Count} skills");
            Assert.All(list, s => Assert.Equal(c, SkillBook.ClassOf(s.Id)));
            Assert.Contains(list, s => s.Sub == "active");
            Assert.Contains(list, s => s.Sub == "stigma");
            Assert.Contains(list, s => s.Sub == "passive");
        }
        Assert.Empty(book.ForClass("tutorial"));
    }

    [Fact]
    public void Deadshot_resolves_per_level_and_language()
    {
        var book = new SkillBook(SkillsDir());
        var s = book.Get(14010000)!;                          // lazily loads ranger from the id
        Assert.Equal("Deadshot", s.Name("en"));
        Assert.Equal("Flecha al objetivo", book.Name(14010000, "es"));
        Assert.Equal("Deadshot", s.Name("fr"));               // unknown language → English
        var en = book.Resolve(14010000, 20, "en")!;
        Assert.Contains("<b>2741-8225</b>", en);
        Assert.Contains("Deals 2741-8225 Wind damage", book.Resolve(14010000, 20, "en", plain: true));
        Assert.Contains("2741~8225", book.Resolve(14010000, 20, "es"));
        Assert.Contains("599-1798", book.Resolve(14010000, 1, "en"));
        Assert.Equal(book.Resolve(14010000, 40, "en"), book.Resolve(14010000, 99, "en"));   // clamped to max level
        Assert.Equal("ICON_RA_SKILL_002", s.Icon);
        Assert.Equal("https://cdn.questlog.gg/aion-2/assets/Game/UI/Resource/Texture/Skill/ICON_RA_SKILL_002.webp", SkillBook.IconUrl(s.Icon));
        Assert.Equal(5, s.Specs.Length);
        Assert.Equal(8, s.Specs[0].RequiredLevel);
        Assert.StartsWith("https://cdn.questlog.gg/aion-2/assets/Game/UI/Resource/Texture/Item/ETC/", SkillBook.SpecIconUrl(s.Specs[0].Icon!));
        Assert.Null(book.Get(99000000));
    }

    [Fact]
    public void Search_page_parses_builds()
    {
        var page = QuestlogBuilds.ParseSearchPage(Fixture("search.json"));
        Assert.Equal(18, page.Builds.Count);
        Assert.Equal(15, page.PageCount);
        Assert.Equal(254, page.TotalHits);
        var b = page.Builds[0];
        Assert.Equal("Ranger updated 10.08", b.Name);
        Assert.Equal("whelps", b.Author);
        Assert.Equal("X2aYOSzHtbA6", b.AuthorSlug);
        Assert.Equal("ranger", b.ClassId);
        Assert.Equal(71, b.LikeCount);
        Assert.Equal(9025, b.Id);
        Assert.True(b.HasDescription);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 28, 24, 275, TimeSpan.Zero), b.UpdatedAt);
        Assert.Equal(15, b.Build.Skills.Count);
        Assert.Equal("4.0", b.Build.Skill(14010000)!.Slot);
        Assert.Equal(20, b.Build.Skill(14010000)!.Level);
        Assert.Null(b.Build.Skill(14060000)!.Slot);
        Assert.Equal(14010000, b.Build.Priority[0]);
        Assert.Equal(8, b.Build.Priority.Count);
        Assert.Contains(page.Builds, x => x.Tags.Contains("pvp"));
        Assert.Empty(QuestlogBuilds.ParseSearch("not json"));
    }

    [Fact]
    public void Single_build_parses_skills_specs_priority_and_macros()
    {
        var b = QuestlogBuilds.ParseBuild(Fixture("irin-build.json"))!;
        Assert.Equal("ranger", b.ClassId);
        Assert.Equal(15969, b.Source!.Id);
        Assert.Equal(24, b.Skills.Count);
        var d = b.Skill(14010000)!;
        Assert.Equal("1.0", d.Slot);
        Assert.Equal(20, d.Level);
        Assert.Equal(new[] { 14010030, 14010050, 14010020 }, d.Specs);
        Assert.Equal(14010000, b.Priority[0]);
        Assert.Equal(13, b.Priority.Count);
        Assert.Equal(2, b.Macros.Count);
        Assert.Equal("Snare", b.Macros[0].Name);
        Assert.Equal(new[] { 14130000 }, b.Macros[0].Steps);
        Assert.StartsWith("<p>", b.Description);
        Assert.Null(QuestlogBuilds.ParseBuild("{}"));
    }

    [Fact]
    public void Urls_round_trip()
    {
        Assert.Equal<(string, long)?>(("X2aYOSzHtbA6", 9025L), QuestlogBuilds.ParseUrl("https://questlog.gg/aion-2/en/skill-builder/X2aYOSzHtbA6?build-id=9025"));
        Assert.Equal<(string, long)?>(("AXIIyLxFWMU0", 15969L), QuestlogBuilds.ParseUrl(" https://questlog.gg/aion-2/es/skill-builder/AXIIyLxFWMU0?tab=1&build-id=15969#top "));
        Assert.Equal<(string, long)?>(("abc", 7L), QuestlogBuilds.ParseUrl(QuestlogBuilds.BuildUrl("abc", 7)));
        Assert.Null(QuestlogBuilds.ParseUrl("https://questlog.gg/aion-2/en/skill-builder/X2aYOSzHtbA6"));
        Assert.Null(QuestlogBuilds.ParseUrl("https://example.com/?build-id=1"));
        Assert.Null(QuestlogBuilds.ParseUrl(null));
        var url = QuestlogBuilds.SearchUrl("ranger pve", 2);
        Assert.StartsWith("https://questlog.gg/aion-2/api/trpc/skillBuilder.searchSkillBuilds?input=", url);
        Assert.Equal("""{"searchTerm":"ranger pve","page":2}""", Uri.UnescapeDataString(url[(url.IndexOf("input=") + 6)..]));
    }

    [Fact]
    public void Store_round_trips_and_tracks_the_active_build()
    {
        var root = Path.Combine(Path.GetTempPath(), "builds-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BuildStore(Path.Combine(root, "builds"));
            Assert.Empty(store.List());
            Assert.Null(store.ActiveId);
            var b = new Build
            {
                Name = "PvE", ClassId = "ranger", Source = new BuildSource(9025, "X2aYOSzHtbA6", "whelps"),
                Skills = [new BuildSkill(14010000, 20, "1.0", [14010030, 14010050]), new BuildSkill(14360000, 20, null, [])],
                Priority = [14010000], Macros = [new BuildMacro("Open", [14010000, 14110000])],
                Notes = new() { [14010000] = "after Precision" }, Daevanion = new() { [31] = [310033, 310034] }, Description = "d",
            };
            var saved = store.Save(b);
            Assert.False(string.IsNullOrEmpty(saved.Id));
            Assert.True(saved.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
            store.ActiveId = saved.Id;
            Assert.Equal(saved.Id, File.ReadAllText(Path.Combine(root, "build-active.txt")));
            var back = new BuildStore(Path.Combine(root, "builds")).Active!;
            Assert.Equal("PvE", back.Name);
            Assert.Equal("whelps", back.Source!.Author);
            Assert.Equal(new[] { 14010030, 14010050 }, back.Skill(14010000)!.Specs);
            Assert.Null(back.Skill(14360000)!.Slot);
            Assert.Equal("after Precision", back.Notes[14010000]);
            Assert.Equal(new[] { 310033, 310034 }, back.Daevanion[31]);
            Assert.Equal(new[] { 14010000, 14110000 }, back.Macros[0].Steps);
            Assert.Single(store.List());
            Assert.Null(store.Load("../x"));
            Assert.True(store.Delete(saved.Id));
            Assert.Null(store.ActiveId);
            Assert.Empty(store.List());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Progress_against_the_profile()
    {
        var build = new Build
        {
            ClassId = "ranger",
            Skills = [new BuildSkill(14010000, 20, "1.0", []), new BuildSkill(14130000, 12, "9.0", []), new BuildSkill(14060000, 5, null, []), new BuildSkill(14110000, 20, null, [])],
            Daevanion = new() { [31] = [310033, 310034, 310035], [32] = [320011] },
        };
        var profile = new CharacterProfile(new CharacterRef("c", "Irin", 0, 45, 1, "Vaizel", "eu"), "Ranger", 45, 47000, null, null, null, [], 1042, [], [], null, null,
            [new SkillLine("Active", "Deadshot", 13, true, true, 14010000), new SkillLine("Active", "Snare Shot", 12, true, true, 14130000),
             new SkillLine("Dp", "Griffon Arrow", 0, false, false, 14060000), new SkillLine("Active", "Gale Arrow", 17, true, true, 14110000)],
            [new DaevanionNode(31, 3, 3, 310033, true, "Common", "Stat", "", []), new DaevanionNode(31, 3, 4, 310034, false, "Common", "Stat", "", []),
             new DaevanionNode(32, 1, 1, 320011, true, "Start", "Stat", "", [])],
            [], DateTimeOffset.UtcNow);
        var p = Progress.Compare(build, profile, id => id == 14060000);
        Assert.Equal(SkillState.Far, p.Skills[0].State);
        Assert.Equal(13, p.Skills[0].Current);
        Assert.Equal(SkillState.AtTarget, p.Skills[1].State);
        Assert.Equal(SkillState.NotLearned, p.Skills[2].State);
        Assert.Null(p.Skills[2].Current);
        Assert.True(p.Skills[2].InInventory);
        Assert.Equal(SkillState.Near, p.Skills[3].State);
        Assert.Equal(4, p.TotalSkills);
        Assert.Equal(3, p.MissingSkills);
        Assert.Equal(1, p.NotLearned);
        Assert.Equal(7 + 5 + 3, p.MissingLevels);
        Assert.Equal(new BoardProgress(31, 3, 1, 2), p.Boards[0]);
        Assert.Equal(new BoardProgress(32, 1, 1, 0), p.Boards[1]);
        var none = Progress.Compare(build, null);
        Assert.Equal(4, none.NotLearned);
        Assert.False(none.Skills[2].InInventory);
    }
    [Fact]
    public void Editor_equips_into_the_first_free_row_and_unequips()
    {
        var book = new SkillBook(SkillsDir());
        var b = QuestlogBuilds.ParseBuild(Fixture("irin-build.json"))!;
        Assert.Equal(1, BuildEditor.FirstFreeRow(b, 1));                 // Deadshot holds 1.0
        Assert.Equal(-1, BuildEditor.FirstFreeRow(b, 10));               // 10.0–10.3 all taken
        var trap = book.Get(14170000)!;                                   // on 3.0
        var e = BuildEditor.Equip(b, trap, 1);
        Assert.Equal("1.1", e.Skill(14170000)!.Slot);
        Assert.Equal("3.0", b.Skill(14170000)!.Slot);                     // the input is untouched
        Assert.Same(b, BuildEditor.Equip(b, trap, 10));                  // full key refused
        Assert.Same(b, BuildEditor.Equip(b, book.Get(14740000)!, 1));    // passives never on the bar
        var kick = book.Get(14120000)!;                                   // not in the build: added at level 1
        var k = BuildEditor.Equip(b, kick, 4);
        Assert.Equal((1, "4.0"), (k.Skill(14120000)!.Level, k.Skill(14120000)!.Slot));
        Assert.Equal(25, k.Skills.Count);
        var u = BuildEditor.Unequip(e, 14170000);
        Assert.Null(u.Skill(14170000)!.Slot);
        Assert.Same(u, BuildEditor.Unequip(u, 14170000));
        Assert.Equal(14010000, BuildEditor.SkillAt(b, 1, 0));
        Assert.Same(b, BuildEditor.MoveTo(b, trap, 1, 0));               // taken cell
        Assert.Equal("5.3", BuildEditor.MoveTo(b, trap, 5, 3).Skill(14170000)!.Slot);
        Assert.False(BuildEditor.TryParseSlot("13.0", out _, out _));
        Assert.False(BuildEditor.TryParseSlot("1.4", out _, out _));
        Assert.Equal(12, BuildEditor.KeyOf("12.2"));
    }

    [Fact]
    public void Editor_specs_respect_level_and_cap()
    {
        var book = new SkillBook(SkillsDir());
        var b = QuestlogBuilds.ParseBuild(Fixture("irin-build.json"))!;
        var dead = book.Get(14010000)!;                                   // lv 20, specs 30/50/20 chosen
        var full = BuildEditor.ToggleSpec(b, dead, 14010010, out var r);
        Assert.Equal(SpecToggle.Full, r); Assert.Same(b, full);
        var off = BuildEditor.ToggleSpec(b, dead, 14010050, out r);
        Assert.Equal(SpecToggle.Removed, r);
        Assert.Equal(new[] { 14010030, 14010020 }, off.Skill(14010000)!.Specs);
        var on = BuildEditor.ToggleSpec(off, dead, 14010040, out r);
        Assert.Equal(SpecToggle.Added, r);
        Assert.Equal(3, on.Skill(14010000)!.Specs.Count);
        // stigmas take four
        var griffon = book.Get(14060000)!;
        Assert.Equal(4, BuildEditor.SpecCap(griffon));
        Assert.Equal(3, BuildEditor.SpecCap(dead));
        // a spec above the skill's level is refused, and lowering the level drops the chosen ones above it
        var low = BuildEditor.SetLevel(b, dead, 10);
        Assert.Equal(10, low.Skill(14010000)!.Level);
        Assert.Equal(new[] { 14010030, 14010020 }, low.Skill(14010000)!.Specs);   // 14010050 needs 16
        BuildEditor.ToggleSpec(low, dead, 14010040, out r);               // needs 12
        Assert.Equal(SpecToggle.Locked, r);
        BuildEditor.ToggleSpec(b, dead, 99, out r);
        Assert.Equal(SpecToggle.Unknown, r);
        Assert.Equal(1, BuildEditor.SetLevel(b, dead, -5).Skill(14010000)!.Level);
        Assert.Equal(dead.MaxLevel, BuildEditor.SetLevel(b, dead, 999).Skill(14010000)!.Level);
        Assert.Same(b, BuildEditor.SetLevel(b, dead, 20));
    }

    [Fact]
    public void Editor_priority_moves_within_its_category_and_removes()
    {
        var book = new SkillBook(SkillsDir());
        var b = QuestlogBuilds.ParseBuild(Fixture("irin-build.json"))!;
        string? Cat(int id) => book.Get(id)?.Sub;
        // 14010000 14110000 14020000 14050000 14340000 14080000 | 14310000 14220000 14060000 14380000 | 14740000 14750000 14800000
        var m = BuildEditor.MovePriority(b, 14110000, -1, Cat);
        Assert.Equal(new[] { 14110000, 14010000 }, m.Priority.Take(2));
        Assert.Same(b, BuildEditor.MovePriority(b, 14010000, -1, Cat));  // already first
        Assert.Same(b, BuildEditor.MovePriority(b, 14080000, 1, Cat));   // last active: the next entry is a stigma
        var s = BuildEditor.MovePriority(b, 14310000, 1, Cat);
        Assert.Equal(new[] { 14220000, 14310000 }, s.Priority.Skip(6).Take(2));
        var r = BuildEditor.RemovePriority(b, 14020000);
        Assert.Equal(12, r.Priority.Count);
        Assert.DoesNotContain(14020000, r.Priority);
        Assert.Equal(14020000, BuildEditor.AddPriority(r, 14020000).Priority[^1]);
        Assert.Same(b, BuildEditor.AddPriority(b, 14120000));            // not in the build
        var gone = BuildEditor.RemoveSkill(b, 14130000);
        Assert.Null(gone.Skill(14130000));
        Assert.Empty(gone.Macros[0].Steps);
        var mac = BuildEditor.AddMacro(b, new BuildMacro("Open", [14090000, 14010000]));
        Assert.Equal(3, mac.Macros.Count);
        Assert.Equal(2, BuildEditor.RemoveMacro(mac, 0).Macros.Count);
        Assert.Same(b, BuildEditor.AddMacro(b, new BuildMacro("empty", [])));
    }

    [Fact]
    public void Editor_new_build_from_the_profile()
    {
        var profile = new CharacterProfile(new CharacterRef("c", "Irin", 0, 45, 1, "Vaizel", "eu"), "Ranger", 45, 47000, null, null, null, [], 1042, [], [], null, null,
            [new SkillLine("Active", "Deadshot", 13, true, true, 14010000), new SkillLine("Dp", "Griffon Arrow", 0, false, false, 14060000),
             new SkillLine("Passive", "Focused Eye", 13, false, true, 14740000), new SkillLine("Active", "Basic", 1, true, true, 99000001)],
            [], [], DateTimeOffset.UtcNow);
        var b = BuildEditor.FromProfile(profile, "Irin now");
        Assert.Equal("ranger", b.ClassId);
        Assert.Equal("Irin now", b.Name);
        Assert.Equal(new[] { 14010000, 14740000 }, b.Skills.Select(s => s.SkillId));
        Assert.Equal(13, b.Skill(14010000)!.Level);
        Assert.All(b.Skills, s => { Assert.Null(s.Slot); Assert.Empty(s.Specs); });
        Assert.Empty(b.Priority);
        Assert.Equal("elementalist", BuildEditor.ClassIdOf("Spiritmaster"));
        Assert.Equal("chanter", BuildEditor.ClassIdOf(" CHANTER "));
        Assert.Null(BuildEditor.ClassIdOf("Brawler"));
        Assert.Null(BuildEditor.ClassIdOf(null));
    }
}
