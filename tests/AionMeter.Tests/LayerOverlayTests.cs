using System.Net.Sockets;
using System.Text;
using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Overlay;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-48: the layer-shell overlay's parts that need no display (GTK is never loaded here).</summary>
public class LayerOverlayTests
{
    // ---- placement: layer surfaces move by margins
    [Fact]
    public void Drag_TopRight_MovingLeftAndDownGrowsBothMargins()
    {
        // press at margins (24, 140), pointer moved 300 left and 100 down since the press (Sway: offsets are totals)
        var (h, v) = Placement.Drag(Anchor.TopRight, 24, 140, -300, 100, 2560, 1440, 560, 540);
        Assert.Equal((324, 240), (h, v));
    }

    [Fact]
    public void Drag_BottomLeft_MovingRightAndUp()
    {
        var (h, v) = Placement.Drag(Anchor.BottomLeft, 10, 10, 50, -30, 2560, 1440, 560, 540);
        Assert.Equal((60, 40), (h, v));
    }

    [Fact]
    public void Drag_IsClampedToTheOutput()
    {
        Assert.Equal((0, 0), Placement.Drag(Anchor.TopRight, 24, 140, 5000, -5000, 2560, 1440, 560, 540));
        Assert.Equal((2000, 900), Placement.Drag(Anchor.TopLeft, 24, 140, 5000, 5000, 2560, 1440, 560, 540));
    }

    [Fact]
    public void Drag_IsNotCumulativePerUpdate()
    {
        // the same total offset fed twice gives the same place (the 2026-10-13 bug added each update to the last margins: 1650 px for 300)
        var a = Placement.Drag(Anchor.TopRight, 24, 140, -300, 100, 2560, 1440, 560, 540);
        var b = Placement.Drag(Anchor.TopRight, 24, 140, -300, 100, 2560, 1440, 560, 540);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Resize_RightAnchor_KeepsTheLeftEdgeStill()
    {
        // left edge at 2560 - 300 - 560 = 1700; growing 100 px moves the right margin in by 100
        var (w, h, mh, mv) = Placement.Resize(Anchor.TopRight, 560, 540, 300, 140, 100, 40, 340, 220, 2560, 1440);
        Assert.Equal((660, 580, 200, 140), (w, h, mh, mv));
    }

    [Fact]
    public void Resize_StopsAtTheOutputEdgeAndAtTheMinimum()
    {
        // measured on DESK: margin 24, +60 px only fits 24 more (584 wide, margin 0)
        Assert.Equal((584, 580, 0, 140), Placement.Resize(Anchor.TopRight, 560, 540, 24, 140, 60, 40, 340, 220, 2560, 1440));
        Assert.Equal((340, 220, 24, 140), Placement.Resize(Anchor.TopLeft, 560, 540, 24, 140, -900, -900, 340, 220, 2560, 1440));
    }

    [Fact]
    public void Resize_BottomAnchor_KeepsTheTopEdgeStill()
    {
        var (_, h, _, mv) = Placement.Resize(Anchor.BottomLeft, 560, 540, 24, 100, 0, 50, 340, 220, 2560, 1440);
        Assert.Equal((590, 50), (h, mv));
    }

    [Theory]
    [InlineData("top-right", Anchor.TopRight)]
    [InlineData("TOP-LEFT", Anchor.TopLeft)]
    [InlineData("br", Anchor.BottomRight)]
    [InlineData("bottom-left", Anchor.BottomLeft)]
    public void Anchor_Parses(string s, Anchor expected)
    {
        Assert.True(Placement.TryParse(s, out var a));
        Assert.Equal(expected, a);
        Assert.True(Placement.TryParse(Placement.Name(a), out var back));
        Assert.Equal(a, back);
    }

    [Fact]
    public void Anchor_RejectsJunk() => Assert.False(Placement.TryParse("middle", out _));

    // ---- overlay.json
    [Fact]
    public void Settings_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aionmeter-overlay-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "overlay.json");
        var s = OverlaySettings.Load(path);
        Assert.Equal((Anchor.TopRight, 560, 540, false), (s.Anchor, s.Width, s.Height, s.ClickThrough));
        s.Monitor = "DP-1"; s.Anchor = Anchor.BottomLeft; s.MarginH = 300; s.MarginV = 77; s.Width = 600; s.Height = 480; s.Scale = 1.25; s.ClickThrough = true;
        s.Save();
        Assert.Contains("\"BottomLeft\"", File.ReadAllText(path));
        var r = OverlaySettings.Load(path);
        // the scale is the shared ui.json "UiScale" now (A2M-48 UX), not part of overlay.json
        Assert.Equal(("DP-1", Anchor.BottomLeft, 300, 77, 600, 480, 1.0, true), (r.Monitor, r.Anchor, r.MarginH, r.MarginV, r.Width, r.Height, r.Scale, r.ClickThrough));
        Assert.DoesNotContain("Scale", File.ReadAllText(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Settings_ClampsAndSurvivesJunk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aionmeter-overlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "overlay.json");
        File.WriteAllText(path, """{"Width": 10, "Height": 5, "Scale": 9, "Opacity": 0}""");
        var s = OverlaySettings.Load(path);
        Assert.Equal((OverlaySettings.MinWidth, OverlaySettings.MinHeight, 1.0, 0.2), (s.Width, s.Height, s.Scale, s.Opacity));
        File.WriteAllText(path, "{ not json");
        Assert.Equal(560, OverlaySettings.Load(path).Width);
        Directory.Delete(dir, true);
    }

