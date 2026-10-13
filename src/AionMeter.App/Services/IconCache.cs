using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AionMeter.Core.GameDb;

namespace AionMeter.App.Services;

/// <summary>
/// Item icons for the Craft and Prices rows (A2M-26). Nothing ships in the zip (12,816 icons ≈ 30 MB): the first row that needs an
/// icon queues a download from aion2hub (one request per 400 ms, same pace as every other read of that site) into
/// %AppData%\AionMeter\icons\&lt;id&gt;.&lt;ext&gt;; <see cref="Loaded"/> fires on the UI thread when one lands so the view can redraw.
/// WebP icons (3,205) decode only when Windows has the WebP codec; a failed decode is remembered and the row stays without icon.
/// </summary>
public sealed class IconCache : IDisposable
{
    private readonly ItemIcons _index;
    private readonly string _dir;
    // keys: item ids as decimal strings for aion2hub item icons, "<prefix>:<stem>" for URL icons (skill icons from questlog's CDN, A2M-21)
    private readonly ConcurrentDictionary<string, ImageSource?> _mem = new();
    private readonly ConcurrentQueue<(string Key, string Url)> _queue = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    public event Action? Loaded;
    /// <summary>Settings → "download icons": off = no network, only what is on disk already.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Icon URL for ids item-icons.json lacks (questlog CDN, from ItemNames extras).</summary>
    public Func<int, string?>? ExtraUrl { get; set; }

    /// <param name="dir">Where icons live (default %AppData%\AionMeter\icons); the render tool points it at a folder of its own.</param>
    public IconCache(ItemIcons index, string? dir = null)
    {
        _index = index;
        _dir = dir ?? Path.Combine(AppSettings.AppDataDir, "icons");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.3 (private build; item icons on demand, one request per 400 ms)");
    }

    /// <summary>The icon when it is on disk already (decoded at 56 px and frozen); null while it downloads or when there is none.</summary>
    public ImageSource? Get(int item)
    {
        var key = item.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_mem.TryGetValue(key, out var cached)) return cached;
        var rel = _index.Path(item);
        // item-icons.json holds aion2hub paths, or an absolute questlog CDN URL for the items aion2hub lacks (tools/item-icons.py, 2026-10-13)
        if (rel is not null) return GetOrQueue(key, rel.StartsWith("http", StringComparison.Ordinal) ? rel : "https://aion2hub.me/media/icons/" + rel, 56);
        // not cached as null: a questlog name lookup may bring the icon URL later
        return ExtraUrl?.Invoke(item) is { } url ? GetOrQueue(key, url, 56) : null;
    }

    /// <summary>Any icon by URL, cached under <paramref name="key"/> (letters, digits, '_' '-' ':' only; the ':' becomes the file's folder
    /// separator). Decoded at <paramref name="decodeWidth"/> px. Same queue and pace as the item icons.</summary>
    public ImageSource? GetUrl(string key, string url, int decodeWidth = 56)
    {
        if (_mem.TryGetValue(key, out var cached)) return cached;
        return GetOrQueue(key, url, decodeWidth);
    }

    private static string FileFor(string dir, string key) => Path.Combine(dir, key.Replace(':', Path.DirectorySeparatorChar) + ".png");

    private ImageSource? GetOrQueue(string key, string url, int decodeWidth)
    {
        var file = FileFor(_dir, key);
        if (File.Exists(file))
        {
            var img = Decode(file, decodeWidth);
            _mem[key] = img;
            return img;
        }
        if (Enabled && _queued.TryAdd(key, 0)) { _queue.Enqueue((key, url)); _worker ??= Task.Run(WorkAsync); }
        return null;
    }

    private static ImageSource? Decode(string file, int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.DecodePixelWidth = width;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or ArgumentException) { return null; }
    }

    private static byte[]? Shrink(byte[] original)
    {
        try
        {
            using var ms = new MemoryStream(original);
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.DecodePixelWidth = 64; bmp.EndInit(); bmp.Freeze();
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using var outMs = new MemoryStream(); enc.Save(outMs); return outMs.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or ArgumentException) { return null; }   // no WebP codec, broken file
    }

    private async Task WorkAsync()
    {
        Directory.CreateDirectory(_dir);
        while (!_cts.IsCancellationRequested)
        {
            if (!_queue.TryDequeue(out var job)) { await Task.Delay(500, _cts.Token).ConfigureAwait(false); continue; }
            var (item, url) = job;
            var file = FileFor(_dir, item);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var bytes = await _http.GetByteArrayAsync(url, _cts.Token).ConfigureAwait(false);
                // keep a 64 px PNG, not the 256 px original (75 KB each → ~5 KB; 1,684 recipe icons would be 126 MB otherwise)
                var small = Shrink(bytes);
                if (small is null) { _mem[item] = null; continue; }
                await File.WriteAllBytesAsync(file, small, _cts.Token).ConfigureAwait(false);
                _mem.TryRemove(item, out _);   // Get() decodes it on the UI thread next time
                Application.Current?.Dispatcher.BeginInvoke(() => Loaded?.Invoke());
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _mem[item] = null;   // not retried this session
                if (ex is not TaskCanceledException) Log.Info($"Icon {item} not fetched: {ex.Message}");
            }
            try { await Task.Delay(400, _cts.Token).ConfigureAwait(false); } catch (TaskCanceledException) { }
        }
    }

    public void Dispose() { _cts.Cancel(); _http.Dispose(); }
}
