using System.Diagnostics;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using C = AionMeter.Overlay.Views.BuildColors.C;

namespace AionMeter.Overlay.Views;

/// <summary>
/// The Build tab's styles as GTK widgets (A2M-50, the Avalonia BuildUi: WPF BuildView.xaml resources Card, Band, Lbl, SubTab, Flat, TextBtn,
/// links, tooltip frame). Look = the k-build-* classes of <see cref="BuildColors.Sheet"/>. Inline links (source line, refresh, check questlog)
/// are Pango &lt;a&gt; in a wrapping label: the WrapPanel of runs + link TextBlocks does not exist in GTK, and a label wraps inside the card.
/// </summary>
internal static class BuildUi
{
    public static T Fg<T>(T w, C c) where T : Gtk.Widget
    {
        foreach (var cls in BuildColors.Classes) w.RemoveCssClass(cls);
        w.AddCssClass(BuildColors.Class(c));
        return w;
    }

    /// <summary>A label in the tab's greys: <paramref name="size"/> px (k-s classes), wrapped with a 1-char natural width unless <paramref name="wrap"/> is false
    /// (then one line, ellipsized).</summary>
    public static Gtk.Label Label(string text, double size = 12.5, C c = C.Text, int weight = 400, bool wrap = true, int maxChars = 1)
    {
        var l = Gtk.Label.New(text);
        l.Xalign = 0;
        l.MaxWidthChars = maxChars;
        l.Hexpand = true;
        if (wrap) { l.Wrap = true; l.WrapMode = Pango.WrapMode.WordChar; }
        else l.Ellipsize = Pango.EllipsizeMode.End;
        if (Math.Abs(size - 12.5) > 0.01) l.AddCssClass(KitCss.SizeClass(size));
        Fg(l, c);
        if (weight >= 500) l.AddCssClass(weight >= 700 ? "k-w7" : weight >= 600 ? "k-w6" : "k-w5");
        return l;
    }

    /// <summary>A short label that keeps its natural width (counts, values, a "+"): no wrapping, no expanding.</summary>
    public static Gtk.Label Tight(string text, double size = 12.5, C c = C.Text, int weight = 400)
    {
        var l = Gtk.Label.New(text);
        l.Valign = Gtk.Align.Center;
        if (Math.Abs(size - 12.5) > 0.01) l.AddCssClass(KitCss.SizeClass(size));
        Fg(l, c);
        if (weight >= 500) l.AddCssClass(weight >= 700 ? "k-w7" : weight >= 600 ? "k-w6" : "k-w5");
        return l;
    }

    public static Gtk.Box Card(Gtk.Widget child)
    {
        var b = UiKit.VBox();
        b.AddCssClass("k-build-card");
        b.Append(child);
        return b;
    }

    public static Gtk.Box Band(Gtk.Widget child)
    {
        var b = UiKit.HBox();
        b.AddCssClass("k-build-band");
        child.Hexpand = true;
        b.Append(child);
        return b;
    }

    /// <summary>[title (ellipsized) | right] in a band.</summary>
    public static Gtk.Box Band(Gtk.Widget title, Gtk.Widget right)
    {
        var row = UiKit.HBox();
        title.Hexpand = true;
        row.Append(title);
        right.Hexpand = false;
        right.Valign = Gtk.Align.Center;
        row.Append(right);
        return Band(row);
    }

    public static Gtk.Label BandTitle(string text)
    {
        var l = Gtk.Label.New(text);
        l.AddCssClass("k-build-bandtitle");
        l.Xalign = 0;
        l.Ellipsize = Pango.EllipsizeMode.End;
        l.MaxWidthChars = 1;
        l.Hexpand = true;
        l.Valign = Gtk.Align.Center;
        return l;
    }

    public static Gtk.Label Lbl(string text)
    {
        var l = Gtk.Label.New(text);
        l.AddCssClass("k-build-lbl");
        l.Xalign = 0;
        l.Valign = Gtk.Align.Center;
        return l;
    }

    /// <summary>questlog's flat button: #2b2b2b fill, #373737 hairline, brighter frame under the mouse; <paramref name="big"/> = the 13 px padded one.</summary>
    public static Gtk.Button Flat(string text, Action click, bool big = false, string? tip = null)
    {
        var l = Gtk.Label.New(text);
        l.Ellipsize = Pango.EllipsizeMode.End;
        var b = Button(l, "k-build-flat", click);
        if (big) b.AddCssClass("k-build-big");
        if (tip is not null) b.SetTooltipText(tip);
        return b;
    }

