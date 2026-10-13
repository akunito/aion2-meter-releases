using System.Runtime.CompilerServices;
using AionMeter.Core.GameDb;
using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// One item of the Items tab (WPF Controls/ItemTile, A2M-29; the Avalonia ItemTile.Render) on a cairo DrawingArea (A2M-50): the tile with
/// the grade-coloured frame, count badge bottom-right (k/M), "+N" enchant top-left, blue dot top-right for an account-warehouse slot newer
/// than the last full list, grey initials without an icon, the blue search ring, 0.16 opacity when dimmed by a search. Drawn in the 48-unit
/// space of the original and scaled to the widget. The icon is asked for on the first draw only; <see cref="RetryIcon"/> after
/// IconProvider.Loaded asks again for tiles still without one. Set() with the same values does not redraw.
/// </summary>
internal sealed class ItemTile
{
    /// <summary>The ring and the badges reach 2 units past the 48-unit tile (Avalonia drew outside the control; a DrawingArea clips).</summary>
    public const double Pad = 2;
    private const uint Back = 0x151515, Initials = 0x666666, Num = 0xFCC78B, Blue = 0x65B0FC;
    private static Pango.FontDescription? _bold11, _bold10, _semi12;

    public Gtk.DrawingArea Widget { get; }
    public int Item { get; private set; }
    public long Count { get; private set; }
    private IconProvider? _icons;
    private Gdk.Texture? _icon;
    private bool _asked, _dot, _hit, _dim;
    private Grade _grade;
    private int _enchant;
    private string _name = "";
    private Pango.Layout? _countText, _enchText, _initText;

    /// <param name="size">The tile edge in unscaled px (48 in the grid, 36 in list rows and "Across characters", 44 in the tooltip).</param>
    public ItemTile(double size = ItemsLogic.Tile)
    {
        Widget = Gtk.DrawingArea.New();
        var px = UiKit.Px(size + 2 * Pad);
        Widget.SetContentWidth(px);
        Widget.SetContentHeight(px);
        Widget.Hexpand = false;
        Widget.Vexpand = false;
        Widget.Halign = Gtk.Align.Start;
        Widget.Valign = Gtk.Align.Center;
        Widget.SetDrawFunc(Draw);
    }

    public void Set(int item, long count, int enchant, Grade grade, string name, IconProvider? icons, bool dot = false, bool dim = false, bool hit = false)
    {
        var dirty = false;
        if (item != Item || !ReferenceEquals(icons, _icons)) { _icon = null; _asked = false; _initText = null; dirty = true; }
        if (name != _name) { _name = name; _initText = null; dirty = true; }
        if (count != Count) { Count = count; _countText = null; dirty = true; }
        if (enchant != _enchant) { _enchant = enchant; _enchText = null; dirty = true; }
        if (grade != _grade || dot != _dot || hit != _hit) dirty = true;
        Item = item; _icons = icons; _grade = grade; _dot = dot; _hit = hit;
        if (dim != _dim) { _dim = dim; Widget.Opacity = dim ? 0.16 : 1; }
        if (dirty) Widget.QueueDraw();
    }

    /// <summary>Icons landed (IconProvider.Loaded, coalesced by the view): a tile that asked and got nothing asks again.</summary>
    public void RetryIcon()
    {
        if (_icon is not null || !_asked || _icons is null) return;
        if (_icons.Item(Item) is { } tex) { _icon = tex; Widget.QueueDraw(); }
    }

