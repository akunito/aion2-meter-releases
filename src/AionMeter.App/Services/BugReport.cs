using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AionMeter.App.Services;

/// <summary>
/// Bug reports a clanmate can produce without knowing where anything is (A2M-19, part 1: local bundle; part 2 sends it).
/// A crash writes logs\crash_&lt;ts&gt;.txt (exception + the last 200 log lines); "Report a problem" zips the last two logs, the
/// crash files, settings.json without the solver url, state\summary.json, BUILD-INFO.txt and the player's note into
/// reports\report_&lt;ts&gt;.zip. Character files only when the player ticks "include my character state".
/// </summary>
public static class BugReport
{
    public static string ReportsDir => Path.Combine(AppSettings.AppDataDir, "reports");

    public static string? WriteCrash(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            var path = Path.Combine(Log.Directory, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var sb = new StringBuilder(8192);
            sb.Append(kind).Append(' ').Append(DateTime.Now.ToString("O")).Append('\n');
            sb.Append(Environment()).Append('\n');
            sb.Append(ex?.ToString() ?? "(no exception object)").Append("\n\n--- last 200 log lines ---\n");
            foreach (var line in TailLog(200)) sb.Append(line).Append('\n');
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Builds the zip; returns its path.</summary>
    public static string Create(string note, bool includeState)
    {
        Directory.CreateDirectory(ReportsDir);
        var path = Path.Combine(ReportsDir, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("note.txt").Write(note.Trim() + "\n\n" + Environment());
        var app = AppContext.BaseDirectory;
        foreach (var f in new[] { "BUILD-INFO.txt", "SHA256SUMS.txt" }) Add(zip, Path.Combine(app, f), f);
        if (Directory.Exists(Log.Directory))
        {
            foreach (var f in Directory.EnumerateFiles(Log.Directory, "aionmeter_*.log").OrderByDescending(x => x).Take(2)) Add(zip, f, "logs/" + Path.GetFileName(f));
            foreach (var f in Directory.EnumerateFiles(Log.Directory, "crash_*.txt").OrderByDescending(x => x).Take(5)) Add(zip, f, "logs/" + Path.GetFileName(f));
        }
        Add(zip, Path.Combine(AppSettings.AppDataDir, "state", "summary.json"), "state/summary.json");
        Add(zip, Path.Combine(AppSettings.AppDataDir, "top-dps.json"), "top-dps.json");
        try
        {
            // settings without the private solver url (a private-network address, not for the clan)
            if (File.Exists(AppSettings.FilePath))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(AppSettings.FilePath)) ?? new();
                node.Remove("CloudflareSolverUrl");
                zip.CreateEntry("settings.json").Write(JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (JsonException) { }
        if (includeState)
        {
            var chars = Path.Combine(AppSettings.AppDataDir, "characters");
            if (Directory.Exists(chars))
                foreach (var f in Directory.EnumerateFiles(chars, "*.json", SearchOption.AllDirectories))
                    Add(zip, f, "characters/" + Path.GetRelativePath(chars, f).Replace('\\', '/'));
        }
        return path;
    }

    public static void Reveal(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error("Could not open Explorer", ex); }
    }

    private static string Environment()
    {
        var build = "?";
        try { var p = Path.Combine(AppContext.BaseDirectory, "BUILD-INFO.txt"); if (File.Exists(p)) build = string.Join(" | ", File.ReadLines(p)); } catch (IOException) { }
        return $"build: {build}\nos: {System.Environment.OSVersion} 64bit={System.Environment.Is64BitOperatingSystem}\n" +
               $"npcap: {(Core.Capture.LiveCapture.IsNpcapInstalled() ? "installed" : "MISSING")}\nuptime: {TimeSpan.FromMilliseconds(System.Environment.TickCount64):g}\n" +
               $"culture: {System.Globalization.CultureInfo.CurrentUICulture.Name} ui={UiText.Current.Code}\nfolder: {AppContext.BaseDirectory}";
    }

    private static IEnumerable<string> TailLog(int n)
    {
        try
        {
            var f = Directory.EnumerateFiles(Log.Directory, "aionmeter_*.log").OrderByDescending(x => x).FirstOrDefault();
            if (f is null) return [];
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            var q = new Queue<string>(n + 1);
            while (r.ReadLine() is { } line) { q.Enqueue(line); if (q.Count > n) q.Dequeue(); }
            return q;
        }
        catch (IOException) { return []; }
    }

    private static void Add(ZipArchive zip, string file, string entry)
    {
        try
        {
            if (!File.Exists(file)) return;
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // today's log is open for append
            using var dst = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
            src.CopyTo(dst);
        }
        catch (IOException) { }
    }

    private static void Write(this ZipArchiveEntry entry, string text)
    {
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }
}
