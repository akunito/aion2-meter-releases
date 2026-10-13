using AionMeter.Core.Capture;
using AionMeter.Core.Game;

namespace AionMeter.Tests;

/// <summary>A2M-27: a PC without Npcap. The capture reports the missing library and stays quiet; nothing else in the meter depends on it
/// (Atlas data, Prices, Craft, Top DPS rankings, profiles and updates have their own tests with no capture involved).</summary>
public class NoCaptureTests
{
    private static GameData Data() => GameData.Empty;

    [Fact]
    public async Task Missing_library_is_reported_once_and_sharppcap_is_never_touched()
    {
        var statuses = new List<CaptureStatus>();
        using var capture = new LiveCapture(Data(), new LiveCaptureOptions { LibraryPresent = () => false, LibraryPollMs = 20 });
        capture.StatusChanged += s => { lock (statuses) statuses.Add(s); };
        capture.Start();
        await Task.Delay(300);
        Assert.Equal(CaptureState.Error, capture.Status.State);
        Assert.Equal(LiveCapture.NpcapMissingMessage, capture.Status.Message);
        Assert.Null(capture.Health());
        lock (statuses) Assert.Equal(1, statuses.Count(s => s.State == CaptureState.Error));   // ~15 polls, one report
        capture.Stop();
        Assert.Equal(CaptureState.Stopped, capture.Status.State);
    }

    [Fact]
    public async Task Library_installed_during_the_session_is_picked_up_without_a_restart()
    {
        var present = false;
        // a forced, non-existent adapter keeps the recovered monitor on the libpcap device list (present in the test container) instead of the Windows TCP table
        using var capture = new LiveCapture(Data(), new LiveCaptureOptions { LibraryPresent = () => present, LibraryPollMs = 20, DeviceName = "no-such-adapter" });
        capture.Start();
        await Task.Delay(100);
        Assert.Equal(LiveCapture.NpcapMissingMessage, capture.Status.Message);
        present = true;
        var left = false;
        for (var i = 0; i < 50 && !left; i++) { await Task.Delay(50); left = capture.Status.Message != LiveCapture.NpcapMissingMessage; }
        Assert.True(left, $"still '{capture.Status.Message}' 2.5 s after the library appeared");
        capture.Stop();
    }

    [Fact]
    public void Adapter_list_is_empty_without_the_library()
    {
        Assert.Empty(LiveCapture.ListDevices(() => false));
    }

    [Fact]
    public void Replay_without_the_library_stops_with_a_reason_instead_of_throwing()
    {
        var statuses = new List<CaptureStatus>();
        var replay = new PcapReplaySource(Data(), "does-not-matter.pcap", realtime: false, libraryPresent: () => false);
        replay.StatusChanged += statuses.Add;
        Assert.Equal(0, replay.RunToEnd());
        // Stopped is what takes the meter out of replay mode; an Error would leave it there for good
        Assert.Equal(CaptureState.Stopped, replay.Status.State);
        Assert.Equal(PcapReplaySource.NeedsNpcapMessage, replay.Status.Message);
        Assert.Single(statuses);
    }

    [Fact]
    public void Replay_of_a_missing_file_reports_instead_of_throwing()
    {
        var replay = new PcapReplaySource(Data(), Path.Combine(Path.GetTempPath(), "a2m27-no-such-file.pcap"), realtime: false, libraryPresent: () => true);
        Assert.Equal(0, replay.RunToEnd());
        Assert.Equal(CaptureState.Stopped, replay.Status.State);
        Assert.StartsWith("Replay failed", replay.Status.Message);
    }
}

/// <summary>A2M-27: the per-feature matrix and the texts Settings, the overlay footer and the tray build from it.</summary>
public class CaptureAvailabilityTests
{
    private static readonly CaptureAvailability Missing = new(static () => false);
    private static readonly CaptureAvailability Present = new(static () => true);

