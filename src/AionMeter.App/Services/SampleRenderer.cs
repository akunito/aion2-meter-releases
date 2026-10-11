using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AionMeter.App.Windows;
using AionMeter.Core.Events;

namespace AionMeter.App.Services;

/// <summary>
/// Developer tool (<c>--render-sample out.png</c>): draws the overlay or a window with a scripted fight of every class
/// into a PNG, without showing a window, capturing traffic or touching settings and history — safe while the game is
/// running. <c>--animate &lt;seconds&gt;</c> writes the overlay frame by frame instead (into the folder given), for videos.
/// </summary>
public static class SampleRenderer
{
    /// <param name="Window">overlay (default), overlay-update, overlay-crowd (a world boss whose crowd's damage never
    /// arrives), breakdown, history, timers, settings, restore (the Restore-from-code dialog after a restore), update, update-portable, build or build-es (the Build tab, skill texts in Spanish), build-daev (its Daevanion sub-tab), import (the questlog import dialog), items (the Items tab with the sample inventories), atlas / top / prices / craft (that tab inside the overlay card, made-up data).</param>
    /// <param name="Width">Overlay width (default 560): narrow sizes show how the footer copes.</param>
    /// <param name="Tab">Breakdown tab: dps (default), accuracy, rotation or defense. Build: done (finished skills hidden), tip (Explosion Trap and Deadshot tooltips), rel (Marking Shot selected), suggest (no build: suggestions card with "Your questlog builds") or update ("Check questlog" found a newer version). Import: mine / mine-all ("My builds", class filter on / off). Items: search ("odyle" typed in the box), list (every card as a list, Equipped open) or es (item names in Spanish). Craft: profit (the profit list against the sample market).</param>
    /// <param name="Backdrop">Behind the translucent overlay: game (a stand-in colour), dark (a navy gradient) or none (transparent).</param>
    /// <param name="RowSize">Overlay row size in percent (Settings → Overlay → Row size).</param>
    /// <param name="AbyssLogs">Inject a sample Abyss Logs percentile line (A2M-38) for the sample fight: breakdown header, overlay status line.</param>
    /// <param name="Icons">build / build-es: icon folder laid out as the icon cache (skill\&lt;stem&gt;.png); items: &lt;item id&gt;.png in the same folder; nothing is downloaded.</param>
    public sealed record Options(string Window = "overlay", int? Width = null, int? Height = null, string? Tab = null,
        string Backdrop = "game", double Scale = 1.5, int RowSize = 100, string? Icons = null, bool AbyssLogs = false);

