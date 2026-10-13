using System.Globalization;
using System.Text;
using AionMeter.Core.Builds;
using AionMeter.Overlay.Shell;

namespace AionMeter.Overlay.Views;

/// <summary>
/// The Build tab's questlog greys (WPF BuildView resources: docs/mockups/build-tab, questlog's planner CSS) as cairo values and as the
/// tab's own stylesheet. The sheet is a second display-wide provider (KitCss stays the shared kit's): every selector under `.k-root`,
/// every class `k-build-`, scaled like the card's sheet and reloaded when a view is built at another UI scale (the card rebuilds views then).
/// </summary>
internal static class BuildColors
{
    public const uint Text = 0xFAFAFA, Dim = 0xAFAFAF, Mute = 0x7D7D7D, Blue = 0x65B0FC, Line = 0x2B2B2B, Line2 = 0x373737, Btn = 0x2B2B2B,
        Band = 0x1F1F1F, Amber = 0xF5B544, Green = 0x3DDC84, Bad = 0xFF5C5C, Num = 0xFCC78B, Purple = 0xA979CB, KeyBack = 0x1F1F1F, KeyOn = 0x2F5F8F,
        KeyText = 0xDCDCDC, Cell = 0x1A1A1A, Marker = 0x3A3A3A, Gt = 0x555555, AddLine = 0x444444, AddText = 0x888888, TipBack = 0x141414,
        Field = 0x1A1A1A, Hover = 0x5A5A5A, BoardBack = 0x111111, RowBack = 0x141414, BdOnBack = 0x333333, BdOnLine = 0x555555, SelectAll = 0x3B8BE0;
    /// <summary>ARGB: the card fill, the chosen spec row.</summary>
    public const uint Card = 0xCC1F1F1F, SpecOnBack = 0x1A65B0FC, SpecOnLine = 0x5965B0FC;

    /// <summary>Text colours of the tab (k-build-c-*), set with <see cref="BuildUi.Fg"/>.</summary>
    public enum C : byte { Text, Dim, Mute, Blue, Amber, Green, Bad, Purple, Num, White, Black, Gt, AddText, KeyText }

    private static readonly uint[] CRgb = [Text, Dim, Mute, Blue, Amber, Green, Bad, Purple, Num, 0xFFFFFF, 0x000000, Gt, AddText, KeyText];
    private static readonly string[] CClass = ["k-build-c-text", "k-build-c-dim", "k-build-c-mute", "k-build-c-blue", "k-build-c-amber", "k-build-c-green",
        "k-build-c-bad", "k-build-c-purple", "k-build-c-num", "k-build-c-white", "k-build-c-black", "k-build-c-gt", "k-build-c-add", "k-build-c-key"];
    public static uint Rgb(C c) => CRgb[(int)c];
    public static string Class(C c) => CClass[(int)c];
    internal static ReadOnlySpan<string> Classes => CClass;

    private static Gtk.CssProvider? _css;
    private static double _cssScale = -1;

    /// <summary>Loads (or rescales) the tab's sheet; called by every Build view / import dialog constructor.</summary>
    public static void EnsureCss()
    {
        var scale = Icons.Scale;
        if (_css is not null && Math.Abs(scale - _cssScale) < 0.001) return;
        if (_css is null)
        {
            if (Gdk.Display.GetDefault() is not { } display) return;
            _css = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(display, _css, 801);   // just above the card's provider (800): the tab's rules win ties
        }
        _cssScale = scale;
        _css.LoadFromString(CardText.ScaleCss(Sheet(), scale));
    }

