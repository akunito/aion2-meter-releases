using AionMeter.Core.Events;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// What the fight breakdown (Views/FightView, FightCharts, ProfilePages) needs and the kit lacks (A2M-50): its own scaled stylesheet (so
/// KitCss stays shared), a wrap panel, cairo text through Pango, textures drawn by cairo, the class glyph and icon tiles.
/// </summary>
internal static class FightKit
{
    private static Gtk.CssProvider? _css;
    private static double _cssScale = -1;

    /// <summary>The k-fight-* rules as a second display provider, scaled like the card's sheet; reloaded when a view is built at a new scale
    /// (the card recreates its views on a rescale).</summary>
    public static void LoadCss(double scale)
    {
        if (_css is not null && Math.Abs(scale - _cssScale) < 0.001) return;
        if (_css is null)
        {
            _css = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, _css, 800);
        }
        _cssScale = scale;
        _css.LoadFromString(CardText.ScaleCss(Sheet(), scale));
    }

    /// <summary>Unscaled; every selector under .k-root, every class k-fight-.</summary>
    internal static string Sheet() => $$"""
        .k-fight-top { margin: 0 16px; }
        .k-fight-tab { padding: 4px 8px 6px 8px; margin-right: 2px; border-bottom: 2px solid transparent; }
        .k-fight-tab.on { border-bottom-color: {{Palette.Hex(Palette.Gold)}}; }
        .k-root .k-fight-tab label { color: {{Palette.Hex(Palette.TextDim)}}; font-size: 12.5px; }
        .k-root .k-fight-tab:hover label { color: {{Palette.Hex(Palette.Text)}}; }
        .k-root .k-fight-tab.on label { color: {{Palette.Hex(Palette.GoldBright)}}; font-weight: 600; }
        .k-fight-row { min-height: 26px; border-radius: 4px; }
        .k-fight-row:hover { background-color: {{Palette.Rgba(Palette.HoverFill)}}; }
        .k-root label.k-fight-num { font-feature-settings: "tnum"; }
        .k-root label.k-fight-th { font-size: 10.5px; font-weight: 600; color: {{Palette.Hex(Palette.TextMute)}}; padding: 2px 0; font-feature-settings: "tnum"; }
        .k-root label.k-fight-th.on { color: {{Palette.Hex(Palette.Gold)}}; }
        .k-root label.k-fight-th:hover { color: {{Palette.Hex(Palette.GoldBright)}}; }
        .k-tile.k-fight-r1 { background-image: linear-gradient(to bottom, #F6DB8E, #C99A45); border-color: #FFEBB0; }
        .k-tile.k-fight-r2 { background-image: linear-gradient(to bottom, #EEF2F7, #A7B2C1); border-color: #FFFFFF; }
        .k-tile.k-fight-r3 { background-image: linear-gradient(to bottom, #E9A87A, #A8633B); border-color: #F5C39E; }
        .k-root .k-tile.k-fight-r1 label { color: #1A140A; }
        .k-root .k-tile.k-fight-r2 label { color: #12161F; }
        .k-root .k-tile.k-fight-r3 label { color: #1A0F08; }
        .k-fight-card { background-color: {{Palette.Rgba(Palette.SectionFill)}}; border: 1px solid {{Palette.Rgba(Palette.SectionLine)}}; border-radius: 8px; }
        .k-fight-companion { background-color: {{Palette.Rgba(Palette.HoverFill)}}; border: 1px solid {{Palette.Hex(Palette.Line)}}; border-radius: 6px; padding: 4px 8px; margin: 0 8px 4px 0; }
        .k-fight-chip { background-color: {{Palette.Rgba(Palette.HoverFill)}}; border: 1px solid {{Palette.Hex(Palette.Line)}}; border-radius: 5px; padding: 2px 7px 3px 7px; margin: 0 6px 6px 0; }
        .k-fight-groupcap { border-left: 2px solid {{Palette.Hex(Palette.GoldDim)}}; padding-left: 7px; margin: 0 6px 8px 0; }
        """;

    // ------------------------------------------------------------ text in cairo

    private static readonly Dictionary<long, Pango.FontDescription> Fonts = new();

    /// <summary>The kit's UI font at <paramref name="px"/> device-independent px (already scaled by the caller).</summary>
    public static Pango.FontDescription Font(double px, bool bold = false)
    {
        var key = (long)Math.Round(px * 100) * 2 + (bold ? 1 : 0);
        if (Fonts.TryGetValue(key, out var f)) return f;
        f = Pango.FontDescription.FromString("Inter, Noto Sans, DejaVu Sans");
        f.SetAbsoluteSize(px * 1024);   // PANGO_SCALE
        if (bold) f.SetWeight(Pango.Weight.Bold);
        Fonts[key] = f;
        return f;
    }

    /// <summary>One Pango layout per draw call, reused for every label of that draw.</summary>
    public static Pango.Layout Layout(Cairo.Context cr) => PangoCairo.Functions.CreateLayout(cr);

    /// <summary>Sets text + font and returns the pixel size.</summary>
    public static (int W, int H) Measure(Pango.Layout l, string text, Pango.FontDescription font)
    {
        l.SetFontDescription(font);
        l.SetText(text, -1);
        l.GetPixelSize(out var w, out var h);
        return (w, h);
    }

    /// <summary>Draws the layout as last measured at (x, y) in <paramref name="rgb"/>.</summary>
    public static void Show(Cairo.Context cr, Pango.Layout l, double x, double y, uint rgb, double alpha = 1)
    {
        Paint.Rgb(cr, rgb, alpha);
        cr.MoveTo(x, y);
        PangoCairo.Functions.ShowLayout(cr, l);
    }

    // ------------------------------------------------------------ textures in cairo

    private static readonly Dictionary<Gdk.Texture, GdkPixbuf.Pixbuf?> Pixbufs = new();

    /// <summary>A pixbuf for cairo (IconCache keeps one texture per icon, so this map is bounded by the icons on disk).</summary>
    public static GdkPixbuf.Pixbuf? PixbufOf(Gdk.Texture t)
    {
        if (Pixbufs.TryGetValue(t, out var pb)) return pb;
        try { pb = Gdk.Functions.PixbufGetFromTexture(t); }
        catch (Exception e) when (e is GLib.GException or InvalidOperationException) { pb = null; }
        Pixbufs[t] = pb;
        return pb;
    }

    /// <summary>The texture "uniform to fill" into a rounded box (WPF Image Stretch=UniformToFill in a clipping border).</summary>
    public static void DrawTexture(Cairo.Context cr, Gdk.Texture t, double x, double y, double w, double h, double radius, double alpha = 1)
    {
        if (PixbufOf(t) is not { } pb) return;
        double pw = pb.GetWidth(), ph = pb.GetHeight();
        if (pw <= 0 || ph <= 0) return;
        var s = Math.Max(w / pw, h / ph);
        cr.Save();
        Paint.RoundRect(cr, x, y, w, h, radius);
        cr.Clip();
        cr.Translate(x + (w - pw * s) / 2, y + (h - ph * s) / 2);
        cr.Scale(s, s);
        Gdk.Functions.CairoSetSourcePixbuf(cr, pb, 0, 0);
        if (alpha >= 1) cr.Paint(); else cr.PaintWithAlpha(alpha);
        cr.Restore();
    }

    // ------------------------------------------------------------ class emblems

    private static readonly string?[] EmblemPaths = new string?[256];
    private static readonly bool[] EmblemLooked = new bool[256];

    /// <summary>Assets/Classes/&lt;class&gt;.png next to the binary (the card's RowWidget files), null for Unknown or a missing file.</summary>
    public static string? Emblem(GameClass c)
    {
        if (c == GameClass.Unknown) return null;
        if (EmblemLooked[(byte)c]) return EmblemPaths[(byte)c];
        EmblemLooked[(byte)c] = true;
        var p = Path.Combine(AppContext.BaseDirectory, "Assets", "Classes", c.ToString().ToLowerInvariant() + ".png");
        return EmblemPaths[(byte)c] = File.Exists(p) ? p : null;
    }

    /// <summary>Sets a label's text only when it changed (GTK re-lays out on every set_text).</summary>
    public static void Set(Gtk.Label l, string text)
    {
        if (!string.Equals(l.GetLabel(), text, StringComparison.Ordinal)) l.SetText(text);
    }

    /// <summary>A one-line label whose natural width is one char (a layer surface takes its content's natural width): ellipsized, hexpands.</summary>
    public static Gtk.Label Cell(string text, double size, Fg fg, float xalign, int weight = 400)
    {
        var l = UiKit.Line(text, size, fg, weight);
        l.Xalign = xalign;
        return l;
    }

    /// <summary>Section card of the profile pages (ProfilePages.Card): faint fill, hairline, 8 px corners, padding per side.</summary>
    public static Gtk.Box Card(Gtk.Widget child, double left = 10, double top = 10, double right = 10, double bottom = 10)
    {
        var b = UiKit.VBox();
        b.AddCssClass("k-fight-card");
        child.MarginStart = UiKit.Px(left); child.MarginTop = UiKit.Px(top); child.MarginEnd = UiKit.Px(right); child.MarginBottom = UiKit.Px(bottom);
        b.Append(child);
        return b;
    }

    /// <summary>Pango markup span in an RGB colour.</summary>
    public static string Span(string text, uint rgb, string? extra = null) =>
        "<span foreground=\"" + Palette.Hex(rgb) + "\"" + (extra is null ? "" : " " + extra) + ">" + GLib.Functions.MarkupEscapeText(text, -1) + "</span>";
}

