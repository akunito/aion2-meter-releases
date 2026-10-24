using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>settings.json written by builds that still had telemetry, report upload and backup/restore: it loads, keeps every other value,
/// and the next save drops the old keys (writes AppSettings.AppDataDir).</summary>
[Collection(ProcessStaticsCollection.Name)]
public class AppSettingsCompatTests
{
    private const string OldFile = """
        {
          "Telemetry": true, "ShareUsage": false, "ShareBuilds": true, "ShareBackup": true, "ShareAtlasNames": false,
          "TelemetryNoticeShown": true, "TelemetryLastSent": "2026-10-20T08:00:00+00:00", "InstallId": "3f9c0a12b7e44d85a6c1e09f2b7d5e31",
          "ReportSends": ["2026-10-20T08:00:00+00:00"], "BuildTelemetrySent": { "a1b2c3d4e5f6": { "Sent": "2026-10-20T08:00:00+00:00", "Updated": "2026-10-19T08:00:00+00:00" } },
          "BuildTelemetryVersion": 2, "StateTelemetrySent": "2026-10-20T08:00:00+00:00", "StateTelemetryHash": "0123456789abcdef",
          "ReportInventory": true, "BrokerFeePercent": 7.5, "LayoutVersion": 4
        }
        """;

    [Fact]
    public void Old_server_keys_are_ignored_and_dropped_on_save()
    {
        var before = AppSettings.AppDataDir;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aion-settings-compat-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            AppSettings.UseAppDataDir(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, OldFile);
            var s = AppSettings.Load();
            Assert.True(s.ReportInventory);
            Assert.Equal(7.5, s.BrokerFeePercent);
            s.Save();
            var written = File.ReadAllText(path);
            foreach (var key in (string[])["Telemetry", "ShareUsage", "ShareBackup", "InstallId", "ReportSends", "StateTelemetryHash"])
                Assert.DoesNotContain("\"" + key + "\"", written);
        }
        finally { AppSettings.UseAppDataDir(before); Directory.Delete(dir, recursive: true); }
    }
}
