using System.Net;
using System.Text;

namespace AionMeter.Service;

/// <summary>One run of a skill description: <see cref="Num"/> marks the numbers questlog wraps in &lt;b&gt; or a coloured span.</summary>
public readonly record struct TextSeg(string Text, bool Num);

/// <summary>
/// Build tab text helpers shared by the WPF and Linux views (A2M-48 phase 3c, moved out of the WPF BuildView): skill descriptions as runs,
/// questlog's HTML build description as plain text.
/// </summary>
public static class BuildText
{
    /// <summary>A resolved description (SkillBook.Resolve) as runs: &lt;br&gt; becomes a line break, &lt;b&gt;/&lt;span&gt; toggle the number look, other tags drop.</summary>
    public static TextSeg[] ParseDescription(string s)
    {
        var list = new List<TextSeg>(8);
        var sb = new StringBuilder(s.Length);
        var num = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<' && s.IndexOf('>', i) is var end and > 0)
            {
                var tag = s.AsSpan(i + 1, end - i - 1);
                var close = tag.Length > 0 && tag[0] == '/';
                if (close) tag = tag[1..];
                if (tag.StartsWith("br", StringComparison.OrdinalIgnoreCase)) sb.Append('\n');
                else if (tag.StartsWith("b", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("span", StringComparison.OrdinalIgnoreCase))
                {
                    if (sb.Length > 0) { list.Add(new TextSeg(sb.ToString(), num)); sb.Clear(); }
                    num = !close;
                }
                i = end;
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(new TextSeg(sb.ToString(), num));
        return list.ToArray();
    }

    /// <summary>The runs up to the first line break (the tooltip's "at level N" line).</summary>
    public static TextSeg[] FirstLine(TextSeg[] segs)
    {
        for (var i = 0; i < segs.Length; i++)
        {
            var nl = segs[i].Text.IndexOf('\n');
            if (nl < 0) continue;
            var cut = new TextSeg[i + 1];
            Array.Copy(segs, cut, i);
            cut[i] = segs[i] with { Text = segs[i].Text[..nl] };
            return cut;
        }
        return segs;
    }

    /// <summary>questlog's build description is HTML (p, ul/li, b): plain text with line breaks and bullets.</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var sb = new StringBuilder(html.Length);
        for (var i = 0; i < html.Length; i++)
        {
            var c = html[i];
            if (c == '<' && html.IndexOf('>', i) is var end and > 0)
            {
                var tag = html.AsSpan(i + 1, end - i - 1);
                if (tag.StartsWith("li", StringComparison.OrdinalIgnoreCase)) sb.Append("\n• ");
                else if (tag.StartsWith("br", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("/p", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("/ul", StringComparison.OrdinalIgnoreCase)) sb.Append('\n');
                i = end;
                continue;
            }
            sb.Append(c);
        }
        var text = WebUtility.HtmlDecode(sb.ToString());
        while (text.Contains("\n\n\n", StringComparison.Ordinal)) text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim();
    }
}
