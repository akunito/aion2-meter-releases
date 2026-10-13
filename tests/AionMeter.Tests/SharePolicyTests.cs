using AionMeter.Core.Diagnostics;

namespace AionMeter.Tests;

public class SharePolicyTests
{
    [Fact]
    public void Default_shares_the_three_telemetry_kinds_and_reports_without_inventory()
    {
        var p = SharePolicy.Default;
        Assert.True(p.Filter(ShareKind.Usage));
        Assert.True(p.Filter(ShareKind.Builds));
        Assert.True(p.Filter(ShareKind.Backup));
        Assert.True(p.Filter(ShareKind.ReportLogs));
        Assert.True(p.Filter(ShareKind.ReportSummary));
        Assert.True(p.Filter(ShareKind.ReportTopDps));
        Assert.False(p.Filter(ShareKind.ReportInventory));
        Assert.True(p.SendsAnything);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_telemetry_switch_becomes_each_of_the_three_kinds(bool legacy)
    {
        var p = SharePolicy.FromLegacyTelemetry(legacy);
        Assert.Equal(legacy, p.Usage);
        Assert.Equal(legacy, p.Builds);
        Assert.Equal(legacy, p.Backup);
        Assert.Equal(legacy, p.SendsAnything);
        // the report defaults do not depend on the old switch
        Assert.True(p.ReportSummary);
        Assert.True(p.ReportTopDps);
        Assert.False(p.ReportInventory);
    }

    [Theory]
    [InlineData(true, false, false, "usage")]
    [InlineData(false, true, false, "build")]
    [InlineData(false, false, true, "watch")]
    public void Webhook_kinds_follow_their_own_switch_only(bool usage, bool builds, bool backup, string allowed)
    {
        var p = new SharePolicy(usage, builds, backup, true, true, true);
        foreach (var kind in new[] { "usage", "build", "watch" }) Assert.Equal(kind == allowed, p.Filter(kind));
    }

    [Theory]
    [InlineData("")]
    [InlineData("report")]
    [InlineData("USAGE")]
    public void Unknown_webhook_kinds_are_refused(string kind) => Assert.False(new SharePolicy(true, true, true, true, true, true).Filter(kind));

    [Fact]
    public void Logs_always_go_in_a_report()
    {
        var p = new SharePolicy(false, false, false, false, false, false);
        Assert.True(p.Filter(ShareKind.ReportLogs));
        Assert.False(p.SendsAnything);
    }

    // ------------------------------------------------------------------ bug-report file list

    private static (string AppData, string AppDir, string Logs) Tree()
    {
        var root = Path.Combine(Path.GetTempPath(), "aion-report-" + Guid.NewGuid().ToString("N")[..8]);
        var appData = Path.Combine(root, "appdata");
        var appDir = Path.Combine(root, "app");
        var logs = Path.Combine(appData, "logs");
        void W(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "{}"); }
        W(Path.Combine(appDir, "BUILD-INFO.txt"));
        W(Path.Combine(appDir, "SHA256SUMS.txt"));
        foreach (var d in new[] { "20261010", "20261011", "20261012" }) W(Path.Combine(logs, $"aionmeter_{d}.log"));
        for (var i = 1; i <= 6; i++) W(Path.Combine(logs, $"crash_2026101{i}_120000.txt"));
        W(Path.Combine(appData, "state", "summary.json"));
        W(Path.Combine(appData, "state", "character.json"));
        W(Path.Combine(appData, "state", "price-watch.json"));
        W(Path.Combine(appData, "top-dps.json"));
        W(Path.Combine(appData, "inventory.json"));
        W(Path.Combine(appData, "account-warehouse.json"));
        W(Path.Combine(appData, "settings.json"));
        foreach (var c in new[] { "1303-Irin", "1303-Arielka" })
        {
            W(Path.Combine(appData, "characters", c, "character.json"));
            W(Path.Combine(appData, "characters", c, "profile.json"));
            W(Path.Combine(appData, "characters", c, "inventory.json"));
            W(Path.Combine(appData, "characters", c, "craftable.json"));
            W(Path.Combine(appData, "characters", c, "builds", "b1.json"));
        }
        return (appData, appDir, logs);
    }

    private static readonly string[] Always =
    [
        "BUILD-INFO.txt", "SHA256SUMS.txt",
        "logs/aionmeter_20261012.log", "logs/aionmeter_20261011.log",
        "logs/crash_20261016_120000.txt", "logs/crash_20261015_120000.txt", "logs/crash_20261014_120000.txt",
        "logs/crash_20261013_120000.txt", "logs/crash_20261012_120000.txt",
    ];
    private static readonly string[] Summary =
    [
        "state/summary.json", "state/character.json",
        "characters/1303-Arielka/builds/b1.json", "characters/1303-Arielka/character.json", "characters/1303-Arielka/profile.json",
        "characters/1303-Irin/builds/b1.json", "characters/1303-Irin/character.json", "characters/1303-Irin/profile.json",
    ];
    private static readonly string[] Top = ["top-dps.json"];
    private static readonly string[] Inventory =
    [
        "inventory.json", "account-warehouse.json",
        "characters/1303-Arielka/craftable.json", "characters/1303-Arielka/inventory.json",
        "characters/1303-Irin/craftable.json", "characters/1303-Irin/inventory.json",
    ];

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Report_holds_logs_always_and_each_part_only_when_ticked(bool summary, bool top, bool inventory)
    {
        var (appData, appDir, logs) = Tree();
        try
        {
            // the telemetry switches never change a report
            var entries = BugReportFiles.Select(appData, appDir, logs, new SharePolicy(false, true, false, summary, top, inventory));
            var expected = new List<string>(Always);
            if (summary) expected.AddRange(Summary);
            if (top) expected.AddRange(Top);
            if (inventory) expected.AddRange(Inventory);
            Assert.Equal(expected.Order(StringComparer.Ordinal), entries.Select(e => e.Entry).Order(StringComparer.Ordinal));
            Assert.Equal(entries.Count, entries.Select(e => e.Entry).Distinct().Count());
            Assert.All(entries, e => Assert.True(File.Exists(e.Source)));
            // settings.json and the note are written by the app (sanitised), never copied raw; unrelated state never goes
            Assert.DoesNotContain(entries, e => e.Entry is "settings.json" or "state/price-watch.json");
        }
        finally { Directory.Delete(Path.GetDirectoryName(appData)!, recursive: true); }
    }

    [Fact]
    public void Missing_folders_give_only_what_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "aion-report-empty-" + Guid.NewGuid().ToString("N")[..8]);
        var entries = BugReportFiles.Select(Path.Combine(root, "a"), Path.Combine(root, "b"), Path.Combine(root, "c"), new SharePolicy(true, true, true, true, true, true));
        Assert.Empty(entries);
    }
}
