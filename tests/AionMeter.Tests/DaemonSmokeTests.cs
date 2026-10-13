using System.Text.Json;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>
/// A2M-48: the Linux daemon's service (HeadlessMeter → MeterEngine) fed the 2026-10-09 login capture as if it were live writes the same
/// state files the Windows App writes under %AppData%\AionMeter. Needs libpcap (build.sh installs it). The only test that moves
/// AppSettings.AppDataDir and Log's file (process-wide statics).
/// </summary>
public class DaemonSmokeTests
{
    private static string Pcap => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pcaps", "login-20261009.pcap");

    private static string RepoData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "servers.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/servers.json above " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task LoginReplay_WritesTheStateFilesInTheDataDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "aionmeter-daemon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var human = new StringWriter();
            var json = new StringWriter();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using (var m = new HeadlessMeter(new HeadlessOptions { DataDir = root, GameDataDir = RepoData(), Replay = Pcap, Offline = true, JsonEvents = true, StatusEverySeconds = 0 }, human, json))
            {
                Assert.Equal(root, AppSettings.AppDataDir);
                Assert.Equal(0, await m.RunAsync(cts.Token));
            }
            Assert.False(cts.IsCancellationRequested, "the replay never ended");

            const string key = "1303-Akunita";
            foreach (var f in new[] { "settings.json", "state/summary.json", "state/character.json", "inventory.json", "names-cache.json", "boss-timers.json",
                                      $"characters/{key}/inventory.json", $"characters/{key}/character.json", $"characters/{key}/achievements.json" })
                Assert.True(File.Exists(Path.Combine(root, f)), f);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "logs"), "aionmeter_*.log"));
            // fights go to disk on the thread pool: give the last one a moment
            for (var i = 0; i < 50 && !File.Exists(Path.Combine(root, "fights", "index.jsonl")); i++) await Task.Delay(100);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "fights"), "*.json"));

            using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "state", "summary.json")));
            var s = summary.RootElement;
            Assert.Equal(key, s.GetProperty("characterKey").GetString());
            Assert.Equal(1303, s.GetProperty("serverId").GetInt32());
            Assert.InRange(s.GetProperty("kinah").GetInt64(), 2_169_808, 2_172_000);   // same range as RegressionTests
            Assert.InRange(s.GetProperty("inventory").GetProperty("stacks").GetInt32(), 135, 150);
            Assert.Equal(1010, s.GetProperty("mapId").GetInt32());

            var events = json.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("type").GetString()).ToList();
            Assert.Contains("character", events);
            Assert.Contains("fight", events);
            Assert.Equal("stopped", events[^1]);
            Assert.Contains("fight ", human.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A2M-48 phase 3: the UI socket. A client connects while the login capture replays (held open after its end), gets `status`,
    /// `overlay` snapshots with ranked rows, answers `ping`, lists `fights` and switches the card to one of them with `show-fight`.</summary>
    [Fact]
    public async Task Socket_ServesOverlaySnapshotsAndCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "aionmeter-sock-" + Guid.NewGuid().ToString("N"));
        var sock = Path.Combine(Path.GetTempPath(), "am-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var m = new HeadlessMeter(new HeadlessOptions { DataDir = root, GameDataDir = RepoData(), Replay = Pcap, Offline = true, Hold = true, Socket = sock, StatusEverySeconds = 0 },
                TextWriter.Null, null);
            var run = m.RunAsync(cts.Token);

            using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            await client.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(sock), cts.Token);
            using var stream = new System.Net.Sockets.NetworkStream(client, ownsSocket: false);
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };

            async Task<JsonElement> Next(string type, Func<JsonElement, bool>? when = null)
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException("socket closed waiting for " + type);
                    var e = JsonDocument.Parse(line).RootElement;
                    if (e.GetProperty("type").GetString() == type && (when is null || when(e))) return e;
                }
            }

            var status = await Next("status");
            Assert.False(string.IsNullOrEmpty(status.GetProperty("data").GetProperty("state").GetString()));
            // the replay runs as fast as it can; once it is over the last fight stays on the card (1 Hz idle snapshots)
            var overlay = await Next("overlay", e => e.GetProperty("data").GetProperty("fight").GetProperty("rows").GetArrayLength() > 0);
            var d = overlay.GetProperty("data");
            var fight = d.GetProperty("fight");
            Assert.True(fight.GetProperty("hasData").GetBoolean());
            var row = fight.GetProperty("rows")[0];
            Assert.Equal(1, row.GetProperty("rank").GetInt32());
            Assert.EndsWith("/s", row.GetProperty("dps").GetString());
            Assert.False(string.IsNullOrEmpty(row.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrEmpty(row.GetProperty("class").GetString()));
            Assert.False(string.IsNullOrEmpty(d.GetProperty("headerLabel").GetString()));
            Assert.False(string.IsNullOrEmpty(d.GetProperty("timersLabel").GetString()));
            Assert.Equal("live", d.GetProperty("fightId").GetString());

            await writer.WriteLineAsync("{\"cmd\":\"ping\"}");
            await Next("pong");

            await writer.WriteLineAsync("{\"cmd\":\"fights\",\"max\":10}");
            var fights = (await Next("fights")).GetProperty("data");
            Assert.True(fights.GetArrayLength() > 0);
            var pick = fights[fights.GetArrayLength() - 1];
            var id = pick.GetProperty("id").GetString()!;
            await writer.WriteLineAsync($"{{\"cmd\":\"show-fight\",\"id\":\"{id}\"}}");
            var shown = await Next("overlay", e => e.GetProperty("data").GetProperty("fightId").GetString() == id);
            Assert.True(shown.GetProperty("data").GetProperty("view").GetString() is "Session" or "Saved");

            await writer.WriteLineAsync("{\"cmd\":\"set-measure\",\"mode\":\"all\"}");
            await Next("overlay", e => e.GetProperty("data").GetProperty("mode").GetString() == "All");

            await writer.WriteLineAsync("{\"cmd\":\"nope\"}");
            await Next("error");

            cts.CancelAfter(TimeSpan.Zero);
            Assert.Equal(0, await run);
            Assert.False(File.Exists(sock), "socket file left behind");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            try { File.Delete(sock); } catch (IOException) { }
        }
    }

    [Fact]
    public void AppDataDir_FollowsXdgDataHomeOffWindows()
    {
        var d = AppSettings.DefaultAppDataDir();
        Assert.Equal("AionMeter", Path.GetFileName(d));
        if (!OperatingSystem.IsWindows()) Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AionMeter"), d);
    }
}
