using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AionMeter.Core.Events;
using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// Cairo copies of icon textures for the Build tab's DrawingAreas (A2M-50): a GdkTexture cannot be painted by cairo, so each one is
/// downloaded once into an ARGB32 image surface (premultiplied BGRA = cairo's byte order on little endian) and kept per texture instance
/// (IconCache returns the same texture per key). The grey copy (WPF/Avalonia grayscale(1), BT.601 luma) is made on first use.
/// </summary>
internal static class TexSurf
{
    private sealed class Entry
    {
        public Cairo.ImageSurface? Color;
        public Cairo.ImageSurface? Grey;
        public byte[]? Pixels;
        public int Width, Height;
    }

    private static readonly ConditionalWeakTable<Gdk.Texture, Entry> Cache = new();

    private static Entry? Of(Gdk.Texture tex)
    {
        if (Cache.TryGetValue(tex, out var e)) return e.Color is null ? null : e;
        e = new Entry();
        try
        {
            int w = tex.GetWidth(), h = tex.GetHeight();
            if (w > 0 && h > 0)
            {
                using var dl = Gdk.TextureDownloader.New(tex);
                dl.SetFormat(Gdk.MemoryFormat.B8g8r8a8Premultiplied);
                using var bytes = dl.DownloadBytes(out var stride);
                var size = (int)bytes.GetSize();
                var ptr = bytes.GetRegion(1, 0, (nuint)size);
                if (ptr != IntPtr.Zero && (int)stride >= w * 4 && size >= (int)stride * (h - 1) + w * 4)
                {
                    var raw = new byte[size];
                    Marshal.Copy(ptr, raw, 0, size);
                    var px = new byte[w * h * 4];
                    for (var y = 0; y < h; y++) Buffer.BlockCopy(raw, y * (int)stride, px, y * w * 4, w * 4);
                    e.Pixels = px; e.Width = w; e.Height = h;
                    e.Color = Surface(px, w, h);
                }
            }
        }
        catch (Exception ex) when (ex is GLib.GException or ArgumentException or InvalidOperationException) { Log.Info("icon to cairo: " + ex.Message); }
        Cache.AddOrUpdate(tex, e);
        return e.Color is null ? null : e;
    }

    private static Cairo.ImageSurface Surface(byte[] px, int w, int h)
    {
        var s = new Cairo.ImageSurface(Cairo.Format.Argb32, w, h);
        s.Flush();
        var data = s.GetData();
        var ss = s.Stride;
        for (var y = 0; y < h; y++) px.AsSpan(y * w * 4, w * 4).CopyTo(data.Slice(y * ss, w * 4));
        s.MarkDirty();
        return s;
    }

    /// <summary>Paints <paramref name="tex"/> into (x, y, w, h); grey and/or with alpha as the views need. False when it cannot be painted.</summary>
    public static bool Draw(Cairo.Context cr, Gdk.Texture? tex, double x, double y, double w, double h, bool grey = false, double alpha = 1)
    {
        if (tex is null || Of(tex) is not { } e) return false;
        var s = e.Color!;
        if (grey)
        {
            if (e.Grey is null)
            {
                var g = (byte[])e.Pixels!.Clone();
                BuildPixels.Greyscale(g);
                e.Grey = Surface(g, e.Width, e.Height);
                e.Pixels = null;   // both surfaces exist: the managed copy is not needed any more
            }
            s = e.Grey;
        }
        cr.Save();
        cr.Translate(x, y);
        cr.Scale(w / e.Width, h / e.Height);
        cr.SetSourceSurface(s, 0, 0);
        if (alpha >= 0.999) cr.Paint(); else cr.PaintWithAlpha(alpha);
        cr.Restore();
        return true;
    }
}

/// <summary>Pixel helpers without GTK (tested display-free).</summary>
internal static class BuildPixels
{
    /// <summary>BT.601 luma into B, G and R of premultiplied BGRA (premultiplied stays premultiplied: luma of premultiplied channels).</summary>
    public static void Greyscale(Span<byte> bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var y = (byte)((bgra[i] * 29 + bgra[i + 1] * 150 + bgra[i + 2] * 77) >> 8);
            bgra[i] = bgra[i + 1] = bgra[i + 2] = y;
        }
    }
}

/// <summary>Widgets the kit lacks, for the Build tab and its import dialog (A2M-50): class emblems, drop-downs that cannot widen the card,
/// pop-up menus and the yes/no question (Avalonia MenuFlyout / Flyout), the clipboard.</summary>
internal static class BuildKit
{
    private static readonly string?[] EmblemPaths = new string?[256];
    private static readonly bool[] EmblemLooked = new bool[256];

    /// <summary>The official class emblem shipped beside the card (Assets/Classes/&lt;class&gt;.png), null when missing.</summary>
    public static string? EmblemPath(GameClass c)
    {
        if (c == GameClass.Unknown) return null;
        if (EmblemLooked[(byte)c]) return EmblemPaths[(byte)c];
        EmblemLooked[(byte)c] = true;
        var p = Path.Combine(AppContext.BaseDirectory, "Assets", "Classes", c.ToString().ToLowerInvariant() + ".png");
        return EmblemPaths[(byte)c] = File.Exists(p) ? p : null;
    }

