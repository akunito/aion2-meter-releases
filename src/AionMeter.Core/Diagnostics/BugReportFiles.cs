namespace AionMeter.Core.Diagnostics;

/// <summary>
/// The files of a "Report a problem" zip (A2M-19, A2M-45), as (source path, zip entry). The note and the sanitised settings.json are
/// written by the app itself and are not listed. Missing files are skipped.
/// </summary>
public static class BugReportFiles
{
    /// <summary>Inventory-derived files inside a character folder: they go only with <see cref="ShareKind.ReportInventory"/>.</summary>
    private static bool IsInventoryFile(string fileName) =>
        fileName.Equals("inventory.json", StringComparison.OrdinalIgnoreCase) || fileName.Equals("craftable.json", StringComparison.OrdinalIgnoreCase);

    public static List<(string Source, string Entry)> Select(string appData, string appDir, string logDir, SharePolicy policy)
    {
        var list = new List<(string, string)>(16);
        foreach (var f in (ReadOnlySpan<string>)["BUILD-INFO.txt", "SHA256SUMS.txt"]) AddIfExists(list, Path.Combine(appDir, f), f);
        if (Directory.Exists(logDir))
        {
            var logs = Directory.GetFiles(logDir, "aionmeter_*.log");
            Array.Sort(logs, StringComparer.Ordinal);
            for (var i = logs.Length - 1; i >= 0 && i >= logs.Length - 2; i--) list.Add((logs[i], "logs/" + Path.GetFileName(logs[i])));
            // Linux (A2M-48): the card logs apart (Log.Prefix "aionmeter-overlay"; A2M-50, was the Avalonia UI's "aionmeter-ui"); "aionmeter_*" above is the daemon's
            var ui = Directory.GetFiles(logDir, "aionmeter-overlay_*.log");
            Array.Sort(ui, StringComparer.Ordinal);
            for (var i = ui.Length - 1; i >= 0 && i >= ui.Length - 2; i--) list.Add((ui[i], "logs/" + Path.GetFileName(ui[i])));
            var crashes = Directory.GetFiles(logDir, "crash_*.txt");
            Array.Sort(crashes, StringComparer.Ordinal);
            for (var i = crashes.Length - 1; i >= 0 && i >= crashes.Length - 5; i--) list.Add((crashes[i], "logs/" + Path.GetFileName(crashes[i])));
        }
        if (policy.ReportSummary)
        {
            AddIfExists(list, Path.Combine(appData, "state", "summary.json"), "state/summary.json");
            AddIfExists(list, Path.Combine(appData, "state", "character.json"), "state/character.json");
        }
        if (policy.ReportTopDps) AddIfExists(list, Path.Combine(appData, "top-dps.json"), "top-dps.json");
        if (policy.ReportInventory)
        {
            AddIfExists(list, Path.Combine(appData, "inventory.json"), "inventory.json");
            AddIfExists(list, Path.Combine(appData, "account-warehouse.json"), "account-warehouse.json");
        }
        var chars = Path.Combine(appData, "characters");
        if ((policy.ReportSummary || policy.ReportInventory) && Directory.Exists(chars))
        {
            var files = Directory.GetFiles(chars, "*.json", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
                if (IsInventoryFile(Path.GetFileName(f)) ? policy.ReportInventory : policy.ReportSummary)
                    list.Add((f, "characters/" + Path.GetRelativePath(chars, f).Replace('\\', '/')));
        }
        return list;
    }

    private static void AddIfExists(List<(string, string)> list, string file, string entry)
    {
        if (File.Exists(file)) list.Add((file, entry));
    }
}
