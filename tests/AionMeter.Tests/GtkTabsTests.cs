using System.Net.Sockets;
using System.Text;
using AionMeter.Overlay;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-50: the GTK card's tab host and kit parts that need no display (GTK is never loaded here).</summary>
public class GtkTabsTests
{
    private static readonly HashSet<string> Ported = ["settings", "items", "fight"];
    private static TabState NewTabs() => new(Ported.Contains);

    // ---- the tab state machine (Avalonia OverlayWindow.SelectTab rules)
    [Fact]
    public void Tabs_StartOnDpsAndIgnoreUnportedOrUnknown()
    {
        var t = NewTabs();
        Assert.Equal("meter", t.Current);
        Assert.False(t.Select("atlas"));     // not registered: dimmed pill, nothing happens
        Assert.False(t.Select("nonsense"));
        Assert.False(t.Select("meter"));     // already in front
        Assert.Equal("meter", t.Current);
        Assert.True(new TabState(Ported.Contains, "items").Current == "items");
    }

    [Fact]
    public void Tabs_PillAgainGoesBackToDps()
    {
        var t = NewTabs();
        Assert.Equal("items", t.PillTarget("items"));
        Assert.True(t.Select("items"));
        Assert.Equal("meter", t.PillTarget("items"));   // the selected pill again
        Assert.Equal("meter", t.Back());                // Back chip / Esc
        Assert.Equal("meter", t.PillTarget("meter"));   // the DPS pill from a tab
    }

    [Fact]
    public void Tabs_SettingsReturnsToTheTabBeforeIt()
    {
        var t = NewTabs();
        t.Select("items");
        Assert.Equal("settings", t.PillTarget("settings")); // the gear is the Settings pill
        t.Select("settings");
        Assert.Equal("items", t.BeforeSettings);
        Assert.Equal("items", t.PillTarget("settings")); // gear again
        Assert.Equal("items", t.Back());                // Back / Esc / Cancel / Save
        t.Select(t.Back());
        Assert.Equal("items", t.Current);
        // from DPS, Settings goes back to DPS
        t.Select("meter");
        t.Select("settings");
        Assert.Equal("meter", t.Back());
    }

    /// <summary>One control per tab: the gear is the Settings pill and always shows (no second pill appears next to it when Settings is
    /// in front, Diego 2026-10-13); the breakdown's pill shows only while the breakdown is in front; the strip pills always show.</summary>
    [Fact]
    public void Tabs_StripShowsTheGearAlwaysAndTheFightPillOnlyInFront()
    {
        foreach (var cur in (string[])["meter", "settings", "items", "fight"])
        {
            Assert.True(TabState.PillVisible("settings", cur), cur);
            Assert.Equal(cur == "fight", TabState.PillVisible("fight", cur));
            foreach (var k in UiTabs.Strip) Assert.True(TabState.PillVisible(k, cur), k);
        }
        Assert.Same(Icons.Gear, Icons.ForTab("settings"));
        Assert.True(Icons.Gear.Filled);
    }

    [Fact]
    public void Tabs_FightOnlyOnceOpenedFromARow()
    {
        var t = NewTabs();
        Assert.False(t.Select("fight"));   // → meter, already in front
        t.Select("items");
        Assert.True(t.Select("fight"));    // FightOpen false: falls back to DPS
        Assert.Equal("meter", t.Current);
        t.FightOpen = true;
        Assert.True(t.Select("fight"));
        Assert.Equal("fight", t.Current);
        Assert.Equal("meter", t.Back());
    }

    [Fact]
    public void Tabs_KeyboardOnDemandOnlyOnATab()
    {
        Assert.Equal(LayerShell.KeyboardMode.None, TabState.Keyboard("meter"));
        foreach (var k in (string[])["settings", "items", "fight", "atlas"]) Assert.Equal(LayerShell.KeyboardMode.OnDemand, TabState.Keyboard(k));
    }