    /// <summary>The stylesheet (display-free: tested for its prefixes).</summary>
    public static string Sheet()
    {
        static string H(uint rgb) => Palette.Hex(rgb);
        static string A(uint argb) => Palette.Rgba(argb);
        var sb = new StringBuilder(8 * 1024);
        sb.Append($$"""
        .k-root .k-build-card { background-color: {{A(Card)}}; border: 1px solid {{H(Line)}}; border-radius: 8px; margin-bottom: 10px; }
        .k-root .k-build-band { background-color: {{H(Band)}}; border-radius: 8px 8px 0 0; padding: 8px 12px; }
        .k-root label.k-build-bandtitle { font-size: 14px; font-weight: 500; color: {{H(Text)}}; }
        .k-root label.k-build-lbl { font-size: 11px; color: {{H(Dim)}}; }
        .k-root .k-build-flat { all: unset; background-color: {{H(Btn)}}; border: 1px solid {{H(Line2)}}; border-radius: 6px; padding: 5px 10px; }
        .k-root .k-build-flat:hover { border-color: {{H(Hover)}}; }
        .k-root .k-build-flat:disabled { opacity: 0.45; }
        .k-root .k-build-flat label { color: {{H(Text)}}; font-size: 12.5px; }
        .k-root .k-build-flat.k-build-big { padding: 8px 12px; }
        .k-root .k-build-flat.k-build-big label { font-size: 13px; }
        .k-root .k-build-flat.k-build-selectall { background-color: {{H(SelectAll)}}; border-color: {{H(SelectAll)}}; }
        .k-root .k-build-textbtn { all: unset; padding: 0 2px; }
        .k-root .k-build-textbtn label { color: {{H(Mute)}}; font-size: 12px; }
        .k-root .k-build-textbtn.k-build-blue label { color: {{H(Blue)}}; }
        .k-root .k-build-textbtn.k-build-add label { color: {{H(AddText)}}; font-size: 13px; }
        .k-root .k-build-textbtn.k-build-s13 label { font-size: 13px; }
        .k-root .k-build-textbtn:hover label { color: {{H(Text)}}; }
        .k-root label.k-build-links link { color: {{H(Blue)}}; }
        .k-root label.k-build-links link:hover { color: {{H(Text)}}; }
        .k-root .k-build-subtab { all: unset; padding: 6px 12px; border-radius: 6px; border: 1px solid transparent; margin-right: 6px; }
        .k-root .k-build-subtab label { font-size: 13px; color: {{H(Dim)}}; }
        .k-root .k-build-subtab.k-build-on { background-color: {{H(Btn)}}; border-color: {{H(Line2)}}; }
        .k-root .k-build-subtab.k-build-on label { color: {{H(Text)}}; }
        .k-root .k-build-tip { background-color: {{H(TipBack)}}; border: 1px solid {{H(Line2)}}; border-radius: 8px; padding: 10px; }
        .k-root label.k-build-tipsection { font-size: 11px; color: {{H(Mute)}}; margin: 4px 0 3px 0; }
        .k-root .k-build-tag { background-color: {{H(Btn)}}; border-radius: 4px; padding: 1px 7px; margin: 0 4px 2px 0; }
        .k-root .k-build-tag label { font-size: 11px; color: {{H(Dim)}}; }
        .k-root .k-build-cmp { background-color: {{H(Band)}}; border-radius: 6px; padding: 6px 8px; margin-bottom: 8px; }
        .k-root .k-build-spec { border-radius: 6px; border: 1px solid transparent; padding: 7px 10px; margin-bottom: 4px; }
        .k-root .k-build-spec.k-build-on { background-color: {{A(SpecOnBack)}}; border-color: {{A(SpecOnLine)}}; }
        .k-root .k-build-spec.k-build-locked { opacity: 0.45; }
        .k-root .k-build-marker { min-width: 26px; min-height: 26px; border-radius: 5px; background-color: {{H(Marker)}}; }
        .k-root .k-build-marker.k-build-on { background-color: {{H(Blue)}}; }
        .k-root .k-build-marker label { font-size: 12px; color: #CCCCCC; }
        .k-root .k-build-marker.k-build-on label { color: #000000; }
        .k-root .k-build-macro { background-color: {{H(Btn)}}; border-radius: 6px; padding: 3px 4px 3px 10px; margin: 0 0 6px 0; }
        .k-root .k-build-count { background-color: {{H(Marker)}}; border-radius: 4px; padding: 0 6px; margin-left: 8px; }
        .k-root textview.k-build-note > text { background-color: {{H(Field)}}; border-color: {{H(Line2)}}; color: {{H(Text)}}; padding: 5px 6px; }
        .k-root entry.k-build-field { background-color: {{H(Band)}}; border-color: {{H(Line2)}}; }
        .k-root .k-build-board { background-color: {{H(BoardBack)}}; border: 1px solid {{H(Line)}}; border-radius: 8px; padding: 10px; margin: 0 12px; }
        .k-root .k-build-bd { all: unset; background-color: {{H(Btn)}}; border: 1px solid {{H(Line2)}}; border-radius: 6px; padding: 6px 8px; }
        .k-root .k-build-bd.k-build-on { background-color: {{H(BdOnBack)}}; border-color: {{H(BdOnLine)}}; }
        .k-root .k-build-statrow { background-color: {{H(RowBack)}}; border-radius: 4px; padding: 4px 12px; margin: 2px 8px; }
        .k-root .k-build-stathead { padding: 8px 12px 0 12px; margin: 0 8px; }
        .k-root .k-build-row { background-color: {{H(Bg1)}}; border: 1px solid {{H(Palette.Line)}}; border-radius: 6px; padding: 7px 10px; margin-bottom: 6px; }
        .k-root .k-build-row.k-build-ql { background-color: {{H(Band)}}; border-color: {{H(Btn)}}; }
        .k-root .k-build-rowtag { background-color: {{H(Bg2)}}; border-radius: 4px; padding: 0 6px 1px 6px; margin: 0 4px 2px 0; }
        .k-root .k-build-row.k-build-ql .k-build-rowtag { background-color: {{H(Btn)}}; }
        .k-root .k-build-rowtag label { font-size: 10.5px; color: {{H(Palette.TextMute)}}; }
        .k-root .k-build-row.k-build-ql .k-build-rowtag label { color: {{H(Mute)}}; }
        .k-root .k-build-sep { background-color: {{H(Line)}}; min-height: 1px; }
        .k-root .k-build-topline { border-top: 1px solid {{H(Palette.Line)}}; }
        .k-root .k-build-menuitem { all: unset; padding: 4px 10px; border-radius: 5px; }
        .k-root .k-build-menuitem:hover { background-color: rgba(201,164,92,0.18); }
        .k-root .k-build-menuitem:disabled label { color: {{H(Mute)}}; }
        .k-root .k-build-menuitem label { font-size: 12.5px; color: {{H(Text)}}; }
        .k-root dropdown.k-build-combo > button { min-height: 26px; }
        .k-root dropdown.k-build-combo label { font-size: 12px; }
        .k-root label.k-build-italic { font-style: italic; }
        """);
        sb.Append('\n');
        for (var i = 0; i < CClass.Length; i++)
            sb.Append(".k-root label.").Append(CClass[i]).Append(", .k-root .").Append(CClass[i]).Append(" label { color: ").Append(H(CRgb[i])).Append("; }\n");
        return sb.ToString();
    }