/// <summary>
/// A wrap panel (Avalonia WrapPanel) for the fight pages: children go into lines by their natural widths once the box has a width. A 0-px
/// probe reports width changes; the relayout is posted to the next loop turn (never inside an allocation). Until the first allocation every
/// child sits on its own line, so the natural width is the widest child and the card never grows past its budget.
/// </summary>
internal sealed class WrapBox
{
    private readonly List<Gtk.Widget> _children = new();
    private readonly List<Gtk.Box> _lines = new();
    private int[] _lineOf = [];
    private int _width = -1;
    private uint _posted;
    public Gtk.Box Widget { get; }

    public WrapBox()
    {
        Widget = UiKit.VBox();
        var probe = Gtk.DrawingArea.New();
        probe.Hexpand = true;
        probe.SetSizeRequest(-1, 0);
        probe.OnResize += (_, e) =>
        {
            if (e.Width == _width) return;
            _width = e.Width;
            if (_posted == 0) _posted = UiLoop.Delay(0, () => { _posted = 0; Relayout(); });
        };
        Widget.Append(probe);
    }

    public int Count => _children.Count;

    public void Add(Gtk.Widget w)
    {
        _children.Add(w);
        var line = NewLine();
        line.Append(w);
        Array.Resize(ref _lineOf, _children.Count);
        _lineOf[^1] = _lines.Count - 1;
    }