    [Fact]
    public void Tabs_MinimumSizeOnlyWhileATabIsOpen()
    {
        Assert.Equal((340, 220), TabState.Size("meter", 340, 220));          // the DPS size stays what the user set
        Assert.Equal((560, 540), TabState.Size("items", 340, 220));          // grown for the tab
        Assert.Equal((800, 900), TabState.Size("items", 800, 900));          // a bigger card keeps its size whatever the tab
        Assert.Equal((560, 600), TabState.Size("settings", 400, 600));
        Assert.Equal((OverlaySettings.MinWidth, OverlaySettings.MinHeight), TabState.MinSize("meter"));
        Assert.Equal((TabState.MinTabWidth, TabState.MinTabHeight), TabState.MinSize("settings"));
    }

    // ---- registry
    [Fact]
    public void Registry_StripIsUiTabsStripAndSettingsIsPorted()
    {
        Assert.Equal(UiTabs.Strip, ViewRegistry.Strip.Select(s => s.Key).ToArray());
        foreach (var s in ViewRegistry.Strip)
        {
            Assert.NotNull(s.Icon);
            Assert.False(string.IsNullOrEmpty(s.Tip(UiText.Current)));
        }
        Assert.True(ViewRegistry.Has("settings"));
        Assert.False(ViewRegistry.Has("meter"));   // the DPS rows are the card itself
        foreach (var k in ViewRegistry.Factories.Keys) Assert.True(k == "fight" || UiTabs.IsKnown(k), k);
    }

    // ---- help pages
    [Fact]
    public void Help_BlocksGroupLinesUnderHeadings()
    {
        var md = "# Prices\n\nWatch items.\nSecond line.\n\n## Check now\n- one\n- two\n";
        var blocks = HelpPage.Blocks(md, "en");
        Assert.Equal(4, blocks.Count);
        Assert.True(blocks[0].Heading);
        Assert.Equal("Prices", blocks[0].Text);
        Assert.False(blocks[1].Heading);
        Assert.Contains("Watch items.", blocks[1].Text);
        Assert.Contains("Second line.", blocks[1].Text);
        Assert.True(blocks[2].Heading);
        Assert.False(blocks[3].Heading);
        Assert.Contains("one", blocks[3].Text);
        Assert.Contains("two", blocks[3].Text);
        Assert.DoesNotContain(blocks, b => b.Text.Length == 0);
    }

    [Fact]
    public void Help_MissingPageAndPathFallback()
    {
        var missing = HelpPage.Blocks(null, "en");
        Assert.Single(missing);
        Assert.Equal(UiText.Current.HelpMissing, missing[0].Text);

        var dir = Path.Combine(Path.GetTempPath(), "a2m50-help-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "help"));
        File.WriteAllText(Path.Combine(dir, "help", "meter.en.md"), "# DPS\n");
        File.WriteAllText(Path.Combine(dir, "help", "items.es.md"), "# Objetos\n");
        try
        {
            Assert.EndsWith("meter.en.md", HelpPage.PathFor(dir, "fight", "es"));   // breakdown → DPS page, ES missing → EN
            Assert.EndsWith("items.es.md", HelpPage.PathFor(dir, "items", "es"));
            Assert.Null(HelpPage.PathFor(dir, "atlas", "en"));
        }
        finally { Directory.Delete(dir, true); }
        Assert.EndsWith("DPS", HelpPage.Title("fight", UiText.Current));
        Assert.EndsWith(UiText.Current.SettingsTitle, HelpPage.Title("settings", UiText.Current));
    }

    [Fact]
    public void Help_EveryShippedPageParses()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data", "help");
        if (!Directory.Exists(dir)) return;   // data/ is copied next to the overlay; absent in a bare test run
        foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
        {
            var blocks = HelpPage.Blocks(File.ReadAllText(f), f.Contains(".es.") ? "es" : "en");
            Assert.Contains(blocks, b => b.Heading);
        }
    }