    private const uint Bg1 = Palette.Bg1, Bg2 = Palette.Bg2;
}

/// <summary>What one skill tile shows (the Avalonia SkillTile.Set arguments); the views keep one per tile and change it in place.</summary>
internal sealed class TileModel
{
    public enum Look : byte { Normal, Off, Done }
    public int SkillId;
    public Gdk.Texture? Icon;
    public string Sub = "active";
    public Look State;
    public int Diamonds;
    public bool Selected, Badges = true;
    public string? Level, Current;
    public uint CurrentRgb;
}

/// <summary>
/// questlog's 48 px skill tile drawn with cairo (WPF Controls/SkillTile, the Avalonia SkillTile.Render): type-coloured frame, build level
/// bottom-right, the character's level bottom-left, one diamond per chosen spec, the selection outline. Laid out at 48 and scaled.
/// </summary>
internal static class SkillTilePainter
{
    // 7 px squares rotated 45° (half diagonal 4.95) at left 3 / top 3, centres 9 px apart (the mock's flex row)
    private const double DiamondR = 4.95;

    public static void Draw(Cairo.Context cr, TileModel m, double x, double y, double size)
    {
        cr.Save();
        cr.Translate(x, y);
        var k = size / 48;
        if (Math.Abs(k - 1) > 0.001) cr.Scale(k, k);
        var done = m.State == TileModel.Look.Done;
        if (done) cr.PushGroup();
        cr.Save();
        Paint.RoundRect(cr, 0, 0, 48, 48, 6);
        cr.Clip();
        Paint.Rgb(cr, 0x0D0D0D);
        cr.Paint();
        // mock CSS: grayscale(1) brightness(.55) for "not in the build", grayscale at .28 opacity (whole tile) for "done"
        if (m.Icon is not null) TexSurf.Draw(cr, m.Icon, 0, 0, 48, 48, m.State != TileModel.Look.Normal, m.State == TileModel.Look.Off ? 0.55 : 1);
        cr.Restore();
        Paint.RoundRect(cr, 0.4, 0.4, 47.2, 47.2, 5.6);
        Paint.Argb(cr, m.State == TileModel.Look.Off ? 0xFF3A3A3A : m.Sub == "stigma" ? 0xB3A979CB : m.Sub == "passive" ? 0xB3EA584C : 0xB365B0FC);
        cr.LineWidth = 0.8;
        cr.Stroke();
        var n = Math.Min(m.Diamonds, 4);
        for (var i = 0; i < n; i++)
        {
            double cx = 6.5 + i * 9, cy = 6.5;
            cr.NewPath();
            cr.MoveTo(cx, cy - DiamondR); cr.LineTo(cx + DiamondR, cy); cr.LineTo(cx, cy + DiamondR); cr.LineTo(cx - DiamondR, cy); cr.ClosePath();
            Paint.Rgb(cr, BuildColors.Blue);
            cr.FillPreserve();
            Paint.Argb(cr, 0xB0000000);
            cr.LineWidth = 0.8;
            cr.Stroke();
        }
        if (m.Badges && m.State != TileModel.Look.Off)
        {
            if (m.Level is { Length: > 0 } lv) Badge(cr, lv, 11, 0xFFFFFF, 0xCC000000, 21, 16, right: true);
            if (m.Current is { Length: > 0 } cur) Badge(cr, cur, 10, m.CurrentRgb, 0xD9000000, 16, 14, right: false);
        }
        if (m.Selected)
        {
            Paint.RoundRect(cr, -2, -2, 52, 52, 8);
            Paint.Rgb(cr, BuildColors.Blue);
            cr.LineWidth = 2;
            cr.Stroke();
        }
        if (done) { cr.PopGroupToSource(); cr.PaintWithAlpha(0.28); }
        cr.Restore();
    }

