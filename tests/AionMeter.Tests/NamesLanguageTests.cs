using System.Text.Json;
using AionMeter.Core.Game;
using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Tests;

/// <summary>A2M-47: Settings → "Names: items, skills and monsters" (AppSettings.NamesLanguage) — the resolution rule, the per-tab switches,
/// the migration of the old per-tab keys, and the engine's name tables following it on a settings reload (daemon) or in place (viewer).</summary>
[Collection(ProcessStaticsCollection.Name)]
public class NamesLanguageTests
{
    [Theory]
    [InlineData("en", "", "en")]
    [InlineData("es", "", "es")]
    [InlineData("en", "es", "es")]
    [InlineData("es", "en", "en")]
    [InlineData("es", "es", "es")]
    [InlineData("ru", "", "en")]      // an older build's Russian loads as English
    [InlineData("es", "fr", "es")]    // anything but en/es follows the interface
    [InlineData(null, null, "en")]
    public void Resolve_EmptyFollowsTheInterfaceAndAnExplicitValueWins(string? language, string? names, string expected)
    {
        Assert.Equal(expected, AppSettings.ResolveNames(language, names));
        Assert.Equal(expected, new AppSettings { Language = language!, NamesLanguage = names ?? "" }.ResolvedNamesLanguage);
    }

    [Fact]
    public void Default_IsSameAsInterface()
    {
        var s = new AppSettings();
        Assert.Equal("", s.NamesLanguage);
        Assert.Equal("en", s.ResolvedNamesLanguage);
        s.Language = "es";
        Assert.Equal("es", s.ResolvedNamesLanguage);
    }

