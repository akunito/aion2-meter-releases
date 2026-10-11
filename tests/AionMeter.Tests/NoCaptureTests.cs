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
}
