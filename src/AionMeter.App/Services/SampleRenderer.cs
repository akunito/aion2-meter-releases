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
    /// arrives), breakdown, history, timers, settings, update or update-portable.</param>
    /// <param name="Width">Overlay width (default 560): narrow sizes show how the footer copes.</param>
    /// <param name="Tab">Breakdown tab: dps (default), accuracy, rotation or defense.</param>
    /// <param name="Backdrop">Behind the translucent overlay: game (a stand-in colour), dark (a navy gradient) or none (transparent).</param>
    /// <param name="RowSize">Overlay row size in percent (Settings → Overlay → Row size).</param>
    public sealed record Options(string Window = "overlay", int? Width = null, int? Height = null, string? Tab = null,
        string Backdrop = "game", double Scale = 1.5, int RowSize = 100);

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

        Window host;
        Action refresh;
        switch (o.Window)
        {
            case "breakdown":
                var segment = meter.Tracker.Segments()[0].Id;
                var breakdown = new BreakdownWindow(new ViewModels.LiveFightView(meter.Tracker, segment), 2, meter.Data, meter.Icons, meter.Portraits)
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
            case "settings":
                host = new SettingsWindow(meter) { Width = o.Width ?? 640, Height = o.Height ?? 900 };
                refresh = () => { };
                break;
            case "update" or "update-portable":
                var release = SampleRelease(meter.Updates.Current);
                meter.Updates.Preview(release);
                host = new UpdateWindow(meter.Updates, release, installed: o.Window == "update") { Width = 580, Height = 560 };
                refresh = () => { };
                break;
            default:
                if (o.Window == "overlay-update") meter.Updates.Preview(SampleRelease(meter.Updates.Current)); // with the footer's update banner
                var overlay = new OverlayWindow(meter) { Width = o.Width ?? 560, Height = o.Height ?? 820 };
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