    /// <summary>Badge with one rounded corner (6 px): top-left for the build level (bottom-right of the tile), top-right for the current level.</summary>
    private static void Badge(Cairo.Context cr, string text, double fontSize, uint fg, uint back, double minW, double h, bool right)
    {
        cr.SelectFontFace("Inter", Cairo.FontSlant.Normal, Cairo.FontWeight.Bold);
        cr.SetFontSize(fontSize);
        cr.TextExtents(text, out var ext);
        var bw = Math.Max(minW, ext.XAdvance + 6);
        double x = right ? 49 - bw : -1, y = right ? 33 : 35;
        if (right) Paint.RoundRect(cr, x, y, bw, h, 6, 0, 0, 0);
        else Paint.RoundRect(cr, x, y, bw, h, 0, 6, 0, 0);
        Paint.Argb(cr, back);
        cr.Fill();
        Paint.Rgb(cr, fg);
        cr.MoveTo(x + (bw - ext.XAdvance) / 2, y + (h - ext.Height) / 2 - ext.YBearing);
        cr.ShowText(text);
    }
}

/// <summary>One Daevanion node, 32 px (WPF Controls/NodeTile, the Avalonia NodeTile.Render) drawn with cairo.</summary>
internal static class NodeTilePainter
{
    public const double Size = 32;

    public static void Draw(Cairo.Context cr, DaevNode node, bool open, bool planned, bool offPlan, bool dim, Gdk.Texture? sprite, Gdk.Texture? icon, double x, double y)
    {
        cr.Save();
        cr.Translate(x, y);
        if (dim) cr.PushGroup();
        if (open)
        {
            Paint.RoundRect(cr, 0, 0, Size, Size, 4);
            Paint.Argb(cr, node.Grade switch { "Unique" => 0x4DD3AD0F, "Rare" => 0x4D3C78C8, "Legend" => 0x4DA050C8, _ => 0x4D808080 });
            cr.Fill();
        }
        if (sprite is not null) TexSurf.Draw(cr, sprite, 0, 0, Size, Size);
        if (icon is not null)
        {
            cr.Save();
            Paint.RoundRect(cr, 5, 5, Size - 10, Size - 10, 3);
            cr.Clip();
            // mock: grayscale(1) brightness(.5) while closed
            TexSurf.Draw(cr, icon, 5, 5, Size - 10, Size - 10, !open, open ? 1 : 0.5);
            cr.Restore();
        }
        if (planned || offPlan)
        {
            Paint.RoundRect(cr, 0.75, 0.75, Size - 1.5, Size - 1.5, 3.25);
            Paint.Argb(cr, planned ? (open ? 0x663DDC84u : 0xBF3DDC84u) : 0xB3FF5C5Cu);
            cr.LineWidth = planned && open ? 1.0 : 1.5;
            cr.Stroke();
        }
        if (dim) { cr.PopGroupToSource(); cr.PaintWithAlpha(0.22); }
        cr.Restore();
    }
}

/// <summary>Layout maths of the Build tab's DrawingAreas, in unscaled px (display-free: tested without GTK).</summary>
internal static class BuildGeometry
{
    public const double Tile = 48, Step = 54;   // 48 px cells, 6 px gap (questlog's bar)
    public const double KeyRowGap = 6, KeyRowHeight = 18;
    public static double BarWidth => BuildEditor.Keys * Step - 6;
    public static double BarHeight => BuildEditor.Rows * Step - 6 + KeyRowGap + KeyRowHeight;
    public const double NodeCell = NodeTilePainter.Size + 2;   // mock .board: 32 px cells, 2 px gap

