using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// Item and skill icons as GdkTextures (A2M-50): the cache, download pacing and skill-name rules are <see cref="IconCache{T}"/> in
/// AionMeter.Service (shared with the Avalonia UI, same `icons/` folder); this part decodes with GDK and shrinks with gdk-pixbuf. Show one in a
/// <c>Gtk.Image</c> with a pixel size (<see cref="ItemKit.Image"/>), never a Gtk.Picture (height-for-width: it inflated the card's rows, measured).
/// gdk-pixbuf only decodes what its loaders know: PNG/JPEG always; questlog's WebP icons only with webp-pixbuf-loader on the host — the ones
/// the Avalonia UI already shrank are PNG in the cache and work.
/// </summary>
internal sealed class IconProvider : IconCache<Gdk.Texture>
{
    public IconProvider(string dataDirectory, bool enabled) : base(dataDirectory, enabled, UiLoop.Post) { }

    protected override Gdk.Texture? Decode(string file, int width)
    {
        try { return Gdk.Texture.NewFromFilename(file); }
        catch (Exception e) when (e is GLib.GException or IOException or UnauthorizedAccessException) { return null; }
    }

    protected override bool WriteSmall(byte[] original, string tmpFile)
    {
        try
        {
            var loader = GdkPixbuf.PixbufLoader.New();
            loader.Write(original);
            loader.Close();
            if (loader.GetPixbuf() is not { } pb) return false;
            int w = pb.GetWidth(), h = pb.GetHeight();
            if (w <= 0 || h <= 0) return false;
            var small = w > 64 ? pb.ScaleSimple(64, Math.Max(1, h * 64 / w), GdkPixbuf.InterpType.Hyper) ?? pb : pb;
            return small.Savev(tmpFile, "png", [], []);
        }
        catch (Exception e) when (e is GLib.GException or IOException) { return false; }
    }
}