    // ---- kit stylesheet and palette
    [Fact]
    public void Kit_CssHasEveryColourSizeAndGradeClass()
    {
        var css = KitCss.Sheet();
        foreach (Fg fg in Enum.GetValues<Fg>()) Assert.Contains("label." + Palette.Class(fg) + ",", css);
        Assert.Equal("k-s125", KitCss.SizeClass(12.5));
        Assert.Equal("k-s110", KitCss.SizeClass(11.2));
        Assert.Equal("k-s90", KitCss.SizeClass(3));
        Assert.Equal("k-s240", KitCss.SizeClass(40));
        for (var t = 90; t <= 240; t += 5) Assert.Contains("label.k-s" + t + " ", css);
        for (var g = 0; g < Palette.GradeCount; g++) Assert.Contains(".k-grade.k-g" + g + " ", css);
        // every kit rule is rooted at .k-root or is a k- class: nothing hits the card's own widgets
        foreach (var line in css.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(line.TrimStart().StartsWith(".k-", StringComparison.Ordinal), line);
    }

    [Fact]
    public void Kit_CssScalesWithTheCard()
    {
        var css = CardText.ScaleCss(KitCss.Sheet(), 1.5);
        Assert.Contains(".k-section { background-color: rgba(255,255,255,0.051); border: 1.5px solid rgba(255,255,255,0.102); border-radius: 15px; padding: 18px 24px; margin-bottom: 18px; }", css);
        Assert.Contains("label.k-s125 { font-size: 18.75px; }", css);
        // the whole card sheet + kit still parses as one string GTK loads
        Assert.Contains(".k-root label", CardText.ScaleCss(Css.Sheet(0.9) + KitCss.Sheet(), 1.2));
    }

    [Fact]
    public void Palette_HexAndRgba()
    {
        Assert.Equal("#C9A45C", Palette.Hex(Palette.Gold));
        Assert.Equal("rgba(255,255,255,0.051)", Palette.Rgba(Palette.SectionFill));
        Assert.Equal(Palette.TextMute, Palette.Rgb(Fg.Mute));
        Assert.Equal("k-fg-gold", Palette.Class(Fg.Gold));
    }

    // ---- ui.json single keys
    [Fact]
    public void UiJson_WritesOneKeyAndKeepsTheRest()
    {
        var path = Path.Combine(Path.GetTempPath(), "a2m50-ui-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            File.WriteAllText(path, "{\"Width\":600,\"LastTab\":\"items\",\"Windows\":{\"help\":[1,2,3,4]}}");
            Assert.Equal("items", UiJson.ReadString(path, "LastTab"));
            UiJson.Write(path, "LastTab", "prices");
            UiScale.Write(path, 1.25);
            Assert.Equal("prices", UiJson.ReadString(path, "LastTab"));
            Assert.Equal(1.25, UiScale.Read(path));
            var text = File.ReadAllText(path);
            Assert.Contains("\"Width\": 600", text);
            Assert.Contains("\"help\"", text);
            Assert.Null(UiJson.ReadString(path, "Width"));   // not a string
            File.WriteAllText(path, "{broken");
            Assert.Null(UiJson.ReadString(path, "LastTab"));
            UiJson.Write(path, "LastTab", "craft");
            Assert.Equal("craft", UiJson.ReadString(path, "LastTab"));
            Assert.Null(UiJson.ReadString(path + ".missing", "LastTab"));
        }
        finally { File.Delete(path); }
    }

    // ---- shared item formats (both ItemKits)
    [Fact]
    public void ItemFormat_ShortKinahAndPrices()
    {
        Assert.Equal("9,999", ItemFormat.Short(9_999));
        Assert.Equal("13k", ItemFormat.Short(12_847));
        Assert.Equal("1.2M", ItemFormat.Short(1_234_567));
        Assert.Equal("12M", ItemFormat.Short(12_345_678));
        Assert.Equal("1.25M", ItemFormat.Kinah(1_250_000));
        Assert.Equal("850.5k", ItemFormat.Kinah(850_500));
        Assert.Equal("120", ItemFormat.Kinah(120));
        Assert.True(ItemFormat.TryParsePrice("1.2m", out var v) && v == 1_200_000);
        Assert.True(ItemFormat.TryParsePrice(" 850k ", out v) && v == 850_000);
        Assert.True(ItemFormat.TryParsePrice("1,200,000", out v) && v == 1_200_000);
        Assert.False(ItemFormat.TryParsePrice("-5", out _));
        Assert.False(ItemFormat.TryParsePrice("abc", out _));
    }

    // ---- the shared icon cache rules (both IconProviders)
    private sealed class NoImages : IconCache<string>
    {
        public NoImages(string dir) : base(dir, false, a => a()) { }
        protected override string? Decode(string file, int width) => file;
        protected override bool WriteSmall(byte[] original, string tmpFile) => false;
    }

    [Fact]
    public void IconCache_SkillNamesAndCacheFiles()
    {
        using var icons = new NoImages(Path.Combine(Path.GetTempPath(), "a2m50-no-data"));
        Assert.Equal("ICON_GL_SKILL_001", icons.SkillIconName(11_01_00_00));
        Assert.Equal("ICON_GL_SKILL_Passive_002", icons.SkillIconName(11_72_00_00));
        Assert.Null(icons.SkillIconName(11_00_00_01));      // basic variant
        Assert.Null(icons.SkillIconName(5_000_000));        // mob skill
        Assert.Equal("Icon_Item_Usable_Godstone_WP_r_00c", icons.SkillIconName(30_000_120));
        Assert.EndsWith(Path.Combine("icons", "skill", "stem.png"), icons.FileFor("skill:stem"));
        Assert.Null(icons.Item(123));   // no item-icons.json, downloads off: nothing, never blocks
    }

    // ---- every daemon message reaches the GTK shell (OverlayClient = the Avalonia DaemonClient's events)
    [Fact]
    public async Task OverlayClient_CarriesEveryDaemonMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), "aionmeter-a2m50-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var events = new System.Collections.Concurrent.BlockingCollection<string>();
        using var client = new OverlayClient(path, a => a());
        client.ConnectionChanged += on => events.Add(on ? "up" : "down");
        client.SettingsReloaded += l => events.Add("reloaded " + l);
        client.TimersChanged += () => events.Add("timers");
        client.BossAlert += t => events.Add("alert " + t);
        client.NodeNameWanted += c => events.Add("node " + c);
        client.PricePolled += p => events.Add($"polled {p.Ran} {p.Reason} {p.Items} {p.Next}");
        client.Error += m => events.Add("error " + m);
        client.Position += p => events.Add($"position {p.Map}");
        client.Start();
        using var server = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
        string Take() => events.Take(new CancellationTokenSource(5000).Token);
        Assert.Equal("up", Take());
        server.Send(Encoding.UTF8.GetBytes(
            "{\"type\":\"settings-reloaded\",\"ts\":1,\"data\":{\"language\":\"es\",\"reloaded\":true}}\n" +
            "{\"type\":\"timers-changed\",\"ts\":1,\"data\":{\"server\":1303,\"npc\":5}}\n" +
            "{\"type\":\"boss-alert\",\"ts\":1,\"data\":{\"text\":\"Gartua 14:05\"}}\n" +
            "{\"type\":\"node-name-wanted\",\"ts\":1,\"data\":{\"code\":42}}\n" +
            "{\"type\":\"price-polled\",\"ts\":1,\"data\":{\"ran\":false,\"reason\":\"offline\",\"items\":0,\"errors\":0,\"next\":0}}\n" +
            "{\"type\":\"error\",\"ts\":1,\"data\":{\"message\":\"no fight x\"}}\n" +
            "{\"type\":\"position\",\"ts\":1,\"data\":{\"map\":1010,\"x\":1,\"y\":2,\"z\":0,\"exact\":true,\"ageS\":0,\"samples\":1,\"heading\":null,\"selfId\":0,\"live\":[]}}\n"));
        Assert.Equal("reloaded es", Take());
        Assert.Equal("timers", Take());
        Assert.Equal("alert Gartua 14:05", Take());
        Assert.Equal("node 42", Take());
        Assert.Equal("polled False offline 0 0", Take());
        Assert.Equal("error no fight x", Take());
        Assert.Equal("position 1010", Take());
        Assert.Equal(1010, client.LastPosition?.Map);

        client.Timer("killed", 1303, 5, 0);
        var buf = new byte[512];
        var got = Encoding.UTF8.GetString(buf, 0, await server.ReceiveAsync(buf).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("{\"cmd\":\"timer\",\"id\":\"killed\",\"server\":1303,\"npc\":5,\"value\":0}\n", got);
        server.Close();
        Assert.Equal("down", Take());
        File.Delete(path);
    }
}