    private Gtk.Box NewLine()
    {
        var l = UiKit.HBox();
        l.Hexpand = false;
        l.Halign = Gtk.Align.Start;
        _lines.Add(l);
        Widget.Append(l);
        return l;
    }

    private void Relayout()
    {
        if (_width <= 0 || _children.Count == 0) return;
        Span<int> line = _children.Count <= 512 ? stackalloc int[_children.Count] : new int[_children.Count];
        int x = 0, l = 0;
        for (var i = 0; i < _children.Count; i++)
        {
            _children[i].Measure(Gtk.Orientation.Horizontal, -1, out _, out var nat, out _, out _);
            if (x > 0 && x + nat > _width) { l++; x = 0; }
            line[i] = l;
            x += nat;
        }
        var same = true;
        for (var i = 0; same && i < line.Length; i++) same = _lineOf[i] == line[i];
        if (same) return;
        for (var i = 0; i < _children.Count; i++) if (_children[i].GetParent() is Gtk.Box p) p.Remove(_children[i]);
        while (_lines.Count <= l) NewLine();
        for (var i = 0; i < _children.Count; i++) { _lines[line[i]].Append(_children[i]); _lineOf[i] = line[i]; }
        for (var i = 0; i < _lines.Count; i++) _lines[i].Visible = i <= l;
    }
}

/// <summary>Avalonia ClassGlyph: dark disc with a faint ring, the class emblem inside (a "?" in the class colour when there is none).</summary>
internal sealed class ClassGlyph
{
    private GameClass _class = (GameClass)255;
    private readonly Gtk.Image _emblem;
    private readonly IconView _unknown;
    public Gtk.Overlay Widget { get; }

    public ClassGlyph(double size)
    {
        var px = UiKit.Px(size);
        var disc = Gtk.DrawingArea.New();
        disc.SetSizeRequest(px, px);
        disc.SetDrawFunc((_, cr, w, h) =>
        {
            var r = Math.Min(w, h) / 2.0;
            cr.Arc(w / 2.0, h / 2.0, r - 0.5, 0, 2 * Math.PI);
            Paint.Argb(cr, 0xB4070910);
            cr.FillPreserve();
            Paint.Argb(cr, 0x55FFFFFF);
            cr.LineWidth = 1;
            cr.Stroke();
        });
        Widget = Gtk.Overlay.New();
        Widget.SetChild(disc);
        Widget.Valign = Gtk.Align.Center;
        Widget.Halign = Gtk.Align.Start;
        _emblem = Gtk.Image.New();
        _emblem.PixelSize = (int)Math.Round(px * 0.84);
        _emblem.Halign = Gtk.Align.Center;
        _emblem.Valign = Gtk.Align.Center;
        Widget.AddOverlay(_emblem);
        _unknown = Icons.View(Icons.Unknown, Palette.TextDim, (int)Math.Round(size * 0.6), 1.8);
        _unknown.Widget.Halign = Gtk.Align.Center;
        _unknown.Widget.Valign = Gtk.Align.Center;
        Widget.AddOverlay(_unknown.Widget);
    }

