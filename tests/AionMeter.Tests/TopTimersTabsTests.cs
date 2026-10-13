using AionMeter.Core.Events;
using AionMeter.Core.Leaderboard;
using AionMeter.Core.Ranking;
using AionMeter.Overlay.Shell;
using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>A2M-50: the display-free parts of the GTK Top DPS and Boss Timers tabs (GTK is never loaded here).</summary>
public class TopTimersTabsTests
{
    private static readonly UiText En = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static BossTimer Timer(int code, DateTimeOffset? kill = null, int respawn = 0, bool learned = false, bool watch = false,
        DateTimeOffset? listedAt = null, bool listedAlive = false, DateTimeOffset? listedTime = null, DateTimeOffset? seenAlive = null, DateTimeOffset? seenDead = null) =>
        new(code, 0, null, kill, seenAlive, seenDead, respawn, learned, null, 0, listedAt, listedAlive, listedTime, watch);

    // ---- Boss Timers

    [Fact]
    public void Timers_StatusCountsDownAndTurnsAmberInsideTheAlertWindow()
    {
        var t = Timer(1, kill: Now.AddMinutes(-50), respawn: 60);   // back in 10 min
        var s = BossTimersLogic.Status(t, Now, 5, En);
        Assert.Equal("10:00", s.Text);
        Assert.Equal(Fg.GoldBright, s.Fg);
        Assert.False(s.Hot);
        Assert.Equal(19, s.Size);
        Assert.Equal(string.Format(En.TimerAt, BossTimersLogic.When(Now.AddMinutes(10), Now)), s.Detail);
        Assert.Equal(Fg.Amber, BossTimersLogic.Status(t, Now, 10, En).Fg);
        // alert off still warns in the last minute (Math.Max(1, …))
        Assert.Equal(Fg.Amber, BossTimersLogic.Status(t, Now.AddMinutes(9.5), 0, En).Fg);
        Assert.Equal("1:30:00", BossTimersLogic.Countdown(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void Timers_StatusDueAliveAndUnknown()
    {
        var due = BossTimersLogic.Status(Timer(1, kill: Now.AddMinutes(-70), respawn: 60), Now, 5, En);
        Assert.Equal(En.TimerDue, due.Text);
        Assert.True(due.Hot);
        Assert.Equal(Fg.Green, due.Fg);
        Assert.Equal(14, due.Size);

        var alive = BossTimersLogic.Status(Timer(2, seenAlive: Now.AddMinutes(-1)), Now, 5, En);
        Assert.Equal(En.TimerAlive, alive.Text);
        Assert.Equal(16, alive.Size);
        Assert.True(alive.Hot);

        var unknown = BossTimersLogic.Status(Timer(3, kill: Now.AddMinutes(-10)), Now, 5, En);
        Assert.Equal("—", unknown.Text);
        Assert.Equal(En.TimerNeedsInterval, unknown.Detail);
        Assert.Equal(Fg.Mute, unknown.Fg);
    }

    [Fact]
    public void Timers_OrderIsAliveOrDueThenSoonestThenUnknown()
    {
        var unknown = Timer(10, kill: Now.AddMinutes(-5));
        var later = Timer(11, kill: Now.AddMinutes(-10), respawn: 60);
        var soon = Timer(12, kill: Now.AddMinutes(-55), respawn: 60);
        var due = Timer(13, kill: Now.AddMinutes(-90), respawn: 60);
        var list = new List<BossTimer> { unknown, later, soon, due };
        list.Sort(new TimerOrder { Now = Now });
        Assert.Equal([13, 12, 11, 10], list.Select(t => t.NpcCode));
        Assert.Equal(0, BossTimersLogic.Group(due, Now));
        Assert.Equal(2, BossTimersLogic.Group(unknown, Now));
    }

    [Fact]
    public void Timers_UnnamedSlotsAndTheWatchedFilter()
    {
        Assert.False(BossTimersLogic.Listed(Timer(-3), false));
        Assert.True(BossTimersLogic.Listed(Timer(5), false));
        Assert.False(BossTimersLogic.Listed(Timer(5), true));
        Assert.True(BossTimersLogic.Listed(Timer(5, watch: true), true));
    }

    [Fact]
    public void Timers_MetaRespawnAndWhen()
    {
        var today = Now.AddMinutes(-30);
        Assert.Equal(today.ToLocalTime().ToString("HH:mm"), BossTimersLogic.When(today, today));
        var other = Now.AddDays(-2);
        Assert.Equal(other.ToLocalTime().ToString("dd.MM HH:mm"), BossTimersLogic.When(other, Now));

        var t = Timer(1, kill: today, respawn: 90, learned: true);
        var killed = string.Format(En.TimerKilled, BossTimersLogic.When(today, Now));
        Assert.Equal("Zone · " + killed, BossTimersLogic.Meta("Zone", t, Now, En));
        Assert.Equal(killed, BossTimersLogic.Meta(null, t, Now, En));
        Assert.Equal(En.OpenWorld, BossTimersLogic.Meta("Open world", Timer(2), Now, En));
        Assert.Equal("", BossTimersLogic.Meta("", Timer(2), Now, En));
        Assert.Equal("≈ " + En.Duration(90), BossTimersLogic.RespawnLabel(t, En));
        Assert.Equal(En.RespawnUnknown, BossTimersLogic.RespawnLabel(Timer(2), En));
        Assert.Equal(En.SourceMeter, BossTimersLogic.SourceTip(t, Now, En));
    }

    [Fact]
    public void Timers_RespawnMenuAddsTheCurrentIntervalInOrder()
    {
        Assert.Same(BossTimersLogic.Presets, BossTimersLogic.RespawnChoices(60));
        Assert.Equal([0, 15, 30, 45, 60, 75, 90, 120, 180, 240, 360, 480, 720, 1440], BossTimersLogic.RespawnChoices(75));
        Assert.Equal(1441, BossTimersLogic.RespawnChoices(1441)[^1]);
        Assert.Equal(14, BossTimersLogic.RespawnChoices(1441).Length);
        Assert.Equal("▸ 1 h", BossTimersLogic.MenuLine("1 h", true));
        Assert.Equal("   1 h", BossTimersLogic.MenuLine("1 h", false));
        Assert.Equal(En.AlertOff, BossTimersLogic.AlertText(0, En));
    }

    [Fact]
    public void Timers_PortraitsByCodeOrByTheShippedFileMap()
    {
        var files = BossTimersLogic.ParsePortraitFiles("""{"portraits": {"2100": "dungeon/boss_a", "x": "bad", "7": ""}}""");
        Assert.Single(files);
        Assert.Equal(("portrait:dungeon_boss_a", BossTimersLogic.PortraitBase + "dungeon/boss_a.webp"), BossTimersLogic.Portrait(2100, files));
        Assert.Equal(("portrait:4321", BossTimersLogic.PortraitBase + "4321.webp"), BossTimersLogic.Portrait(4321, files));
        Assert.Empty(BossTimersLogic.ParsePortraitFiles("[]"));
        // the shipped catalogue parses (data/ is copied next to the test assembly as for the overlay)
        var shipped = Path.Combine(MeterEngine.DataDirectory, "boss_portraits.json");
        if (File.Exists(shipped)) Assert.NotEmpty(BossTimersLogic.LoadPortraitFiles(MeterEngine.DataDirectory));
    }

    // ---- Top DPS

    private static TopEntry Entry(params (int T, int S)[] casts) => new(
        "Aku", GameClass.Sorcerer, 1001, true, 12_345, 1_234_567, 95_000, "Boss", "Zone", 1, new DateTimeOffset(2026, 10, 13, 20, 30, 0, TimeSpan.Zero), "f.json",
        [new TopSkill(1, "Fire", 10, 600, 0.2), new TopSkill(2, "Ice", 5, 300, 0.5), new TopSkill(3, "Wind", 7, 100, 0.3)],
        casts.Select(c => new TopCast(c.T, c.S)).ToList());

    [Fact]
    public void Top_RowTexts()
    {
        var e = Entry();
        Assert.Equal("Boss · Zone · " + e.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · " + AionMeter.Core.Format.Compact(e.Damage) + " dmg", TopDpsLogic.FightMeta(e));
        Assert.DoesNotContain("· ·", TopDpsLogic.FightMeta(e with { Zone = null }));
        Assert.Equal("1:35", TopDpsLogic.CombatTime(e));
        Assert.Equal(AionMeter.Core.Format.Compact(e.Dps) + "/s", TopDpsLogic.Dps(e));
        Assert.Equal("Sorcerer (3)", TopDpsLogic.ClassTab("Sorcerer", 3, true));
        Assert.Equal("Sorcerer", TopDpsLogic.ClassTab("Sorcerer", 3, false));
        var b = new LeaderboardRow(12, "X", "nae", "Srv", "srv", "SORCERER", 45, 99_000, 1500, null);
        Assert.Equal("Srv · lv45 · #12 in NAE", TopDpsLogic.BoardMeta(b));
        Assert.StartsWith("Srv · lv45 · #12 in NAE · seen ", TopDpsLogic.BoardMeta(b with { LastInfoAt = DateTimeOffset.UtcNow }));
    }

    [Fact]
    public void Top_RotationSummary()
    {
        var e = Entry((0, 1), (1000, 2), (2000, 3), (3000, 1), (4000, 2), (5000, 3), (6000, 1));
        Assert.Equal("Ice 50% ×5   Wind 30% ×7   Fire 20% ×10", TopDpsLogic.Skills(e));
        Assert.Equal("Ice 50% ×5", TopDpsLogic.Skills(e, 1));
        string Name(int s) => "s" + s;
        Assert.Equal("s1 → s2 → s3", TopDpsLogic.Opening(e, Name, 3));
        Assert.StartsWith("s1 → s2 → s3   ×2", TopDpsLogic.Repeated(e, Name));
        Assert.Equal("—", TopDpsLogic.Repeated(Entry((0, 1)), Name));
        Assert.StartsWith("s1 " + (3 / (95_000 / 60_000.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "/min", TopDpsLogic.Cadence(e, Name));
        Assert.Equal("", TopDpsLogic.Skills(e with { Skills = [] }));
    }

    [Fact]
    public void Top_ClassPickFollowsThePickThenTheCharacterThenTheFirst()
    {
        GameClass[] own = [GameClass.Templar, GameClass.Sorcerer];
        Assert.Equal(GameClass.Sorcerer, TopDpsLogic.PickClass(GameClass.Unknown, GameClass.Sorcerer, own));
        Assert.Equal(GameClass.Templar, TopDpsLogic.PickClass(GameClass.Templar, GameClass.Sorcerer, own));
        Assert.Equal(GameClass.Templar, TopDpsLogic.PickClass(GameClass.Chanter, GameClass.Sorcerer, own));   // picked class not in "My fights"
        Assert.Equal(GameClass.Templar, TopDpsLogic.PickClass(GameClass.Unknown, GameClass.Unknown, own));
        Assert.Equal(GameClass.Unknown, TopDpsLogic.PickClass(GameClass.Cleric, GameClass.Sorcerer, []));
        Assert.Equal(8, TopDpsLogic.AllClasses.Length);
    }

    [Fact]
    public void Registry_TopAndTimersArePorted()
    {
        Assert.True(ViewRegistry.Has("top"));
        Assert.True(ViewRegistry.Has("timers"));
        Assert.All(TopTimersKit.Sheet().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), l => Assert.True(l.StartsWith(".k-", StringComparison.Ordinal), l));
    }
}
