using System.Net.Sockets;
using System.Text;
using AionMeter.Overlay;
using AionMeter.Service.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-50 window mode: the card's own container in a real get_tree (nested Sway 1.11, 2026-10-14: the card, its Report dialog
/// with the same pid and app_id, a tiled foot), the overlay.json fields, the restore and resize commands, the tab size rule, the i3-ipc
/// framing (no display, no compositor).</summary>
public class WindowModeTests
{
    private const int CardPid = 3140352;
    private static ReadOnlyMemory<byte> Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sway", name));
    private static readonly SwayOutput[] Headless = [new("HEADLESS-1", 0, 0, 1600, 900)];

    [Fact]
    public void FindOwn_PicksTheCardNotItsDialog()
    {
        var n = WindowGeometry.FindOwn(Fixture("get_tree-card-dialog.json"), CardPid, "AION 2 meter");
        Assert.NotNull(n);
        Assert.Equal(new SwayNode(15, "HEADLESS-1", 171, 177, 479, 613, Floating: true, Sticky: true, Focused: false), n);
        var d = WindowGeometry.FindOwn(Fixture("get_tree-card-dialog.json"), CardPid, "Report a problem");
        Assert.Equal(16, d!.Value.Id);
        Assert.True(d.Value.Focused);
        Assert.Null(WindowGeometry.FindOwn(Fixture("get_tree-card-dialog.json"), CardPid + 1, "AION 2 meter"));
        Assert.Null(WindowGeometry.FindOwn("not json"u8.ToArray(), CardPid, "AION 2 meter"));
    }

    [Fact]
    public void FindOwn_TiledAndScratchpad()
    {
        var tiled = """{"type":"root","nodes":[{"type":"output","name":"DP-1","nodes":[{"type":"workspace","nodes":[{"type":"con","id":7,"pid":42,"name":"AION 2 meter","rect":{"x":0,"y":30,"width":800,"height":900},"sticky":false,"focused":true}],"floating_nodes":[]}]}]}"""u8.ToArray();
        Assert.Equal(new SwayNode(7, "DP-1", 0, 30, 800, 900, false, false, true), WindowGeometry.FindOwn(tiled, 42, "AION 2 meter"));
        var scratch = """{"type":"root","nodes":[{"type":"output","name":"__i3","nodes":[{"type":"workspace","floating_nodes":[{"type":"floating_con","id":8,"pid":42,"name":"AION 2 meter","rect":{"x":0,"y":0,"width":500,"height":500}}]}]}]}"""u8.ToArray();
        Assert.Null(WindowGeometry.FindOwn(scratch, 42, "AION 2 meter"));
    }

    [Fact]
    public void Outputs_ActiveOnly()
    {
        Assert.Equal(Headless, WindowGeometry.Outputs(Fixture("get_outputs.json")));
        var two = """[{"name":"DP-1","active":true,"rect":{"x":0,"y":0,"width":2560,"height":1440}},{"name":"DP-2","active":false,"rect":{"x":0,"y":0,"width":0,"height":0}}]"""u8.ToArray();
        Assert.Equal([new SwayOutput("DP-1", 0, 0, 2560, 1440)], WindowGeometry.Outputs(two));
        Assert.Empty(WindowGeometry.Outputs("{}"u8.ToArray()));
    }