    /// <summary>Lays items of <paramref name="widths"/> left to right, wrapping at <paramref name="avail"/> (each item takes its width,
    /// rows <paramref name="rowHeight"/> apart; the Avalonia WrapPanel of fixed-height items). Writes the origins; returns the total height.</summary>
    public static double Flow(ReadOnlySpan<double> widths, double avail, double rowHeight, Span<(double X, double Y)> at)
    {
        double x = 0, y = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            if (x > 0 && x + widths[i] > avail + 0.01) { x = 0; y += rowHeight; }
            at[i] = (x, y);
            x += widths[i];
        }
        return widths.Length == 0 ? 0 : y + rowHeight;
    }

    /// <summary>The bar's scale at a width: the 642 px bar shrinks to fit the card (Avalonia Viewbox DownOnly), never grows.</summary>
    public static double BarScale(double avail) => avail <= 0 ? 1 : Math.Min(1, avail / BarWidth);

    /// <summary>Top-left of the cell of (key 1..12, row 0..3): row 0 (the first chain link) at the bottom, as questlog.</summary>
    public static (double X, double Y) Cell(int key, int row) => ((key - 1) * Step, (BuildEditor.Rows - 1 - row) * Step);

    public enum BarHit : byte { None, Cell, Key }

    /// <summary>What (x, y) in unscaled bar px hits: a cell (key, row) or a key label (key); gaps hit nothing.</summary>
    public static BarHit HitBar(double x, double y, out int key, out int row)
    {
        key = 0; row = 0;
        if (x < 0 || y < 0) return BarHit.None;
        var col = (int)(x / Step);
        if (col >= BuildEditor.Keys || x - col * Step > Tile) return BarHit.None;
        key = col + 1;
        var gridH = BuildEditor.Rows * Step - 6;
        if (y < gridH)
        {
            var r = (int)(y / Step);
            if (y - r * Step > Tile) return BarHit.None;
            row = BuildEditor.Rows - 1 - r;
            return BarHit.Cell;
        }
        var ky = y - gridH - KeyRowGap;
        return ky >= 0 && ky <= KeyRowHeight ? BarHit.Key : BarHit.None;
    }

    /// <summary>A board's size in unscaled px, cropped to the box of its nodes (not 15×15 with empty margins).</summary>
    public static (double W, double H) Board(int minCol, int maxCol, int minRow, int maxRow) =>
        ((maxCol - minCol + 1) * NodeCell - 2, (maxRow - minRow + 1) * NodeCell - 2);

    /// <summary>The node cell under (x, y) in unscaled board px, or false in a gap.</summary>
    public static bool HitNode(double x, double y, out int col, out int row)
    {
        col = (int)Math.Floor(x / NodeCell); row = (int)Math.Floor(y / NodeCell);
        return x >= 0 && y >= 0 && x - col * NodeCell <= NodeTilePainter.Size && y - row * NodeCell <= NodeTilePainter.Size;
    }
}

/// <summary>
/// A wrapping row of skill tiles (library rows, priority chains) in ONE DrawingArea: tiles are models redrawn in place, hit-tested by
/// position; the height follows the width (a wrap panel). Entries can also be the priority chain's "›" and the dashed "+" cell.
/// </summary>
internal sealed class TileWrap
{
    public enum Kind : byte { Tile, Sep, Add }
    private const double Pad = 3;   // the selection outline sits 2 px outside a tile, the current badge 1 px
    private readonly double _gap;
    private readonly List<(Kind Kind, TileModel? Tile)> _items = new();
    private (double X, double Y)[] _at = new (double, double)[16];
    private double[] _w = new double[16];
    private double _laidFor = -1;
    private int _tipIndex = -1, _tipGen = -1, _gen;
    private Gtk.Widget? _tip;

    public Gtk.DrawingArea Area { get; } = Gtk.DrawingArea.New();
    /// <summary>(index, n-press, button 1/3, x, y in widget px)</summary>
    public Action<int, int, uint, double, double>? Pressed;
    /// <summary>The tooltip of the entry at an index (built when it opens, reused while the pointer stays on it).</summary>
    public Func<int, Gtk.Widget?>? Tip;

    public TileWrap(double gap)
    {
        _gap = gap;
        Area.Hexpand = true;
        Area.ContentHeight = UiKit.Px(Pad * 2);
        Area.SetDrawFunc((_, cr, w, h) => Draw(cr, w));
        Area.OnResize += (_, e) => Relayout(e.Width);
        Area.SetCursorFromName("pointer");
        var click = Gtk.GestureClick.New();
        click.Button = 0;
        click.OnPressed += (g, e) =>
        {
            var i = Hit(e.X, e.Y);
            if (i >= 0) Pressed?.Invoke(i, e.NPress, g.GetCurrentButton(), e.X, e.Y);
        };
        Area.AddController(click);
        Area.HasTooltip = true;
        Area.OnQueryTooltip += (_, e) =>
        {
            var i = Hit(e.X, e.Y);
            if (i < 0 || Tip is null || _items[i].Kind != Kind.Tile) return false;
            if (i != _tipIndex || _tipGen != _gen) { _tip = Tip(i); _tipIndex = i; _tipGen = _gen; }
            if (_tip is null) return false;
            e.Tooltip.SetCustom(_tip);
            return true;
        };
    }

    public int Count => _items.Count;
    public Kind KindAt(int i) => _items[i].Kind;
    public TileModel? TileAt(int i) => _items[i].Tile;

