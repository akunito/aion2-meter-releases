using System.Globalization;
using System.Text;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// The hosted views' stylesheet (A2M-50): the WPF Theme.xaml styles UiKit builds (Avalonia UiKit numbers), appended to the card's Css.Sheet and
/// scaled with it by CardText.ScaleCss, so a UI-scale change restyles every view on the card's rebuild. Every selector sits under `.k-root`
/// (the view frame and the dialog surfaces) and every class is `k-` prefixed: the card's own rules (`.card label`, `.num`, `.chip`) never hit a view.
/// Rules for a colour (`k-fg-*`), size (`k-s<10×px>`) and weight (`k-w5/6/7`) come last so they win over the component defaults.
/// </summary>
internal static class KitCss
{
    public static string Sheet()
    {
        var sb = new StringBuilder(16 * 1024);
        sb.Append($$"""
        .k-root label { font-family: Inter, "Noto Sans", "DejaVu Sans", sans-serif; text-shadow: none; color: {{Palette.Hex(Palette.Text)}}; font-size: 12.5px; }
        .k-view { background-color: {{Palette.Hex(Palette.Bg0)}}; border: 1px solid {{Palette.Hex(Palette.GoldDim)}}; border-radius: 8px; margin: 10px 2px 0 2px; }
        .k-head { margin: 8px 8px 8px 14px; min-height: 26px; }
        .k-root label.k-title { font-family: "Noto Serif", "DejaVu Serif", serif; font-size: 18px; font-weight: 600; color: {{Palette.Hex(Palette.Text)}}; }
        .k-root label.k-dtitle { font-family: "Noto Serif", "DejaVu Serif", serif; font-size: 19px; font-weight: 600; color: {{Palette.Hex(Palette.Text)}}; }
        .k-foot { border-top: 1px solid {{Palette.Hex(Palette.Line)}}; padding: 10px 16px; }
        .k-scroll > viewport > * { margin: 0 16px; }
        .k-section { background-color: {{Palette.Rgba(Palette.SectionFill)}}; border: 1px solid {{Palette.Rgba(Palette.SectionLine)}}; border-radius: 10px; padding: 12px 16px; margin-bottom: 12px; }
        .k-root label.k-seclabel { font-size: 11px; font-weight: 600; color: {{Palette.Hex(Palette.Gold)}}; letter-spacing: 0.6px; margin-bottom: 6px; }
        .k-root label.k-hint { font-size: 11.5px; color: {{Palette.Hex(Palette.TextMute)}}; margin-bottom: 6px; }
        .k-root label.k-fieldlabel { margin-right: 8px; }
        .k-field { margin-top: 8px; }
        .k-chip { all: unset; min-height: 22px; padding: 0 9px; border-radius: 5px; border: 1px solid {{Palette.Hex(Palette.GoldDim)}}; background-color: rgba(201,164,92,0.1); }
        .k-chip:hover { border-color: {{Palette.Hex(Palette.Gold)}}; }
        .k-root .k-chip label { font-size: 11.5px; font-weight: 600; color: {{Palette.Hex(Palette.Gold)}}; }
        .k-root .k-chip:hover label { color: {{Palette.Hex(Palette.GoldBright)}}; }
        .k-chip > box > drawingarea { margin: 1px 6px 0 0; }
        .k-btn { all: unset; padding: 6px 14px; border-radius: 6px; border: 1px solid {{Palette.Hex(Palette.Line)}}; background-color: {{Palette.Hex(Palette.Bg2)}}; margin-right: 8px; }
        .k-btn:hover { border-color: {{Palette.Hex(Palette.GoldDim)}}; }
        .k-btn:disabled { opacity: 0.45; }
        .k-btn.k-primary { background-color: {{Palette.Hex(Palette.Gold)}}; border-color: {{Palette.Hex(Palette.Gold)}}; }
        .k-btn.k-primary:hover { background-color: {{Palette.Hex(Palette.GoldBright)}}; }
        .k-root .k-btn.k-primary label { color: {{Palette.Hex(Palette.Dark)}}; font-weight: 600; }
        .k-icon { all: unset; min-width: 22px; min-height: 22px; border-radius: 6px; }
        .k-icon:hover { background-color: {{Palette.Rgba(Palette.HoverFill)}}; }
        .k-root entry, .k-root textview > text { background-color: {{Palette.Hex(Palette.Bg2)}}; color: {{Palette.Hex(Palette.Text)}}; border: 1px solid {{Palette.Hex(Palette.Line)}}; border-radius: 4px; min-height: 26px; padding: 0 6px; font-size: 12.5px; caret-color: {{Palette.Hex(Palette.Gold)}}; box-shadow: none; outline: none; }
        .k-root entry:focus-within { border-color: {{Palette.Hex(Palette.GoldDim)}}; }
        .k-root entry > text > placeholder { color: {{Palette.Hex(Palette.TextMute)}}; }
        .k-root entry.k-mono { font-family: "DejaVu Sans Mono", "Liberation Mono", monospace; color: {{Palette.Hex(Palette.Gold)}}; }
        .k-root entry.k-mono13 { font-family: "DejaVu Sans Mono", "Liberation Mono", monospace; font-size: 13.5px; }
        .k-root textview { background: none; }
        .k-root textview > text { padding: 6px; }
        .k-root checkbutton { padding: 2px 0; }
        .k-root checkbutton > check { min-width: 14px; min-height: 14px; margin-right: 8px; border-radius: 3px; border: 1px solid {{Palette.Hex(Palette.TextDim)}}; background-color: {{Palette.Hex(Palette.Bg2)}}; background-image: none; color: {{Palette.Hex(Palette.Dark)}}; box-shadow: none; -gtk-icon-size: 12px; }
        .k-root checkbutton > check:checked { background-color: {{Palette.Hex(Palette.Gold)}}; border-color: {{Palette.Hex(Palette.Gold)}}; }
        .k-root checkbutton:hover > check { border-color: {{Palette.Hex(Palette.GoldBright)}}; }
        .k-root dropdown > button { all: unset; min-height: 26px; padding: 0 8px; border-radius: 4px; border: 1px solid {{Palette.Hex(Palette.Line)}}; background-color: {{Palette.Hex(Palette.Bg2)}}; }
        .k-root dropdown > button:hover { border-color: {{Palette.Hex(Palette.GoldDim)}}; }
        .k-root dropdown > button arrow { color: {{Palette.Hex(Palette.TextDim)}}; min-width: 12px; min-height: 12px; -gtk-icon-size: 12px; margin-left: 6px; }
        .k-root popover > contents { background-color: {{Palette.Hex(Palette.Bg1)}}; border: 1px solid {{Palette.Hex(Palette.GoldDim)}}; border-radius: 8px; padding: 4px; box-shadow: 0 4px 14px rgba(0,0,0,0.6); }
        .k-root popover listview, .k-root popover list { background: none; }
        .k-root popover row { padding: 4px 10px; border-radius: 5px; background: none; min-height: 0; }
        .k-root popover row:hover, .k-root popover row:selected { background-color: rgba(201,164,92,0.18); }
        .k-root popover row label { font-size: 12.5px; }
        .k-root popover row check, .k-root popover row image { -gtk-icon-size: 10px; min-width: 10px; color: {{Palette.Hex(Palette.Gold)}}; margin-left: 6px; }
        .k-root scale { padding: 6px 0; }
        .k-root scale > trough { min-height: 4px; border-radius: 2px; background-color: rgba(255,255,255,0.13); border: none; }
        .k-root scale > trough > highlight { min-height: 4px; min-width: 4px; background-color: {{Palette.Hex(Palette.Gold)}}; border-radius: 2px; border: none; }
        .k-root scale > trough > slider { min-width: 14px; min-height: 14px; margin: -6px; border-radius: 7px; background-color: {{Palette.Hex(Palette.GoldBright)}}; border: none; box-shadow: 0 1px 3px rgba(0,0,0,0.6); }
        .k-root scrollbar { background: none; border: none; }
        .k-root scrollbar slider { min-width: 6px; min-height: 6px; border-radius: 3px; background-color: rgba(255,255,255,0.18); border: none; }
        .k-root scrollbar slider:hover { background-color: rgba(201,164,92,0.5); }
        .k-tile { background-color: {{Palette.Rgba(Palette.TileFill)}}; border: 1px solid {{Palette.Rgba(Palette.TileLine)}}; border-radius: 8px; padding: 6px 12px; margin: 0 6px 6px 0; }
        .k-root label.k-tilelabel { font-size: 10px; font-weight: 600; color: {{Palette.Hex(Palette.Gold)}}; letter-spacing: 0.5px; }
        .k-root label.k-tilevalue { font-weight: 600; margin-top: 2px; }
        .k-root label.k-tilecap { font-size: 10.5px; color: {{Palette.Hex(Palette.TextMute)}}; }
        .k-grade { border-radius: 3px; border: 1px solid; padding: 0 4px 1px 4px; margin-left: 6px; }
        .k-root .k-grade label { font-size: 10.5px; font-weight: 600; }
        .k-search > entry { padding-right: 24px; }
        .k-search > .k-icon { margin-right: 4px; }
        .k-dialog { margin: 8px; border-radius: 14px; padding: 1px; background-image: linear-gradient(to bottom, #6E5B36, #2E2A26 35%, #24252C); box-shadow: 0 0 14px rgba(0,0,0,0.6); }
        .k-dialog > .k-dinner { border-radius: 13px; background-color: rgba(13,17,26,0.97); padding: 8px; }
        .k-dhead { margin: 4px 2px 10px 12px; }
        .k-root label.k-help { font-size: 13px; }
        """);
        sb.Append('\n');
        var classes = Palette.Classes;
        for (var i = 0; i < classes.Length; i++)
            sb.Append(".k-root label.").Append(classes[i]).Append(", .k-root .").Append(classes[i]).Append(" label { color: ").Append(Palette.Hex(Palette.Rgb((Fg)i))).Append("; }\n");
        // Text(size): one class per half pixel from 9 to 24 px
        for (var t = 90; t <= 240; t += 5)
            sb.Append(".k-root label.k-s").Append(t.ToString(CultureInfo.InvariantCulture)).Append(" { font-size: ")
              .Append((t / 10.0).ToString("0.#", CultureInfo.InvariantCulture)).Append("px; }\n");
        sb.Append(".k-root label.k-w5 { font-weight: 500; } .k-root label.k-w6 { font-weight: 600; } .k-root label.k-w7 { font-weight: 700; }\n");
        for (var g = 0; g < Palette.GradeCount; g++)
        {
            var grade = (AionMeter.Core.GameDb.Grade)g;
            var line = Palette.GradeLine(grade);
            sb.Append(".k-grade.k-g").Append(g).Append(" { border-color: ").Append(Palette.Hex(line)).Append("; background-color: ")
              .Append(Palette.Rgba(0x2E000000 | line)).Append("; } .k-root .k-grade.k-g").Append(g).Append(" label, .k-root label.k-gt").Append(g)
              .Append(" { color: ").Append(Palette.Hex(Palette.GradeText(grade))).Append("; }\n");
        }
        return sb.ToString();
    }

    /// <summary>The size class for a font size in px (nearest half pixel, 9–24).</summary>
    public static string SizeClass(double px)
    {
        var t = (int)Math.Round(Math.Clamp(px, 9, 24) * 2) * 5;
        return "k-s" + t.ToString(CultureInfo.InvariantCulture);
    }
}
