using AionMeter.Core.Market;
using AionMeter.Core.Net;

namespace AionMeter.Tests;

public class CloudflareHttpTests
{
    [Fact]
    public void UnwrapsPreWrappedJsonFromHeadlessBrowser()
    {
        // What solvearr/FlareSolverr return when a real browser renders a raw JSON endpoint.
        const string html = "<html><head><meta charset=\"utf-8\"></head><body>" +
            "<pre style=\"word-wrap: break-word;\">{&quot;points&quot;:[{&quot;ts&quot;:&quot;2026-10-08T00:00:00Z&quot;,&quot;low&quot;:1020000}]}</pre>" +
            "</body></html>";
        var json = CloudflareHttp.UnwrapJson(html);
        Assert.StartsWith("{\"points\"", json);
        var pts = PriceWatchStore.ParsePrices(json); // the unwrapped text must still parse as the real payload
        Assert.Single(pts);
        Assert.Equal(1_020_000, pts[0].Low);
    }

    [Fact]
    public void UnwrapJsonPassesRawJsonThrough()
    {
        const string raw = "{\"points\":[]}";
        Assert.Equal(raw, CloudflareHttp.UnwrapJson(raw));
    }

    [Theory]
    [InlineData("Just a moment...", true)]
    [InlineData("<div class=\"cf-chl-widget\"></div>", true)]
    [InlineData("Checking your browser before accessing", true)]
    [InlineData("Attention Required! | Cloudflare", true)]
    [InlineData("DDoS-Guard", true)]
    [InlineData("{\"points\":[]}", false)]
    public void DetectsChallengeBodies(string body, bool expected) =>
        Assert.Equal(expected, CloudflareHttp.LooksLikeChallenge("", body));

    [Fact]
    public void DetectsChallengeFromServerHeader() =>
        Assert.True(CloudflareHttp.LooksLikeChallenge("cloudflare", ""));
}