    public void Clear() { _items.Clear(); Changed(); }
    public void Add(TileModel t) => _items.Add((Kind.Tile, t));
    public void AddSep() => _items.Add((Kind.Sep, null));
    public void AddPlus() => _items.Add((Kind.Add, null));

    /// <summary>After Add/Clear calls: lays out again and redraws.</summary>
    public void Changed()
    {
        _gen++;
        _laidFor = -1;
        Relayout(Area.GetWidth());
        Area.QueueDraw();
    }

    /// <summary>Tiles changed in place (icons, selection): redraw only.</summary>
    public void Redraw() { _gen++; Area.QueueDraw(); }

    /// <summary>The rectangle of entry <paramref name="i"/> in widget px (pop-up menus point at it).</summary>
    public (double X, double Y, double W, double H) Rect(int i)
    {
        var s = Icons.Scale;
        return ((_at[i].X + Pad) * s, (_at[i].Y + Pad) * s, BuildGeometry.Tile * s, BuildGeometry.Tile * s);
    }

    private double Width(int i) => _items[i].Kind switch
    {
        Kind.Sep => 14,
        Kind.Add => BuildGeometry.Tile + (i > 0 ? 6 : 0) + _gap,
        _ => BuildGeometry.Tile + _gap,
    };

    private void Relayout(int width)
    {
        var avail = width > 0 ? width / Icons.Scale - Pad * 2 : 440;
        if (Math.Abs(avail - _laidFor) < 0.01 && _at.Length >= _items.Count) return;
        _laidFor = avail;
        var n = _items.Count;
        if (_at.Length < n) { _at = new (double, double)[n * 2]; _w = new double[n * 2]; }
        for (var i = 0; i < n; i++) _w[i] = Width(i);
        var h = BuildGeometry.Flow(_w.AsSpan(0, n), avail, BuildGeometry.Tile + _gap, _at.AsSpan(0, n));
        // the Add cell's left margin is part of its slot
        for (var i = 0; i < n; i++) if (_items[i].Kind == Kind.Add && i > 0 && _at[i].X > 0) _at[i].X += 6;
        var px = UiKit.Px(h + Pad * 2);
        if (Area.ContentHeight != px) Area.ContentHeight = px;
    }

    private int Hit(double x, double y)
    {
        var s = Icons.Scale;
        x = x / s - Pad; y = y / s - Pad;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Kind == Kind.Sep) continue;
            if (x >= _at[i].X && x <= _at[i].X + BuildGeometry.Tile && y >= _at[i].Y && y <= _at[i].Y + BuildGeometry.Tile) return i;
        }
        return -1;
    }

    private void Draw(Cairo.Context cr, int w)
    {
        if (_laidFor < 0) Relayout(w);
        var s = Icons.Scale;
        cr.Scale(s, s);
        cr.Translate(Pad, Pad);
        for (var i = 0; i < _items.Count; i++)
        {
            var (x, y) = _at[i];
            switch (_items[i].Kind)
            {
                case Kind.Tile: SkillTilePainter.Draw(cr, _items[i].Tile!, x, y, BuildGeometry.Tile); break;
                case Kind.Sep:
                    cr.SelectFontFace("Inter", Cairo.FontSlant.Normal, Cairo.FontWeight.Normal);
                    cr.SetFontSize(12);
                    Paint.Rgb(cr, BuildColors.Gt);
                    cr.MoveTo(x + 2, y + 28);
                    cr.ShowText("›");
                    break;
                default:
                    Paint.RoundRect(cr, x + 0.4, y + 0.4, BuildGeometry.Tile - 0.8, BuildGeometry.Tile - 0.8, 6);
                    Paint.Rgb(cr, BuildColors.AddLine);
                    cr.LineWidth = 0.8;
                    cr.SetDash([4 * 0.8, 3 * 0.8], 0);
                    cr.Stroke();
                    cr.SetDash([], 0);
                    cr.SelectFontFace("Inter", Cairo.FontSlant.Normal, Cairo.FontWeight.Normal);
                    cr.SetFontSize(18);
                    cr.TextExtents("+", out var ext);
                    Paint.Rgb(cr, BuildColors.AddText);
                    cr.MoveTo(x + (BuildGeometry.Tile - ext.XAdvance) / 2, y + (BuildGeometry.Tile - ext.Height) / 2 - ext.YBearing);
                    cr.ShowText("+");
                    break;
            }
        }
    }
}

/// <summary>One skill tile as its own widget (the detail header, the tooltip): a fixed-size DrawingArea around a <see cref="TileModel"/>.</summary>
internal sealed class SkillTile
{
    private readonly double _size;
    public TileModel Model { get; } = new();
    public Gtk.DrawingArea Area { get; } = Gtk.DrawingArea.New();

