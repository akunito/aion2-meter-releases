using System.Text.RegularExpressions;
using AionMeter.Overlay.Shell;

namespace AionMeter.Tests;

/// <summary>A2M-50: a layer surface takes its content's natural width (docs/33 "Size budget"), so a widget wider than the card grows the
/// card. These read the Overlay sources (GTK cannot load in the test container) for the two rules the card broke on 2026-10-13.</summary>
public class CardWidthBudgetTests
{
    // measured in a nested sway at 560 px: a UiKit.Section inside a view's scroll body gives its rows 444 px; the Settings adapter row
    // (190 + 260 = 450) widened the card to 542 visible instead of 540
    private const int SectionInside = 444;
    private const int FieldLabelDefault = 190;

    private static string OverlaySrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "AionMeter.Overlay");
            if (File.Exists(Path.Combine(p, "Card.cs"))) return p;
        }
        throw new DirectoryNotFoundException("src/AionMeter.Overlay above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Header_IsInAClippingScroller_SoNoHeaderWidgetWidensTheCard()
    {
        var card = File.ReadAllText(Path.Combine(OverlaySrc(), "Card.cs"));
        // fight/settings pill + "SAVED FIGHT   12.10 19:30" grew the card 540 → 622 visible; ES "COMBATE GUARDADO …" too
        Assert.Matches(@"(\w+)\.SetPolicy\(Gtk\.PolicyType\.External, Gtk\.PolicyType\.Never\);\s*\1\.PropagateNaturalHeight = true;\s*\1\.Child = header;\s*inner\.Append\(\1\);", card);
        Assert.DoesNotContain("inner.Append(header);", card);
        // the state label ellipsizes inside it (minimum "…") so it never pushes the clock out
        Assert.Matches(@"_state\.Ellipsize = Pango\.EllipsizeMode\.End;", card);
    }

    [Fact]
    public void FieldRows_FitASectionAtTheTabMinimumWidth()
    {
        Assert.Equal(560, TabState.MinTabWidth);   // SectionInside was measured at this width
        var call = new Regex(@"UiKit\.Field\(", RegexOptions.Compiled);
        var checkedRows = 0;
        foreach (var file in Directory.GetFiles(Path.Combine(OverlaySrc(), "Views"), "*.cs"))
        {
            var src = File.ReadAllText(file);
            foreach (Match m in call.Matches(src))
            {
                var args = Args(src, m.Index + m.Length);
                if (args.Count < 2) continue;
                var control = Regex.Match(args[1], @"UiKit\.(Combo|Input)\(");
                if (!control.Success) continue;   // a control built elsewhere: its width is not in the call
                var cargs = Args(args[1], control.Index + control.Length);
                if (!double.TryParse(cargs[^1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var width)) continue;
                var label = args.Count >= 4 && double.TryParse(args[3].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lw) ? lw : FieldLabelDefault;
                Assert.True(label + width <= SectionInside, $"{Path.GetFileName(file)}: {args[0].Trim()} is {label} + {width} px, a section has {SectionInside}");
                checkedRows++;
            }
        }
        Assert.True(checkedRows >= 8, $"only {checkedRows} Field rows with a sized control found");
    }

    /// <summary>Top-level arguments of the call whose '(' ends right before <paramref name="start"/>.</summary>
    private static List<string> Args(string s, int start)
    {
        var list = new List<string>();
        int depth = 0, from = start;
        var inStr = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
            switch (c)
            {
                case '"': inStr = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}' when depth > 0: depth--; break;
                case ')': list.Add(s[from..i]); return list;
                case ',' when depth == 0: list.Add(s[from..i]); from = i + 1; break;
            }
        }
        return list;
    }
}
