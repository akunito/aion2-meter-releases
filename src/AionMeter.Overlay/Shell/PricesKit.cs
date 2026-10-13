using System.Text;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// What the Prices tab (A2M-50) needs beyond UiKit: the WPF "Small" button, the EN/ES and 24h/7d/30d toggles, the watch-row frame and the
/// price/trend colours. Its own display-wide provider (priority one above the card's) so the shared KitCss stays untouched; scaled like the
/// card's sheet and reloaded when a view is built at another scale.
/// </summary>
internal static class PricesKit
{
    private static Gtk.CssProvider? _css;
    private static double _scale = -1;

    /// <summary>Up (dearer, red), Down (cheaper, green), Flat (grey): the Avalonia PricesView brushes.</summary>
    public const uint Up = 0xF67A6C, Down = 0x8EE08A, Flat = 0x9AA0B0;

    public static string Sheet()
    {
        var sb = new StringBuilder(4096);
        sb.Append($$"""
        .k-root .k-prices-small { all: unset; padding: 4px 10px; border-radius: 6px; border: 1px solid {{Palette.Hex(Palette.Gold)}}; background-color: rgba(17,23,38,0.6); }
        .k-root .k-prices-small:hover { border-color: #FFE3A6; background-color: rgba(26,34,53,0.8); }
        .k-root .k-prices-small:disabled { opacity: 0.5; }
        .k-root .k-prices-small label { color: #F3D58F; font-size: 12.5px; }
        .k-root .k-prices-small.k-prices-mode { padding: 4px 0; min-width: 30px; }
        .k-root .k-prices-tog { all: unset; padding: 3px 10px; border: 1px solid {{Palette.Hex(Palette.GoldDim)}}; background-color: rgba(17,23,38,0.4); }
        .k-root .k-prices-tog label { color: {{Palette.Hex(Palette.TextDim)}}; font-size: 11.5px; }
        .k-root .k-prices-tog.k-prices-on { border-color: {{Palette.Hex(Palette.Gold)}}; background-color: rgba(201,164,92,0.33); }
        .k-root .k-prices-tog.k-prices-on label { color: {{Palette.Hex(Palette.Text)}}; }
        .k-root .k-prices-tog.k-prices-lang { border-radius: 5px; margin-right: 4px; }
        .k-root .k-prices-tog.k-prices-lang label { font-size: 12px; font-weight: 600; }
        .k-root .k-prices-tog.k-prices-segl { border-radius: 6px 0 0 6px; }
        .k-root .k-prices-tog.k-prices-segm { border-radius: 0; margin-left: -1px; }
        .k-root .k-prices-tog.k-prices-segr { border-radius: 0 6px 6px 0; margin-left: -1px; }
        .k-root .k-prices-row { background-color: rgba(255,255,255,0.05); border: 1px solid #3A4055; border-radius: 9px; padding: 8px 10px; margin-bottom: 7px; }
        .k-root .k-prices-row.k-prices-hit { border-color: #5EB85A; }
        .k-root .k-prices-results { background-color: #1A1F2E; border: 1px solid {{Palette.Hex(Palette.GoldDim)}}; margin-top: 4px; }
        .k-root .k-prices-hitrow { padding: 2px 6px; }
        .k-root .k-prices-hitrow:hover { background-color: {{Palette.Rgba(Palette.HoverFill)}}; }
        .k-root entry.k-prices-e11 { font-size: 11px; }
        .k-root label.k-prices-up { color: {{Palette.Hex(Up)}}; }
        .k-root label.k-prices-down { color: {{Palette.Hex(Down)}}; }
        .k-root label.k-prices-flat { color: {{Palette.Hex(Flat)}}; }
        """);
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>Loads (or reloads at a new UI scale) the tab's provider; call in the view's constructor (views are rebuilt on a rescale).</summary>
    public static void EnsureCss()
    {
        var scale = Icons.Scale;
        if (_css is not null && Math.Abs(scale - _scale) < 0.001) return;
        if (_css is null)
        {
            _css = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, _css, 801);   // the card's sheet is 800
        }
        _scale = scale;
        _css.LoadFromString(CardText.ScaleCss(Sheet(), scale));
    }

    /// <summary>The tone class of a price / trend / holding label.</summary>
    public static string ToneClass(PriceTone t) => t switch { PriceTone.Up => "k-prices-up", PriceTone.Down => "k-prices-down", _ => "k-prices-flat" };

    public static T SetTone<T>(T label, PriceTone t) where T : Gtk.Widget
    {
        label.RemoveCssClass("k-prices-up");
        label.RemoveCssClass("k-prices-down");
        label.RemoveCssClass("k-prices-flat");
        label.AddCssClass(ToneClass(t));
        return label;
    }

    /// <summary>WPF "Small": gold text, gold hairline on a dark fill, brighter under the mouse; never takes the focus.</summary>
    public static Gtk.Button Small(string text, Action click) => Button(text, "k-prices-small", click);

    /// <summary>An on/off toggle (EN/ES with k-prices-lang, a range segment with k-prices-segl/segm/segr); <see cref="SetOn"/> paints it.</summary>
    public static Gtk.Button Toggle(string text, string shape, Action click)
    {
        var b = Button(text, "k-prices-tog", click);
        b.AddCssClass(shape);
        return b;
    }

    public static void SetOn(Gtk.Widget w, bool on)
    {
        if (on) w.AddCssClass("k-prices-on"); else w.RemoveCssClass("k-prices-on");
    }

    private static Gtk.Button Button(string text, string css, Action click)
    {
        var b = Gtk.Button.New();
        b.AddCssClass(css);
        b.Child = Gtk.Label.New(text);
        b.SetCursorFromName("pointer");
        b.FocusOnClick = false;
        b.Focusable = false;
        b.Valign = Gtk.Align.Center;
        b.OnClicked += (_, _) => click();
        return b;
    }
}

/// <summary>Colour of a price, trend or holding value.</summary>
internal enum PriceTone : byte { Flat, Up, Down }
