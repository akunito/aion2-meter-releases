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

    [Fact]
    public void AppDataDir_FollowsXdgDataHomeOffWindows()
    {
        var d = AppSettings.DefaultAppDataDir();
        Assert.Equal("AionMeter", Path.GetFileName(d));
        if (!OperatingSystem.IsWindows()) Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AionMeter"), d);
    }
}