    public static void SetText(Gtk.Button b, string text)
    {
        if (b.GetChild() is Gtk.Label l) l.SetText(text);
    }

    /// <summary>Text-only button (Clear, + Add macro, the footer's save): muted, white under the mouse; <paramref name="look"/> = k-build-blue / -add / -s13.</summary>
    public static Gtk.Button TextBtn(string text, Action click, string? look = null, string? tip = null)
    {
        var b = Button(Gtk.Label.New(text), "k-build-textbtn", click);
        if (look is not null) b.AddCssClass(look);
        if (tip is not null) b.SetTooltipText(tip);
        return b;
    }

    private static Gtk.Button Button(Gtk.Widget child, string css, Action click)
    {
        var b = Gtk.Button.New();
        b.AddCssClass(css);
        b.Child = child;
        b.SetCursorFromName("pointer");
        b.FocusOnClick = false;
        b.Valign = Gtk.Align.Center;
        b.OnClicked += (_, _) => click();
        return b;
    }

    /// <summary>A wrapping label with Pango markup whose &lt;a href="id"&gt; links call <paramref name="link"/>(id) (BuildMarkup.Link).</summary>
    public static Gtk.Label Links(double size, C c, Action<string> link)
    {
        var l = Label("", size, c);
        l.AddCssClass("k-build-links");
        l.UseMarkup = true;
        l.OnActivateLink += (_, e) => { link(e.Uri); return true; };
        return l;
    }

    /// <summary>questlog's sub-tab button: transparent until chosen, then #2b2b2b with a #373737 frame.</summary>
    public static Gtk.Button SubTab(string text, Action click) => Button(Gtk.Label.New(text), "k-build-subtab", click);

    public static void SetSubTab(Gtk.Button b, bool on)
    {
        if (on) b.AddCssClass("k-build-on"); else b.RemoveCssClass("k-build-on");
    }

    /// <summary>A label inside a tooltip: tooltips are their own surface (no card width to protect), and a 1-char natural width there made
    /// GTK measure the tip at its 1-char minimum ("natural size must be >= min size" warnings, measured): 44 chars as for the 340 px frame.</summary>
    public static Gtk.Label TipLabel(string text, double size, C c, int weight = 400)
    {
        var l = Label(text, size, c, weight, true, 44);
        l.WidthChars = 30;   // a minimum width too: with a 1-word minimum GTK measured the tip's box taller than its natural height (warnings)
        return l;
    }

    /// <summary>The questlog-style tooltip body frame (mock .tip: #141414, #373737 hairline, radius 8, 10 padding).</summary>
    public static Gtk.Box TipFrame(Gtk.Widget content, double width = 340)
    {
        var b = UiKit.VBox();
        b.AddCssClass("k-root");
        b.AddCssClass("k-build-tip");
        b.SetSizeRequest(UiKit.Px(width), -1);
        b.Append(content);
        return b;
    }

    public static Gtk.Label TipSection(string text)
    {
        var l = Gtk.Label.New(text.ToUpper(UiText.Current.Culture));
        l.AddCssClass("k-build-tipsection");
        l.Xalign = 0;
        return l;
    }

    public static Gtk.Box TagChip(string text)
    {
        var b = UiKit.HBox();
        b.AddCssClass("k-build-tag");
        b.Append(Gtk.Label.New(text));
        return b;
    }

    /// <summary>Runs of a description into a label (numbers in the questlog peach, medium weight).</summary>
    public static void SetRuns(Gtk.Label l, ReadOnlySpan<TextSeg> segs) => l.SetMarkup(BuildMarkup.Runs(segs));

    /// <summary>A 1 px hairline (the priority rows' separators).</summary>
    public static Gtk.Box Sep()
    {
        var b = UiKit.HBox();
        b.AddCssClass("k-build-sep");
        return b;
    }

    /// <summary>The default browser through xdg-open.</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Info($"xdg-open {url}: {e.Message}"); }
    }

    /// <summary>A small drop-down of the tab (12 px text, 28 high) that cannot widen the card past <paramref name="maxChars"/>.</summary>
    public static Gtk.DropDown Combo(IEnumerable<string> items, int selected, double minWidth, int maxChars)
    {
        var d = BuildKit.Combo(items, selected, minWidth, maxChars);
        d.AddCssClass("k-build-combo");
        return d;
    }
}