    [Theory]
    [InlineData("en", "en", "")]
    [InlineData("en", "es", "es")]
    [InlineData("es", "es", "")]
    [InlineData("es", "en", "en")]
    public void TabSwitch_StoresTheInterfaceLanguageAsFollow(string language, string pick, string stored)
    {
        var s = new AppSettings { Language = language, NamesLanguage = pick == "en" ? "es" : "en" };
        s.PickNames(pick);
        Assert.Equal(stored, s.NamesLanguage);
        Assert.Equal(pick, s.ResolvedNamesLanguage);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "en")]
    [InlineData(2, "es")]
    public void SettingsDropDown_RowsRoundTrip(int index, string value)
    {
        Assert.Equal(value, AppSettings.NamesValue(index));
        Assert.Equal(index, AppSettings.NamesIndex(value));
    }

    [Fact]
    public void SettingsDropDown_UnknownMapsToSameAsInterface()
    {
        Assert.Equal(0, AppSettings.NamesIndex(null));
        Assert.Equal(0, AppSettings.NamesIndex("ru"));
        Assert.Equal("", AppSettings.NamesValue(-1));
    }

    [Theory]
    [InlineData("en", "es", "en", "es")]   // Build switched to Spanish
    [InlineData("en", "en", "es", "es")]   // Items / Prices switched to Spanish
    [InlineData("en", "es", "es", "es")]
    [InlineData("en", "en", "en", "")]     // both followed the interface
    [InlineData("es", "es", "es", "")]
    [InlineData("es", "en", null, "en")]
    [InlineData("es", null, "en", "en")]
    [InlineData("en", null, null, "")]
    public void Migrate_APerTabKeyThatDiffersFromTheInterfaceWins(string language, string? skill, string? price, string expected) =>
        Assert.Equal(expected, AppSettings.MigrateNames(language, skill, price));

    [Fact]
    public void Load_FoldsTheOldKeysIntoNamesLanguageAndNeverWritesThemAgain()
    {
        WithDataDir(dir =>
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """{ "LayoutVersion": 4, "Language": "en", "SkillLanguage": "es", "PriceSearchLanguage": "en" }""");
            var s = AppSettings.Load();
            Assert.Equal("es", s.NamesLanguage);
            Assert.Equal("es", s.ResolvedNamesLanguage);
            Assert.Equal("en", s.Language);
            Assert.Null(s.LegacySkillLanguage);
            Assert.Null(s.LegacyPriceSearchLanguage);
            s.Save();
            using (var doc = JsonDocument.Parse(File.ReadAllText(file)))
            {
                Assert.False(doc.RootElement.TryGetProperty("SkillLanguage", out _));
                Assert.False(doc.RootElement.TryGetProperty("PriceSearchLanguage", out _));
                Assert.Equal("es", doc.RootElement.GetProperty("NamesLanguage").GetString());
            }
            Assert.Equal("es", AppSettings.Load().NamesLanguage);

            // an explicit NamesLanguage wins over old keys an older build wrote next to it
            File.WriteAllText(file, """{ "LayoutVersion": 4, "Language": "es", "NamesLanguage": "en", "SkillLanguage": "es", "PriceSearchLanguage": "es" }""");
            Assert.Equal("en", AppSettings.Load().NamesLanguage);

            // per-tab keys equal to the interface: names follow it
            File.WriteAllText(file, """{ "LayoutVersion": 4, "Language": "es", "SkillLanguage": "es", "PriceSearchLanguage": "es" }""");
            var follow = AppSettings.Load();
            Assert.Equal("", follow.NamesLanguage);
            Assert.Equal("es", follow.ResolvedNamesLanguage);

            // a junk value loads as "same as interface"
            File.WriteAllText(file, """{ "LayoutVersion": 4, "Language": "en", "NamesLanguage": "fr" }""");
            Assert.Equal("", AppSettings.Load().NamesLanguage);
        });
    }

    [Fact]
    public void Engine_NameTablesFollowNamesLanguageOnReloadAndInPlace()
    {
        var data = RepoData();
        var en = GameData.Load(data, "en");
        var es = GameData.Load(data, "es");
        var code = 0;
        foreach (var (c, _) in en.Skills) if (es.SkillName(c) != en.SkillName(c)) { code = c; break; }
        Assert.NotEqual(0, code);

        var previousData = MeterEngine.DataDirectory;
        var previousUi = UiText.Current.Code;
        MeterEngine.DataDirectory = data;
        try
        {
            WithDataDir(dir =>
            {
                var file = Path.Combine(dir, "settings.json");
                File.WriteAllText(file, """{ "LayoutVersion": 4, "Language": "en", "NamesLanguage": "es" }""");
                using var engine = MeterEngine.OpenViewer();
                Assert.Equal("es", engine.Data.Language);           // interface English, names Spanish from the start
                Assert.Equal(es.SkillName(code), engine.Data.SkillName(code));
                Assert.Equal("en", UiText.Current.Code);

                // the daemon's path: settings.json rewritten by the UI, `reload-settings` → ReloadSettingsIfChanged(force)
                File.WriteAllText(file, """{ "LayoutVersion": 4, "Language": "es", "NamesLanguage": "en" }""");
                Assert.True(engine.ReloadSettingsIfChanged(force: true));
                Assert.Equal("en", engine.Data.Language);           // interface Spanish, names English
                Assert.Equal(en.SkillName(code), engine.Data.SkillName(code));
                Assert.Equal("es", UiText.Current.Code);

                // the viewer's path: the shell edits its Settings in place and calls ApplySettingsInPlace
                engine.Settings.NamesLanguage = "";
                engine.ApplySettingsInPlace();
                Assert.Equal("es", engine.Data.Language);           // back to following the interface
                Assert.False(engine.ApplyNamesLanguage());          // nothing moved: no reload
            });
        }
        finally
        {
            MeterEngine.DataDirectory = previousData;
            UiText.Use(previousUi);
        }
    }

    [Fact]
    public void AtlasCatalog_ChipsInTheNamesLanguage_EnglishStaysTheStoredName()
    {
        var dir = Directory.CreateTempSubdirectory("atlas-names-");
        try
        {
            var path = Path.Combine(dir.FullName, "gather-catalog.json");
            File.WriteAllText(path, """
                { "categories": [ { "key": "gem", "en": "Gems", "es": "Gemas", "nodes": [ { "key": "ruby", "en": "Ruby", "es": "Rubí" }, { "key": "odyle", "en": "Odyle" } ] } ] }
                """);
            var list = AtlasCatalog.Load(path);
            Assert.Equal("Rubí", list[0].Label("es"));
            Assert.Equal("Ruby", list[0].Label("en"));
            Assert.Equal("Ruby", list[0].Name);                  // objects.json and the map keep the English name
            Assert.Equal("Gemas", list[0].CategoryLabel("es"));
            Assert.Equal("Gems", list[0].CategoryLabel("en"));
            Assert.Equal("Odyle", list[1].Label("es"));          // no Spanish name: the English one
            Assert.True(AtlasCatalog.IsCurrent("Ruby", list[0].Name));
        }
        finally { dir.Delete(recursive: true); }
    }

    private static void WithDataDir(Action<string> test)
    {
        var previous = AppSettings.AppDataDir;
        var dir = Path.Combine(Path.GetTempPath(), "aionmeter-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppSettings.UseAppDataDir(dir);
        try { test(dir); }
        finally
        {
            AppSettings.UseAppDataDir(previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static string RepoData()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "servers.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/servers.json above " + AppContext.BaseDirectory);
    }
}
