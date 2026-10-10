using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionMeter.Core.Net;

/// <summary>
/// Shared GET that transparently falls back to a FlareSolverr / solvearr proxy when a third-party site answers with a
/// Cloudflare (or DDoS-Guard) "under attack" challenge instead of the data. The proxy drives a headless browser, solves
/// the JS challenge and returns the final body — the same bypass the NAS media stack gives its torrent indexers.
///
/// Off unless <see cref="SolverUrl"/> is set (Settings → "CloudflareSolverUrl" in settings.json). The proxy must be
/// reached over the private network only (e.g. a Tailscale address): its /v1 endpoint will fetch ANY url for whoever
/// asks, so exposing it publicly is an open SSRF relay.
/// </summary>
public static class CloudflareHttp
{
    /// <summary>Base url of the solver, e.g. "http://127.0.0.1:8191". null/empty = disabled (direct GET only).</summary>
    public static string? SolverUrl { get; set; }

    // The solver renders a real browser, so a single request can take tens of seconds; one shared client, long timeout.
    private static readonly HttpClient Solver = new() { Timeout = TimeSpan.FromSeconds(70) };
    private static readonly JsonSerializerOptions SolverJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// GET <paramref name="url"/> through <paramref name="http"/>; on a Cloudflare challenge retry once via the solver
    /// when one is configured. <paramref name="expectJson"/> peels the &lt;pre&gt; a headless browser wraps a raw JSON
    /// endpoint in. Throws the original error (EnsureSuccessStatusCode) when it is a plain failure, not a CF wall, or
    /// when no solver is set — so behaviour is unchanged wherever the solver is off or irrelevant.
    /// </summary>
    public static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct, bool expectJson = false)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Only 403/503 can carry a challenge; anything else is the site's own answer. Read the body once to be sure.
        var body = resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
            ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
            : "";
        if (string.IsNullOrEmpty(SolverUrl) || !LooksLikeChallenge(resp.Headers.Server.ToString(), body))
        {
            resp.EnsureSuccessStatusCode(); // real failure, or no solver to try: surface it exactly as before
            return body;
        }

        var solved = await SolveAsync(url, ct).ConfigureAwait(false);
        return expectJson ? UnwrapJson(solved) : solved;
    }

    /// <summary>A Cloudflare / DDoS-Guard interstitial, not the requested data. Server header or a body marker gives it away.</summary>
    public static bool LooksLikeChallenge(string serverHeader, string body) =>
        serverHeader.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
        || body.Contains("cf-chl", StringComparison.Ordinal)
        || body.Contains("Just a moment", StringComparison.Ordinal)
        || body.Contains("Checking your browser", StringComparison.Ordinal)
        || body.Contains("Attention Required", StringComparison.Ordinal)
        || body.Contains("DDoS-Guard", StringComparison.OrdinalIgnoreCase);

    /// <summary>How many requests went through the solver since start (the price poll logs the delta: direct vs solver).</summary>
    public static int SolverUses;

    private static async Task<string> SolveAsync(string url, CancellationToken ct)
    {
        Interlocked.Increment(ref SolverUses);
        var req = new SolverRequest("request.get", url, 60000);
        using var resp = await Solver.PostAsJsonAsync($"{SolverUrl!.TrimEnd('/')}/v1", req, SolverJson, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var res = await resp.Content.ReadFromJsonAsync<SolverResponse>(SolverJson, ct).ConfigureAwait(false);
        if (res?.Solution?.Response is not string html)
            throw new HttpRequestException($"Cloudflare solver returned no solution for {url} (status {res?.Status})");
        return html;
    }

    /// <summary>A headless browser rendering a raw JSON endpoint wraps it as &lt;html&gt;…&lt;pre&gt;JSON&lt;/pre&gt;…&lt;/html&gt;; peel it back.</summary>
    public static string UnwrapJson(string html)
    {
        var open = html.IndexOf("<pre", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
        {
            var start = html.IndexOf('>', open);
            var end = start > 0 ? html.IndexOf("</pre>", start + 1, StringComparison.OrdinalIgnoreCase) : -1;
            if (end > start) return WebUtility.HtmlDecode(html.Substring(start + 1, end - start - 1));
        }
        var trimmed = html.TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[') ? trimmed : html; // already raw JSON
    }

    private sealed record SolverRequest(
        [property: JsonPropertyName("cmd")] string Cmd,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("maxTimeout")] int MaxTimeout);

    private sealed record SolverResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("solution")] SolverSolution? Solution);

    private sealed record SolverSolution(
        [property: JsonPropertyName("response")] string? Response);
}
