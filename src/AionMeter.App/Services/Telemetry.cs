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
        s.EnsureInstallId();
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
    private static readonly Dictionary<string, (Build Build, string Class, int Level, string Key)> PendingBuilds = new();
    private static Timer? _buildTimer;
    private static AppSettings? _buildSettings;
    /// <summary>A build is sent this long after the last save of any build, so one editing session sends once.</summary>
    public static readonly TimeSpan BuildQuiet = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BuildEvery = TimeSpan.FromHours(24);

    /// <summary>Called on every build save: the newest copy of each build waits for <see cref="BuildQuiet"/> without saves, then goes if due.</summary>
    /// <param name="characterKey">Key of the character whose store holds the build ("" = shared folder): only its hash is sent.</param>
    public static void QueueBuild(AppSettings s, Build b, string installClass, int characterLevel, string characterKey)
    {
        if (!s.Telemetry || s.Transient || b.Id.Length == 0) return;
        lock (BuildLock)
        {
            PendingBuilds[b.Id] = (b, installClass, characterLevel, characterKey);
            _buildSettings = s;
            _buildTimer ??= new Timer(static _ => _ = FlushBuildsAsync());
            _buildTimer.Change(BuildQuiet, Timeout.InfiniteTimeSpan);
        }
    }

    private static async Task FlushBuildsAsync()
    {
        (Build Build, string Class, int Level, string Key)[] batch;
        AppSettings? s;
        lock (BuildLock) { batch = [.. PendingBuilds.Values]; PendingBuilds.Clear(); s = _buildSettings; }
        if (s is null) return;
        foreach (var (b, cls, lvl, key) in batch) await SendBuildAsync(s, b, cls, lvl, key).ConfigureAwait(false);
    }

    /// <summary>Not sent in the last 24 h and changed since the last send.</summary>
    public static bool BuildDue(AppSettings s, Build b, DateTimeOffset now) =>
        s.Telemetry && (!s.BuildTelemetrySent.TryGetValue(b.Id, out var last) || (last.Updated != b.UpdatedAt && now - last.Sent >= BuildEvery));

    public static Dictionary<string, object?> BuildPayload(AppSettings s, Build b, string installClass, int characterLevel, string characterKey, DateTimeOffset now)
    {
        var install = s.EnsureInstallId();
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
            ["install"] = install,
            ["charhash"] = BackupCode.CharHash(install, characterKey),
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

    /// <summary>Posts one build when due (n8n allows 20 per install per day); the send is recorded only on a 2xx. False on a refusal or a network error.</summary>
    public static async Task<bool> SendBuildAsync(AppSettings s, Build build, string installClass, int characterLevel, string characterKey)
    {
        var now = DateTimeOffset.UtcNow;
        if (s.Transient || !BuildDue(s, build, now)) return true;
        try
        {
            var body = JsonSerializer.Serialize(BuildPayload(s, build, installClass, characterLevel, characterKey, now));
            using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Meter-Token", BugReport.UploadToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.Info($"Build telemetry refused: {(int)resp.StatusCode}"); return false; }
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
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { Log.Info($"Build telemetry not sent: {ex.Message}"); return false; }
    }

    /// <summary>
    /// After the day's record: every build on disk not sent yet (new installs of A2M-30 cleared the marks so old rows get a charhash),
    /// at most <see cref="SweepMax"/> a day so edits keep room under n8n's 20 per install per day.
    /// </summary>
    public const int SweepMax = 10;
    private static async Task SweepBuildsAsync(AppSettings s)
    {
        var now = DateTimeOffset.UtcNow;
        var sent = 0;
        foreach (var (key, dir) in BuildFolders())
        {
            foreach (var b in new BuildStore(dir).List())
            {
                if (!BuildDue(s, b, now)) continue;
                if (sent == SweepMax || !await SendBuildAsync(s, b, "", 0, key).ConfigureAwait(false)) return;
                sent++;
            }
        }
    }

    private static IEnumerable<(string Key, string Dir)> BuildFolders()
    {
        yield return ("", Path.Combine(AppSettings.AppDataDir, "builds"));
        var chars = Path.Combine(AppSettings.AppDataDir, "characters");
        if (!Directory.Exists(chars)) yield break;
        foreach (var d in Directory.EnumerateDirectories(chars))
            if (Path.GetFileName(d) is { Length: > 0 } key && Directory.Exists(Path.Combine(d, "builds"))) yield return (key, Path.Combine(d, "builds"));
    }

    // ------------------------------------------------------------------ state (A2M-30: watch list + craft favourites, kind "watch")

    private static readonly object StateLock = new();
    private static Timer? _stateTimer;
    private static MeterService? _stateMeter;
    private static int _stateSending;
    /// <summary>n8n keeps 6 "watch" rows per install per day: change-driven sends are spaced at least this far apart.</summary>
    public static readonly TimeSpan StateGap = TimeSpan.FromMinutes(30);

    /// <summary>The watch list or the favourites changed: send the state 60 s after the last change (later if one went out recently).</summary>
    public static void QueueState(MeterService m)
    {
        var s = m.Settings;
        if (!s.Telemetry || s.Transient) return;
        lock (StateLock)
        {
            _stateMeter = m;
            _stateTimer ??= new Timer(static _ => { if (_stateMeter is { } mm) _ = SendStateAsync(mm, daily: false); });
            var wait = BuildQuiet;
            if (s.StateTelemetrySent is { } last && DateTimeOffset.UtcNow - last + wait < StateGap) wait = StateGap - (DateTimeOffset.UtcNow - last);
            _stateTimer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    public static string StateJson(MeterService m) => BackupState.ToJson(m.Prices.All(), m.Settings.CraftFavourites.ToArray());

    /// <summary>daily: with the day's record, sent even when unchanged so the server copy stays the latest; otherwise only when it changed.</summary>
    public static async Task SendStateAsync(MeterService m, bool daily)
    {
        var s = m.Settings;
        if (!s.Telemetry || s.Transient || Interlocked.Exchange(ref _stateSending, 1) == 1) return;
        try
        {
            // the favourites list is edited on the UI thread: read it there
            var data = System.Windows.Application.Current?.Dispatcher is { } ui && !ui.CheckAccess() ? await ui.InvokeAsync(() => StateJson(m)) : StateJson(m);
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data)))[..16];
            var now = DateTimeOffset.UtcNow;
            if (hash == s.StateTelemetryHash && !(daily && (s.StateTelemetrySent is null || now - s.StateTelemetrySent.Value >= TimeSpan.FromHours(20)))) return;
            // an empty state never overwrites a stored one unless something was sent from here before (a fresh PC before its restore)
            if (s.StateTelemetryHash.Length == 0 && data == BackupState.ToJson([], [])) return;
            var body = "{\"kind\":\"watch\",\"install\":" + JsonSerializer.Serialize(s.EnsureInstallId()) + ",\"ts\":\"" + now.UtcDateTime.ToString("o") + "\",\"data\":" + data + "}";
            using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Meter-Token", BugReport.UploadToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log.Info($"State telemetry refused: {(int)resp.StatusCode}"); return; }
            void Record() { s.StateTelemetrySent = now; s.StateTelemetryHash = hash; try { s.Save(); } catch (IOException ex) { Log.Info($"State telemetry: settings not saved: {ex.Message}"); } }
            if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) await d.InvokeAsync(Record);
            else Record();
            Log.Info($"State telemetry sent ({data.Length} bytes)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { Log.Info($"State telemetry not sent: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _stateSending, 0); }
    }

    // ------------------------------------------------------------------ restore (A2M-30)

    public const string RestoreUrl = "https://example.invalid/webhook/aion2-meter-restore";

    /// <summary>POST {install} to the restore webhook. Status 0 = no answer (Error says why); 200 body = the stored builds and state.</summary>
    public static async Task<(int Status, string Body, string? Error)> FetchRestoreAsync(string code)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, RestoreUrl) { Content = new StringContent("{\"install\":\"" + code + "\"}", System.Text.Encoding.UTF8, "application/json") };
            req.Headers.Add("X-Meter-Token", BugReport.UploadToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            Log.Info($"Restore: {(int)resp.StatusCode}, {body.Length} bytes");
            return ((int)resp.StatusCode, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { Log.Info($"Restore failed: {ex.Message}"); return (0, "", ex.Message); }
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
                await SendStateAsync(m, daily: true);
                await SweepBuildsAsync(m.Settings);
            }
            else Log.Info($"Telemetry refused: {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { Log.Info($"Telemetry not sent: {ex.Message}"); }
        finally { Interlocked.Exchange(ref _sending, 0); }
    }
}
