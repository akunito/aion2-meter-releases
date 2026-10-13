using System.Net.Sockets;
using System.Text.RegularExpressions;
using AionMeter.Overlay;
using AionMeter.Service.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-48 UX: the tools window's command socket, the canonical tab strip, the shared UI scale, Sway screen factors, pid files.</summary>
public class LinuxUxTests
{
    [Theory]
    [InlineData("""{"cmd":"show","tab":"items"}""", UiCommandKind.Show, "items")]
    [InlineData("""{"cmd":"show"}""", UiCommandKind.Show, null)]
    [InlineData("""{"cmd":"show","tab":"settings"}""", UiCommandKind.Show, "settings")]
    [InlineData("""  {"tab":"atlas","cmd":"show"}  """, UiCommandKind.Show, "atlas")]
    [InlineData("""{"cmd":"hide"}""", UiCommandKind.Hide, null)]
    [InlineData("""{"cmd":"raise"}""", UiCommandKind.Raise, null)]
    [InlineData("""{"cmd":"toggle"}""", UiCommandKind.Toggle, null)]
    [InlineData("""{"cmd":"quit"}""", UiCommandKind.Quit, null)]
    public void UiChannel_ParsesCommands(string line, UiCommandKind kind, string? tab)
    {
        Assert.True(UiChannel.TryParse(line, out var cmd));
        Assert.Equal(new UiCommand(kind, tab), cmd);
        // what the senders write parses back to the same command
        Assert.True(UiChannel.TryParse(UiChannel.Format(cmd).TrimEnd('\n'), out var again));
        Assert.Equal(cmd, again);
        Assert.EndsWith("\n", UiChannel.Format(cmd));
    }

    [Theory]
    [InlineData("")]
    [InlineData("show")]
    [InlineData("{not json")]
    [InlineData("""{"tab":"items"}""")]
    [InlineData("""{"cmd":"launch"}""")]
    [InlineData("""{"cmd":3}""")]
    [InlineData("""{"cmd":"show","tab":"../../etc"}""")]
    [InlineData("""{"cmd":"show","tab":"fight"}""")]
    [InlineData("""{"cmd":"show","tab":7}""")]
    [InlineData("""{"cmd":"hide","tab":"items"}""")]
    [InlineData("""["show"]""")]
    public void UiChannel_RejectsJunk(string line) => Assert.False(UiChannel.TryParse(line, out _));

    [Fact]
    public void UiChannel_RejectsLongLines() => Assert.False(UiChannel.TryParse("""{"cmd":"show","x":" """ + new string('a', 600) + "\"}", out _));