    public SkillTile(double size)
    {
        _size = size;
        Area.ContentWidth = UiKit.Px(size + 4);
        Area.ContentHeight = UiKit.Px(size + 4);
        Area.Valign = Gtk.Align.Center;
        Area.Halign = Gtk.Align.Start;
        Area.SetDrawFunc((_, cr, _, _) =>
        {
            cr.Scale(Icons.Scale, Icons.Scale);
            SkillTilePainter.Draw(cr, Model, 2, 2, _size);
        });
    }

    public void Redraw() => Area.QueueDraw();
}

/// <summary>Pango markup of the Build tab (display-free: tested).</summary>
internal static class BuildMarkup
{
    public static string Esc(string s)
    {
        if (s.AsSpan().IndexOfAny("&<>'\"") < 0) return s;
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '\'' => "&apos;", '"' => "&quot;", _ => c.ToString() });
        return sb.ToString();
    }

    /// <summary>A description's runs: numbers in the questlog peach at medium weight (BuildUi.SetRuns).</summary>
    public static string Runs(ReadOnlySpan<AionMeter.Service.TextSeg> segs)
    {
        var sb = new StringBuilder(256);
        foreach (var s in segs)
        {
            if (s.Num) sb.Append("<span foreground=\"").Append(Palette.Hex(BuildColors.Num)).Append("\" weight=\"500\">").Append(Esc(s.Text)).Append("</span>");
            else sb.Append(Esc(s.Text));
        }
        return sb.ToString();
    }

    /// <summary>"text" in a colour.</summary>
    public static StringBuilder Span(StringBuilder sb, string text, uint rgb, bool bold = false)
    {
        sb.Append("<span foreground=\"").Append(Palette.Hex(rgb)).Append('"');
        if (bold) sb.Append(" weight=\"bold\"");
        return sb.Append('>').Append(Esc(text)).Append("</span>");
    }

    /// <summary>An inline link: the label's activate-link handler gets <paramref name="id"/> (never a web address: the view dispatches it).</summary>
    public static StringBuilder Link(StringBuilder sb, string text, string id, string? tip = null)
    {
        sb.Append("<a href=\"").Append(Esc(id)).Append('"');
        if (tip is not null) sb.Append(" title=\"").Append(Esc(tip)).Append('"');
        return sb.Append('>').Append(Esc(text)).Append("</a>");
    }

    /// <summary>One relation line: a green arrow for what the skill gives, blue for what it takes, grey for chain/priority.</summary>
    public static string Relation(SkillRelation r)
    {
        var (glyph, rgb) = r.Kind switch
        {
            RelationKind.Grants or RelationKind.Applies or RelationKind.Shortens or RelationKind.Boosts or RelationKind.ChainSkill => ("→ ", BuildColors.Green),
            RelationKind.Consumes or RelationKind.Needs or RelationKind.ShortenedBy or RelationKind.BoostedBy => ("← ", BuildColors.Blue),
            _ => ("· ", BuildColors.Mute),
        };
        var sb = new StringBuilder(r.Text.Length + 64);
        Span(sb, glyph, rgb, bold: true);
        return sb.Append(Esc(r.Text)).ToString();
    }

    /// <summary>The legend line: a coloured square per state and its text (Avalonia LegendItem row, wrapped as one label; no-break spaces keep
    /// each item on one line, as the WrapPanel did).</summary>
    public static string Legend(ReadOnlySpan<(uint Rgb, string Text)> items, string? tail)
    {
        var sb = new StringBuilder(256);
        for (var i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append("    ");
            Span(sb, "■ ", items[i].Rgb).Append(Esc(items[i].Text).Replace(' ', '\u00A0'));
        }
        if (tail is { Length: > 0 }) sb.Append("    ").Append(Esc(tail).Replace(' ', '\u00A0'));
        return sb.ToString();
    }

    public static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// questlog's 12×4 key bar in ONE DrawingArea (the Avalonia Canvas of cells + tiles + key labels inside a DownOnly Viewbox): drawn at
/// 642 px and shrunk to the width it gets (the card's ~460 px), clicks scaled back. Row 0 (the first chain link) is the bottom row.
/// </summary>
internal sealed class KeyBar
{
    private const double Pad = 2;   // the selection outline sits 2 px outside a tile
    private readonly TileModel?[] _slots = new TileModel?[BuildEditor.Keys * BuildEditor.Rows];
    private readonly string[] _names = new string[BuildEditor.Keys];
    private int _key, _tipSlot = -1, _tipGen = -1, _gen;
    private Gtk.Widget? _tip;

    public Gtk.DrawingArea Area { get; } = Gtk.DrawingArea.New();
    /// <summary>(key 1..12, row 0..3, n-press, button 1/3)</summary>
    public Action<int, int, int, uint>? CellPressed;
    public Action<int>? KeyPressed;
    public Func<TileModel, Gtk.Widget?>? Tip;

