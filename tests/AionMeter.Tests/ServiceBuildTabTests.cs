using AionMeter.Core.Builds;
using AionMeter.Core.Diagnostics;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>
/// A2M-48 phase 3c: what the Build tab and Report a problem keep in AionMeter.Service (written for the Avalonia UI, used by
/// the GTK card since A2M-50): description runs, questlog HTML, the character folder helpers and the report zip.
/// UiText.Current is process-wide and EsTextTests switches it: texts are checked by their numbers, not their words.
/// </summary>
public class ServiceBuildTabTests
{
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

            // a build saved through the folder the Linux tab uses is what WPF reads (characters/<key>/builds + build-active.txt)
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
            var entries = BugReportFiles.Select(Path.Combine(root, "data"), Path.Combine(root, "app"), logs, new SharePolicy(false, false, false)).Select(e => e.Entry).ToList();
            Assert.Equal(["logs/aionmeter_20261013.log", "logs/aionmeter_20261012.log", "logs/aionmeter-overlay_20261013.log", "logs/aionmeter-overlay_20261012.log"], entries);
        }
        finally { Directory.Delete(root, true); }
    }
}