    private void Draw(Gtk.DrawingArea area, Cairo.Context cr, int w, int h)
    {
        if (w <= 0 || Item <= 0) return;
        if (!_asked) { _asked = true; _icon = _icons?.Item(Item); }
        var s = w / (48 + 2 * Pad);
        cr.Save();
        cr.Scale(s, s);
        cr.Translate(Pad, Pad);

        cr.Save();
        Paint.RoundRect(cr, 0, 0, 48, 48, 6);
        cr.Clip();
        Paint.Rgb(cr, Back);
        cr.Paint();
        if (_icon is { } tex && IconSurfaces.Of(tex) is { } surf)
        {
            cr.Save();
            cr.Scale(48.0 / surf.Width, 48.0 / surf.Height);
            cr.SetSourceSurface(surf, 0, 0);
            cr.Paint();
            cr.Restore();
        }
        else
        {
            _initText ??= Layout(area, ItemsLogic.InitialsOf(_name), _semi12 ??= Font("600 12"));
            _initText.GetPixelSize(out var tw, out var th);
            Paint.Rgb(cr, Initials);
            cr.MoveTo((48 - tw) / 2.0, (48 - th) / 2.0);
            PangoCairo.Functions.ShowLayout(cr, _initText);
        }
        cr.Restore();

        Paint.RoundRect(cr, 0.75, 0.75, 46.5, 46.5, 5.5);
        Paint.Rgb(cr, Palette.GradeLine(_grade));
        cr.LineWidth = 1.5;
        cr.Stroke();

        if (Count > 1)
        {
            _countText ??= Layout(area, ItemFormat.Short(Count), _bold11 ??= Font("Bold 11"));
            _countText.GetPixelSize(out var tw, out var th);
            var bw = Math.Max(16, tw + 6);
            Paint.RoundRect(cr, 49 - bw, 34, bw, 15, 4);
            Paint.Argb(cr, 0xD1000000);
            cr.Fill();
            Paint.Rgb(cr, 0xFFFFFF);
            cr.MoveTo(49 - bw + (bw - tw) / 2.0, 34 + (15 - th) / 2.0);
            PangoCairo.Functions.ShowLayout(cr, _countText);
        }
        if (_enchant > 0)
        {
            _enchText ??= Layout(area, "+" + _enchant.ToString(System.Globalization.CultureInfo.InvariantCulture), _bold10 ??= Font("Bold 10"));
            _enchText.GetPixelSize(out var tw, out var th);
            var bw = tw + 6;
            Paint.RoundRect(cr, -1, -1, bw, 14, 4);
            Paint.Argb(cr, 0xCC000000);
            cr.Fill();
            Paint.Rgb(cr, Num);
            cr.MoveTo(2, -1 + (14 - th) / 2.0);
            PangoCairo.Functions.ShowLayout(cr, _enchText);
        }
        if (_dot)
        {
            cr.NewPath();
            cr.Arc(42.5, 5.5, 3.5, 0, 2 * Math.PI);
            Paint.Rgb(cr, Blue);
            cr.FillPreserve();
            Paint.Rgb(cr, 0);
            cr.LineWidth = 1.5;
            cr.Stroke();
        }
        if (_hit)
        {
            Paint.RoundRect(cr, -1, -1, 50, 50, 7);
            Paint.Rgb(cr, Blue);
            cr.LineWidth = 2;
            cr.Stroke();
        }
        cr.Restore();
    }

    /// <summary>Inter → Noto Sans → DejaVu Sans as the kit CSS asks, absolute px in the tile's 48-unit space.</summary>
    private static Pango.FontDescription Font(string weightAndPx) => Pango.FontDescription.FromString("Inter,Noto Sans,DejaVu Sans " + weightAndPx + "px");

    private static Pango.Layout Layout(Gtk.Widget area, string text, Pango.FontDescription font)
    {
        var l = area.CreatePangoLayout(text);
        l.SetFontDescription(font);
        return l;
    }
}

/// <summary>A GdkTexture as a cairo image surface, made once per texture (the IconProvider hands out one texture per icon).</summary>
internal static class IconSurfaces
{
    private static readonly ConditionalWeakTable<Gdk.Texture, Cairo.ImageSurface> Made = new();

    public static Cairo.ImageSurface? Of(Gdk.Texture tex)
    {
        if (Made.TryGetValue(tex, out var s)) return s;
        try
        {
            int w = tex.GetWidth(), h = tex.GetHeight();
            if (w <= 0 || h <= 0) return null;
            var d = Gdk.TextureDownloader.New(tex);
            d.SetFormat(Gdk.MemoryFormat.B8g8r8a8Premultiplied);   // = CAIRO_FORMAT_ARGB32 on little-endian
            using var bytes = d.DownloadBytes(out var strideN);
            var stride = (int)strideN;
            var src = bytes.GetRegionSpan<byte>(0, bytes.GetSize());
            s = new Cairo.ImageSurface(Cairo.Format.Argb32, w, h);
            s.Flush();
            var dst = s.GetData();
            var ds = s.Stride;
            var row = Math.Min(w * 4, Math.Min(stride, ds));
            for (var y = 0; y < h; y++) src.Slice(y * stride, row).CopyTo(dst.Slice(y * ds, row));
            s.MarkDirty();
            Made.AddOrUpdate(tex, s);
            return s;
        }
        catch (Exception e) when (e is GLib.GException or ArgumentException or IndexOutOfRangeException)
        {
            Log.Info("item icon not drawable: " + e.Message);
            return null;
        }
    }
}
