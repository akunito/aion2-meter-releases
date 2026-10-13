using AionMeter.Core.Updates;
using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// The help pages' model (A2M-50, what the Avalonia HelpWindow renders): data/help/&lt;tab&gt;.&lt;lang&gt;.md (EN when the language has none) through
/// the release-notes parser (ReleaseNotes.Lines: headings and lines), grouped into blocks — one heading, or the run of lines up to the next
/// heading as one paragraph — so GTK needs one label per block. Display-free, tested.
/// </summary>
internal static class HelpPage
{
    public readonly record struct Block(string Text, bool Heading);

    /// <summary>The fight breakdown shares the DPS page.</summary>
    public static string Tab(string tab) => tab == "fight" ? "meter" : tab;

    /// <summary>The file for a tab and language, else the EN one, else null.</summary>
    public static string? PathFor(string dataDir, string tab, string lang)
    {
        var dir = System.IO.Path.Combine(dataDir, "help");
        var path = System.IO.Path.Combine(dir, $"{Tab(tab)}.{lang}.md");
        if (File.Exists(path)) return path;
        path = System.IO.Path.Combine(dir, $"{Tab(tab)}.en.md");
        return File.Exists(path) ? path : null;
    }

    /// <summary>"Help · Prices" (the window title).</summary>
    public static string Title(string tab, UiText t) => t.HelpTitle + " · " + Tab(tab) switch
    {
        "atlas" => t.AtlasTitle, "top" => t.TopTitle, "prices" => t.PricesTitle, "craft" => t.CraftTitle, "build" => t.BuildTitle,
        "items" => t.ItemsTitle, "timers" => t.TimersTitle, "settings" => t.SettingsTitle, _ => "DPS",
    };

    /// <summary>The page as blocks; a missing page is one paragraph with UiText.HelpMissing.</summary>
    public static List<Block> Blocks(string? markdown, string lang)
    {
        var blocks = new List<Block>();
        if (markdown is null) { blocks.Add(new(UiText.Current.HelpMissing, false)); return blocks; }
        var para = new System.Text.StringBuilder();
        void Flush()
        {
            // a blank line inside a run stays (the Avalonia text had a LineBreak per line); trailing ones go
            var s = para.ToString().TrimEnd('\n');
            if (s.Length > 0) blocks.Add(new(s, false));
            para.Clear();
        }
        foreach (var (text, heading) in ReleaseNotes.Lines(markdown, lang))
        {
            if (heading) { Flush(); blocks.Add(new(text, true)); continue; }
            if (para.Length == 0 && text.Length == 0) continue;
            para.Append(text).Append('\n');
        }
        Flush();
        return blocks;
    }
}
