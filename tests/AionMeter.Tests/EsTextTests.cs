using System.Reflection;
using System.Text.RegularExpressions;
using AionMeter.Service;
using AionMeter.Core.Events;

namespace AionMeter.Tests;

/// <summary>A2M-47: Spanish replaces upstream's Russian; every key of the English defaults has a Spanish value.</summary>
public sealed class EsTextTests
{
    // Same text in both languages on purpose: proper nouns, loanwords the Spanish client keeps, and format-only strings.
    private static readonly HashSet<string> SameInBoth =
    [
        "HkAtlas", "AtlasTitle", "AtlasZoom", "TopTitle", "SecOverlay", "Wipe", "ThMulti",
        "ColDps", "ColGs", "TabDps", "TileDps", "ThDps", "HoursShort", "MinutesShort", "Seconds", "WaitingTitle",
        "ItemsTkKeys", "RaceElyos", "RegionAs", "ProfArcana", "BuildMacros",
        "BuildTitle", "BuildCard", "BuildTipBuild", "BuildSubDaevanion", "TabDaevanion", "DaevTitle", "DaevTitleNoChar",
        "DaevPoints", "DaevBoardLine", "DaevPlanFrom", "DaevColPlan", "DaevCost", "BuildPlanPaired",
    ];

    private static readonly Regex Placeholder = new(@"\{\d+(?::[^}]*)?\}", RegexOptions.Compiled);

    private static (UiText En, UiText Es) Both()
    {
        lock (Lock)
        {
            UiText.Use("es");
            var es = UiText.Current;
            UiText.Use("en");
            return (UiText.Current, es);
        }
    }

    private static readonly object Lock = new();

    private static IEnumerable<PropertyInfo> StringKeys() =>
        typeof(UiText).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string) && p.CanRead);

    [Fact]
    public void Normalize_maps_old_russian_settings_to_english()
    {
        Assert.Equal("en", UiText.Normalize("ru"));
        Assert.Equal("es", UiText.Normalize("es"));
        Assert.Equal("en", UiText.Normalize("en"));
        Assert.Equal("en", UiText.Normalize(null));
        Assert.Equal(new[] { "en", "es" }, UiText.Languages);
    }

    [Fact]
    public void Use_es_selects_the_spanish_table()
    {
        var (en, es) = Both();
        Assert.Equal("es", es.Code);
        Assert.Equal("Español", es.LanguageName);
        Assert.Equal("es-ES", es.Culture.Name);
        Assert.Equal("en", en.Code);
    }

    [Fact]
    public void Every_key_has_a_spanish_value()
    {
        var (en, es) = Both();
        var untranslated = new List<string>();
        var count = 0;
        foreach (var p in StringKeys())
        {
            count++;
            var e = (string)p.GetValue(en)!;
            var s = (string)p.GetValue(es)!;
            if (e.Length > 0 && string.IsNullOrWhiteSpace(s)) untranslated.Add(p.Name + " (empty)");
            else if (s == e && !SameInBoth.Contains(p.Name)) untranslated.Add(p.Name);
        }
        Assert.True(count > 800, $"only {count} string keys found");
        Assert.Empty(untranslated);
    }

    [Fact]
    public void Same_in_both_lists_only_keys_that_really_are_the_same()
    {
        var (en, es) = Both();
        var keys = StringKeys().ToDictionary(p => p.Name);
        foreach (var name in SameInBoth)
        {
            Assert.True(keys.TryGetValue(name, out var p), $"{name} is not a key");
            Assert.Equal((string)p!.GetValue(en)!, (string)p.GetValue(es)!);
        }
    }

    [Fact]
    public void Placeholders_match_per_key()
    {
        var (en, es) = Both();
        var wrong = new List<string>();
        foreach (var p in StringKeys())
        {
            var e = Placeholder.Matches((string)p.GetValue(en)!).Select(m => m.Value).Order();
            var s = Placeholder.Matches((string)p.GetValue(es)!).Select(m => m.Value).Order();
            if (!e.SequenceEqual(s)) wrong.Add(p.Name);
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public void Classes_flags_and_chat_words_are_spanish()
    {
        var (en, es) = Both();
        Assert.Equal(en.ClassNames.Count, es.ClassNames.Count);
        Assert.Equal(es.ClassNames.Keys.Order(), es.ClassPlurals!.Keys.Order());
        Assert.Equal("Templario", es.ClassName(GameClass.Templar));
        Assert.Equal("Arquero", es.ClassName(GameClass.Ranger));
        Assert.Equal(en.FlagNames.Count, es.FlagNames.Count);
        Assert.Equal("Crítico · Espalda", es.FlagsText(HitFlags.Critical | HitFlags.Back));
        Assert.Equal("1 jugador", es.Players(1));
        Assert.Equal("3 jugadores", es.Players(3));
        Assert.Equal("{0} daño", es.Chat.Damage);
        Assert.Null(en.ClassPlurals);
        Assert.Equal("Grieta", es.EventName("rift", "Rift"));
        Assert.Equal("Shugo", es.EventName("shugo", "Shugo"));
        Assert.Equal("Rift", en.EventName("rift", "Rift"));
        Assert.Equal("Arquero", es.ClassNameOf("ranger"));
        Assert.Equal("Templar", en.ClassNameOf("templar"));
        Assert.Equal(4, es.FootCurrencies.Length);
    }
}
