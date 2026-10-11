using System.Text.Json;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;

namespace AionMeter.Tests;

public class DaevanionTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "questlog", name));

    // build.sh copies data/daevanion/*.json (tools/daevanion-catalog.py) into upstream/data/gamedb/daevanion before the tests
    private static string BoardsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "data", "gamedb", "daevanion");
            if (File.Exists(Path.Combine(p, "ranger.json"))) return p;
        }
        Assert.Fail("data/gamedb/daevanion not found: run build/build.sh (it copies data/daevanion from the project root)");
        return "";
    }

    private static DaevClass Ranger() => new DaevanionBook(BoardsDir()).ForClass("ranger");

    private static Dictionary<int, List<int>> Boards(JsonElement e)
    {
        var d = new Dictionary<int, List<int>>();
        foreach (var p in e.EnumerateObject()) d[int.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => int.Parse(x.GetString()!)).ToList();
        return d;
    }

    [Fact]
    public void Catalogue_loads_every_class()
    {
        var book = new DaevanionBook(BoardsDir());
        foreach (var c in SkillBook.Classes)
        {
            var dc = book.ForClass(c);
            Assert.Equal(["Nezekan", "Zikel", "Vaizel", "Triniel", "Azphel"], dc.Boards.Select(b => b.NameEn));
            Assert.Equal([88, 88, 88, 116, 152], dc.Boards.Select(b => b.Total));
            foreach (var b in dc.Boards)
            {
                Assert.Equal(b.Total + 1, b.Nodes.Length);
                var start = dc.Node(b.Start)!;
                Assert.True(start.Start);
                Assert.Equal((8, 8), (start.Row, start.Col));
                Assert.All(b.Nodes, n => Assert.Equal(b.Id * 10000 + (n.Row - 1) * 15 + n.Col, n.Id));
                Assert.True(DaevanionPlan.Connected(b, b.Nodes.Select(n => n.Id)), $"{c} {b.NameEn}: not every node reaches the start");
            }
        }
        Assert.Empty(book.ForClass("nope").Boards);
    }

    [Fact]
    public void Ranger_Nezekan_board()
    {
        var b = Ranger().Board(31)!;
        Assert.Equal(89, b.Nodes.Length);
        Assert.Equal(310113, b.Start);
        // the catalogue (official API, 2026-10) links the start up and down only on Nezekan; Zikel's start links left and right
        Assert.Equal([310098, 310128], b.Neighbours(310113).Order());
        Assert.True(b.MaxRow - b.MinRow < 15 && b.MaxCol - b.MinCol < 15);
    }

    [Fact]
    public void Toggle_follows_the_connection_rule()
    {
        var c = Ranger();
        var b = c.Board(31)!;
        var open = DaevanionPlan.OpenSet(null, c);          // starts only
        Assert.Contains(310113, open);

        var plan = DaevanionPlan.Toggle(b, [], 310033, open, out var r);
        Assert.Equal(DaevToggle.NotConnected, r);
        Assert.Empty(plan);

        plan = DaevanionPlan.Toggle(b, plan, 310098, open, out r);
        Assert.Equal(DaevToggle.Added, r);
        plan = DaevanionPlan.Toggle(b, plan, 310097, open, out r);
        Assert.Equal(DaevToggle.Added, r);
        plan = DaevanionPlan.Toggle(b, plan, 310082, open, out r);
        Assert.Equal(DaevToggle.Added, r);
        plan = DaevanionPlan.Toggle(b, plan, 310128, open, out r);
        Assert.Equal([310082, 310097, 310098, 310128], plan.Order());
        Assert.Equal(4, DaevanionPlan.Cost(c, plan));

        // 310098 is the bridge to 310097 and 310082; 310128 hangs on the start directly
        plan = DaevanionPlan.Toggle(b, plan, 310098, open, out r);
        Assert.Equal(DaevToggle.Removed, r);
        Assert.Equal([310128], plan);

        DaevanionPlan.Toggle(b, plan, 310113, open, out r);
        Assert.Equal(DaevToggle.Unknown, r);
    }

    [Fact]
    public void Open_nodes_never_leave_the_plan()
    {
        var c = Ranger();
        var b = c.Board(31)!;
        var open = new HashSet<int> { 310113, 310098, 310097 };
        var plan = DaevanionPlan.Toggle(b, null, 310082, open, out var r);
        Assert.Equal(DaevToggle.Added, r);
        Assert.Equal([310082, 310097, 310098], plan.Order());
        plan = DaevanionPlan.Toggle(b, plan, 310098, open, out r);
        Assert.Equal(DaevToggle.Open, r);
        Assert.Equal(3, plan.Count);
        Assert.Equal([310097, 310098], DaevanionPlan.Reset(b, open).Order());
        Assert.Equal(88, DaevanionPlan.SelectAll(b).Count);
        Assert.Equal(DaevanionPlan.Cost(c, b.Nodes.Select(n => n.Id)), DaevanionPlan.Cost(c, DaevanionPlan.SelectAll(b)));
    }

    [Fact]
    public void Effect_texts_parse_and_sum()
    {
        Assert.True(DaevanionPlan.TryParseEffect("HP +400", out var n, out var v, out var s));
        Assert.Equal(("HP", 400d, ""), (n, v, s));
        Assert.True(DaevanionPlan.TryParseEffect("Combat Speed +1.5%", out n, out v, out s));
        Assert.Equal(("Combat Speed", 1.5, "%"), (n, v, s));
        Assert.False(DaevanionPlan.TryParseEffect("Nezekan - Start", out _, out _, out _));

        var c = Ranger();
        var all = DaevanionPlan.Summarize(c, c.Board(31)!.Nodes.Select(x => x.Id), "en");
        var hp = all.Single(e => e.Key == "HP");
        Assert.Equal(DaevGroup.Resource, hp.Group);
        Assert.Equal(c.Board(31)!.Nodes.Sum(x => x.EffectsEn.Where(e => e.StartsWith("HP +")).Sum(e => int.Parse(e[4..]))), hp.Value);
        var speed = all.Single(e => e.Key == "Combat Speed%");
        Assert.Equal("%", speed.Suffix);
        Assert.Equal("+" + speed.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%", speed.Amount);
        Assert.Contains(all, e => e.Group == DaevGroup.Skill && e.Key == "Concentrated Fire");
        Assert.True(all.Select(e => (int)e.Group).SequenceEqual(all.Select(e => (int)e.Group).Order()));
        Assert.Equal(DaevGroup.Critical, all.Single(e => e.Key == "Critical Hit").Group);
        Assert.Equal(DaevGroup.Defense, all.Single(e => e.Key == "Defense Bonus").Group);

        var es = DaevanionPlan.Summarize(c, [310033], "es");
        Assert.Equal(("Combat Speed%", "Velocidad de Hechizo", "+1.5%"), (es[0].Key, es[0].Name, es[0].Amount));
    }

    [Fact]
    public void Irin_open_set_and_plan_are_connected()
    {
        var c = Ranger();
        using var doc = JsonDocument.Parse(Fixture("irin-daevanion.json"));
        var irin = Boards(doc.RootElement.GetProperty("irin"));
        var plan = Boards(doc.RootElement.GetProperty("selectedNodes"));
        Assert.Equal(47, irin[31].Count);
        foreach (var b in c.Boards)
        {
            var open = irin.GetValueOrDefault(b.Id) ?? [];
            Assert.True(DaevanionPlan.Connected(b, open), $"open set of {b.NameEn} is not connected");
            Assert.Subset(new HashSet<int>(plan.GetValueOrDefault(b.Id) ?? []), new HashSet<int>(open));
        }
        // questlog's planner does not enforce the rule: plan 8602 (a consensus of top rangers) holds nodes cut off from the start
        Assert.False(DaevanionPlan.Connected(c.Board(31)!, plan[31]));
        var cut = DaevanionPlan.Toggle(c.Board(31)!, plan[31], 310193, new HashSet<int>(irin[31]) { 310113 }, out var cr);
        Assert.Equal(DaevToggle.Removed, cr);
        Assert.DoesNotContain(310039, cut);   // any removal drops what has no link to the start
        var nodes = irin[31].Select(id => new DaevanionNode(31, 0, 0, id, true, "", "", "", [])).ToList();
        var profile = new CharacterProfile(new CharacterRef("x", "Irin", 0, 45, 2305, "Vaizel", "eu"), "Ranger", 45, 0, null, null, null, [], 0, [], [], null, null, [], nodes, [],
                                           DateTimeOffset.UnixEpoch);
        var set = DaevanionPlan.OpenSet(profile, c);
        Assert.Equal(47 + 5, set.Count);
        Assert.Equal(plan[31].Order(), DaevanionPlan.Effective(c.Board(31)!, plan[31], set).Order());
    }

    [Fact]
    public void Questlog_planner_answer_parses()
    {
        Assert.Equal<(string, long)?>(("AXIIyLxFWMU0", 8602L), DaevanionPlan.ParsePlannerUrl("https://questlog.gg/aion-2/en/daevanion-planner/AXIIyLxFWMU0?build-id=8602"));
        Assert.Null(DaevanionPlan.ParsePlannerUrl("https://questlog.gg/aion-2/en/skill-builder/AXIIyLxFWMU0?build-id=8602"));
        Assert.Contains("daevanionPlanner.getDaevanionPlannerBySlug", DaevanionPlan.PlannerApiUrl("AXIIyLxFWMU0"));
        var p = DaevanionPlan.ParsePlanner(Fixture("daevanion-planner.json"), 8602)!;
        Assert.Equal("ranger", p.ClassId);
        Assert.Equal([31, 32, 33, 34, 36], p.Boards.Keys.Order());
        Assert.Equal(67, p.Boards[31].Count);
        Assert.DoesNotContain(310113, p.Boards[31]);
        Assert.Null(DaevanionPlan.ParsePlanner(Fixture("daevanion-planner.json"), 1));
        Assert.Null(DaevanionPlan.ParsePlanner("not json", 8602));
    }
}
