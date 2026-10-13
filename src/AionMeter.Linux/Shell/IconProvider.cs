using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>
/// Item and skill icons as Avalonia bitmaps (A2M-48 phase 3b): the cache, download and skill-name rules are <see cref="IconCache{T}"/> in
/// AionMeter.Service (shared with the GTK card since A2M-50); this part decodes and shrinks with Skia.
/// </summary>
public sealed class IconProvider : IconCache<Bitmap>
{
    public IconProvider(string dataDirectory, bool enabled) : base(dataDirectory, enabled, a => Dispatcher.UIThread.Post(a)) { }

    protected override Bitmap? Decode(string file, int width)
    {
        try
        {
            using var s = File.OpenRead(file);
            return Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    protected override bool WriteSmall(byte[] original, string tmpFile)
    {
        try
        {
            using var ms = new MemoryStream(original);
            using var bmp = Bitmap.DecodeToWidth(ms, 64, BitmapInterpolationMode.HighQuality);
            bmp.Save(tmpFile);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException) { return false; }
    }
}
