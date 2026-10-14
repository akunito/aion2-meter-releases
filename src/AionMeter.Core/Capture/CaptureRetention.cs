namespace AionMeter.Core.Capture;

/// <summary>
/// Keeps the recorded pcaps (Settings "Record packets") under a size budget: oldest first by file name (aion2_yyyyMMdd_HHmmss.pcap sorts
/// chronologically), never the file being written. The Windows install had grown to 1 GB unnoticed (147 files, 2026-10-14); the clan
/// default is 2 GB (AppSettings.CaptureKeepGb), 0 = unlimited.
/// </summary>
public static class CaptureRetention
{
    public const string Pattern = "aion2_*.pcap";

    /// <summary>Deletes the oldest recordings in <paramref name="dir"/> until the rest fit <paramref name="keepBytes"/>; returns how many went.</summary>
    public static int Prune(string dir, long keepBytes, string? active)
    {
        if (keepBytes <= 0 || !Directory.Exists(dir)) return 0;
        var files = Directory.GetFiles(dir, Pattern);
        if (files.Length == 0) return 0;
        Array.Sort(files, StringComparer.Ordinal);
        var sizes = new long[files.Length];
        long total = 0;
        for (var i = 0; i < files.Length; i++) { sizes[i] = new FileInfo(files[i]).Length; total += sizes[i]; }
        var deleted = 0;
        for (var i = 0; i < files.Length && total > keepBytes; i++)
        {
            if (active is not null && string.Equals(Path.GetFullPath(files[i]), Path.GetFullPath(active), StringComparison.Ordinal)) continue;
            try { File.Delete(files[i]); total -= sizes[i]; deleted++; }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }
}
