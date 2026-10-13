using AionMeter.Core.GameDb;
using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>Shared bits of the Items, Craft and Prices tabs on GTK (A2M-50, the Avalonia ItemKit): grade chips (WPF GradeTag), kinah formats,
/// the price parser, the search box, an icon image.</summary>
internal static class ItemKit
{
    /// <summary>"12,847" → "13k", 1,234,567 → "1.2M".</summary>
    public static string Short(long n) => ItemFormat.Short(n);

    /// <summary>Craft's compact kinah: 1.25M, 850.5k, 120.</summary>
    public static string Kinah(long v) => ItemFormat.Kinah(v);

    public static string N(long v) => ItemFormat.N(v);

    /// <summary>"1200000", "1,200,000", "1.2m", "850k" → Kinah.</summary>
    public static bool TryParsePrice(string? text, out long value) => ItemFormat.TryParsePrice(text, out value);

    /// <summary>The grade chip after an item name ("Artisan's Refining Stone [legendary]"); null for an unknown grade.</summary>
    public static Gtk.Box? GradeChip(Grade g, string lang, double size = 10.5)
    {
        if (g == Grade.Unknown) return null;
        var b = UiKit.HBox();
        b.AddCssClass("k-grade");
        b.AddCssClass("k-g" + ((int)g < Palette.GradeCount ? (int)g : 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.Valign = Gtk.Align.Center;
        var l = Gtk.Label.New(ItemGrades.Word(g, lang));
        if (Math.Abs(size - 10.5) > 0.01) l.AddCssClass(KitCss.SizeClass(size));
        b.Append(l);
        return b;
    }

    /// <summary>Text colour of a grade on a label (k-gt&lt;n&gt;, the WPF GradeText brushes).</summary>
    public static T GradeText<T>(T label, Grade g) where T : Gtk.Widget
    {
        label.AddCssClass("k-gt" + ((int)g < Palette.GradeCount ? (int)g : 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return label;
    }

    /// <summary>[name][chip] on one line (Avalonia AddChip on an inline): the name ellipsizes, the chip stays.</summary>
    public static Gtk.Box NameWithChip(string name, Grade g, string lang, double size = 12.5)
    {
        var row = UiKit.HBox();
        row.Append(UiKit.Line(name, size));
        if (GradeChip(g, lang) is { } chip) row.Append(chip);
        return row;
    }

    /// <summary>An icon texture at <paramref name="size"/> px (unscaled); null texture leaves it empty (set it later with SetFromPaintable).</summary>
    public static Gtk.Image Image(Gdk.Texture? texture, double size)
    {
        var img = texture is null ? Gtk.Image.New() : Gtk.Image.NewFromPaintable(texture);
        img.PixelSize = UiKit.Px(size);
        return img;
    }

    /// <summary>Search box with a hint text and a clear ✕ (WPF SearchField / Field + ClearSearch): Esc clears too (and is not passed to the card while there is text).</summary>
    public static (Gtk.Overlay Host, Gtk.Entry Box) SearchBox(string hint, double width, Action changed)
    {
        var box = UiKit.Input("", width);
        box.PlaceholderText = hint;
        var host = Gtk.Overlay.New();
        host.AddCssClass("k-search");
        host.SetChild(box);
        host.SetSizeRequest(UiKit.Px(width), -1);
        host.MarginEnd = UiKit.Px(6);
        host.Valign = Gtk.Align.Center;
        var clear = UiKit.IconButton(Icons.Close, UiText.Current.CraftClear, () => { box.SetText(""); box.GrabFocus(); }, 18);
        clear.Halign = Gtk.Align.End;
        clear.Valign = Gtk.Align.Center;
        clear.Visible = false;
        host.AddOverlay(clear);
        box.OnChanged += (_, _) => { clear.Visible = box.GetText().Length > 0; changed(); };
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != 0xff1b || box.GetText().Length == 0) return false;   // GDK_KEY_Escape
            box.SetText("");
            return true;
        };
        box.AddController(keys);
        return (host, box);
    }
}
