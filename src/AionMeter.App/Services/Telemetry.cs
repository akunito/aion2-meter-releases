using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AionMeter.App.Services;

/// <summary>
/// A2M-20: one anonymous usage record per day to Diego's n8n (opt-out in Settings → Updates, balloon the first time). What development
/// needs and nothing that names a player: build, Windows version, Npcap present, capture health, crash and error counts, tabs used,
/// UI language, how many characters and fights. The install id is a random GUID so installs can be told apart; the IP is seen by
/// n8n like any request and not stored. Same token and host as the bug reports.
/// </summary>
public static class Telemetry
{
    public const string Url = "https://example.invalid/webhook/aion2-meter-telemetry";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Counters = new();
    private static int _sending;

    /// <summary>Counts an interaction for the next record (e.g. "tab.atlas").</summary>
    public static void Count(string key) => Counters.AddOrUpdate(key, 1, static (_, v) => v + 1);

    public static bool Due(AppSettings s, DateTimeOffset now) =>
        s.Telemetry && (s.TelemetryLastSent is null || now - s.TelemetryLastSent.Value >= TimeSpan.FromHours(24));

    public static Dictionary<string, object?> Payload(MeterService m, DateTimeOffset now)
    {
        var s = m.Settings;
        if (string.IsNullOrEmpty(s.InstallId)) { s.InstallId = Guid.NewGuid().ToString("N"); s.Save(); }
        var health = m.CaptureHealth();
        var since = s.TelemetryLastSent ?? now.AddDays(-1);
        var crashes = 0;
        try { if (Directory.Exists(Log.Directory)) foreach (var f in Directory.EnumerateFiles(Log.Directory, "crash_*.txt")) if (File.GetLastWriteTimeUtc(f) > since.UtcDateTime) crashes++; } catch (IOException) { }
        var tabs = string.Join(",", Counters.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
        return new Dictionary<string, object?>
        {
            ["ts"] = now.UtcDateTime.ToString("o"),
            ["install"] = s.InstallId,
            ["build"] = BugReport.BuildLabel(),
            ["os"] = System.Environment.OSVersion.Version.ToString(),
            ["npcap"] = Core.Capture.LiveCapture.IsNpcapInstalled(),
            ["uptime_min"] = (int)(System.Environment.TickCount64 / 60_000),
            ["capture"] = m.CaptureStatus.State.ToString(),
            ["packets"] = health?.Received ?? 0,
            ["drops"] = health?.Dropped ?? 0,
            ["gaps"] = health?.Gaps ?? 0,
            ["events"] = m.CaptureStatus.Events,
            ["crashes"] = crashes,
            ["errors"] = Log.Errors,
            ["tabs"] = tabs,
            ["lang"] = UiText.Current.Code,
            ["characters"] = m.CharacterKeys().Count,
            ["fights"] = m.Tracker.Segments().Count,
        };
    }

    /// <summary>Sends the day's record when due; records the time only on a 2xx so a failed send retries next tick.</summary>
    public static async Task SendIfDueAsync(MeterService m)
    {
        var now = DateTimeOffset.UtcNow;
        if (!Due(m.Settings, now) || Interlocked.Exchange(ref _sending, 1) == 1) return;
        try
        {
            var payload = Payload(m, now);
            using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Meter-Token", BugReport.UploadToken);
            using var resp = await Http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                m.Settings.TelemetryLastSent = now; m.Settings.Save();
                Counters.Clear(); Log.ResetErrors();
                Log.Info("Telemetry sent");
            }
            else Log.Info($"Telemetry refused: {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { Log.Info($"Telemetry not sent: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _sending, 0); }
    }
}
