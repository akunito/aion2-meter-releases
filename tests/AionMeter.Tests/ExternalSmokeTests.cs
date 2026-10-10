using System.Net;
using System.Net.Http;
using System.Text.Json;
using AionMeter.Core.Armory;
using AionMeter.Core.Market;

namespace AionMeter.Tests;

/// <summary>
/// Opt-in network checks of the external sources the meter and the tools depend on (A2M-7). They do not run in the build gate:
/// set AION_SMOKE=1 (build/smoke.sh does) and they hit the live endpoints once each, asserting only the shape we parse, so a
/// game update or a site redesign fails here first instead of silently emptying a tab. aion2.exchange is behind a second
/// switch, AION_SMOKE_EXCHANGE=1, because its robots.txt disallows automated agents other than the meter's own hourly poll.
/// </summary>
[Trait("Category", "Smoke")]
public class ExternalSmokeTests
{
    private sealed class SmokeFactAttribute : FactAttribute
    {
        public SmokeFactAttribute(string? gate = null)
        {
            if (Environment.GetEnvironmentVariable("AION_SMOKE") != "1") Skip = "set AION_SMOKE=1 to hit the live endpoints";
            else if (gate is not null && Environment.GetEnvironmentVariable(gate) != "1") Skip = $"set {gate}=1 to include this source";
        }
    }

    private static readonly HttpClient Http = Make();
    private static HttpClient Make()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.1 (private build; smoke test, one request per endpoint)");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return h;
    }

    private static async Task<JsonDocument> Get(string url)
    {
        var s = await Http.GetStringAsync(url);
        Assert.False(string.IsNullOrWhiteSpace(s), url);
        return JsonDocument.Parse(s);
    }

    /// <summary>tRPC GET: {"result":{"data":{"json":…}}} (superjson) or {"result":{"data":…}}; returns the payload.</summary>
    private static JsonElement Trpc(JsonDocument d)
    {
        var e = d.RootElement;
        if (e.TryGetProperty("result", out var r)) e = r;
        if (e.TryGetProperty("data", out var da)) e = da;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("json", out var j)) e = j;
        return e;
    }

    /// <summary>Depth-first: the first array whose elements are objects with a "name" (tRPC pages wrap lists in different keys).</summary>
    private static JsonElement? NamedArray(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray()) { if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out _)) return e; break; }
            return null;
        }
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject()) { var f = NamedArray(p.Value); if (f is not null) return f; }
        return null;
    }

    [SmokeFact]
    public async Task Official_search_and_profile_still_have_the_fields_we_flatten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aion-smoke-armory-" + Guid.NewGuid().ToString("N"));
        using var client = new ArmoryClient(dir);
        var r = await client.FindAsync("Akunita", 1303);
        Assert.NotNull(r);
        Assert.Equal(10, r!.PcId);                       // Templar
        Assert.Equal("Vaizel", r.ServerName);
        var p = await client.FetchAsync(r, withDaevanion: true);
        Assert.Equal("Templar", p.ClassName);
        Assert.InRange(p.Level, 45, 99);
        Assert.True(p.CombatPower > 10_000, "combat power");
        Assert.True(p.Stats.Count >= 10, "stats");
        Assert.True(p.Equipment.Count >= 10, "equipment");
        Assert.True(p.Skills.Count >= 20, "skills");
        Assert.InRange(p.Boards.Count, 5, 6);            // Daevanion boards 21–26 for a Templar
        Assert.True(p.Daevanion.Count >= 50, "daevanion nodes");
        Assert.All(p.Daevanion, n => Assert.InRange(n.BoardId, 21, 26));
        Assert.True(p.Daevanion.Any(n => n.Open), "at least one open node");
        Directory.Delete(dir, true);
    }

    [SmokeFact]
    public async Task Questlog_skills_list_keeps_game_ids_and_names()
    {
        using var d = await Get("https://questlog.gg/aion-2/api/trpc/skillBuilder.getSkills?input=%7B%22language%22%3A%22en%22%7D");
        var arr = NamedArray(Trpc(d));
        Assert.NotNull(arr);
        var n = 0; var ranger = false;
        foreach (var s in arr!.Value.EnumerateArray())
        {
            n++;
            Assert.True(s.TryGetProperty("id", out _), "skill id");
            if (s.GetProperty("name").GetString() == "Deadshot") ranger = true;
        }
        Assert.True(n >= 300, $"only {n} skills");       // 323 on 2026-10-09
        Assert.True(ranger, "Deadshot missing: English names changed?");
    }

    [SmokeFact]
    public async Task Questlog_pets_page_and_detail_keep_the_soul_fields()
    {
        using var d = await Get("https://questlog.gg/aion-2/api/trpc/database.getPets?input=%7B%22language%22%3A%22en%22%2C%22page%22%3A1%7D");
        var arr = NamedArray(Trpc(d));
        Assert.NotNull(arr);
        Assert.True(arr!.Value.GetArrayLength() >= 30, "pets per page");   // 207 pets over 6 pages on 2026-10-10
        using var one = await Get("https://questlog.gg/aion-2/api/trpc/database.getPet?input=%7B%22language%22%3A%22en%22%2C%22id%22%3A%221097%22%7D");
        var pet = Trpc(one);
        Assert.Equal("Karnif", pet.GetProperty("name").GetString());
        Assert.Equal(5, pet.GetProperty("needSoul").GetInt32());
        Assert.True(pet.TryGetProperty("mainCategory", out _), "mainCategory");
    }

    [SmokeFact]
    public async Task Aion2hub_entities_resolve_an_item_and_list_npcs()
    {
        using var item = await Get("https://aion2hub.me/api/entities/item/610511013?locale=en");
        var txt = item.RootElement.GetRawText();
        Assert.Contains("Wrathful", txt);                  // Wrathful Mind (Perversión de ira), the price-watch item
        using var npcs = await Get("https://aion2hub.me/api/entities/npc?locale=en&limit=5&offset=0");
        var arr = NamedArray(npcs.RootElement);
        Assert.NotNull(arr);
        Assert.Equal(5, arr!.Value.GetArrayLength());
    }

    [SmokeFact("AION_SMOKE_EXCHANGE")]
    public async Task Exchange_price_history_parses_for_the_watched_item()
    {
        var path = Path.Combine(Path.GetTempPath(), "aion-smoke-prices-" + Guid.NewGuid().ToString("N") + ".json");
        using var store = new PriceWatchStore(path);
        store.Add(610511013, "Wrathful Mind", 1_200_000);
        var hits = await store.PollAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var w = store.All().Single();
        Assert.True(w.LastError is null, w.LastError);
        Assert.True(w.LastPrice is > 0, "no price");
        Assert.NotNull(w.LastCheck);
        _ = hits;
        File.Delete(path);
    }
}
