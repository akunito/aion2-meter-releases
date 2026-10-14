using AionMeter.Core.Capture;
using Xunit;

namespace AionMeter.Tests;

public class CaptureRetentionTests
{
    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "aionmeter-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Fake(string dir, string stamp, int bytes) => File.WriteAllBytes(Path.Combine(dir, $"aion2_{stamp}.pcap"), new byte[bytes]);

    [Fact]
    public void Oldest_recordings_go_first_until_the_budget_fits()
    {
        var d = Dir();
        Fake(d, "20261008_120000", 300); Fake(d, "20261009_120000", 300); Fake(d, "20261010_120000", 300);
        Assert.Equal(1, CaptureRetention.Prune(d, 700, null));
        Assert.False(File.Exists(Path.Combine(d, "aion2_20261008_120000.pcap")));
        Assert.True(File.Exists(Path.Combine(d, "aion2_20261010_120000.pcap")));
        Directory.Delete(d, true);
    }

    [Fact]
    public void The_active_file_and_other_files_are_never_deleted_and_zero_means_unlimited()
    {
        var d = Dir();
        Fake(d, "20261008_120000", 500); File.WriteAllBytes(Path.Combine(d, "notes.txt"), new byte[500]);
        var active = Path.Combine(d, "aion2_20261008_120000.pcap");
        Assert.Equal(0, CaptureRetention.Prune(d, 100, active));
        Assert.Equal(0, CaptureRetention.Prune(d, 0, null));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(Path.Combine(d, "notes.txt")));
        Assert.Equal(0, CaptureRetention.Prune(Path.Combine(d, "missing"), 100, null));
        Directory.Delete(d, true);
    }
}