    [Fact]
    public void UiChannel_SendsOneLineToTheListener()
    {
        // a Unix socket path holds at most 108 bytes: not under the test's temp dir
        var path = $"/tmp/a2m-ux-{Guid.NewGuid():N}"[..24] + ".sock";
        Assert.False(UiChannel.TrySend(path, new UiCommand(UiCommandKind.Raise)));
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            var accept = listener.AcceptAsync();
            Assert.True(UiChannel.TrySend(path, new UiCommand(UiCommandKind.Show, "craft")));
            using var c = accept.Result;
            using var reader = new StreamReader(new NetworkStream(c));
            Assert.True(UiChannel.TryParse(reader.ReadLine()!, out var cmd));
            Assert.Equal(new UiCommand(UiCommandKind.Show, "craft"), cmd);
            // the probe (null) connects and sends nothing
            var accept2 = listener.AcceptAsync();
            Assert.True(UiChannel.TrySend(path, null));
            using var c2 = accept2.Result;
            using var reader2 = new StreamReader(new NetworkStream(c2));
            Assert.Null(reader2.ReadLine());
        }
        finally { File.Delete(path); }
    }

    /// <summary>Both Linux strips are built from UiTabs.Strip; it must be WPF's TabStrip order (OverlayWindow.xaml), fight/settings excluded.</summary>
    [Fact]
    public void TabStrip_IsTheWpfOrder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? xaml = null;
        for (; dir is not null && xaml is null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "AionMeter.App", "Windows", "OverlayWindow.xaml");
            if (File.Exists(p)) xaml = p;
        }
        Assert.NotNull(xaml);
        var text = File.ReadAllText(xaml!);
        var strip = text[text.IndexOf("x:Name=\"TabStrip\"", StringComparison.Ordinal)..];
        strip = strip[..strip.IndexOf("</StackPanel>", StringComparison.Ordinal)];
        var keys = Regex.Matches(strip, "<ToggleButton x:Name=\"Tab\\w+\"[^>]*?Tag=\"(\\w+)\"").Select(m => m.Groups[1].Value)
            .Where(k => k is not ("fight" or "settings")).ToArray();
        Assert.Equal(["meter", "atlas", "top", "prices", "craft", "build", "items", "timers"], keys);
        Assert.Equal(keys, UiTabs.Strip);
        foreach (var k in UiTabs.Strip) Assert.NotEqual(k, CardText.TabTip(k));
    }

    [Theory]
    [InlineData(null, "items")]
    [InlineData("", "items")]
    [InlineData("meter", "items")]
    [InlineData("settings", "items")]
    [InlineData("nonsense", "items")]
    [InlineData("craft", "craft")]
    [InlineData("atlas", "atlas")]
    public void StartTab_IsTheLastToolTab(string? last, string expected) => Assert.Equal(expected, UiTabs.StartTab(last));

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.8)]
    [InlineData(3.0, 2.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(1.23, 1.25)]
    [InlineData(1.07, 1.05)]
    [InlineData(1.0 + UiScale.Step, 1.1)]
    public void UiScale_Clamps(double v, double expected) => Assert.Equal(expected, UiScale.Clamp(v), 6);

    [Fact]
    public void UiScale_PatchesOnlyItsKeyInUiJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aionmeter-ux-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "ui.json");
        Assert.Equal(1, UiScale.Read(path));
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, """{"Output":"DP-2","Left":12,"Top":34,"Width":600,"LastTab":"craft","Windows":{"help":[1,2,3,4]}}""");
        UiScale.Write(path, 1.37);
        Assert.Equal(1.35, UiScale.Read(path), 6);
        var text = File.ReadAllText(path);
        foreach (var keep in new[] { "\"DP-2\"", "\"craft\"", "\"Left\": 12", "\"help\"" }) Assert.Contains(keep, text);
        UiScale.Write(path, 9);
        Assert.Equal(UiScale.Max, UiScale.Read(path));
        File.WriteAllText(path, "{ junk");
        Assert.Equal(1, UiScale.Read(path));
        UiScale.Write(path, 1.5);   // a broken file is replaced, not kept broken
        Assert.Equal(1.5, UiScale.Read(path));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ScreenFactors_FromSwayOutputs()
    {
        const string outputs = """
            [{"name":"DP-2","active":true,"scale":1.25,"make":"NSL"},
             {"name":"DP-1","active":true,"scale":1.5},
             {"name":"HDMI-A-1","active":false,"scale":-1}]
            """;
        Assert.Equal("DP-2=1.25;DP-1=1.5", UiScale.ScreenFactors(outputs));
        Assert.Null(UiScale.ScreenFactors("[]"));
        Assert.Null(UiScale.ScreenFactors("{oops"));
        Assert.True(UiScale.ZeroScaling("font pango:Inter 10\nxwayland_zero_scaling yes\nexec_always xrdb"));
        Assert.False(UiScale.ZeroScaling("xwayland_zero_scaling no\n"));
        Assert.False(UiScale.ZeroScaling("# xwayland_zero_scaling yes\n"));
    }

    [Fact]
    public void LivePid_ChecksTheCmdline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aionmeter-ux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pidFile = Path.Combine(dir, "x.pid");
        var me = Environment.ProcessId;
        var cmdline = File.ReadAllText($"/proc/{me}/cmdline").Split('\0')[0];
        File.WriteAllText(pidFile, me.ToString());
        Assert.Equal(me, FrontEnds.LivePid(pidFile, Path.GetFileName(cmdline)));
        Assert.Equal(0, FrontEnds.LivePid(pidFile, "AionMeter.Overlay.dll-not-this"));   // reused pid, someone else
        File.WriteAllText(pidFile, "999999999");
        Assert.Equal(0, FrontEnds.LivePid(pidFile, "dotnet"));
        File.WriteAllText(pidFile, "junk");
        Assert.Equal(0, FrontEnds.LivePid(pidFile, "dotnet"));
        Assert.Equal(0, FrontEnds.LivePid(Path.Combine(dir, "missing.pid"), "dotnet"));
        Directory.Delete(dir, true);
    }
}