    public KeyBar()
    {
        for (var i = 0; i < _names.Length; i++) _names[i] = BuildEditor.KeyName(i + 1);
        Area.Hexpand = true;
        Area.ContentHeight = UiKit.Px(BuildGeometry.BarHeight * 0.7 + Pad * 2);
        Area.SetDrawFunc((_, cr, w, _) => Draw(cr, w));
        Area.OnResize += (_, e) => Size(e.Width);
        Area.SetCursorFromName("pointer");
        var click = Gtk.GestureClick.New();
        click.Button = 0;
        click.OnPressed += (g, e) =>
        {
            var hit = Hit(e.X, e.Y, out var key, out var row);
            if (hit == BuildGeometry.BarHit.Cell) CellPressed?.Invoke(key, row, e.NPress, g.GetCurrentButton());
            else if (hit == BuildGeometry.BarHit.Key && g.GetCurrentButton() == 1) KeyPressed?.Invoke(key);
        };
        Area.AddController(click);
        Area.HasTooltip = true;
        Area.OnQueryTooltip += (_, e) =>
        {
            if (Tip is null || Hit(e.X, e.Y, out var key, out var row) != BuildGeometry.BarHit.Cell) return false;
            var slot = Slot(key, row);
            if (_slots[slot] is not { } m) return false;
            if (slot != _tipSlot || _tipGen != _gen) { _tip = Tip(m); _tipSlot = slot; _tipGen = _gen; }
            if (_tip is null) return false;
            e.Tooltip.SetCustom(_tip);
            return true;
        };
    }

    private static int Slot(int key, int row) => (key - 1) * BuildEditor.Rows + row;

    public TileModel? TileAt(int key, int row) => _slots[Slot(key, row)];
    public void Clear() { Array.Clear(_slots); _gen++; }
    public void Put(int key, int row, TileModel m) => _slots[Slot(key, row)] = m;
    public int Key { get => _key; set { if (_key == value) return; _key = value; Area.QueueDraw(); } }
    public void Redraw() { _gen++; Area.QueueDraw(); }
    public void ForEach(Action<TileModel> a) { foreach (var m in _slots) if (m is not null) a(m); }

    private double Scale(int width) => BuildGeometry.BarScale(width > 0 ? width / Icons.Scale - Pad * 2 : 0);

    private void Size(int width)
    {
        var px = UiKit.Px(BuildGeometry.BarHeight * Scale(width) + Pad * 2);
        if (Area.ContentHeight != px) Area.ContentHeight = px;
    }

    private BuildGeometry.BarHit Hit(double x, double y, out int key, out int row)
    {
        var s = Icons.Scale;
        var k = Scale(Area.GetWidth());
        return BuildGeometry.HitBar((x / s - Pad) / k, (y / s - Pad) / k, out key, out row);
    }

    private void Draw(Cairo.Context cr, int width)
    {
        var s = Icons.Scale;
        var k = Scale(width);
        cr.Scale(s, s);
        cr.Translate(Pad, Pad);
        cr.Scale(k, k);
        for (var key = 1; key <= BuildEditor.Keys; key++)
            for (var row = 0; row < BuildEditor.Rows; row++)
            {
                var (x, y) = BuildGeometry.Cell(key, row);
                if (_slots[Slot(key, row)] is { } m) SkillTilePainter.Draw(cr, m, x, y, BuildGeometry.Tile);
                else
                {
                    Paint.RoundRect(cr, x, y, BuildGeometry.Tile, BuildGeometry.Tile, 6);
                    Paint.Rgb(cr, BuildColors.Cell);
                    cr.Fill();
                }
            }
        var ky = BuildEditor.Rows * BuildGeometry.Step - 6 + BuildGeometry.KeyRowGap;
        cr.SelectFontFace("Inter", Cairo.FontSlant.Normal, Cairo.FontWeight.Normal);
        cr.SetFontSize(10);
        for (var i = 0; i < BuildEditor.Keys; i++)
        {
            var x = i * BuildGeometry.Step;
            var on = _key == i + 1;
            Paint.RoundRect(cr, x, ky, BuildGeometry.Tile, BuildGeometry.KeyRowHeight, 4);
            Paint.Rgb(cr, on ? BuildColors.KeyOn : BuildColors.KeyBack);
            cr.Fill();
            cr.TextExtents(_names[i], out var ext);
            Paint.Rgb(cr, on ? 0xFFFFFF : BuildColors.KeyText);
            cr.MoveTo(x + (BuildGeometry.Tile - ext.XAdvance) / 2, ky + (BuildGeometry.KeyRowHeight - ext.Height) / 2 - ext.YBearing);
            cr.ShowText(_names[i]);
        }
    }
}