    [Fact]
    public void Node_ToOverlayJson_AndBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2m50-win-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "overlay.json");
            var n = WindowGeometry.FindOwn(Fixture("get_tree-card-dialog.json"), CardPid, "AION 2 meter")!.Value;
            var s = OverlaySettings.Load(path);
            Assert.False(s.Win.Saved);
            s.Win = new WinGeom(n.Output, n.X, n.Y, n.W, n.H);
            s.Save();
            var json = File.ReadAllText(path);
            Assert.Contains("\"WinOutput\": \"HEADLESS-1\"", json);
            Assert.Contains("\"WinX\": 171", json);
            Assert.Contains("\"WinH\": 613", json);
            var r = OverlaySettings.Load(path);
            Assert.Equal(new WinGeom("HEADLESS-1", 171, 177, 479, 613), r.Win);
            // the --layer fields are untouched by the window mode
            Assert.Equal((560, 540, 24, 140), (r.Width, r.Height, r.MarginH, r.MarginV));
            Assert.Equal("[con_id=15] resize set width 479 px height 613 px, move absolute position 171 px 177 px",
                WindowGeometry.Restore(15, r.Win, Headless));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Restore_PositionOnlyWhenItLandsOnAnOutput()
    {
        Assert.Null(WindowGeometry.Restore(3, default, Headless));
        // partly off-screen is fine while the centre is on an output (measured: 1300,-150 restored as is after SIGUSR2 show)
        Assert.Equal("[con_id=3] resize set width 479 px height 613 px, move absolute position 1300 px -150 px",
            WindowGeometry.Restore(3, new WinGeom("HEADLESS-1", 1300, -150, 479, 613), Headless));
        // the saved output is still there but the layout changed: clamped onto it
        SwayOutput[] moved = [new("DP-2", 0, 0, 1920, 1080), new("DP-1", 1920, 0, 2560, 1440)];
        Assert.Equal("[con_id=3] resize set width 500 px height 500 px, move absolute position 3980 px 100 px",
            WindowGeometry.Restore(3, new WinGeom("DP-1", 5000, 100, 500, 500), moved));
        // its output is gone: size only, Sway's placement keeps it visible
        Assert.Equal("[con_id=3] resize set width 500 px height 500 px", WindowGeometry.Restore(3, new WinGeom("DP-9", 5000, 100, 500, 500), moved));
    }

    [Fact]
    public void Commands()
    {
        Assert.Equal("[con_id=9] move absolute position -20 px 40 px", WindowGeometry.Move(9, -20, 40));
        Assert.Equal("[con_id=9] resize set width 560 px height 540 px", WindowGeometry.Resize(9, 560, 540));
        Assert.Equal("[con_id=9] resize set width 560 px height 613 px, move absolute position 171 px 177 px", WindowGeometry.ResizeAt(9, 171, 177, 560, 613));
    }

    [Fact]
    public void SizeToSave_TabGrowthIsNotTheUsersSize()
    {
        // DPS: whatever Sway reports
        Assert.Equal((420, 600), WindowGeometry.SizeToSave(true, 479, 613, 420, 600, 560, 540));
        // Settings grew 479 → 560 (measured): the DPS width stays 479, the height (613 ≥ 540) is unchanged anyway
        Assert.Equal((479, 613), WindowGeometry.SizeToSave(false, 479, 613, 560, 613, 560, 540));
        // resized by the user on a tab: saved
        Assert.Equal((700, 650), WindowGeometry.SizeToSave(false, 479, 613, 700, 650, 560, 540));
        // nothing saved yet: take it
        Assert.Equal((560, 540), WindowGeometry.SizeToSave(false, 0, 0, 560, 540, 560, 540));
    }

    [Theory]
    [InlineData(-1, -1, 0)]
    [InlineData(0, -1, 1)]
    [InlineData(1, -1, 2)]
    [InlineData(-1, 0, 3)]
    [InlineData(1, 0, 4)]
    [InlineData(-1, 1, 5)]
    [InlineData(0, 1, 6)]
    [InlineData(1, 1, 7)]
    public void SurfaceEdge_IsGdkSurfaceEdge(int ex, int ey, int gdk) => Assert.Equal(gdk, WindowGeometry.SurfaceEdge(ex, ey));

    [Fact]
    public void SurfaceEdge_FromTheGrabPoint()
    {
        // the measured right-drag in the nested sway: 479×680 card pressed at 239,523 (middle third, bottom third) → SOUTH, height only
        var (ex, ey) = Placement.EdgeAt(239, 523, 419, 680);
        Assert.Equal(6, WindowGeometry.SurfaceEdge(ex, ey));
    }

    [Fact]
    public void Ipc_FrameAndHeader()
    {
        var f = SwayIpc.Frame(SwayIpc.GetTree, "");
        Assert.Equal(14, f.Length);
        Assert.Equal("i3-ipc", Encoding.ASCII.GetString(f, 0, 6));
        Assert.True(SwayIpc.TryHeader(f, out var n, out var t));
        Assert.Equal((0, SwayIpc.GetTree), (n, t));
        var c = SwayIpc.Frame(SwayIpc.RunCommand, "[con_id=1] focus");
        Assert.True(SwayIpc.TryHeader(c, out n, out _));
        Assert.Equal(16, n);
        Assert.False(SwayIpc.TryHeader("i3-ipX\0\0\0\0\0\0\0\0"u8, out _, out _));
    }

    [Fact]
    public void Ipc_RequestSkipsEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), "a2m50-ipc-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var server = Task.Run(() =>
        {
            using var s = listener.Accept();
            var head = new byte[14];
            var got = 0;
            while (got < 14) got += s.Receive(head, got, 14 - got, SocketFlags.None);
            SwayIpc.TryHeader(head, out var len, out var type);
            var body = new byte[len];
            for (got = 0; got < len;) got += s.Receive(body, got, len - got, SocketFlags.None);
            // an event first (a subscribed connection interleaves them), then the reply
            s.Send(SwayIpc.Frame(SwayIpc.WindowEvent, """{"change":"focus"}"""));
            s.Send(SwayIpc.Frame(type, "[{\"success\":true,\"echo\":\"" + Encoding.UTF8.GetString(body) + "\"}]"));
        });
        try
        {
            using var c = SwayIpc.Connection.Open(path);
            Assert.NotNull(c);
            var reply = c!.Request(SwayIpc.RunCommand, "[con_id=5] move absolute position 1 px 2 px");
            Assert.NotNull(reply);
            Assert.Equal("[{\"success\":true,\"echo\":\"[con_id=5] move absolute position 1 px 2 px\"}]", Encoding.UTF8.GetString(reply!.Value.Span));
            server.Wait(5000);
        }
        finally { File.Delete(path); }
    }
}
