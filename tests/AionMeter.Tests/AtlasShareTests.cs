using System.Text.Json;
using AionMeter.Core.Atlas;
using AionMeter.Core.Diagnostics;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>A2M-45: "Share the node names I answer" — the policy gate, the pending set, the payload, the acknowledged file and the "other" kind.</summary>
public class AtlasShareTests
{
    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aion-atlas-share-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static Dictionary<int, (string Name, string Category)> Names() => new()
    {
        [150010001] = ("Iron Ore", "mining"),
        [150020002] = ("Aether", "aether"),
        [900000123] = ("door", AtlasShare.OtherCategory),
    };

    [Fact]
    public void Policy_atlas_switch_is_on_by_default_and_gates_only_its_own_kind()
    {
        Assert.True(SharePolicy.Default.AtlasNames);
        Assert.True(SharePolicy.Default.Filter("atlas"));
        Assert.True(SharePolicy.Default.Filter(ShareKind.AtlasNames));
        var off = SharePolicy.Default with { AtlasNames = false };
        Assert.False(off.Filter("atlas"));
        Assert.False(off.Filter(ShareKind.AtlasNames));
        Assert.True(off.Filter("usage") && off.Filter("build") && off.Filter("watch"));
        var onlyAtlas = new SharePolicy(false, false, false, false, false, false) { AtlasNames = true };
        Assert.True(onlyAtlas.SendsAnything);
        Assert.False(onlyAtlas.Filter("usage"));
        Assert.True(onlyAtlas.Filter("atlas"));
        Assert.False(new SharePolicy(false, false, false, false, false, false) { AtlasNames = false }.SendsAnything);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Policy_legacy_switch_decides_atlas_too(bool legacy) => Assert.Equal(legacy, SharePolicy.FromLegacyTelemetry(legacy).AtlasNames);

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void Policy_old_file_without_the_switch_follows_the_other_kinds(bool usage, bool builds, bool backup, bool expected) =>
        Assert.Equal(expected, SharePolicy.AtlasNamesForOldFile(usage, builds, backup));

    [Fact]
    public void Settings_policy_carries_the_atlas_switch()
    {
        Assert.True(new AppSettings().CurrentSharePolicy().AtlasNames);
        Assert.False(new AppSettings { ShareAtlasNames = false }.CurrentSharePolicy().Filter("atlas"));
    }

    [Fact]
    public void Pending_is_every_unacknowledged_or_renamed_name_lowest_code_first()
    {
        var names = Names();
        names[150030003] = ("", "");   // never answered
        var sent = new Dictionary<int, string>
        {
            [150010001] = AtlasShare.Stamp("Iron Ore", "mining"),   // unchanged
            [150020002] = AtlasShare.Stamp("Aether", ""),           // category changed
        };
        Assert.Equal([150020002, 900000123], AtlasShare.Pending(names, sent));
        Assert.Equal([150010001, 150020002, 900000123], AtlasShare.Pending(names, new Dictionary<int, string>()));
        Assert.Equal([150010001], AtlasShare.Pending(names, new Dictionary<int, string>(), max: 1));
    }

    [Fact]
    public void Payload_holds_kind_install_ts_and_per_node_code_category_name_maps_only()
    {
        var json = AtlasShare.Payload("abc", new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero),
            [new AtlasShare.Node(150010001, "Iron Ore", "mining", [210010000, 210020000]), new AtlasShare.Node(900000123, "door \"x\"", "other", [])]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(["install", "kind", "nodes", "ts"], root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("atlas", root.GetProperty("kind").GetString());
        Assert.Equal("abc", root.GetProperty("install").GetString());
        Assert.Equal("2026-10-14T12:00:00.0000000Z", root.GetProperty("ts").GetString());
        var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(2, nodes.Length);
        foreach (var n in nodes) Assert.Equal(["category", "code", "maps", "name"], n.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(150010001, nodes[0].GetProperty("code").GetInt32());
        Assert.Equal("mining", nodes[0].GetProperty("category").GetString());
        Assert.Equal([210010000, 210020000], nodes[0].GetProperty("maps").EnumerateArray().Select(m => m.GetInt32()));
        Assert.Equal("door \"x\"", nodes[1].GetProperty("name").GetString());
        Assert.Equal("other", nodes[1].GetProperty("category").GetString());
        Assert.Equal(0, nodes[1].GetProperty("maps").GetArrayLength());
    }

    [Fact]
    public void Sent_file_round_trips_and_a_broken_one_reads_empty()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "atlas", "objects-shared.json");
        Assert.Empty(AtlasShare.LoadSent(path));
        var sent = new Dictionary<int, string> { [1] = AtlasShare.Stamp("a", "b"), [2] = AtlasShare.Stamp("door", "other") };
        AtlasShare.SaveSent(path, sent);
        Assert.Equal(sent, AtlasShare.LoadSent(path));
        File.WriteAllText(path, "{not json");
        Assert.Empty(AtlasShare.LoadSent(path));
    }

    [Fact]
    public void CodeMaps_reads_maps_not_loaded_from_their_files()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "210010000.json"), "[{\"Code\":150010001,\"X\":1,\"Y\":2},{\"Code\":5,\"X\":1,\"Y\":2}]");
        File.WriteAllText(Path.Combine(dir, "210020000.json"), "[{\"Code\":150010001,\"X\":9,\"Y\":9}]");
        File.WriteAllText(Path.Combine(dir, "objects.json"), "{\"150010001\":{\"name\":\"Iron Ore\",\"category\":\"mining\"}}");
        var maps = new NodeStore(dir).CodeMaps([150010001, 777]);
        Assert.Equal([210010000, 210020000], maps[150010001]);
        Assert.False(maps.ContainsKey(777));
        Assert.False(maps.ContainsKey(5));
    }

    private static AppSettings Settings(bool share) => new() { ShareAtlasNames = share, InstallId = "install-test" };

    [Fact]
    public async Task Gate_switch_off_posts_nothing_and_reads_nothing()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "objects-shared.json");
        var posts = 0; var namesRead = 0;
        var (result, count) = await Telemetry.ShareAtlasAsync(Settings(false), () => { namesRead++; return Names(); }, _ => [], path, _ => { posts++; return Task.FromResult(200); });
        Assert.Equal(Telemetry.AtlasResult.Nothing, result);
        Assert.Equal(0, count);
        Assert.Equal(0, posts);
        Assert.Equal(0, namesRead);
        Assert.False(File.Exists(path));
        var offline = Settings(true); offline.Offline = true;
        await Telemetry.ShareAtlasAsync(offline, Names, _ => [], path, _ => { posts++; return Task.FromResult(200); });
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task First_enable_sends_every_answered_name_in_one_batch_then_only_renames()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "objects-shared.json");
        var names = Names();
        var bodies = new List<string>();
        Task<int> Post(string b) { bodies.Add(b); return Task.FromResult(200); }
        Dictionary<int, int[]> Maps(IReadOnlyCollection<int> codes) => codes.Contains(150010001) ? new() { [150010001] = [210010000] } : new();

        var first = await Telemetry.ShareAtlasAsync(Settings(true), () => names, Maps, path, Post);
        Assert.Equal((Telemetry.AtlasResult.Sent, 3), first);
        Assert.Single(bodies);
        using (var doc = JsonDocument.Parse(bodies[0]))
        {
            Assert.Equal("install-test", doc.RootElement.GetProperty("install").GetString());
            var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            Assert.Equal([150010001, 150020002, 900000123], nodes.Select(n => n.GetProperty("code").GetInt32()));
            Assert.Equal(210010000, nodes[0].GetProperty("maps")[0].GetInt32());
        }

        Assert.Equal((Telemetry.AtlasResult.Nothing, 0), await Telemetry.ShareAtlasAsync(Settings(true), () => names, Maps, path, Post));
        Assert.Single(bodies);

        names[150020002] = ("Aether Crystal", "aether");
        Assert.Equal((Telemetry.AtlasResult.Sent, 1), await Telemetry.ShareAtlasAsync(Settings(true), () => names, Maps, path, Post));
        using (var doc = JsonDocument.Parse(bodies[1]))
            Assert.Equal("Aether Crystal", Assert.Single(doc.RootElement.GetProperty("nodes").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Refused_or_unsent_batches_are_not_recorded()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "objects-shared.json");
        Assert.Equal((Telemetry.AtlasResult.Refused, 3), await Telemetry.ShareAtlasAsync(Settings(true), Names, _ => [], path, _ => Task.FromResult(400)));
        Assert.False(File.Exists(path));
        Assert.Equal((Telemetry.AtlasResult.Nothing, 0), await Telemetry.ShareAtlasAsync(Settings(true), Names, _ => [], path, _ => Task.FromResult(0)));
        Assert.False(File.Exists(path));
        var posts = 0;
        await Telemetry.ShareAtlasAsync(Settings(true), Names, _ => [], path, _ => { posts++; return Task.FromResult(200); });
        Assert.Equal(1, posts);
        Assert.Equal(3, AtlasShare.LoadSent(path).Count);
    }

    [Fact]
    public void Other_category_is_recognised_case_insensitively()
    {
        Assert.True(AtlasShare.IsOther("other"));
        Assert.True(AtlasShare.IsOther("Other"));
        Assert.False(AtlasShare.IsOther(""));
        Assert.False(AtlasShare.IsOther("mining"));
    }
}

