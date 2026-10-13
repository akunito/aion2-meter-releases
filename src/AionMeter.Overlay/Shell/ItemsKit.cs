using AionMeter.Overlay.Views;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// What the GTK Items tab needs beyond UiKit/ItemKit (A2M-50): its stylesheet (the mock's grey palette, k-items-* classes; a second
/// display-wide provider at the card's priority, re-scaled when a view is built at a new scale — fold it into KitCss when convenient),
/// <see cref="Clip"/> and the <see cref="FlowLines"/> wrap panel.
/// </summary>
internal static class ItemsKit
{
    // mock palette (Avalonia ItemsView brushes)
    public const uint Text = 0xFAFAFA, Dim = 0xAFAFAF, Mute = 0x7D7D7D, Line = 0x2B2B2B, Line2 = 0x373737, Band = 0x1F1F1F, ChipBack = 0x2B2B2B;
    public const uint Green = 0x3DDC84, Amber = 0xF5B544, Blue = 0x65B0FC, Num = 0xFCC78B, Faint = 0x555555;

    private static Gtk.CssProvider? _css;
    private static double _cssScale;

    /// <summary>Loads the k-items-* rules at the card's scale (views are rebuilt on a rescale, so the first view of a scale reloads them).</summary>
    public static void EnsureCss(double scale)
    {
        if (_css is not null && Math.Abs(_cssScale - scale) < 0.001) return;
        if (_css is null)
        {
            _css = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, _css, 800);   // GTK_STYLE_PROVIDER_PRIORITY_USER, as the card's sheet
        }
        _cssScale = scale;
        _css.LoadFromString(CardText.ScaleCss(Sheet(), scale));
    }

    public static string Sheet() => $$"""
        .k-items-page { background-color: #0D0D0D; }
        .k-items-top { margin: 0 14px 6px 14px; }
        .k-items-tools { background-color: rgba(31,31,31,0.8); border: 1px solid #2B2B2B; border-radius: 8px; padding: 7px 12px; margin: 0 0 10px 0; }
        .k-items-pill { all: unset; min-height: 20px; padding: 0 10px; border-radius: 12px; border: 1px solid #373737; margin-right: 6px; }
        .k-items-pill.k-items-on { background-color: #2B2B2B; border-color: #555555; }
        .k-root .k-items-pill label { font-size: 12px; color: #AFAFAF; }
        .k-root .k-items-pill:hover label, .k-root .k-items-pill.k-items-on label { color: #FAFAFA; }
        .k-items-head { background-color: #1F1F1F; border: 1px solid #2B2B2B; border-bottom-width: 0; border-radius: 8px 8px 0 0; padding: 7px 12px; }
        .k-items-head.k-items-shut { border-bottom-width: 1px; border-radius: 8px; margin-bottom: 10px; }
        .k-items-body { background-color: rgba(31,31,31,0.8); border: 1px solid #2B2B2B; border-top-width: 0; border-radius: 0 0 8px 8px; padding-bottom: 6px; margin-bottom: 10px; }
        .k-items-card { background-color: rgba(31,31,31,0.8); border: 1px solid #2B2B2B; border-radius: 8px; margin-bottom: 10px; }
        .k-items-chip { background-color: #2B2B2B; border-radius: 4px; padding: 1px 7px; margin-left: 8px; }
        .k-items-warn { background-color: rgba(245,181,68,0.08); border: 1px solid rgba(245,181,68,0.3); border-radius: 6px; padding: 6px 10px; margin: 8px 12px 0 12px; }
        .k-items-row { border-bottom: 1px solid #202020; margin: 0 12px; }
        .k-items-lhead { border-bottom: 1px solid #2B2B2B; margin: 4px 12px 0 12px; padding: 2px 0; }
        .k-items-tiles { margin: 10px 12px 0 12px; }
        .k-items-band { background-color: #1F1F1F; border-radius: 8px 8px 0 0; padding: 8px 12px; }
        .k-items-match { padding: 8px 12px; }
        .k-items-match.k-items-sep { border-top: 1px solid #2B2B2B; }
        .k-items-loc { background-color: #1A1A1A; border: 1px solid #2B2B2B; border-radius: 4px; padding: 1px 6px; }
        .k-items-loc.k-items-me { border-color: rgba(101,176,252,0.45); }
        .k-items-loc.k-items-acct { border-color: rgba(61,220,132,0.4); }
        .k-items-hint { background-color: #141414; border-radius: 6px; padding: 5px 8px; margin-top: 6px; }
        .k-items-link { all: unset; margin-left: 8px; }
        .k-root .k-items-link label { color: #65B0FC; font-size: 11.5px; }
        .k-root .k-items-link:hover label { text-decoration: underline; }
        .k-items-frule { border-top: 1px solid #1E1E1E; }
        .k-items-kv { background-color: #1F1F1F; border-radius: 6px; padding: 6px 8px; margin-top: 8px; }
        .k-root label.k-items-t { color: #FAFAFA; }
        .k-root label.k-items-d { color: #AFAFAF; }
        .k-root label.k-items-m { color: #7D7D7D; }
        .k-root label.k-items-n { color: #FCC78B; }
        .k-root label.k-items-f { color: #555555; }
        .k-root label.k-items-g { color: #3DDC84; }
        .k-root label.k-items-a { color: #F5B544; }
        .k-root label.k-items-b { color: #65B0FC; }
        .k-root label.k-items-hinttext { color: #7D7D7D; font-size: 12px; margin: 10px 12px; }
        """;

    /// <summary>
    /// A layer surface takes its content's natural width and the view frame its children's minimum: a box whose content may be wider than the
    /// card (a long head, the footer table) goes in here, a scrolled window that neither scrolls nor reports any width, so it clips instead.
    /// </summary>
    public static Gtk.ScrolledWindow Clip(Gtk.Widget child)
    {
        var s = Gtk.ScrolledWindow.New();
        s.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        s.PropagateNaturalHeight = true;
        s.Child = child;
        s.Hexpand = true;
        return s;
    }

    /// <summary>A label with the mock's colour class (k-items-t/d/m/n/f/g/a/b), size class and weight; one line, ellipsized.</summary>
    public static Gtk.Label Label(string text, char fg = 't', double size = 12.5, int weight = 400, bool ellipsize = true)
    {
        var l = Gtk.Label.New(text);
        l.Xalign = 0;
        l.Valign = Gtk.Align.Center;
        l.AddCssClass("k-items-" + fg);
        if (Math.Abs(size - 12.5) > 0.01) l.AddCssClass(KitCss.SizeClass(size));
        if (weight >= 500) l.AddCssClass(weight >= 700 ? "k-w7" : weight >= 600 ? "k-w6" : "k-w5");
        if (ellipsize) l.Ellipsize = Pango.EllipsizeMode.End;
        return l;
    }

    /// <summary>Replaces a label's k-items colour class.</summary>
    public static void SetFg(Gtk.Label l, char fg)
    {
        foreach (var c in "tdmnfgab") l.RemoveCssClass("k-items-" + c);
        l.AddCssClass("k-items-" + fg);
    }

    /// <summary>A wrapped label (1-char natural width, so it never widens the view).</summary>
    public static Gtk.Label Wrapped(string text, char fg = 't', double size = 12.5)
    {
        var l = Label(text, fg, size, 400, false);
        l.Wrap = true;
        l.WrapMode = Pango.WrapMode.WordChar;
        l.MaxWidthChars = 1;
        l.Hexpand = true;
        return l;
    }
}

