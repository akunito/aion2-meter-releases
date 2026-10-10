using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AionMeter.Core.Updates;

/// <summary>A published version of the meter: the newest GitHub release and the files it offers.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string PageUrl,
    string Notes,
    DateTimeOffset? PublishedAt,
    ReleaseAsset? Installer,
    ReleaseAsset? Portable,
    bool Prerelease = false);

/// <summary>One file of a release. <see cref="Sha256"/> is GitHub's digest (lower-case hex) when it reports one.</summary>
public sealed record ReleaseAsset(string Name, long Size, string Url, string? Sha256);

/// <summary>
/// Asks GitHub for the newest release of the meter (<c>/releases/latest</c> skips drafts and pre-releases) and
/// downloads its installer, checking the size and SHA-256 GitHub reports for it. Nothing is sent but the request
/// itself (with a "AION2DpsMeter/x.y.z" user agent).
/// </summary>
public sealed class UpdateFeed : IDisposable
{
    // Our own releases (binaries only; the source stays in the private repo). "stable" = /releases/latest (GitHub skips drafts and
    // pre-releases); "dev" = the newest of /releases including pre-releases. Upstream is only watched to know when to re-audit.
    public const string Repository = "akunito/aion2-meter-releases";
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ReleasesListApi = "https://api.github.com/repos/" + Repository + "/releases?per_page=10";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";
    public const string UpstreamRepository = "cyberbadger6969/aion2-dps-meter";
    public const string UpstreamLatestApi = "https://api.github.com/repos/" + UpstreamRepository + "/releases/latest";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly string _api;

    public UpdateFeed(string userAgent, string api = LatestReleaseApi) : this(userAgent, new HttpClientHandler(), api)
    {
    }

    /// <param name="handler">Where requests go (tests pass an in-memory one).</param>
    public UpdateFeed(string userAgent, HttpMessageHandler handler, string api = LatestReleaseApi)
    {
        _api = api;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; // per-request timeouts below
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    /// <summary>The newest release, or null when the repository has none yet.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _api);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>The newest release of the list endpoint, pre-releases included (dev channel); null when none parses.</summary>
    public async Task<ReleaseInfo?> GetNewestIncludingPrereleasesAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesListApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseList(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }

    /// <summary>GitHub's release list (newest first): the first non-draft that is a version, pre-releases allowed.</summary>
    public static ReleaseInfo? ParseList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        ReleaseInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
            if (ParseElement(r, allowPrerelease: true) is { } info && (best is null || info.Version > best.Version)) best = info;
        return best;
    }

    /// <summary>A GitHub "release" object; null for drafts, pre-releases and tags that are not a version.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseElement(doc.RootElement, allowPrerelease: false);
    }

    private static ReleaseInfo? ParseElement(JsonElement r, bool allowPrerelease)
    {
        if (Bool(r, "draft") || (!allowPrerelease && Bool(r, "prerelease"))) return null;
        var tag = Str(r, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;

        ReleaseAsset? installer = null, portable = null;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (name.Length == 0 || url.Length == 0) continue;
                var digest = Str(a, "digest");
                var asset = new ReleaseAsset(name, a.TryGetProperty("size", out var size) ? size.GetInt64() : 0, url,
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    installer ??= asset;
                else if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    portable ??= asset;
            }
        }
        var page = Str(r, "html_url");
        return new ReleaseInfo(version, tag, page.Length > 0 ? page : ReleasesPage, Str(r, "body"),
            DateTimeOffset.TryParse(Str(r, "published_at"), out var at) ? at : null, installer, portable, Bool(r, "prerelease"));
    }

    /// <summary>"v0.2.0", "0.2", "v1.0.3-beta" → 0.2.0, 0.2.0, 1.0.3; null when it is not a version.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!s.Contains('.')) s += ".0";
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    /// <summary>Major.minor.patch only, so 0.2.0.0 (assembly version) equals 0.2.0 (release tag).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>
    /// A file's version-info product name is <paramref name="product"/>. Inno Setup writes an installer's version strings
    /// in place and pads them with spaces, so padding does not count.
    /// </summary>
    public static bool IsProductName(string? productName, string product) =>
        string.Equals(productName?.TrimEnd(' ', '\0'), product, StringComparison.Ordinal);

    /// <summary>
    /// Downloads a release file into <paramref name="folder"/>, reporting progress from 0 to 1. Throws
    /// <see cref="InvalidDataException"/> when the file does not match the size or SHA-256 GitHub reported.
    /// </summary>
    public async Task<string> DownloadAsync(ReleaseAsset asset, string folder, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(asset.Name));
        var part = path + ".part";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);

        // Already fetched (in the background, or on an earlier run) and still intact: no second download.
        if (asset.Sha256 is { } known && File.Exists(path) && new FileInfo(path).Length == asset.Size &&
            await Sha256Async(path, timeout.Token).ConfigureAwait(false) == known)
        {
            progress?.Report(1);
            return path;
        }

        try
        {
            using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string hash;
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = File.Create(part))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, read);
                    total += read;
                    if (asset.Size > 0) progress?.Report(Math.Min(1, (double)total / asset.Size));
                }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (asset.Size > 0 && total != asset.Size)
                throw new InvalidDataException($"{asset.Name}: got {total:N0} bytes, GitHub lists {asset.Size:N0}");
            if (asset.Sha256 is { } expected && hash != expected)
                throw new InvalidDataException($"{asset.Name}: SHA-256 {hash} does not match GitHub's {expected}");
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch
        {
            // Never leave a half or wrong file behind.
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public void Dispose() => _http.Dispose();
}
