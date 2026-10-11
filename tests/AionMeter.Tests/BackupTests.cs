using System.Text.Json;
using AionMeter.Core.Builds;
using AionMeter.Core.Market;

namespace AionMeter.Tests;

public class BackupTests
{
    private const string Install = "3f9c0a12b7e44d85a6c1e09f2b7d5e31";
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "backup", "restore.json"));

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "aion-backup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void CharHash_is_the_first_16_hex_of_sha256_of_install_and_key()
    {
        Assert.Equal("72a839d13f429e84", BackupCode.CharHash(Install, "1303-Irin"));
        Assert.Equal("394ebf1f15c297ce", BackupCode.CharHash(Install, "1303-Arielka"));
        Assert.Equal("", BackupCode.CharHash(Install, ""));
    }

    [Fact]
    public void Code_formats_in_eight_upper_case_groups()
    {
        Assert.Equal("3F9C-0A12-B7E4-4D85-A6C1-E09F-2B7D-5E31", BackupCode.Format(Install));
        Assert.Equal("not-a-code", BackupCode.Format("not-a-code"));
    }

    [Theory]
    [InlineData("3F9C-0A12-B7E4-4D85-A6C1-E09F-2B7D-5E31")]
    [InlineData("3f9c0a12b7e44d85a6c1e09f2b7d5e31")]
    [InlineData("  3F9C 0A12 b7e4 4D85\tA6C1-E09F-2B7D-5E31\r\n")]
    public void Code_parses_with_or_without_dashes_spaces_and_case(string typed) => Assert.Equal(Install, BackupCode.Parse(typed));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("3F9C-0A12-B7E4-4D85-A6C1-E09F-2B7D-5E3")]     // 31 hex
    [InlineData("3F9C-0A12-B7E4-4D85-A6C1-E09F-2B7D-5E311")]   // 33 hex
    [InlineData("3G9C-0A12-B7E4-4D85-A6C1-E09F-2B7D-5E31")]    // not hex
    [InlineData("3F9C_0A12_B7E4_4D85_A6C1_E09F_2B7D_5E31")]    // unknown separator
    public void Invalid_codes_are_refused(string? typed) => Assert.Null(BackupCode.Parse(typed));

    [Fact]
    public void Restore_json_maps_back_to_builds_newest_row_wins()
    {
        var p = BuildTelemetryJson.ParseRestore(Fixture());
        Assert.Equal(3, p.Builds.Count);   // a1b2 deduplicated, the bad id and the row without id dropped
        var a = p.Builds.Single(b => b.Build.Id == "a1b2c3d4e5f6");
        Assert.Equal("Irin PvE", a.Build.Name);
        Assert.Equal("72a839d13f429e84", a.CharHash);
        Assert.Equal(DateTimeOffset.Parse("2026-10-11T09:30:00Z"), a.Ts);
        Assert.Equal(a.Ts, a.Build.UpdatedAt);
        Assert.Equal("ranger", a.Build.ClassId);
        Assert.Equal(15969, a.Build.Source?.Id);
        Assert.Equal(2, a.Build.Skills.Count);
        Assert.Equal(14170000, a.Build.Skills[0].SkillId);
        Assert.Equal(12, a.Build.Skills[0].Level);
        Assert.Equal("3.0", a.Build.Skills[0].Slot);
        Assert.Equal(new[] { 1, 3 }, a.Build.Skills[0].Specs);
        Assert.Null(a.Build.Skills[1].Slot);
        Assert.Equal(new[] { 14170000, 14010000 }, a.Build.Priority);
        Assert.Equal("Opener", a.Build.Macros[0].Name);
        Assert.Equal(new[] { 14170000, 14010000 }, a.Build.Macros[0].Steps);
        Assert.Equal("Use after the pull.\nCombos with Deadshot.", a.Build.Notes[14170000]);
        Assert.Equal(new[] { 110101, 110102 }, a.Build.Daevanion[11]);
        Assert.Empty(a.Build.Daevanion[12]);

        // the same fields as objects instead of JSON strings
        var b = p.Builds.Single(x => x.Build.Id == "b0b0b0b0b0b0");
        Assert.Equal(2, b.Build.Skills.Count);
        Assert.Null(b.Build.Source);
        Assert.Equal(new[] { 110101, 110102 }, b.Build.Daevanion[11]);
        Assert.Equal("", p.Builds.Single(x => x.Build.Id == "c0c0c0c0c0c0").CharHash);

        var st = Assert.Single(p.State);
        Assert.Equal("watch", st.Kind);
        var (watch, favs) = BackupState.Parse(st.Data);
        Assert.Equal(2, watch.Count);
        Assert.Equal(164000073, watch[0].Item);
        Assert.Equal(250000, watch[0].MaxPrice);
        Assert.True(watch[0].Sell);
        Assert.Equal(3, watch[0].Quantity);
        Assert.Equal(180000, watch[0].PaidEach);
        Assert.False(watch[1].Sell);
        Assert.Equal(new[] { 510716007, 510716019 }, favs);
    }

    [Fact]
    public void Newer_local_build_wins_over_the_server_copy()
    {
        var ts = DateTimeOffset.Parse("2026-10-11T09:30:00Z");
        Assert.True(BuildTelemetryJson.RestoreWins(null, ts));
        Assert.True(BuildTelemetryJson.RestoreWins(new Build { Id = "x", UpdatedAt = ts.AddMinutes(-1) }, ts));
        Assert.True(BuildTelemetryJson.RestoreWins(new Build { Id = "x", UpdatedAt = ts }, ts));
        Assert.False(BuildTelemetryJson.RestoreWins(new Build { Id = "x", UpdatedAt = ts.AddSeconds(1) }, ts));
    }

    [Fact]
    public void Restored_builds_go_to_matching_characters_or_wait_for_them()
    {
        var root = TempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "characters", "1303-Irin"));
            var p = BuildTelemetryJson.ParseRestore(Fixture());
            var r = BackupPlacement.Place(p.Builds, root, Install);
            Assert.Equal(2, r.Placed);    // Irin's + the shared-folder one
            Assert.Equal(1, r.Waiting);
            Assert.Equal(0, r.KeptLocal);
            var irin = new BuildStore(Path.Combine(root, "characters", "1303-Irin", "builds"));
            var a = irin.Load("a1b2c3d4e5f6");
            Assert.NotNull(a);
            Assert.Equal(DateTimeOffset.Parse("2026-10-11T09:30:00Z"), a!.UpdatedAt);   // kept, not stamped "now"
            Assert.NotNull(new BuildStore(Path.Combine(root, "builds")).Load("c0c0c0c0c0c0"));
            Assert.True(File.Exists(Path.Combine(root, BackupPlacement.WaitingDir, "ffffffffffffffff", "b0b0b0b0b0b0.json")));

            // a local edit after the server copy survives a second restore
            irin.Put(a with { Name = "edited here", UpdatedAt = a.UpdatedAt.AddHours(1) });
            Assert.Equal(1, BackupPlacement.Place(p.Builds, root, Install).KeptLocal);
            Assert.Equal("edited here", irin.Load("a1b2c3d4e5f6")!.Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Waiting_builds_move_in_when_their_character_logs_in()
    {
        var root = TempDir();
        try
        {
            var hash = BackupCode.CharHash(Install, "1303-Arielka");
            var waiting = new BuildStore(Path.Combine(root, BackupPlacement.WaitingDir, hash));
            waiting.Put(new Build { Id = "w1", Name = "restored", UpdatedAt = DateTimeOffset.Parse("2026-10-10T00:00:00Z") });
            waiting.Put(new Build { Id = "w2", Name = "older than local", UpdatedAt = DateTimeOffset.Parse("2026-10-10T00:00:00Z") });
            var local = new BuildStore(Path.Combine(root, "characters", "1303-Arielka", "builds"));
            local.Put(new Build { Id = "w2", Name = "local", UpdatedAt = DateTimeOffset.Parse("2026-10-11T00:00:00Z") });

            Assert.Equal(0, BackupPlacement.Adopt(root, Install, "1303-Irin"));
            Assert.Equal(1, BackupPlacement.Adopt(root, Install, "1303-Arielka"));
            Assert.Equal("restored", local.Load("w1")!.Name);
            Assert.Equal("local", local.Load("w2")!.Name);
            Assert.False(Directory.Exists(Path.Combine(root, BackupPlacement.WaitingDir)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void State_round_trips_and_only_fills_empty_local_lists()
    {
        var watches = new List<PriceWatch>
        {
            new() { Item = 164000073, Name = "Kinah \"Bundle\"", MaxPrice = 250000, Sell = true, Quantity = 3, PaidEach = 180000, LastPrice = 1, LastError = "x" },
            new() { Item = 152000101, Name = "Enchant Stone", MaxPrice = 9000 },
        };
        var json = BackupState.ToJson(watches, [510716007]);
        Assert.DoesNotContain("LastPrice", json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        var (w, f) = BackupState.Parse(doc.RootElement);
        Assert.Equal("Kinah \"Bundle\"", w[0].Name);
        Assert.Equal(180000, w[0].PaidEach);
        Assert.Null(w[0].LastPrice);
        Assert.Equal(new[] { 510716007 }, f);

        Assert.True(BackupState.Takes(new List<PriceWatch>(), w));
        Assert.False(BackupState.Takes(new List<PriceWatch> { new() { Item = 5 } }, w));
        Assert.False(BackupState.Takes(new List<int>(), new List<int>()));
        Assert.True(BackupState.Takes(new List<int>(), f));

        var store = new PriceWatchStore(null);
        store.AddRange(w);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void State_stays_under_20_KB()
    {
        var many = new List<PriceWatch>();
        for (var i = 1; i <= 1000; i++) many.Add(new PriceWatch { Item = 100000000 + i, Name = new string('n', 40), MaxPrice = 123456789, Quantity = 5, PaidEach = 999999 });
        var json = BackupState.ToJson(many, []);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= BackupState.MaxBytes, json.Length.ToString());
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("watch").GetArrayLength() > 50);
    }
}
