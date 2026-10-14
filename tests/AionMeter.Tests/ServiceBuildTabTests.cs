using AionMeter.Core.Builds;
using AionMeter.Core.Diagnostics;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>
/// A2M-48 phase 3c: what the Build tab, Restore from code and Report a problem keep in AionMeter.Service (written for the Avalonia UI, used by
/// the GTK card since A2M-50): description runs, questlog HTML, the character folder helpers, the restore flow and the report send.
/// UiText.Current is process-wide and EsTextTests switches it: texts are checked by their numbers, not their words.
/// </summary>
public class ServiceBuildTabTests
{
    private const string Code = "3f9c0a12b7e44d85a6c1e09f2b7d5e31";

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "a2m-buildtab-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Description_runs_mark_numbers_and_breaks()
    {
        var segs = BuildText.ParseDescription("Deals <b>1,200</b> damage.<br>Lasts <span style=\"color:#fcc78b\">3</span> s");
        Assert.Equal(["Deals ", "1,200", " damage.\nLasts ", "3", " s"], segs.Select(s => s.Text));
        Assert.Equal([false, true, false, true, false], segs.Select(s => s.Num));
        var first = BuildText.FirstLine(segs);
        Assert.Equal(["Deals ", "1,200", " damage."], first.Select(s => s.Text));
        var noBreak = new[] { new TextSeg("one line", false) };
        Assert.Same(noBreak, BuildText.FirstLine(noBreak));   // no break: the runs come back whole
        Assert.Empty(BuildText.ParseDescription(""));
    }

    [Fact]
    public void Questlog_description_html_becomes_plain_text()
    {
        Assert.Equal("Hi & bye\n\n• A\n• B", BuildText.HtmlToText("<p>Hi &amp; bye</p><ul><li>A</li><li><b>B</b></li></ul>"));
        Assert.Equal("", BuildText.HtmlToText("  "));
        Assert.Equal("", BuildText.HtmlToText(null));
        Assert.Equal("a\n\nb", BuildText.HtmlToText("a<br><br><br><br>b"));   // runs of empty lines collapse to one
    }

    [Fact]
    public void Character_keys_and_the_logged_in_character()
    {
        Assert.Equal(("Akunita", 1303), CharacterFolder.ParseKey("1303-Akunita"));
        Assert.Equal(("Ab-c", 2011), CharacterFolder.ParseKey("2011-Ab-c"));
        Assert.Null(CharacterFolder.ParseKey(""));
        Assert.Null(CharacterFolder.ParseKey(null));
        Assert.Null(CharacterFolder.ParseKey("abc-x"));
        Assert.Null(CharacterFolder.ParseKey("1303-"));
        Assert.Null(CharacterFolder.ParseKey("0-Zero"));
        Assert.Null(CharacterFolder.ParseKey("+12-Sign"));

        var root = TempDir();
        try
        {
            Assert.Null(CharacterFolder.CurrentKey(root));
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "summary.json"), "{\"serverId\":1303,\"characterKey\":\"1303-Akunita\",\"characterName\":\"Akunita\"}");
            Assert.Equal("1303-Akunita", CharacterFolder.CurrentKey(root));
            File.WriteAllText(Path.Combine(root, "state", "summary.json"), "{\"characterKey\":\"\"}");
            Assert.Null(CharacterFolder.CurrentKey(root));
            File.WriteAllText(Path.Combine(root, "state", "summary.json"), "{ not json");
            Assert.Null(CharacterFolder.CurrentKey(root));

            Assert.Equal(Path.Combine(root, "characters", "1303-Akunita", "builds"), CharacterFolder.Builds(root, "1303-Akunita").Directory);
            Assert.Equal(Path.Combine(root, "builds"), CharacterFolder.Builds(root, "").Directory);

            // a build saved through the folder the Linux tab uses is what WPF / the telemetry sweep read (characters/<key>/builds + build-active.txt)
            var store = CharacterFolder.Builds(root, "1303-Akunita");
            var saved = store.Save(new Build { Name = "PvE", ClassId = "templar" });
            store.ActiveId = saved.Id;
            Assert.True(File.Exists(Path.Combine(root, "characters", "1303-Akunita", "build-active.txt")));
            Assert.Equal("PvE", new BuildStore(Path.Combine(root, "characters", "1303-Akunita", "builds")).Active?.Name);

