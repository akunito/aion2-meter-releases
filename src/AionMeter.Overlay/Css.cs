namespace AionMeter.Overlay;

/// <summary>The Avalonia card's look (Visuals.cs / OverlayWindow.cs) as GTK4 CSS. Every px is multiplied by --scale (CardText.ScaleCss).</summary>
internal static class Css
{
    public static string Sheet(double opacity) => $$"""
        window.aionmeter { background: none; }
        .card label { font-family: Inter, "Noto Sans", "DejaVu Sans", sans-serif; text-shadow: 0 1px 3px rgba(0,0,0,1), 0 0 2px rgba(0,0,0,0.9); }
        .frame { margin: 10px; border-radius: 18px; padding: 1px; background-image: linear-gradient(to bottom, #6E5B36, #2E2A26 35%, #24252C); box-shadow: 0 0 18px rgba(0,0,0,0.6); }
        .frame.through { background-image: none; background-color: #2A3350; }
        .inner { border-radius: 17px; background-color: rgba(13,17,26,OPACITY); background-image: linear-gradient(to bottom, rgba(255,255,255,0.05), rgba(255,255,255,0) 40%); padding: 10px 8px; }
        .topline { min-height: 2px; margin: 10px 80px 0 80px; background-image: linear-gradient(to right, rgba(201,164,92,0), #E7C67F, rgba(201,164,92,0)); }
        .header { margin: 0 10px; min-height: 26px; }
        .tab { min-width: 28px; min-height: 20px; margin-right: 3px; border-radius: 6px; border: 1px solid #C9A45C; background-color: rgba(17,23,38,0.6); opacity: 0.45; }
        .tab.on { background-color: #C9A45C; opacity: 1; }
        .tab.live { opacity: 1; }
        .tab.live:hover { border-color: #FFE3A6; background-color: rgba(201,164,92,0.22); }
        .tab.on, .tab.on:hover { background-color: #C9A45C; opacity: 1; }
        .tabs { margin-right: 10px; }
        .card label.title { font-family: "Noto Serif", "DejaVu Serif", serif; font-size: 16px; font-weight: 600; color: #F3E6C8; margin-right: 10px; }
        .card label.limit { font-size: 12.5px; font-weight: 600; color: #F0C060; font-feature-settings: "tnum"; }
        .card label.limit.urgent { color: #EF4D56; }
        .limitbox { margin-right: 10px; }
        .limitbox > drawingarea { margin: 1px 4px 0 0; }
        .statebox { margin-right: 10px; }
        .dot { min-width: 7px; min-height: 7px; border-radius: 4px; margin-right: 7px; background-color: #8A93A8; }
        .dot.live { background-color: #EF4D56; } .dot.saved { background-color: #C9A45C; }
        .card label.state { font-size: 10px; font-weight: 600; letter-spacing: 2px; color: #8A93A8; }
        .card label.state.live { color: #EF4D56; } .card label.state.saved { color: #C9A45C; }
        .card label.clock { font-size: 17px; font-weight: 700; color: #F3E6C8; font-feature-settings: "tnum"; }
        .chip { all: unset; min-height: 20px; padding: 0 8px; margin-right: 5px; border-radius: 5px; border: 1px solid #6E5A33; background-color: rgba(201,164,92,0.1); }
        .chip:hover { border-color: #C9A45C; }
        .card .chip label { font-size: 11px; font-weight: 600; color: #C9A45C; text-shadow: none; }
        .card .chip:hover label { color: #ECCB82; }
        .tool { all: unset; min-width: 22px; min-height: 22px; border-radius: 6px; }
        .tool:hover { background-color: rgba(255,255,255,0.08); }
        .boss { margin: 6px 10px 0 10px; min-height: 20px; }
        .card label.bosstext { font-size: 12px; font-weight: 600; color: #FFFFFF; margin: 0 10px; font-feature-settings: "tnum"; }
        .party { margin-top: 10px; border: 1px solid #6E5A33; padding: {{RowMetrics.PartyPadV}}px 18px; }
        .cols { margin: 0 14px {{RowMetrics.ColsGap}}px 8px; }
        .card label.col { font-size: 11px; font-weight: 700; color: #C9D0DC; }
        .card label.col.num { margin-left: 10px; font-size: 11px; font-weight: 700; }
        .card label.empty { font-size: 12px; color: #6B7590; }
        .row { margin-bottom: {{RowMetrics.Gap}}px; min-height: {{RowMetrics.Height}}px; }
        .rowc { margin: 0 14px 0 8px; }
        .card label.rank { min-width: {{RowMetrics.Rank}}px; min-height: {{RowMetrics.Rank}}px; border-radius: {{RowMetrics.Rank / 2}}px; font-size: 13px; font-weight: 700; border: 1px solid; padding: 0; text-shadow: none; }
        .card label.r1 { background-image: linear-gradient(to bottom, #F6DB8E, #C99A45); color: #1A140A; border-color: #FFEBB0; }
        .card label.r2 { background-image: linear-gradient(to bottom, #EEF2F7, #A7B2C1); color: #12161F; border-color: #FFFFFF; }
        .card label.r3 { background-image: linear-gradient(to bottom, #E9A87A, #A8633B); color: #1A0F08; border-color: #F5C39E; }
        .card label.rn { background-color: #1A2030; color: #F3E6C8; border-color: #6B7590; }
        .coin { min-width: {{RowMetrics.Coin}}px; min-height: {{RowMetrics.Coin}}px; border-radius: {{RowMetrics.Coin / 2}}px; background-color: rgba(7,9,16,0.71); border: 1px solid rgba(255,255,255,0.33); }
        .coin > image, .coin > drawingarea { margin: {{RowMetrics.CoinInset}}px; }
        .card label.name { font-size: 16.5px; font-weight: 700; color: #FFFFFF; margin-left: 4px; }
        .card .self label.name { text-decoration-line: underline; text-decoration-color: #ECCB82; }
        .card label.num { font-size: 16px; font-weight: 600; color: #FFFFFF; margin-left: 10px; font-feature-settings: "tnum"; }
        .card label.gear { font-size: 14px; color: #E8ECF4; }
        .rule { min-width: 1px; background-color: rgba(255,255,255,0.55); margin: {{RowMetrics.RuleInset}}px 5px; }
        .footer { margin: 8px 10px 0 10px; }
        .banner { border-radius: 8px; padding: 4px 10px; border: 1px solid rgba(61,214,140,0.5); background-color: rgba(61,214,140,0.165); margin-bottom: 8px; }
        .card .banner label { color: #D2FAE6; font-size: 12px; font-weight: 600; margin-left: 8px; }
        .statusline { margin: 0 12px 4px 0; }
        .sdot { min-width: 6px; min-height: 6px; border-radius: 3px; margin-right: 7px; background-color: #F5B544; }
        .sdot.good { background-color: #3DD68C; } .sdot.error { background-color: #EF4D56; } .sdot.mute { background-color: #6B7590; }
        .card label.status { font-size: 12px; color: #B4BCCB; margin-right: 14px; }
        .linkbtn { all: unset; }
        .card .linkbtn > box > label { font-size: 12px; color: #D9B56A; margin-right: 5px; }
        .card .linkbtn:hover > box > label { color: #ECCB82; }
        .card label.announce { font-size: 12px; font-weight: 600; color: #F5B544; margin: 0 12px 4px 13px; }
        .card label.abyss { font-size: 12px; color: #ECCB82; margin: 0 12px 4px 13px; }
        .pillrow { margin-right: 12px; }
        .pill { border-radius: 11px; padding: 3px 11px 3px 10px; border: 1px solid; margin: 2px 6px 2px 0; }
        .card .pill label { font-size: 12px; font-weight: 600; color: #F4F1E8; font-feature-settings: "tnum"; }
        .pill.p0 { background-color: #3A4A66; border-color: #8FA3C7; } .pill.p1 { background-color: #7E7036; border-color: #D9C56A; }
        .pill.p2 { background-color: #A8612A; border-color: #FFB071; } .pill.p3 { background-color: #A8362B; border-color: #FF8A7A; }
        .pill.timers { background-image: linear-gradient(to bottom, #F3D58F, #C99A45); border-color: #FFE3A6; box-shadow: 0 0 8px rgba(233,169,60,0.55); margin-right: 12px; }
        .card .pill.timers label { color: #1A140A; text-shadow: none; margin-left: 6px; }
        .grip { min-width: 14px; min-height: 14px; margin: 0 15px 15px 0; }
        popover.fights > contents { background-color: #111726; border: 1px solid #6E5A33; border-radius: 8px; padding: 4px; box-shadow: 0 4px 14px rgba(0,0,0,0.6); }
        popover.fights button { all: unset; padding: 4px 10px; border-radius: 5px; }
        popover.fights button:hover { background-color: rgba(201,164,92,0.18); }
        popover.fights button label, popover.fights label { font-family: Inter, "Noto Sans", sans-serif; color: #F1E9D8; font-size: 12px; font-feature-settings: "tnum"; }
        popover.fights label.off { color: #6B7590; }
        tooltip { background-color: rgba(17,23,38,0.96); border: 1px solid #6E5A33; border-radius: 6px; }
        tooltip label { font-family: Inter, "Noto Sans", sans-serif; color: #F1E9D8; font-size: 12px; }
        """.Replace("OPACITY", opacity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
}