    // ---- card strings
    [Fact]
    public void CardText_HeaderRankTitlePills()
    {
        Assert.Equal("LIVE FIGHT", CardText.HeaderLabel("Live fight", null));
        Assert.Equal("SAVED   05.10 19:04", CardText.HeaderLabel("saved", "05.10 19:04"));
        Assert.Equal("3", CardText.Rank(3));
        Assert.Equal("·", CardText.Rank(0));
        Assert.Equal(("r1", "r3", "rn", "rn"), (CardText.RankClass(1), CardText.RankClass(3), CardText.RankClass(4), CardText.RankClass(0)));
        Assert.Equal(("p0", "p1", "p2", "p3"), (CardText.PillClass(PillTone.Calm), CardText.PillClass(PillTone.Soon), CardText.PillClass(PillTone.Close), CardText.PillClass(PillTone.Now)));
        var f = new OverlayFight(null, false, false, "", "", "Waiting for combat…", "0:00", "", "", "", false, "", "", null, [], false, 0, "", 0);
        Assert.Equal("Waiting for combat…", CardText.Title(f));
        Assert.Equal("Tahabata", CardText.Title(f with { Title = "Tahabata" }));
    }

    [Fact]
    public void CardText_FightMenuItems()
    {
        var t = new UiText();
        var at = new DateTimeOffset(2026, 10, 13, 19, 4, 0, TimeSpan.Zero);
        var kill = new OverlayFightItem("s:abc", "Marsh Targon", at, 83_000, EncounterEndReason.Kill, false, 30_550, 1, 61_000, false);
        Assert.Equal($"▸ ✔ 19:04  Marsh Targon  ·  {Format.Clock(83_000)}  ·  30.55K/s #1", CardText.FightItem(kill, "s:abc", t, TimeZoneInfo.Utc));
        var party = kill with { Id = "h:x.json", Reason = EncounterEndReason.Wipe, SelfDps = 0, Place = 0 };
        Assert.Equal($"✖ 19:04  Marsh Targon  ·  {Format.Clock(83_000)}  ·  {t.Party} 61K/s", CardText.FightItem(party, "live", t, TimeZoneInfo.Utc));
        Assert.StartsWith("● ", CardText.FightItem(kill with { Active = true }, null, t, TimeZoneInfo.Utc));
    }

    [Fact]
    public void CardText_ScaleCss_ScalesPixelsOnly()
    {
        var css = ".a { margin: 10px 0 1px 2.5px; color: #10px; min-width: 0.4px; }";
        Assert.Equal(css, CardText.ScaleCss(css, 1));
        Assert.Equal(".a { margin: 15px 0 1.5px 3.75px; color: #10px; min-width: 1px; }", CardText.ScaleCss(css, 1.5));
    }

    [Fact]
    public void Icons_AllPathsParse()
    {
        foreach (var f in typeof(Icons).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            Assert.NotNull(f.GetValue(null));   // the static constructor parses every path (a bad one throws TypeInitializationException)
        Assert.Throws<FormatException>(() => new Icon("M1 1 Q2 2 3 3"));
    }

    // ---- the toolkit-free socket client (OverlayClient, shared by any UI)
    [Fact]
    public async Task OverlayClient_ReadsSnapshotsAndSendsCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "aionmeter-client-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var events = new System.Collections.Concurrent.BlockingCollection<string>();
        using var client = new OverlayClient(path, a => a());
        client.ConnectionChanged += on => events.Add(on ? "up" : "down");
        client.Overlay += m => events.Add("overlay " + m.Fight.Title + " " + m.Fight.Rows.Length);
        client.Status += s => events.Add("status " + s.Character);
        client.Start();
        using var server = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("up", events.Take(new CancellationTokenSource(5000).Token));

        var row = new OverlayRow(7, 1, "Lucy", Core.Events.GameClass.Cleric, true, false, "", "", "30.55K/s", "30.55K", "100.0%", 1, "");
        var fight = new OverlayFight(null, true, true, "Tahabata", "", "", "0:12", "", "", "", false, "", "", null, [row], false, 1, "#1", 1);
        var model = new OverlayModel(fight, OverlayView.Live, "LIVE FIGHT", null, "live", null, Core.Capture.CaptureState.Capturing, "", "", StatusTone.Good,
            "", null, "Boss timers", [], null, "BOSS", TargetMode.BossOnly, "en", "0.2.8", "Akunita", 0);
        var json = System.Text.Json.JsonSerializer.Serialize(model, OverlayJson.Default.OverlayModel);
        var status = System.Text.Json.JsonSerializer.Serialize(new OverlayStatus(Core.Capture.CaptureState.Capturing, "", "1303-Akunita", "0.2.8", "/d", false), OverlayJson.Default.OverlayStatus);
        // one line split across two writes, then a junk line that must be skipped
        var bytes = Encoding.UTF8.GetBytes($"{{\"type\":\"status\",\"ts\":1,\"data\":{status}}}\n{{\"type\":\"overlay\",\"ts\":2,\"data\":{json}}}\nnot json\n");
        server.Send(bytes.AsSpan(0, 40));
        await Task.Delay(50);
        server.Send(bytes.AsSpan(40));
        Assert.Equal("status 1303-Akunita", events.Take(new CancellationTokenSource(5000).Token));
        Assert.Equal("overlay Tahabata 1", events.Take(new CancellationTokenSource(5000).Token));

        client.Send("show-fight", "id", "h:a\"b.json");
        client.Send("fights", "max", 40);
        var buf = new byte[512];
        var got = "";
        while (got.Count(c => c == '\n') < 2) got += Encoding.UTF8.GetString(buf, 0, await server.ReceiveAsync(buf).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("{\"cmd\":\"show-fight\",\"id\":\"h:a\\u0022b.json\"}\n{\"cmd\":\"fights\",\"max\":40}\n", got);

        server.Close();
        Assert.Equal("down", events.Take(new CancellationTokenSource(5000).Token));
        File.Delete(path);
    }
}