/// <summary>
/// Avalonia WrapPanel for variable-width children (location pills, the legend): <see cref="Pack"/> measures their natural widths and deals
/// them into horizontal lines of a vertical box (ItemsLogic.Flow). GTK's FlowBox lines children up in columns of the widest one; a
/// CustomLayout costs a never-freed native callback per instance (gtk_custom_layout_new takes no user data). Pack again on a width change.
/// </summary>
internal sealed class FlowLines
{
    public Gtk.Box Widget { get; }
    private readonly List<Gtk.Widget> _items = new();
    private readonly List<Gtk.Box> _lines = new();
    private readonly int _hgap;
    private int[] _w = new int[16], _ln = new int[16];
    private int _packedFor = -1;

    public FlowLines(double hgap = 4, double vgap = 4)
    {
        _hgap = UiKit.Px(hgap);
        Widget = UiKit.VBox((int)vgap);
        Widget.Hexpand = true;
    }

    public void Clear()
    {
        foreach (var l in _lines) while (l.GetFirstChild() is { } c) l.Remove(c);
        _items.Clear();
        _packedFor = -1;
    }

    /// <summary>Adds a child to the last line (rooted, so CSS applies before <see cref="Pack"/> measures it).</summary>
    public void Add(Gtk.Widget w)
    {
        _items.Add(w);
        if (_lines.Count == 0) NewLine();
        _lines[^1].Append(w);
        _packedFor = -1;
    }

    public void Pack(int width)
    {
        if (width <= 0 || width == _packedFor) return;
        _packedFor = width;
        var n = _items.Count;
        if (_w.Length < n) { _w = new int[n * 2]; _ln = new int[n * 2]; }
        for (var i = 0; i < n; i++)
        {
            _items[i].Measure(Gtk.Orientation.Horizontal, -1, out _, out var nat, out _, out _);
            _w[i] = nat;
        }
        var lines = ItemsLogic.Flow(_w.AsSpan(0, n), width, _hgap, _ln);
        foreach (var l in _lines) while (l.GetFirstChild() is { } c) l.Remove(c);
        while (_lines.Count < lines) NewLine();
        for (var i = 0; i < n; i++) _lines[_ln[i]].Append(_items[i]);
        for (var i = 0; i < _lines.Count; i++) _lines[i].Visible = i < lines;
    }

    private void NewLine()
    {
        var l = Gtk.Box.New(Gtk.Orientation.Horizontal, _hgap);
        _lines.Add(l);
        Widget.Append(l);
    }
}

/// <summary>"Open in Craft →" from Items (Avalonia CraftView.RequestSearch): the text Craft's search box takes when it is shown next.
/// The GTK CraftView consumes it (<see cref="Take"/> in Shown, <see cref="Requested"/> while it lives); until Craft is ported the link is hidden.</summary>
internal static class CraftSearchRequest
{
    private static string? _pending;
    public static event Action<string>? Requested;

    public static void Request(string text) { _pending = text; Requested?.Invoke(text); }

    /// <summary>The pending text once (null when none).</summary>
    public static string? Take() { var t = _pending; _pending = null; return t; }
}