/// <summary>A2M-45: settings.json files from before the atlas switch (writes AppSettings.AppDataDir).</summary>
[Collection(ProcessStaticsCollection.Name)]
public class AtlasShareSettingsTests
{
    private static AppSettings LoadFrom(string json)
    {
        var before = AppSettings.AppDataDir;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aion-atlas-set-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            AppSettings.UseAppDataDir(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), json);
            return AppSettings.Load();
        }
        finally { AppSettings.UseAppDataDir(before); }
    }

    [Theory]
    [InlineData("{\"ShareUsage\":true,\"ShareBuilds\":true,\"ShareBackup\":true}", true)]
    [InlineData("{\"ShareUsage\":false,\"ShareBuilds\":false,\"ShareBackup\":false}", false)]
    [InlineData("{\"ShareUsage\":false,\"ShareBuilds\":false,\"ShareBackup\":true}", true)]
    [InlineData("{\"ShareUsage\":true,\"ShareBuilds\":true,\"ShareBackup\":true,\"ShareAtlasNames\":false}", false)]
    [InlineData("{\"ShareUsage\":false,\"ShareBuilds\":false,\"ShareBackup\":false,\"ShareAtlasNames\":true}", true)]
    [InlineData("{\"Telemetry\":false}", false)]
    [InlineData("{\"Telemetry\":true}", true)]
    public void Atlas_switch_from_an_older_file(string json, bool expected) => Assert.Equal(expected, LoadFrom(json).ShareAtlasNames);
}