    public GameClass Class
    {
        get => _class;
        set
        {
            if (_class == value) return;
            _class = value;
            var path = FightKit.Emblem(value);
            _emblem.Visible = path is not null;
            _unknown.Widget.Visible = path is null;
            if (path is not null) _emblem.SetFromFile(path);
            _unknown.Rgb = OverlayPalette.ClassColor(value);
        }
    }
}

/// <summary>
/// A 44 px icon tile drawn with cairo (ProfilePages GearTile / SkillCell, the docs/33 ItemTile rule): frame, the icon asked for on draw,
/// a badge (enchant top-left, level bottom-right) or the slot label when empty. <see cref="Missing"/> is true while the icon is not on disk:
/// the page redraws those on IconProvider.Loaded.
/// </summary>
internal sealed class IconTile
{
    private readonly Func<Gdk.Texture?>? _icon;
    private readonly uint _line, _fill;
    private readonly double _lineWidth, _alpha;
    private readonly string? _badge, _empty;
    private readonly bool _badgeBottomRight;
    public Gtk.DrawingArea Widget { get; }
    public bool Missing { get; private set; }

    public IconTile(double size, Func<Gdk.Texture?>? icon, uint line, double lineWidth, string? badge, bool badgeBottomRight, string? empty, double alpha = 1, uint fill = 0x161C2C)
    {
        _icon = icon;
        _line = line;
        _lineWidth = lineWidth;
        _badge = badge;
        _badgeBottomRight = badgeBottomRight;
        _empty = empty;
        _alpha = alpha;
        _fill = fill;
        Missing = icon is not null;
        Widget = Gtk.DrawingArea.New();
        Widget.SetSizeRequest(UiKit.Px(size), UiKit.Px(size));
        Widget.Hexpand = false;
        Widget.Halign = Gtk.Align.Start;
        Widget.Valign = Gtk.Align.Start;
        Widget.SetDrawFunc(Draw);
    }

    public void Retry() { if (Missing) Widget.QueueDraw(); }

    private void Draw(Gtk.DrawingArea area, Cairo.Context cr, int w, int h)
    {
        var k = Icons.Scale;
        var r = 6 * k;
        var inset = _lineWidth * k / 2;
        Paint.RoundRect(cr, inset, inset, w - 2 * inset, h - 2 * inset, r);
        Paint.Rgb(cr, _fill, _alpha);
        cr.Fill();
        if (_icon is not null)
        {
            if (_icon() is { } tex) { Missing = false; FightKit.DrawTexture(cr, tex, inset, inset, w - 2 * inset, h - 2 * inset, r, _alpha); }
            else Missing = true;
        }
        var l = _empty is not null || _badge is not null ? FightKit.Layout(cr) : null;
        if (_empty is not null && l is not null)
        {
            l.SetWidth((int)((w - 4 * k) * 1024));
            l.SetAlignment(Pango.Alignment.Center);
            l.SetWrap(Pango.WrapMode.WordChar);
            var (_, th) = FightKit.Measure(l, _empty, FightKit.Font(8.5 * k));
            FightKit.Show(cr, l, 2 * k, (h - th) / 2.0, Palette.TextMute);
            l.SetWidth(-1);
        }
        Paint.RoundRect(cr, inset, inset, w - 2 * inset, h - 2 * inset, r);
        Paint.Rgb(cr, _line, _alpha);
        cr.LineWidth = _lineWidth * k;
        cr.Stroke();
        if (_badge is not null && l is not null)
        {
            var (tw, th) = FightKit.Measure(l, _badge, FightKit.Font(10 * k, true));
            double bw = tw + 6 * k, bh = th;
            double bx = _badgeBottomRight ? w - bw - inset : inset, by = _badgeBottomRight ? h - bh - inset : inset;
            if (_badgeBottomRight) Paint.RoundRect(cr, bx, by, bw, bh, 4 * k, 0, r, 0);
            else Paint.RoundRect(cr, bx, by, bw, bh, r, 0, 4 * k, 0);
            Paint.Argb(cr, 0xCC000000);
            cr.Fill();
            FightKit.Show(cr, l, bx + 3 * k, by, Palette.GoldBright);
        }
    }
}