    /// <summary>
    /// A drop-down whose closed button ellipsizes at <paramref name="maxChars"/>: GTK's default factory asks for the longest item's width
    /// and a layer surface takes its content's natural width, so a long build name would widen the whole card. The open list keeps full names.
    /// </summary>
    public static Gtk.DropDown Combo(IEnumerable<string> items, int selected, double minWidth, int maxChars)
    {
        var d = UiKit.Combo(items, selected, minWidth);
        var f = Gtk.SignalListItemFactory.New();
        f.OnSetup += (_, e) =>
        {
            var l = Gtk.Label.New("");
            l.Xalign = 0;
            l.Ellipsize = Pango.EllipsizeMode.End;
            l.MaxWidthChars = maxChars;
            l.WidthChars = 1;
            ((Gtk.ListItem)e.Object).SetChild(l);
        };
        f.OnBind += (_, e) =>
        {
            var item = (Gtk.ListItem)e.Object;
            if (item.GetChild() is Gtk.Label l && item.GetItem() is Gtk.StringObject s) l.SetText(s.GetString());
        };
        d.SetFactory(f);
        var list = Gtk.SignalListItemFactory.New();
        list.OnSetup += (_, e) => { var l = Gtk.Label.New(""); l.Xalign = 0; ((Gtk.ListItem)e.Object).SetChild(l); };
        list.OnBind += (_, e) =>
        {
            var item = (Gtk.ListItem)e.Object;
            if (item.GetChild() is Gtk.Label l && item.GetItem() is Gtk.StringObject s) l.SetText(s.GetString());
        };
        d.SetListFactory(list);
        return d;
    }

    /// <summary>One entry of <see cref="Menu"/>: an optional icon, the text, the action (null = disabled line).</summary>
    public readonly record struct MenuItem(string Text, Action? Click, Gdk.Texture? Icon = null);

    /// <summary>A pop-up menu under <paramref name="parent"/> pointing at <paramref name="x"/>,<paramref name="y"/> (widget px; -1 = the whole widget).</summary>
    public static void Menu(Gtk.Widget parent, IReadOnlyList<MenuItem> items, double x = -1, double y = -1, double w = 1, double h = 1)
    {
        var pop = NewPopover(parent, x, y, w, h);
        var box = UiKit.VBox();
        box.AddCssClass("k-build-menu");
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var row = UiKit.HBox(8);
            if (it.Icon is not null) row.Append(ItemKit.Image(it.Icon, 22));
            var l = Gtk.Label.New(it.Text);
            l.Xalign = 0;
            row.Append(l);
            var b = Gtk.Button.New();
            b.AddCssClass("k-build-menuitem");
            b.Child = row;
            b.FocusOnClick = false;
            b.Sensitive = it.Click is not null;
            var click = it.Click;
            b.OnClicked += (_, _) => { pop.Popdown(); click?.Invoke(); };
            box.Append(b);
        }
        // a class has up to ~20 skills: past 420 px the list scrolls instead of running off the output
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.PropagateNaturalHeight = true;
        scroll.PropagateNaturalWidth = true;
        scroll.MaxContentHeight = UiKit.Px(420);
        scroll.Child = box;
        pop.SetChild(scroll);
        pop.Popup();
    }

    /// <summary>A yes/no question in a pop-over (WPF MessageBox YesNo).</summary>
    public static void Confirm(Gtk.Widget anchor, string text, Action yes)
    {
        var pop = NewPopover(anchor, -1, -1, 1, 1);
        var panel = UiKit.VBox();
        UiKit.Margin(panel, 4, 4, 4, 4);
        var l = UiKit.Text(text);
        l.SetSizeRequest(UiKit.Px(300), -1);
        l.MarginBottom = UiKit.Px(10);
        panel.Append(l);
        var row = UiKit.HBox();
        row.Halign = Gtk.Align.End;
        row.Append(UiKit.Primary(UiText.Current.Code == "es" ? "Sí" : "Yes", () => { pop.Popdown(); yes(); }));
        row.Append(UiKit.Ghost(UiText.Current.Cancel, () => pop.Popdown()));
        panel.Append(row);
        pop.SetChild(panel);
        pop.Popup();
    }

    private static Gtk.Popover NewPopover(Gtk.Widget parent, double x, double y, double w, double h)
    {
        var pop = Gtk.Popover.New();
        pop.HasArrow = false;
        pop.SetParent(parent);
        if (x >= 0)
        {
            var r = new Gdk.Rectangle { X = (int)x, Y = (int)y, Width = Math.Max(1, (int)w), Height = Math.Max(1, (int)h) };
            pop.SetPointingTo(r);
        }
        // a popover lives as the parent's child: unparent once it is gone, or every menu would stay attached
        pop.OnClosed += (p, _) => UiLoop.Post(() => p.Unparent());
        return pop;
    }

    /// <summary>Wayland only lets a client set the selection with a recent input serial of its own: call from the click.</summary>
    public static bool Copy(Gtk.Widget from, string text)
    {
        try { from.GetClipboard().SetText(text); return true; }
        catch (GLib.GException e) { Log.Info("clipboard: " + e.Message); return false; }
    }
}
