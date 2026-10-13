using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AionMeter.Service;

/// <summary>
/// Bug reports a clanmate can produce without knowing where anything is (A2M-19, part 1: local bundle; part 2 sends it).
/// A crash writes logs\crash_&lt;ts&gt;.txt (exception + the last 200 log lines); "Report a problem" zips the last two logs, the
/// crash files, settings.json without the solver url, BUILD-INFO.txt and the player's note into reports\report_&lt;ts&gt;.zip, plus the
/// parts the player ticks (A2M-45: character summary, Top DPS rows, inventory; file choice in Core/Diagnostics/BugReportFiles).
/// </summary>
public static class BugReport
{
    public static string ReportsDir => Path.Combine(AppSettings.AppDataDir, "reports");
    private static string SettingsPath => Path.Combine(AppSettings.AppDataDir, "settings.json");

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

    /// <summary>Builds the zip; returns its path. Logs, crash files, settings and the build info always; the rest as the policy's report parts say (A2M-45).</summary>
    public static string Create(string note, Core.Diagnostics.SharePolicy policy)
    {
        Directory.CreateDirectory(ReportsDir);
        var path = Path.Combine(ReportsDir, $"report_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        zip.CreateEntry("note.txt").Write(note.Trim() + "\n\n" + Environment() +
            $"\nincluded: summary={policy.ReportSummary} topdps={policy.ReportTopDps} inventory={policy.ReportInventory}");
        foreach (var (source, entry) in Core.Diagnostics.BugReportFiles.Select(AppSettings.AppDataDir, AppContext.BaseDirectory, Log.Directory, policy))
            Add(zip, source, entry);
        try
        {
            // settings without the private solver url (a private-network address, not for the clan)
            if (File.Exists(SettingsPath))
            {
                var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(SettingsPath)) ?? new();
                node.Remove("CloudflareSolverUrl");
                zip.CreateEntry("settings.json").Write(JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (JsonException) { }
        return path;
    }

    // Upload to Diego's n8n webhook (A2M-19 part 2): the zip + note; the token only protects the upload endpoint (it is in the exe).
    public const string UploadUrl = "https://example.invalid/webhook/aion2-meter-report";
    internal const string UploadToken = "REMOVED-TOKEN";
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>"vX.Y.Z" for a release, "dev-&lt;sha7&gt;" for a hand-deployed build, "dev" without BUILD-INFO.</summary>
    public static string BuildLabel()
    {
        var build = "dev";
        try { var p = Path.Combine(AppContext.BaseDirectory, "BUILD-INFO.txt"); if (File.Exists(p)) foreach (var l in File.ReadLines(p)) { if (l.StartsWith("release ")) build = l[8..].Trim(); else if (l.StartsWith("upstream ") && build == "dev") build = "dev-" + l[9..16]; } } catch (IOException) { }
        return build;
    }

    /// <summary>POSTs the zip; returns the server's id. Throws HttpRequestException on refusal (403 token, 413 size) or network errors.</summary>
    public static async Task<string> UploadAsync(string zipPath, string note, CancellationToken ct)
    {
        var build = BuildLabel();
        using var form = new System.Net.Http.MultipartFormDataContent();
        // StringContent carries "text/plain" and n8n then treats the field as an uploaded file (Diego's first send arrived as data0/data1): no content type
        var noteContent = new System.Net.Http.StringContent(note.Length > 900 ? note[..900] : note); noteContent.Headers.ContentType = null; form.Add(noteContent, "note");
        var buildContent = new System.Net.Http.StringContent(build); buildContent.Headers.ContentType = null; form.Add(buildContent, "build");
        var bytes = await File.ReadAllBytesAsync(zipPath, ct);
        var file = new System.Net.Http.ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", Path.GetFileName(zipPath));
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, UploadUrl) { Content = form };
        req.Headers.Add("X-Meter-Token", UploadToken);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new System.Net.Http.HttpRequestException($"{(int)resp.StatusCode} {body}");
        try { using var doc = System.Text.Json.JsonDocument.Parse(body); return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : ""; }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    /// <summary>Null when a send is allowed now, else the wait and a reason key: "gap" (10 s), "minute" (2/min), "hour" (5/h), "day" (10/day).</summary>
    public static (TimeSpan Wait, string Reason)? SendBlock(AppSettings s, DateTimeOffset now)
    {
        s.ReportSends.RemoveAll(t => now - t > TimeSpan.FromHours(24));
        if (s.ReportSends.Count == 0) return null;
        var last = s.ReportSends.Max();
        if (now - last < TimeSpan.FromSeconds(10)) return (TimeSpan.FromSeconds(10) - (now - last), "gap");
        var minute = s.ReportSends.Where(t => now - t < TimeSpan.FromMinutes(1)).ToList();
        if (minute.Count >= 2) return (TimeSpan.FromMinutes(1) - (now - minute.Min()), "minute");
        var hour = s.ReportSends.Where(t => now - t < TimeSpan.FromHours(1)).ToList();
        if (hour.Count >= 5) return (TimeSpan.FromHours(1) - (now - hour.Min()), "hour");
        if (s.ReportSends.Count >= 10) return (TimeSpan.FromHours(24) - (now - s.ReportSends.Min()), "day");
        return null;
    }

    /// <summary>Explorer on the zip (Windows); on Linux the file manager on the reports folder through xdg-open (no portable "select this file").</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", Path.GetDirectoryName(path) ?? path) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("Could not open the reports folder", ex); }
    }

    /// <summary>
    /// "Send to Diego" without the window (A2M-48 phase 3c, from the WPF BugReportWindow): the zip per <paramref name="policy"/>, the upload,
    /// and the send recorded in <see cref="AppSettings.ReportSends"/> (saved) for <see cref="SendBlock"/>. Throws what Create/upload throw; the
    /// zip path is in <see cref="SendFailure.Zip"/> when the upload failed after the zip was made. <paramref name="upload"/>/<paramref name="create"/>: tests.
    /// </summary>
    public static async Task<(string Zip, string Id)> SendAsync(AppSettings s, string note, Core.Diagnostics.SharePolicy policy, CancellationToken ct = default,
        Func<string, string, CancellationToken, Task<string>>? upload = null, Func<string, Core.Diagnostics.SharePolicy, string>? create = null)
    {
        if (SendBlock(s, DateTimeOffset.UtcNow) is { } block) throw new InvalidOperationException($"report send blocked ({block.Reason}, {block.Wait:g})");
        var zip = (create ?? Create)(note, policy);
        string id;
        try { id = await (upload ?? UploadAsync)(zip, note, ct).ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or TaskCanceledException) { throw new SendFailure(zip, ex); }
        s.ReportSends.Add(DateTimeOffset.UtcNow);
        try { s.Save(); } catch (IOException ex) { Log.Info($"Report send not recorded in settings.json: {ex.Message}"); }
        return (zip, id);
    }

    /// <summary>The upload failed after the zip was written: <see cref="Zip"/> stays for the user to hand over by other means.</summary>
    public sealed class SendFailure(string zip, Exception inner) : IOException(inner is TaskCanceledException ? "timeout" : inner.Message, inner)
    {
        public string Zip { get; } = zip;
    }

    private static string Environment()
    {
        var build = "?";
        try { var p = Path.Combine(AppContext.BaseDirectory, "BUILD-INFO.txt"); if (File.Exists(p)) build = string.Join(" | ", File.ReadLines(p)); } catch (IOException) { }
        return $"build: {build}\nos: {System.Environment.OSVersion} 64bit={System.Environment.Is64BitOperatingSystem}\n" +
               $"{(OperatingSystem.IsWindows() ? "npcap" : "libpcap (this process)")}: {(Core.Capture.LiveCapture.IsNpcapInstalled() ? "installed" : "MISSING")}\nuptime: {TimeSpan.FromMilliseconds(System.Environment.TickCount64):g}\n" +
               $"culture: {System.Globalization.CultureInfo.CurrentUICulture.Name} ui={UiText.Current.Code}\nfolder: {AppContext.BaseDirectory}";
    }

    private static IEnumerable<string> TailLog(int n)
    {
        try
        {
            var f = Directory.EnumerateFiles(Log.Directory, Log.Prefix + "_*.log").OrderByDescending(x => x).FirstOrDefault();
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