    [Fact]
    public void Live_only_features_are_exactly_the_ones_that_decode_traffic()
    {
        Assert.Equal(new[] { CaptureFeature.Dps, CaptureFeature.GameTimers, CaptureFeature.LiveState, CaptureFeature.AtlasPosition, CaptureFeature.PacketRecording },
            CaptureAvailability.All.Where(CaptureAvailability.IsLiveOnly));
        Assert.Equal(CaptureAvailability.LiveOnly, CaptureAvailability.All.Where(CaptureAvailability.IsLiveOnly));
        foreach (var f in new[] { CaptureFeature.Atlas, CaptureFeature.Prices, CaptureFeature.Craft, CaptureFeature.TopDps, CaptureFeature.BuildPlanner,
                     CaptureFeature.ItemsFromFiles, CaptureFeature.SharedBossTimers, CaptureFeature.AbyssLogs, CaptureFeature.FightHistory })
            Assert.False(CaptureAvailability.IsLiveOnly(f), f.ToString());
    }

    [Fact]
    public void Every_feature_has_an_english_label()
    {
        foreach (var f in CaptureAvailability.All) Assert.False(string.IsNullOrWhiteSpace(CaptureAvailability.EnglishLabels[f]), f.ToString());
    }

    [Fact]
    public void Without_the_library_only_live_features_are_off()
    {
        foreach (var f in CaptureAvailability.All)
        {
            Assert.Equal(!CaptureAvailability.IsLiveOnly(f), Missing.Has(f));
            Assert.True(Present.Has(f));
        }
    }

    [Fact]
    public void Settings_text_lists_lost_then_kept_features_from_the_matrix()
    {
        var text = Missing.SettingsText(CaptureAvailability.EnglishLabels, "HEAD", "LOST:", "KEPT:");
        var lines = text.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("HEAD", lines[0]);
        Assert.StartsWith("LOST: ", lines[1]);
        Assert.StartsWith("KEPT: ", lines[2]);
        foreach (var f in CaptureAvailability.All)
        {
            var label = CaptureAvailability.EnglishLabels[f];
            Assert.Contains(label, CaptureAvailability.IsLiveOnly(f) ? lines[1] : lines[2]);
        }
        Assert.Equal("LOST: DPS meter and fight breakdowns, timers read from the in-game event list, live inventory and character state, " +
                     "your position on the Atlas, packet recording (.pcap).", lines[1]);
        Assert.Equal("", Present.SettingsText(CaptureAvailability.EnglishLabels, "HEAD", "LOST:", "KEPT:"));
    }

    [Fact]
    public void Missing_label_falls_back_to_english()
    {
        var partial = new Dictionary<CaptureFeature, string> { [CaptureFeature.Dps] = "ДПС" };
        var text = Missing.SettingsText(partial, "h", "l", "k");
        Assert.Contains("l ДПС, timers read from the in-game event list", text);
        Assert.Contains(CaptureAvailability.EnglishLabels[CaptureFeature.Prices], text);
    }

    [Fact]
    public void Footer_and_tray_status_is_the_capture_status_of_a_missing_library()
    {
        // the overlay footer and the tray tooltip show the capture's status message; UiText swaps exactly this one for its translation
        Assert.Equal(CaptureAvailability.NoCaptureStatus, LiveCapture.NpcapMissingMessage);
        Assert.Equal("No capture (Npcap missing)", LiveCapture.NpcapMissingMessage);
    }

    [Fact]
    public void Health_line_says_why_there_are_no_counters()
    {
        Assert.Equal("Npcap not installed", Missing.HealthText(null));
        Assert.Equal("Npcap not installed", Missing.HealthText(new CaptureHealth(10, 1, 2)));
        Assert.Equal("no adapter open", Present.HealthText(null));
        Assert.Equal($"{1234:N0} packets, 1 dropped, 2 stream gaps", Present.HealthText(new CaptureHealth(1234, 1, 2)));
    }

    [Fact]
    public void Probe_is_cached_then_rechecked_so_an_install_is_picked_up()
    {
        var calls = 0;
        var installed = false;
        var a = new CaptureAvailability(() => { calls++; return installed; }, probeEveryMs: 60_000);
        Assert.False(a.Present);
        Assert.False(a.Present);
        Assert.Equal(1, calls);   // UI ticks ask freely: one probe per interval
        installed = true;
        var fresh = new CaptureAvailability(() => installed, probeEveryMs: 0);
        Assert.True(fresh.Present);
        installed = false;
        Assert.False(fresh.Present);
    }
}
