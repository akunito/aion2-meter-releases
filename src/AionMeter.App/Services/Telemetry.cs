using System.IO;
using System.Net.Http;
using System.Text.Json;
using AionMeter.Core.Builds;

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

    // ------------------------------------------------------------------ builds (A2M-21, same switch; Diego: on by default)

    private static readonly object BuildLock = new();
    private static readonly Dictionary<string, (Build Build, string Class, int Level)> PendingBuilds = new();
    private static Timer? _buildTimer;
    private static AppSettings? _buildSettings;
    /// <summary>A build is sent this long after the last save of any build, so one editing session sends once.</summary>
    public static readonly TimeSpan BuildQuiet = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BuildEvery = TimeSpan.FromHours(24);

    /// <summary>Called on every build save: the newest copy of each build waits for <see cref="BuildQuiet"/> without saves, then goes if due.</summary>
    public static void QueueBuild(AppSettings s, Build b, string installClass, int characterLevel)
    {
        if (!s.Telemetry || s.Transient || b.Id.Length == 0) return;
        lock (BuildLock)
        {
            PendingBuilds[b.Id] = (b, installClass, characterLevel);
            _buildSettings = s;
            _buildTimer ??= new Timer(static _ => _ = FlushBuildsAsync());
            _buildTimer.Change(BuildQuiet, Timeout.InfiniteTimeSpan);
        }
    }

    private static async Task FlushBuildsAsync()
    {
        (Build Build, string Class, int Level)[] batch;
        AppSettings? s;
        lock (BuildLock) { batch = [.. PendingBuilds.Values]; PendingBuilds.Clear(); s = _buildSettings; }
        if (s is null) return;
        foreach (var (b, cls, lvl) in batch) await SendBuildAsync(s, b, cls, lvl).ConfigureAwait(false);
    }

    /// <summary>Not sent in the last 24 h and changed since the last send.</summary>
    public static bool BuildDue(AppSettings s, Build b, DateTimeOffset now) =>
        s.Telemetry && (!s.BuildTelemetrySent.TryGetValue(b.Id, out var last) || (last.Updated != b.UpdatedAt && now - last.Sent >= BuildEvery));

    public static Dictionary<string, object?> BuildPayload(AppSettings s, Build b, string installClass, int characterLevel, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(s.InstallId)) { s.InstallId = Guid.NewGuid().ToString("N"); s.Save(); }
        var skills = new object[b.Skills.Count];
        for (var i = 0; i < skills.Length; i++)
        {
            var k = b.Skills[i];
            skills[i] = new Dictionary<string, object?> { ["skillId"] = k.SkillId, ["level"] = k.Level, ["slot"] = k.Slot, ["specs"] = k.Specs };
        }
        var macros = new object[b.Macros.Count];
        for (var i = 0; i < macros.Length; i++) macros[i] = new Dictionary<string, object?> { ["name"] = b.Macros[i].Name, ["steps"] = b.Macros[i].Steps };
        return new Dictionary<string, object?>
        {
            ["kind"] = "build",
            ["ts"] = now.UtcDateTime.ToString("o"),
            ["install"] = s.InstallId,
            ["class"] = installClass.Length > 0 ? installClass : b.ClassId,
            ["name"] = b.Name,
            ["source"] = b.Source is { Id: > 0 } src ? "questlog:" + src.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "local",
            ["level"] = characterLevel,
            ["skills"] = skills,
            ["priority"] = b.Priority,
            ["macros"] = macros,
            ["daevanion"] = b.Daevanion,
            ["notes"] = b.Notes,
            ["build_id"] = b.Id,
        };
    }

    /// <summary>Posts one build when due (n8n allows 20 per install per day); the send is recorded only on a 2xx.</summary>
    public static async Task SendBuildAsync(AppSettings s, Build build, string installClass, int characterLevel)
    {
        var now = DateTimeOffset.UtcNow;
        if (s.Transient || !BuildDue(s, build, now)) return;
        try
        {
            var body = JsonSerializer.Serialize(BuildPayload(s, build, installClass, characterLevel, now));
            using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Meter-Token", BugReport.UploadToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.Info($"Build telemetry refused: {(int)resp.StatusCode}"); return; }
            // settings are written from the UI thread elsewhere: the dictionary changes there too, never under a serializer
            void Record()
            {
                s.BuildTelemetrySent[build.Id] = new AppSettings.BuildSent(now, build.UpdatedAt);
                List<string>? old = null;
                foreach (var kv in s.BuildTelemetrySent) if (now - kv.Value.Sent > TimeSpan.FromDays(60)) (old ??= []).Add(kv.Key);
                if (old is not null) foreach (var k in old) s.BuildTelemetrySent.Remove(k);
                try { s.Save(); } catch (IOException ex) { Log.Info($"Build telemetry: settings not saved: {ex.Message}"); }
            }
            if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) await d.InvokeAsync(Record);
            else Record();
            Log.Info($"Build telemetry sent: {build.Id}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { Log.Info($"Build telemetry not sent: {ex.Message}"); }
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
