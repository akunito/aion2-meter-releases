namespace AionMeter.Overlay.Shell;

/// <summary>
/// A2M-50: the Craft tab's rules (the Avalonia CraftView's brushes: profession chips, row frames, the ×N / profit figure, the star, the list
/// without GTK's row chrome). Its own display-wide provider at the card's priority instead of lines in KitCss (the view ports run in parallel
/// and KitCss is shared): every selector carries a `k-craft-` class, so nothing else is touched. Reloaded at the card's scale by each new
/// CraftView (views are recreated on a rescale). Fold into KitCss.Sheet() once the ports are merged, if wanted.
/// </summary>
internal static class CraftCss
{
    private static Gtk.CssProvider? _provider;
    private static double _scale = -1;

    public static void Ensure(double scale)
    {
        if (_provider is null)
        {
            _provider = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, _provider, 800);   // GTK_STYLE_PROVIDER_PRIORITY_USER, as the card's
        }
        if (Math.Abs(scale - _scale) < 0.0001) return;
        _scale = scale;
        _provider.LoadFromString(CardText.ScaleCss(Sheet(), scale));
    }

    public const uint Ok = 0x8EE08A, Near = 0xF2C25A, Frame = 0x3A4055, OkFrame = 0x5EB85A, Bad = 0xF67A6C;

    public static string Sheet() => $$"""
        .k-root button.k-craft-prof { all: unset; padding: 3px 10px; margin-right: 4px; border-radius: 5px; border: 1px solid {{Palette.Hex(Palette.GoldDim)}}; background-color: rgba(17,23,38,0.4); }
        .k-root button.k-craft-prof.k-craft-on { border-color: {{Palette.Hex(Palette.Gold)}}; background-color: rgba(201,164,92,0.333); }
        .k-root .k-craft-prof label { font-size: 12px; font-weight: 600; color: {{Palette.Hex(Palette.TextDim)}}; }
        .k-root .k-craft-prof.k-craft-on label { color: {{Palette.Hex(Palette.Text)}}; }
        .k-root .k-craft-prof:hover label { color: {{Palette.Hex(Palette.GoldBright)}}; }
        .k-root listview.k-craft-list { background: none; }
        .k-root listview.k-craft-list > row { padding: 0; margin: 0; background: none; border: none; outline: none; box-shadow: none; min-height: 0; }
        .k-root .k-craft-row { background-color: rgba(255,255,255,0.05); border: 1px solid {{Palette.Hex(Frame)}}; border-radius: 9px; padding: 7px 10px; margin-bottom: 6px; }
        .k-root .k-craft-row.k-craft-okframe { border-color: {{Palette.Hex(OkFrame)}}; }
        .k-root label.k-craft-name { font-weight: 600; }
        .k-root label.k-craft-sub { font-size: 11px; color: {{Palette.Hex(Palette.TextMute)}}; }
        .k-root label.k-craft-times { font-size: 15px; font-weight: 600; margin-left: 10px; }
        .k-root label.k-craft-ok { color: {{Palette.Hex(Ok)}}; }
        .k-root label.k-craft-near { color: {{Palette.Hex(Near)}}; }
        .k-root label.k-craft-bad { color: {{Palette.Hex(Bad)}}; }
        .k-root button.k-craft-star { all: unset; padding: 0 4px; margin-left: 8px; }
        .k-root .k-craft-star label { font-size: 15px; color: {{Palette.Hex(Frame)}}; }
        .k-root .k-craft-star.k-craft-fav label { color: {{Palette.Hex(Near)}}; }
        .k-root .k-craft-star:hover label { color: {{Palette.Hex(Palette.GoldBright)}}; }
        """;
}
