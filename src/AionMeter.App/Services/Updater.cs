using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.IO.Compression;
using System.Windows.Threading;
using AionMeter.Core.Updates;
using Microsoft.Win32;

namespace AionMeter.App.Services;

public enum UpdateCheckResult
{
    UpToDate,
    Available,
    Failed,
}

/// <summary>
/// New versions of the meter. Asks GitHub shortly after start and every few hours (Settings → Updates) and raises
/// <see cref="Changed"/> for the tray, the overlay and the update window. A copy installed by the installer updates
/// itself: it downloads the new installer in the background (<see cref="Downloaded"/>), and the app runs it at a quiet
/// moment without any window; the installer replaces this copy and starts the new one. A portable copy gets the
/// download page.
/// </summary>
public sealed class Updater : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(2);
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AE1B2590-0A93-4500-9CB1-0F543771957A}_is1"; // AppId in installer/AION2DpsMeter.iss

    private readonly AppSettings _settings;
    private readonly UpdateFeed _feed;
    private readonly UpdateFeed _upstream;
    private DispatcherTimer? _timer;
    private Task<UpdateCheckResult>? _running;

    public Updater(AppSettings settings)
    {
        _settings = settings;
        Current = UpdateFeed.Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
        (ReleaseTag, BuildSha) = ReadBuildInfo();
        _feed = new UpdateFeed($"AION2DpsMeter/{Current.ToString(3)}");
        _upstream = new UpdateFeed($"AION2DpsMeter/{Current.ToString(3)}", UpdateFeed.UpstreamLatestApi);
    }

    /// <summary>This copy's version (major.minor.patch).</summary>
    public Version Current { get; }

    /// <summary>"v0.3.0" for a published build (BUILD-INFO.txt has a "release" line); null for a developer build deployed by hand.</summary>
    public string? ReleaseTag { get; }
    public string? BuildSha { get; }
    /// <summary>A hand-deployed build never updates itself from the release feed (it would overwrite work in progress).</summary>
    public bool IsDevBuild => ReleaseTag is null;

    /// <summary>The upstream project's newest release, watched only when Settings.CheckUpstream (Diego: "re-audit" signal).</summary>
    public ReleaseInfo? UpstreamLatest { get; private set; }
    /// <summary>The audited upstream version this build is based on (patch 0001–00xx on v0.2.5).</summary>
    public static readonly Version UpstreamBase = new(0, 2, 5);
    public ReleaseInfo? UpstreamAvailable => UpstreamLatest is { } u && u.Version > UpstreamBase ? u : null;

    private static (string? Tag, string? Sha) ReadBuildInfo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BUILD-INFO.txt");
            if (!File.Exists(path)) return (null, null);
            string? tag = null, sha = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("release ", StringComparison.Ordinal)) tag = line[8..].Trim();
                else if (line.StartsWith("upstream ", StringComparison.Ordinal)) sha = line[9..].Trim();
            }
            return (tag, sha is { Length: >= 7 } ? sha[..7] : sha);
        }
        catch (IOException) { return (null, null); }
    }

    /// <summary>The newest release GitHub reported, newer or not; null before the first successful check.</summary>
    public ReleaseInfo? Latest { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>A release newer than this copy, unless the user chose to skip that version. Never for a developer build.</summary>
    public ReleaseInfo? Available =>
        !IsDevBuild && Latest is { } r && r.Version > Current && r.Version.ToString(3) != _settings.SkippedUpdate ? r : null;

    /// <summary>True when this copy runs from the folder the installer put it in (it can update itself).</summary>
    public static bool IsInstalled { get; } = DetectInstalled();

    /// <summary>This copy fetches and applies new versions by itself: the portable zip is unpacked over this folder (clan build, 2026-10-10).</summary>
    public const bool DownloadsDisabled = false;
    public bool AutoInstall => !IsDevBuild && _settings.AutoInstallUpdates;

    /// <summary>The update whose installer is downloaded and checked, waiting for a quiet moment to be installed.</summary>
    public ReleaseInfo? Downloaded { get; private set; }

    /// <summary>The background download of <see cref="Available"/> failed: fall back to asking the player.</summary>
    public bool DownloadFailed { get; private set; }

    private string? _downloadedPath;
    private bool _downloading;

    /// <summary>Raised on the UI thread after every check and when a version is skipped.</summary>
    public event Action? Changed;

    /// <summary>Automatic checks: the first shortly after start, then every few hours, while the setting is on.</summary>
    public void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FirstCheckDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (_settings.CheckUpdates) _ = CheckAsync();
        };
        _timer.Start();
    }

    /// <summary>Asks GitHub now. <see cref="UpdateCheckResult.Available"/> also for a skipped version (the user asked).</summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (_running is { } running) return await running; // a check is already on its way: share its answer
        _running = RunCheckAsync();
        try
        {
            return await _running;
        }
        finally
        {
            _running = null;
        }
    }

    private async Task<UpdateCheckResult> RunCheckAsync()
    {
        try
        {
            Latest = _settings.UpdateChannel == "dev" ? await _feed.GetNewestIncludingPrereleasesAsync() : await _feed.GetLatestAsync();
            LastError = null;
            Log.Info($"Update check ({_settings.UpdateChannel}): newest release {Latest?.Tag ?? "none"}, this copy {Current.ToString(3)} {ReleaseTag ?? "dev " + BuildSha}.");
            if (_settings.CheckUpstream)
            {
                try { UpstreamLatest = await _upstream.GetLatestAsync(); Log.Info($"Upstream check: newest {UpstreamLatest?.Tag ?? "none"}, audited base {UpstreamBase.ToString(3)}."); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { Log.Info($"Upstream check failed: {ex.Message}"); }
            }
            if (Available is { Portable: not null } update && AutoInstall) _ = PrepareAsync(update);
            return Available is not null || (!IsDevBuild && Latest is { } r && r.Version > Current) ? UpdateCheckResult.Available : UpdateCheckResult.UpToDate;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            LastError = ex is TaskCanceledException ? "timeout" : ex.Message;
            Log.Info($"Update check failed: {LastError}");
            return UpdateCheckResult.Failed;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Stop announcing this version; a newer one is announced again.</summary>
    public void Skip(ReleaseInfo release)
    {
        _settings.SkippedUpdate = release.Version.ToString(3);
        _settings.Save();
        Changed?.Invoke();
    }

    /// <summary>Downloads the release's portable zip to the temp folder, checked against GitHub's size and SHA-256.</summary>
    public Task<string> DownloadInstallerAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct) =>
        release.Portable is { } zip
            ? _feed.DownloadAsync(zip, DownloadFolder, progress, ct)
            : throw new InvalidOperationException($"Release {release.Tag} has no portable zip");

    /// <summary>Fetches the update in the background, so it can go in at the next quiet moment.</summary>
    private async Task PrepareAsync(ReleaseInfo release)
    {
        if (_downloading || Downloaded?.Version == release.Version) return;
        _downloading = true;
        DownloadFailed = false;
        try
        {
            _downloadedPath = await DownloadInstallerAsync(release, null, CancellationToken.None);
            Downloaded = release;
            Log.Info($"Update {release.Tag} downloaded: {_downloadedPath}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException
                                       or OperationCanceledException)
        {
            DownloadFailed = true;
            Log.Info($"Update {release.Tag} not downloaded: {ex.Message}");
        }
        finally
        {
            _downloading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Applies the downloaded zip without any window; the caller exits right after.</summary>
    public void InstallDownloaded()
    {
        if (_downloadedPath is null) throw new InvalidOperationException("No update downloaded");
        LaunchInstaller(_downloadedPath, quiet: true);
    }

    /// <summary>
    /// Portable self-update: the zip is unpacked next to the temp download, the new exe's product name is checked, and a small
    /// cmd script waits for this process to exit, copies the new files over this folder (robocopy, settings and data under
    /// %AppData% untouched), starts the new exe and deletes the unpacked copy. The caller exits right after.
    /// </summary>
    public static void LaunchInstaller(string path, bool quiet = false)
    {
        var unpacked = Path.Combine(DownloadFolder, "unpacked");
        if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
        ZipFile.ExtractToDirectory(path, unpacked, overwriteFiles: true);
        var exe = Directory.EnumerateFiles(unpacked, "AION2DpsMeter.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException($"{Path.GetFileName(path)} holds no AION2DpsMeter.exe");
        if (!UpdateFeed.IsProductName(FileVersionInfo.GetVersionInfo(exe).ProductName, AppInfo.Name))
            throw new InvalidDataException($"{Path.GetFileName(path)} is not an {AppInfo.Name} build");
        var src = Path.GetDirectoryName(exe)!;
        var app = AppContext.BaseDirectory.TrimEnd('\\');
        var pid = Environment.ProcessId;
        var script = Path.Combine(DownloadFolder, "apply-update.cmd");
        File.WriteAllText(script, "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"robocopy \"{src}\" \"{app}\" /E /IS /IT /R:20 /W:1 /NFL /NDL /NJH /NJS >nul\r\n" +
            $"start \"\" /D \"{app}\" \"{app}\\AION2DpsMeter.exe\"\r\n" +
            $"rd /s /q \"{unpacked}\"\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        _ = quiet;
    }

    public static void OpenReleasePage(ReleaseInfo? release)
    {
        try
        {
            Process.Start(new ProcessStartInfo(release?.PageUrl ?? UpdateFeed.ReleasesPage) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error("Could not open the release page", ex);
        }
    }

    private static string DownloadFolder => Path.Combine(Path.GetTempPath(), "AION2DpsMeter-update");

    /// <summary>Installers of earlier updates are no longer needed (the one running right now is locked and stays).</summary>
    public static void CleanDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadFolder)) return;
            foreach (var file in Directory.EnumerateFiles(DownloadFolder))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1))
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The render tool shows the update window and the overlay's update button with a made-up release.</summary>
    internal void Preview(ReleaseInfo release) => Latest = release;

    private static bool DetectInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string location && location.Length > 0 &&
                   string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _feed.Dispose();
    }
}