    public static void Render(string path, string language, Options o)
    {
        using var meter = CreateMeter(language, o.RowSize);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 20_500; // a fight that is still going
        var fight = new SampleFight(start, 20);
        fight.FeedUntil(meter.Tracker, start + 20_000);
        if (o.Window == "overlay-crowd") fight.FeedCrowd(meter.Tracker, start + 20_000, 124);
        if (o.Window == "history")
            foreach (var entry in meter.History.List().Take(20)) meter.PortraitOf(entry);
        WaitForPortraits(meter);
        if (o.AbyssLogs && meter.Tracker.Snapshot(null, start + 20_000) is { } shown)
        {
            // a made-up Elementalist cell of the 100–150K CP bracket, through the real template and interpolation
            var cell = new Core.Ranking.AbyssLogsCell("Elementalist", 100_000, 150_000, 4210, 1180, 1_250_000, 1_020_000, 410_000, 300_000, 90_000, 2_400_000, null);
            meter.ShowAbyssLogs(shown.Id, UiText.Current.AbyssLogsLine(Core.Ranking.AbyssLogs.Estimate(cell, 560_000), Core.Events.GameClass.Elementalist, shown.Title));
        }

        Window host;
        Action refresh;
        switch (o.Window)
        {
            case "breakdown":
                var segment = meter.Tracker.Segments()[0].Id;
                var breakdown = new BreakdownWindow(new ViewModels.LiveFightView(meter.Tracker, segment), 2, meter.Data, meter.Icons, meter.Portraits, meter.AbyssLogsNoteFor)
                    { Width = o.Width ?? 900, Height = o.Height ?? 680 };
                if (o.Tab is { } tab) breakdown.ShowTab(tab);
                host = breakdown;
                refresh = breakdown.ReloadNow;
                break;
            case "history":
                var history = new HistoryWindow(meter) { Width = 900, Height = 600 };
                host = history;
                refresh = history.Reload;
                break;
            case "timers":
                FeedTimers(meter);
                WaitForPortraits(meter);
                var timers = new BossTimersWindow(meter) { Width = o.Width ?? 780, Height = o.Height ?? 640 };
                host = timers;
                refresh = timers.ReloadNow;
                break;
            case "build" or "build-es":
                meter.Settings.SkillLanguage = o.Window == "build-es" ? "es" : "en";
                var icons = new IconCache(Core.GameDb.ItemIcons.Empty, o.Icons ?? Path.Combine(Path.GetTempPath(), "aion2-render-icons")) { Enabled = false };
                var build = new Views.BuildView(meter, icons);
                if (o.Tab == "done") build.SetShowDone(false);
                if (o.Tab == "suggest") build.ShowSampleSuggestions(SampleProfile(), SampleSuggestions(), Core.Builds.QuestlogMine.OfClass(SampleMine().Skills, static b => b.ClassId, "ranger"));   // no active build: the suggestions card ("Your questlog builds" first)
                else build.ShowSample(SampleBuild(), SampleProfile(), o.Tab == "rel" ? 14090000 : 14170000);   // Explosion Trap selected (--tab rel: Marking Shot — no note, so the placeholder, plus its E chain and Precision links)
                if (o.Tab == "update") build.SampleCheck(SampleMine().Skills.Find(static b => b.Id == 15969)! with { UpdatedAt = new DateTimeOffset(2026, 10, 11, 9, 30, 0, TimeSpan.Zero) });   // "Check questlog" found a newer version
                var page = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D));
                // --tab tip: Explosion Trap's and Deadshot's tooltips side by side (a ToolTip never opens offscreen)
                host = o.Tab == "tip" ? new Window { Content = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = page, Children = { build.TipPreview(14170000), build.TipPreview(14010000) } }, Width = o.Width ?? 720, Height = o.Height ?? 640, Background = page }
                                      : new Window { Content = build, Width = o.Width ?? 1000, Height = o.Height ?? (o.Tab == "suggest" ? 720 : 1800), Background = page };
                refresh = o.Tab == "tip" ? () => { } : build.RenderNow;
                break;
            case "import":
                var import = new ImportBuildWindow("ranger", meter.Settings) { Width = o.Width ?? 760, Height = o.Height ?? 680 };
                var all = SampleSuggestions();
                all.Insert(2, all[0] with { Id = 1, Name = "Templar tank (filtered out of a ranger search)", ClassId = "templar" });
                if (o.Tab is "mine" or "mine-all")   // "My builds" of Diego's questlog user (7 skill builds, 6 plans; mine-all = "All classes" ticked)
                {
                    if (o.Tab == "mine-all") ((System.Windows.Controls.CheckBox)import.FindName("AllClasses")).IsChecked = true;
                    import.ShowSampleMine("AXIIyLxFWMU0", SampleMine());
                }
                else import.ShowSample("https://questlog.gg/aion-2/en/skill-builder/AXIIyLxFWMU0?build-id=15969", new Core.Builds.BuildSearchPage(all, 1, 4, 74));
                host = import;
                refresh = () => { };
                break;
            case "items":
                // the Items tab with the three real inventories of 2026-10-11 (data/gamedb/sample-items.json); icons from <icons>\<item id>.png
                meter.Settings.PriceSearchLanguage = o.Tab == "es" ? "es" : "en";
                var itemIcons = new IconCache(Core.GameDb.ItemIcons.Load(Path.Combine(MeterService.DataDirectory, "gamedb", "item-icons.json")), o.Icons ?? Path.Combine(Path.GetTempPath(), "aion2-render-icons")) { Enabled = false };
                var itemsView = new Views.ItemsView(meter, itemIcons);
                itemsView.ShowSample(SampleItems());
                if (o.Tab == "search") itemsView.SampleSearch("odyle");
                if (o.Tab == "list") itemsView.SampleListMode();
                host = new Window { Content = itemsView, Width = o.Width ?? 1180, Height = o.Height ?? 1500, Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)) };
                refresh = itemsView.RenderNow;
                break;
            case "build-daev":
                // the Daevanion sub-tab with Irin's open nodes and questlog planner 8602; "daev:" icons come from <icons>\daev\<sprite stem>[_Disabled].png
                meter.Settings.SkillLanguage = "en";
                var daevIcons = new IconCache(Core.GameDb.ItemIcons.Empty, o.Icons ?? Path.Combine(Path.GetTempPath(), "aion2-render-icons")) { Enabled = false };
                var daevView = new Views.BuildView(meter, daevIcons);
                var (daevBuild, daevProfile) = SampleDaevanion(SampleBuild(), SampleProfile());
                daevView.ShowSample(daevBuild, daevProfile, 14170000);
                ((System.Windows.Controls.RadioButton)daevView.FindName("SubDaev")).IsChecked = true;
                host = new Window { Content = daevView, Width = o.Width ?? 1000, Height = o.Height ?? 900, Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)) };
                refresh = daevView.RenderNow;
                break;
            case "settings":
                host = new SettingsWindow(meter) { Width = o.Width ?? 640, Height = o.Height ?? 900 };
                refresh = () => { };
                break;
            case "restore":
                var restore = new RestoreWindow(meter) { Width = o.Width ?? 520, Height = o.Height ?? 250, SizeToContent = SizeToContent.Manual };
                restore.ShowSample("3F9C-0A12-B7E4-4D85-A6C1-E09F-2B7D-5E31", string.Format(UiText.Current.RestoreDone, 7, 5, 2) + "; " + UiText.Current.RestoreWatchRestored + "; " + UiText.Current.RestoreFavRestored + ".");
                host = restore;
                refresh = () => { };
                break;
            case "update" or "update-portable":
                var release = SampleRelease(meter.Updates.Current);
                meter.Updates.Preview(release);
                host = new UpdateWindow(meter.Updates, release, installed: o.Window == "update") { Width = 580, Height = 560 };
                refresh = () => { };
                break;
            case "atlas" or "top" or "prices" or "craft":
                // a tab inside the overlay card as the player sees it, filled with made-up data (README screenshots); --tab profit: Craft's profit list
                var card = new OverlayWindow(meter) { Width = o.Width ?? 560, Height = o.Height ?? 820 };
                var tabIcons = new IconCache(Core.GameDb.ItemIcons.Load(Path.Combine(MeterService.DataDirectory, "gamedb", "item-icons.json")), o.Icons ?? Path.Combine(Path.GetTempPath(), "aion2-render-icons")) { Enabled = false };
                card.SelectTab(o.Window);
                Action fill = () => { };
                switch (card.TabView)
                {
                    case Views.AtlasView atlasView:
                        var (ax, ay, pets) = SampleAtlas(meter);
                        atlasView.ShowSample(ax, ay, 2.5, pets);
                        fill = atlasView.RenderNow;
                        break;
                    case Views.TopDpsView topView:
                        topView.ShowSample(SampleTop(meter), GameClass.Ranger, meter.Data.ServerName(2305));
                        break;
                    case Views.PriceWatchView pricesView:
                        pricesView.ShowSample(SamplePrices(), tabIcons, DateTimeOffset.Now.AddMinutes(-12));
                        break;
                    case Views.CraftView craftView:
                        var sample = SampleItems();
                        meter.Settings.CraftFavourites.Add(110240012);   // Expert's Splendent Orichalcum Longsword pinned
                        craftView.ShowSample(StockOf(sample, sample.CurrentKey), sample.Market, tabIcons, "near", o.Tab == "profit");
                        break;
                }
                host = card;
                refresh = () => { card.Refresh(); fill(); };
                break;
            default:
                if (o.Window == "overlay-update") meter.Updates.Preview(SampleRelease(meter.Updates.Current)); // with the footer's update banner
                var overlay = new OverlayWindow(meter) { Width = o.Width ?? 560, Height = o.Height ?? 820 };
                // --tab viewing: the selector while Akunita plays and Irin is viewed (eye + amber); --tab current: viewing the logged-in one
                if (o.Tab is "viewing" or "current") overlay.SampleCharacters(["2001-Akunita", "2001-Arielka", "2001-Irin"], "2001-Akunita", o.Tab == "viewing" ? "2001-Irin" : "2001-Akunita");
                host = overlay;
                refresh = overlay.Refresh;
                break;
        }
        Capture(host, refresh, path, o);
    }

    /// <summary>
    /// The overlay through a scripted boss fight, <paramref name="fps"/> frames a second on the fight's own clock
    /// (frame_000.png …): from "waiting for combat" through the Elementalist taking the lead.
    /// </summary>
    public static void Animate(string folder, string language, Options o, double seconds, int fps)
    {
        using var meter = CreateMeter(language, o.RowSize);
        WaitForPortraits(meter);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 600_000;
        var fight = new SampleFight(start, seconds);
        var clock = start;
        OverlayWindow.ClockOverride = () => clock;
        try
        {
            var overlay = new OverlayWindow(meter) { Width = o.Width ?? 500, Height = o.Height ?? 760 };
            Directory.CreateDirectory(folder);
            var frames = (int)Math.Round(seconds * fps);
            for (var f = 0; f <= frames; f++)
            {
                clock = start + (long)(f * 1000.0 / fps);
                fight.FeedUntil(meter.Tracker, clock);
                Capture(overlay, overlay.Refresh, Path.Combine(folder, $"frame_{f:000}.png"), o);
            }
        }
        finally
        {
            OverlayWindow.ClockOverride = null;
        }
    }

    private static MeterService CreateMeter(string language, int rowSize = 100)
    {
        var settings = new AppSettings
        {
            Transient = true, SaveHistory = false, MaxRows = 10, LayoutVersion = 99, Language = language, RowSize = rowSize,
        };
        UiText.Use(settings.Language);
        var meter = new MeterService(settings, _ => new QuietSource());
        meter.StartCapture(); // the footer reads "Capturing", as it does in the game
        meter.Portraits.Get(SampleBossCode); // first run only; then it comes from the cache
        return meter;
    }

    /// <summary>A capture that reports what a working one does and produces nothing: the script feeds the fight.</summary>
    private sealed class QuietSource : Core.Capture.IEventSource
    {
        public event Action<GameEvent>? EventDecoded { add { } remove { } }
        public event Action<Core.Capture.CaptureStatus>? StatusChanged { add { } remove { } }
        public Core.Capture.CaptureStatus Status { get; } = new(Core.Capture.CaptureState.Capturing, "Capturing · 127.0.0.1");
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static void WaitForPortraits(MeterService meter)
    {
        for (var i = 0; i < 80 && meter.Portraits.Busy; i++) Thread.Sleep(100);
    }

    private static void Capture(Window host, Action refresh, string path, Options o)
    {
        refresh();
        if (host.Content is not FrameworkElement root) return;
        root.Measure(new Size(host.Width, host.Height));
        root.Arrange(new Rect(0, 0, host.Width, host.Height));
        root.UpdateLayout();
        refresh();
        root.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)(host.Width * o.Scale), (int)(host.Height * o.Scale), 96 * o.Scale, 96 * o.Scale, PixelFormats.Pbgra32);
        if (Backdrop(host, o.Backdrop) is { } backdrop)
        {
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(backdrop, null, new Rect(0, 0, host.Width, host.Height));
            bmp.Render(bg);
        }
        bmp.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>What shows through the translucent overlay card; other windows draw their own background.</summary>
    private static Brush? Backdrop(Window host, string backdrop)
    {
        if (host is not OverlayWindow) return host.Background;
        return backdrop switch
        {
            "none" => null,
            "dark" => new LinearGradientBrush(
                new GradientStopCollection { new(Color.FromRgb(0x24, 0x31, 0x52), 0), new(Color.FromRgb(0x11, 0x17, 0x29), 0.55), new(Color.FromRgb(0x07, 0x09, 0x10), 1) },
                new Point(0, 0), new Point(1, 1)),
            _ => new SolidColorBrush(Color.FromRgb(0x2B, 0x3A, 0x2E)), // a stand-in for the game
        };
    }

    private const int SampleBossCode = 2701250; // Balhash, a field boss

    /// <summary>Irin's questlog build 15969 (data/gamedb/sample-build.json, copied from the test fixture by build.sh).</summary>
    private static Core.Builds.Build SampleBuild()
    {
        var path = Path.Combine(MeterService.DataDirectory, "gamedb", "sample-build.json");
        var b = File.Exists(path) ? Core.Builds.QuestlogBuilds.ParseBuild(File.ReadAllText(path)) : null;
        b ??= new Core.Builds.Build { Name = "sample", ClassId = "ranger" };
        b.Notes[14170000] = "Situational (key 3). Equipped at level 1: the build's biggest gap, raise it to 12.";   // from the approved mock
        b.Notes[14010000] = "Opener after Marking Shot (Precision +35%). Charge to 3 only in burst windows; Snipe hits shave its cooldown.";
        // the fixture is a bare build without its author: the URL's slug, so the header shows "Open on questlog.gg" as an import does
        return b with { Id = "sample", Source = new Core.Builds.BuildSource(15969, "AXIIyLxFWMU0", "akunito88") };
    }

    /// <summary>Five made-up ranger builds shaped as questlog search rows (the first is the real most-liked ranger build of 2026-10-11).</summary>
    private static List<Core.Builds.BuildSummary> SampleSuggestions()
    {
        (long Id, string Name, string Author, int Likes, string[] Tags, int Day)[] rows =
        [
            (9025, "Ranger updated 10.08", "whelps", 71, [], 8),
            (8120, "Ranger PvE — Vaizel top 1 setup", "Vaizel", 25, ["pve", "dungeon"], 6),
            (10411, "Bow PvP burst (arena)", "Kestrel", 9, ["pvp"], 9),
            (7754, "Leveling 1-45 easy mode", "Nimbus", 6, ["leveling"], 2),
            (11002, "Trap control (Abyss)", "Sable", 4, ["pvp", "abyss"], 10),
        ];
        var list = new List<Core.Builds.BuildSummary>(rows.Length);
        foreach (var r in rows)
            list.Add(new Core.Builds.BuildSummary(r.Id, r.Name, "ranger", r.Author, "X2aYOSzHtbA6", r.Likes, new DateTimeOffset(2026, 10, r.Day, 12, 0, 0, TimeSpan.Zero), r.Tags, true,
                new Core.Builds.Build { Name = r.Name, ClassId = "ranger" }));
        return list;
    }

    /// <summary>Diego's questlog user AXIIyLxFWMU0 as of 2026-10-11: names, classes, tags and dates of his 7 skill builds and 6 planner builds (no skills or nodes).</summary>
    private static QuestlogService.MyBuilds SampleMine()
    {
        (long Id, string Name, string Cls, string[] Tags, int Day, int Hour)[] skills =
        [
            (13277, "P1 Farmeo solo (Leonis Midgame + aguante)", "templar", ["pve"], 4, 7), (13278, "P2 Mazmorras y jefes (Leonis Midgame)", "templar", ["pve"], 4, 7),
            (13279, "P3 Peleas duras (Leonis Defensive)", "templar", ["pve"], 4, 7), (15969, "Irin — Ranger PvE 45 (ref. whelps 10.08 + top Vaizel)", "ranger", ["pve", "dps"], 9, 20),
            (16049, "Arielka - Chanter PvE (ref. dimach + top Vaizel)", "chanter", ["pve", "dps"], 9, 23), (16051, "Akunita - Templar PvE 45 (top Vaizel, 2026-10-10)", "templar", ["pve", "dps"], 9, 23),
            (16056, "Akuna - Cleric PvE (ref. MrRosaPony + top Vaizel)", "cleric", ["pve", "dps"], 9, 23),
        ];
        (long Id, string Name, string Cls, string[] Tags, int Day, int Hour)[] plans =
        [
            (6035, "Akunita", "templar", [], 4, 6), (6046, "Akunita (Aligned)", "templar", [], 4, 6), (8602, "Irin — Daevanion Ranger (top Vaizel)", "ranger", ["pve", "dps"], 9, 21),
            (8661, "Arielka - Daevanion Chanter (top Vaizel)", "chanter", ["pve", "dps"], 9, 23), (8664, "Akunita - Daevanion Templar (top Vaizel, 2026-10-10)", "templar", ["pve", "dps"], 9, 23),
            (8670, "Akuna - Daevanion Cleric (top Vaizel)", "cleric", ["pve", "dps"], 9, 23),
        ];
        var s = new List<Core.Builds.BuildSummary>(skills.Length);
        foreach (var r in skills)
        {
            var at = new DateTimeOffset(2026, 10, r.Day, r.Hour, 0, 0, TimeSpan.Zero);
            s.Add(new Core.Builds.BuildSummary(r.Id, r.Name, r.Cls, "akunito88", "AXIIyLxFWMU0", 0, at, r.Tags, false,
                new Core.Builds.Build { Name = r.Name, ClassId = r.Cls, Source = new Core.Builds.BuildSource(r.Id, "AXIIyLxFWMU0", "akunito88"), UpdatedAt = at }));
        }
        var p = new List<Core.Builds.QuestlogDaevPlan>(plans.Length);
        foreach (var r in plans) p.Add(new Core.Builds.QuestlogDaevPlan(r.Id, r.Name, r.Cls, new(), new DateTimeOffset(2026, 10, r.Day, r.Hour, 0, 0, TimeSpan.Zero), r.Tags));
        return new QuestlogService.MyBuilds(s, p, DateTimeOffset.UtcNow);
    }

    /// <summary>Irin's official profile of 2026-10-10 as far as skills go (Ranger 45): current levels, four build skills not acquired.</summary>
    private static Core.Armory.CharacterProfile SampleProfile()
    {
        (int Id, string Cat, int Level)[] have =
        [
            (14010000, "Active", 13), (14020000, "Active", 12), (14050000, "Active", 12), (14070000, "Active", 11), (14080000, "Active", 12), (14090000, "Active", 8),
            (14110000, "Active", 13), (14130000, "Active", 12), (14170000, "Active", 1), (14340000, "Active", 12), (14310000, "Dp", 10), (14380000, "Dp", 7),
            (14740000, "Passive", 13), (14750000, "Passive", 13), (14800000, "Passive", 13), (14710000, "Passive", 1), (14720000, "Passive", 2), (14770000, "Passive", 2),
            (14730000, "Passive", 2), (14780000, "Passive", 2),
            (14220000, "Dp", 0), (14060000, "Dp", 0), (14360000, "Dp", 0), (14330000, "Active", 0),
        ];
        var skills = have.Select(h => new Core.Armory.SkillLine(h.Cat, h.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), h.Level, h.Level > 0, h.Level > 0, h.Id)).ToList();
        return new Core.Armory.CharacterProfile(new Core.Armory.CharacterRef("sample", "Irin", 0, 45, 2305, "Vaizel", "eu"), "Ranger", 45, 47_000, null, null, null, [], 1042, [], [], null, null,
            skills, [], [], new DateTimeOffset(2026, 10, 10, 18, 46, 0, TimeSpan.Zero));
    }

    /// <summary>Akunita bound, Irin and Arielka on disk, their shared warehouse seeded as on first run, and the market prices they need (data/gamedb/sample-items.json, tools/items-sample.py).</summary>
    private static Views.ItemsView.Data SampleItems()
    {
        var path = Path.Combine(MeterService.DataDirectory, "gamedb", "sample-items.json");
        var chars = new List<Views.ItemsView.CharData>();
        var market = Core.Market.MarketSnapshot.Empty;
        var now = DateTimeOffset.Now;
        if (File.Exists(path))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var latest = DateTime.MinValue;
            foreach (var c in doc.RootElement.GetProperty("characters").EnumerateArray())
            {
                var stacks = new List<Core.Inventory.ItemStack>();
                foreach (var r in c.GetProperty("stacks").EnumerateArray())
                    stacks.Add(new Core.Inventory.ItemStack(r[0].GetUInt32(), r[1].GetInt32(), r[2].GetInt32(), (byte)r[3].GetInt32(), (byte)r[4].GetInt32(), r[5].GetInt32()));
                var written = c.GetProperty("writtenUtc").GetDateTime().ToUniversalTime();
                if (written > latest) latest = written;
                chars.Add(new Views.ItemsView.CharData(c.GetProperty("key").GetString()!, c.GetProperty("cls").GetString() ?? "", new Core.Inventory.InventoryFile(stacks, new(), written)));
            }
            market = Core.Market.MarketSnapshot.Parse(doc.RootElement.GetProperty("market").GetRawText());
            if (latest > DateTime.MinValue) now = new DateTimeOffset(DateTime.SpecifyKind(latest, DateTimeKind.Utc)).ToLocalTime().AddMinutes(10);
        }
        var account = new Core.Inventory.AccountWarehouse(null);
        account.Seed("1303", chars.Where(c => c.File is not null).Select(c => (c.Key, c.File!)));
        return new Views.ItemsView.Data("1303-Akunita", "1303", "Vaizel", chars, account.View("1303"), market, now);
    }

    /// <summary>Verteron at Kernon of the West: the map's own seeded Orichalcum/Inina spots turned into live, out-of-view and emptied
    /// nodes (respawn learned), Kernon killed with a countdown, made-up pet levels (0-3 by turns).</summary>
    private static (float X, float Y, Dictionary<int, int> Pets) SampleAtlas(MeterService meter)
    {
        const int map = 1010; const uint self = 2; const float cx = -88000, cy = -46000;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var atlas = meter.Atlas;
        atlas.SetMap(map);
        atlas.OnEvent(new SelfIdentifiedEvent(now - 60_000, self, "Sylvaen", 2305, GameClass.Elementalist));
        var near = atlas.Spots(map).Where(sp => sp.Seeded && sp.Name is "Orichalcum" or "Inina")
            .OrderBy(sp => (sp.X - cx) * (sp.X - cx) + (sp.Y - cy) * (sp.Y - cy)).Take(9).ToList();
        atlas.OnEvent(new PositionEvent(now - 3_000, self, cx, cy, 0));
        uint id = 50_000;
        for (var i = 0; i < near.Count; i++)
        {
            var sp = near[i]; var code = sp.Name == "Inina" ? 1500042 : 1500004; var oid = id++;
            if (i < 7) atlas.OnEvent(new ObjectSeenEvent(now - 2_000, oid, 1, code, sp.X, sp.Y, 0, i % 3, now - 600_000));
            if (i is 5 or 6) atlas.OnEvent(new ObjectGoneEvent(now - 1_000, oid, false));   // walked out of view: "N?"
            if (i >= 7)
            {
                // emptied, back 20 min later, emptied again 6 min ago: the countdown says 14 min
                atlas.OnEvent(new ObjectSeenEvent(now - 2_400_000, oid, 1, code, sp.X, sp.Y, 0, 0, now - 2_700_000));
                atlas.OnEvent(new ObjectGoneEvent(now - 2_280_000, oid, true));
                atlas.OnEvent(new ObjectSeenEvent(now - 1_000_000, oid + 100, 1, code, sp.X, sp.Y, 0, 0, now - 2_280_000 + 1_200_000));
                atlas.OnEvent(new ObjectGoneEvent(now - 360_000, oid + 100, true));
            }
        }
        meter.Timers.SetCurrentServer(2305);
        meter.Timers.OnNotice(new Core.Combat.BossNotice(2100050, map, null, Core.Combat.BossNoticeKind.Killed, now - 50 * 60_000, ServerId: 2305));
        meter.Timers.SetRespawn(2305, 2100050, 90);
        meter.Settings.AtlasZoneKinds.Clear(); meter.Settings.AtlasZoneKinds.Add("orichalcum");
        var pets = new Dictionary<int, int>();
        var n = 0;
        foreach (var p in meter.Pets.All) pets[p.Id] = n++ % 4;
        return (cx, cy, pets);
    }

    /// <summary>Made-up "My fights" for the Top DPS tab: ten rangers and a few of every other class on Verteron's field bosses,
    /// each with a 1.5-3.5 min rotation that repeats a class opener, so opener, repeated sequences and cadence read like real ones.</summary>
    private static Dictionary<GameClass, List<Core.Ranking.TopEntry>> SampleTop(MeterService meter)
    {
        var rnd = new Random(2305);
        (GameClass Cls, double Dps, int[] Skills, string[] Names)[] classes =
        [
            (GameClass.Ranger, 410_000, [14090000, 14010000, 14020000, 14050000, 14070000, 14080000, 14110000, 14130000, 14340000], ["Talwyn", "Kestrel", "Sable", "Nimbus", "Fenra", "Oriel", "Varka", "Lysse", "Corvan", "Ashe"]),
            (GameClass.Gladiator, 470_000, [11010000, 11020000, 11030000, 11040000], ["Borgrim", "Thrain", "Kael", "Drogo"]),
            (GameClass.Templar, 300_000, [12010000, 12020000, 12030000], ["Aegisa", "Brann", "Ivor"]),
            (GameClass.Assassin, 390_000, [13010000, 13020000, 13120000], ["Nyxara", "Vex", "Shade", "Rook", "Mira"]),
            (GameClass.Sorcerer, 360_000, [15010000, 15020000, 15030000], ["Morwen", "Ilsa", "Quorra"]),
            (GameClass.Elementalist, 520_000, [16010000, 16020000, 16030000, 16040000, 16140000], ["Sylvaen", "Quill", "Ember", "Tamsin"]),
            (GameClass.Cleric, 160_000, [17010000, 17020000, 17030000, 17040000], ["Celesta", "Aurel"]),
            (GameClass.Chanter, 150_000, [18010000, 18020000, 18030000, 18040000], ["Hymnar", "Odo", "Brisa"]),
        ];
        (string Boss, int Code)[] bosses = [("Kernon of the West", 2100050), ("Blooming Korin", 2100141), ("Kusan the Mad Gladiator", 2100076), ("Furious Saursus", 2100177), ("Bloodfang Pnyn", 2100178)];
        var today = DateTimeOffset.Now.Date;
        var all = new Dictionary<GameClass, List<Core.Ranking.TopEntry>>();
        foreach (var (cls, baseDps, skills, names) in classes)
        {
            var list = new List<Core.Ranking.TopEntry>(names.Length);
            for (var i = 0; i < names.Length; i++)
            {
                var dps = baseDps * (1.18 - i * 0.055) * (0.97 + rnd.NextDouble() * 0.06);
                var ms = (long)(90_000 + rnd.Next(0, 120_000));
                var (boss, code) = bosses[rnd.Next(bosses.Length)];
                // opener = the skill list in order, then the same 4-cast loop with a random skill in between now and then
                var casts = new List<Core.Ranking.TopCast>(256);
                var t = 600;
                foreach (var sk in skills) { casts.Add(new(t, sk)); t += 900 + rnd.Next(400); }
                var loop = skills.Length >= 4 ? new[] { skills[0], skills[1], skills[2], skills[3] } : skills;
                for (var k = 0; t < ms; k++)
                {
                    casts.Add(new(t, rnd.NextDouble() < 0.2 ? skills[rnd.Next(skills.Length)] : loop[k % loop.Length]));
                    t += 900 + rnd.Next(500);
                }
                var hits = new Dictionary<int, int>();
                foreach (var c in casts) hits[c.S] = hits.GetValueOrDefault(c.S) + 1;
                var weights = skills.ToDictionary(sk => sk, sk => hits.GetValueOrDefault(sk) * (0.6 + rnd.NextDouble()));
                var sum = weights.Values.Sum();
                var damage = (long)(dps * ms / 1000);
                var table = skills.Select(sk => new Core.Ranking.TopSkill(sk, meter.Data.SkillName(sk), hits.GetValueOrDefault(sk), (long)(damage * weights[sk] / sum), weights[sk] / sum))
                    .OrderByDescending(x => x.Damage).ToList();
                list.Add(new Core.Ranking.TopEntry(names[i], cls, 2305, false, dps, damage, ms, boss, "Verteron", code,
                    new DateTimeOffset(today.AddDays(-rnd.Next(0, 6)).AddHours(18 + rnd.Next(0, 5)).AddMinutes(rnd.Next(60))), "sample.json", table, casts));
            }
            all[cls] = list;
        }
        return all;
    }

    /// <summary>Six made-up watches over items of the sample market (Vaizel, 2026-10-08): two buys and a sell at their price, two not yet, one holding in profit.</summary>
    private static List<Core.Market.PriceWatch> SamplePrices()
    {
        var now = DateTimeOffset.Now;
        (int Item, string Name, long Max, bool Sell, long Last, long DayAgo, int Listings, int Qty, long Paid)[] rows =
        [
            (610511013, "Wrathful Mind", 1_850_000, false, 1_799_990, 1_890_000, 31, 0, 0),
            (610511014, "Wrathful Will", 250_000, false, 240_000, 236_000, 29, 0, 0),
            (610620016, "Radiant Orichalcum Ore", 60_000, true, 64_999, 61_500, 39, 40, 52_000),
            (610610016, "Radiant Odyle", 300_000, false, 355_544, 349_000, 84, 0, 0),
            (610530010, "Artisan's Ultimate Refining Stone", 200_000, false, 225_000, 241_000, 33, 0, 0),
            (110240012, "Expert's Splendent Orichalcum Longsword", 3_500_000, true, 2_950_000, 3_020_000, 23, 1, 2_400_000),
        ];
        return rows.Select(r => new Core.Market.PriceWatch
        {
            Item = r.Item, Name = r.Name, MaxPrice = r.Max, Sell = r.Sell, LastPrice = r.Last, DayAgoPrice = r.DayAgo, LastListings = r.Listings,
            Quantity = r.Qty, PaidEach = r.Paid, LastCheck = now.AddMinutes(-12), SampledAt = now.AddMinutes(-31),
        }).ToList();
    }

    /// <summary>One character's stock from the sample inventories (every container but the equipped set, as the live count does).</summary>
    private static Func<int, int> StockOf(Views.ItemsView.Data d, string key)
    {
        var count = new Dictionary<int, int>();
        foreach (var c in d.Chars)
            if (c.Key == key && c.File is { } f)
                foreach (var st in f.Stacks)
                    if (st.Container != Core.Inventory.Containers.Equipped) count[st.Item] = count.GetValueOrDefault(st.Item) + st.Count;
        return item => count.GetValueOrDefault(item);
    }

    /// <summary>Irin's open Daevanion nodes (profile) and questlog planner 8602 (plan) from data/gamedb/sample-daevanion.json (build.sh copies the test fixture).</summary>
    private static (Core.Builds.Build, Core.Armory.CharacterProfile) SampleDaevanion(Core.Builds.Build b, Core.Armory.CharacterProfile p)
    {
        var path = Path.Combine(MeterService.DataDirectory, "gamedb", "sample-daevanion.json");
        if (!File.Exists(path)) return (b, p);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var plan = new Dictionary<int, List<int>>();
        foreach (var board in doc.RootElement.GetProperty("selectedNodes").EnumerateObject())
            plan[int.Parse(board.Name, System.Globalization.CultureInfo.InvariantCulture)] = board.Value.EnumerateArray().Select(x => int.Parse(x.GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var open = new List<Core.Armory.DaevanionNode>();
        foreach (var board in doc.RootElement.GetProperty("irin").EnumerateObject())
            foreach (var x in board.Value.EnumerateArray())
            {
                var id = int.Parse(x.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                var cell = id % 10000 - 1;   // id = board*10000 + (row-1)*15 + col
                open.Add(new Core.Armory.DaevanionNode(id / 10000, cell / 15 + 1, cell % 15 + 1, id, true, "", "", "", []));
            }
        return (b with { Daevanion = plan }, p with { Daevanion = open });
    }

    /// <summary>
    /// A scripted boss fight: every class with its own skills, crits, back attacks and perfect hits. The Elementalist
    /// ("you") starts slow and takes the lead after a burst; the Brawler joins late. The same random seed every time,
    /// so screenshots and animation frames repeat exactly.
    /// </summary>
    private sealed class SampleFight
    {
        private const uint Boss = 900_001;
        private const long BossHp = 90_000_000;

        private sealed record Member(uint Id, string Name, GameClass Class, int[] Skills, Func<double, double> Dps, double JoinAt = 0);

        private static readonly Member[] Party =
        [
            new(1, "Borgrim", GameClass.Gladiator, [11010000, 11020000, 11030000, 11040000], t => t < 6 ? 500_000 : 420_000),
            new(2, "Sylvaen", GameClass.Elementalist, [16010000, 16020000, 16030000, 16040000, 16140000], t => t < 4 ? 280_000 : t < 9 ? 1_000_000 : 560_000),
            new(3, "Talwyn", GameClass.Ranger, [14010000, 14020000, 14030000, 14040000, 14050000], _ => 340_000),
            new(4, "Aegisa", GameClass.Templar, [12010000, 12020000, 12030000], _ => 280_000),
            new(5, "Nyxara", GameClass.Assassin, [13010000, 13020000, 13120000], t => t is > 7 and < 11 ? 640_000 : 230_000),
            new(6, "Morwen", GameClass.Sorcerer, [15010000, 15020000, 15030000], _ => 250_000),
            new(7, "Celesta", GameClass.Cleric, [17010000, 17020000, 17030000, 17040000], _ => 150_000),
            new(8, "Hymnar", GameClass.Chanter, [18010000, 18020000, 18030000, 18040000], _ => 130_000),
            new(9, "Grohm", GameClass.Brawler, [19010000, 19020000, 19030000], _ => 190_000, JoinAt: 2.5),
        ];

        private readonly List<GameEvent> _events = new();
        private int _next;

        public SampleFight(long start, double seconds)
        {
            var rnd = new Random(2305);
            _events.Add(new SelfIdentifiedEvent(start, 2, "Sylvaen", 2305, GameClass.Elementalist));
            foreach (var m in Party.Where(m => m.Id != 2)) _events.Add(new PlayerSeenEvent(start, m.Id, m.Name, 2305, m.Class));
            // Your party's gear score and combat power come with the roster; the others (not in your party) have none.
            _events.Add(new PartyRosterEvent(start, Party.Take(6)
                .Select(m => new PartyMemberInfo(m.Name, 2305, 45, 2700 + (int)m.Id * 61 % 400, 45_000 + (int)m.Id * 2_731 % 17_000, m.Class))
                .ToList()));
            _events.Add(new NpcSeenEvent(start, Boss, SampleBossCode, BossHp));

            var hits = new List<DamageEvent>();
            foreach (var m in Party)
            {
                for (var t = m.JoinAt + 0.2 + rnd.NextDouble() * 0.4; t < seconds; t += 0.45 + rnd.NextDouble() * 0.5)
                {
                    var amount = m.Dps(t) * 0.7 * (0.75 + rnd.NextDouble() * 0.5); // one hit every ~0.7 s on average
                    var flags = HitFlags.None;
                    if (rnd.NextDouble() < 0.24)
                    {
                        flags |= HitFlags.Critical;
                        amount *= 1.9;
                    }
                    if (rnd.NextDouble() < 0.18) flags |= HitFlags.Back;
                    else if (rnd.NextDouble() < 0.1) flags |= HitFlags.Perfect;
                    hits.Add(new DamageEvent(start + (long)(t * 1000), m.Id, Boss, m.Skills[rnd.Next(m.Skills.Length)], (long)amount, flags));
                }
            }
            hits.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));

            // The boss's HP follows the damage dealt, updated every half second like the game does.
            long dealt = 0;
            var i = 0;
            for (var at = 500L; at <= seconds * 1000 + 500; at += 500)
            {
                while (i < hits.Count && hits[i].TimeMs <= start + at)
                {
                    dealt += hits[i].Damage;
                    _events.Add(hits[i++]);
                }
                _events.Add(new NpcHpEvent(start + at, Boss, Math.Max(1, BossHp - dealt), BossHp));
            }
        }

        /// <summary>Hit notices from <paramref name="players"/> made-up players whose damage the server never sends.</summary>
        public void FeedCrowd(Core.Combat.CombatTracker tracker, long atMs, int players)
        {
            for (var i = 0; i < players; i++) tracker.Process(new HitNoticeEvent(atMs, 910_000 + (uint)i, Boss, 14030120));
        }

        /// <summary>Feeds every event up to <paramref name="untilMs"/> that has not been fed yet.</summary>
        public void FeedUntil(Core.Combat.CombatTracker tracker, long untilMs)
        {
            while (_next < _events.Count && _events[_next].TimeMs <= untilMs) tracker.Process(_events[_next++]);
        }
    }

    /// <summary>A made-up next version (one minor up) for the update window and the overlay's update banner.</summary>
    private static Core.Updates.ReleaseInfo SampleRelease(Version current) => ReleaseOf(new Version(current.Major, current.Minor + 1, 0));

    private static Core.Updates.ReleaseInfo ReleaseOf(Version next) => new(
        next, "v" + next.ToString(3), Core.Updates.UpdateFeed.ReleasesPage,
        """
        ## Что нового

        - **Проверка обновлений**: метр сам сообщает о новой версии
          и обновляется в один клик.
        - Таймеры боссов отдельно по серверам.

        ## What's new (English)

        - **Update check**: the meter tells you about a new version and updates itself in one click.
        """,
        DateTimeOffset.UtcNow.AddDays(-1),
        new Core.Updates.ReleaseAsset($"AION2DpsMeter-Setup-v{next.ToString(3)}.exe", 52_000_000, "https://example.invalid/setup.exe", null),
        new Core.Updates.ReleaseAsset($"AION2DpsMeter-v{next.ToString(3)}-win-x64.zip", 65_000_000, "https://example.invalid/app.zip", null));

    // Altgard's field boss list from a real capture (00:30:07 on 2026-10-06, UTC+3) — the in-game map's timers.
    private const string AltgardList =
        "00005604000018009de30602729d660ea10100000199e30691939bc7645092c700ecdd467f04f30da1010000009fe3066d013f0ea1010000" +
        "00a0e3066efe360ea1010000009ae306c4e9010ea1010000009be306a54a140ea1010000009ee306bc74360ea1010000009ce30630852c0e" +
        "a101000001a1e30600a57447800a0dc800f0db45095a05d40da101000000a2e3061f365b0ea101000000a3e306d4eb670ea101000001a4e3" +
        "06413f42482dc5e5c700b6974683a4f40da101000000ace30650d2de0ea101000000a5e3068054070ea101000000a6e306dae65e0ea10100" +
        "0000a7e3061737050ea101000000a8e306001166e80ea101000000a9e306ef5f4d0ea101000000aae30668a8020ea101000000abe306c284" +
        "d30ea101000000ade306fbccc10ea101000000aee3061cfdc60ea101000000afe306bf00d70ea101000000b0e3063501d10ea10100000000" +
        "00";

    private static void FeedTimers(MeterService meter)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var capturedAt = new DateTimeOffset(2026, 10, 5, 21, 30, 7, 231, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var events = new List<GameEvent>();
        new Core.Protocol.PacketParser(meter.Data, events.Add) { TimeMs = capturedAt }
            .Handle(Core.Protocol.Opcodes.FieldBossList, Core.Protocol.Wire.FromHex(AltgardList));
        if (events.FirstOrDefault() is FieldBossListEvent list)
        {
            // Shifted so the capture reads as "just now".
            var shift = now - capturedAt;
            meter.Timers.OnList(list with { TimeMs = now, Slots = list.Slots.Select(s => s with { AtMs = s.AtMs + shift }).ToList(), ServerId = 2305 });
        }
        // One boss of another map, from a kill the meter saw.
        meter.Timers.OnNotice(new Core.Combat.BossNotice(2701250, 220014, null, Core.Combat.BossNoticeKind.Killed, now - 70 * 60_000, ServerId: 2305));
        meter.Timers.SetRespawn(2305, 2701250, 120);
        meter.Timers.SetWatch(2305, 2400800, true);
        foreach (var t in meter.Timers.All().Where(t => t.NpcCode > 0)) meter.Portraits.Get(t.NpcCode);
    }
}