            Assert.Equal(default, CharacterFolder.ProfileStamp(root, ""));
            Assert.Equal(default, CharacterFolder.ProfileStamp(root, "1303-Akunita"));
            var profile = Path.Combine(root, "characters", "1303-Akunita", "profile.json");
            File.WriteAllText(profile, "{}");
            var t1 = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(profile, t1);
            Assert.Equal(t1, CharacterFolder.ProfileStamp(root, "1303-Akunita"));
            var character = Path.Combine(root, "characters", "1303-Akunita", "character.json");
            File.WriteAllText(character, "{}");
            File.SetLastWriteTimeUtc(character, t1.AddMinutes(5));
            Assert.Equal(t1.AddMinutes(5), CharacterFolder.ProfileStamp(root, "1303-Akunita"));   // the newer of the two
        }
        finally { Directory.Delete(root, true); }
    }

    private static MeterEngine.RestoreSummary Summary(int kept = 0) => new(7, 5, 2, kept, true, true, true);

    [Fact]
    public async Task Restore_flow_maps_every_server_answer()
    {
        var fetched = 0;
        Func<string, Task<(int, string, string?)>> Answer(int status, string body = "{}", string? error = null) => code => { fetched++; Assert.Equal(Code, code); return Task.FromResult((status, body, error)); };
        MeterEngine.RestoreSummary Never(string c, RestorePayload p) => throw new InvalidOperationException("not applied");

        var bad = await RestoreFlow.RunAsync("not a code", true, Answer(200), Never);
        Assert.Equal(RestoreFlow.Outcome.BadCode, bad.Outcome);
        Assert.Equal(0, fetched);   // nothing asked for a malformed code

        foreach (var status in new[] { 0, 404, 429, 500, 302 })
        {
            var r = await RestoreFlow.RunAsync("3F9C-0A12-B7E4-4D85-A6C1-E09F-2B7D-5E31", true, Answer(status, "", status == 0 ? "network down" : null), Never);
            Assert.Equal(RestoreFlow.Outcome.Failed, r.Outcome);
            Assert.Null(r.Summary);
            if (status == 0) Assert.Contains("network down", r.Text);
            if (status is 500 or 302) Assert.Contains(status.ToString(), r.Text);
        }

        var unreadable = await RestoreFlow.RunAsync(Code, true, Answer(200, "{ nope"), Never);
        Assert.Equal(RestoreFlow.Outcome.Failed, unreadable.Outcome);

        var diskFull = await RestoreFlow.RunAsync(Code, true, Answer(200, "{\"ok\":true,\"builds\":[],\"state\":[]}"), (_, _) => throw new IOException("disk full"));
        Assert.Equal(RestoreFlow.Outcome.Failed, diskFull.Outcome);
        Assert.Equal("disk full", diskFull.Text);

        RestorePayload? got = null;
        var ok = await RestoreFlow.RunAsync(Code, true, Answer(200, "{\"ok\":true,\"builds\":[{\"build_id\":\"a1b2c3d4e5f6\",\"name\":\"PvE\",\"ts\":\"2026-10-11T09:30:00Z\"}],\"state\":[]}"),
            (c, p) => { got = p; return Summary(); });
        Assert.Equal(RestoreFlow.Outcome.Restored, ok.Outcome);
        Assert.Equal("a1b2c3d4e5f6", Assert.Single(got!.Builds).Build.Id);
        Assert.Equal(Summary(), ok.Summary);
        Assert.Contains("7", ok.Text);
        Assert.Contains("5", ok.Text);
    }

    [Fact]
    public void Restore_line_says_when_the_backup_switch_is_off()
    {
        var on = RestoreFlow.Describe(Summary(), shareBackup: true);
        var off = RestoreFlow.Describe(Summary(), shareBackup: false);
        Assert.StartsWith(on, off);
        Assert.True(off.Length > on.Length);
        Assert.EndsWith(".", on);
        Assert.True(RestoreFlow.Describe(Summary(kept: 3), true).Length > on.Length);   // "N newer here" is added
    }

    [Fact]
    public async Task Report_send_records_the_send_and_keeps_the_zip_on_failure()
    {
        var s = new AppSettings { Transient = true };
        var policy = new SharePolicy(true, true, true, true, false, false);
        SharePolicy? zipped = null;
        string Create(string note, SharePolicy p) { zipped = p; return "/tmp/report_x.zip"; }

        var (zip, id) = await BugReport.SendAsync(s, "it broke", policy, default, (path, note, _) => { Assert.Equal("/tmp/report_x.zip", path); Assert.Equal("it broke", note); return Task.FromResult("r-42"); }, Create);
        Assert.Equal(("/tmp/report_x.zip", "r-42"), (zip, id));
        Assert.Equal(policy, zipped);   // the ticks decide the zip
        Assert.Single(s.ReportSends);

        // inside the 10 s gap: refused before a zip is made
        zipped = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BugReport.SendAsync(s, "again", policy, default, (_, _, _) => Task.FromResult("x"), Create));
        Assert.Null(zipped);
        Assert.Single(s.ReportSends);

        var fresh = new AppSettings { Transient = true };
        var fail = await Assert.ThrowsAsync<BugReport.SendFailure>(() => BugReport.SendAsync(fresh, "n", policy, default, (_, _, _) => throw new HttpRequestException("413 too big"), Create));
        Assert.Equal("/tmp/report_x.zip", fail.Zip);
        Assert.Contains("413", fail.Message);
        Assert.Empty(fresh.ReportSends);   // a failed send does not count against the limits
        var timeout = await Assert.ThrowsAsync<BugReport.SendFailure>(() => BugReport.SendAsync(fresh, "n", policy, default, (_, _, _) => throw new TaskCanceledException(), Create));
        Assert.Equal("timeout", timeout.Message);
    }

    [Fact]
    public void Report_zip_takes_the_two_newest_logs_of_the_daemon_and_of_the_linux_card()
    {
        var root = TempDir();
        try
        {
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            foreach (var f in new[] { "aionmeter_20261011.log", "aionmeter_20261012.log", "aionmeter_20261013.log", "aionmeter-overlay_20261012.log", "aionmeter-overlay_20261013.log", "aionmeter-overlay_20261010.log", "aionmeter-ui_20261013.log" })
                File.WriteAllText(Path.Combine(logs, f), "x");
            var entries = BugReportFiles.Select(Path.Combine(root, "data"), Path.Combine(root, "app"), logs, new SharePolicy(true, true, true, false, false, false)).Select(e => e.Entry).ToList();
            Assert.Equal(["logs/aionmeter_20261013.log", "logs/aionmeter_20261012.log", "logs/aionmeter-overlay_20261013.log", "logs/aionmeter-overlay_20261012.log"], entries);
        }
        finally { Directory.Delete(root, true); }
    }
}
