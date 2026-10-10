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
    private readonly string _dir = Path.Combine(AppSettings.AppDataDir, "icons");
    private readonly ConcurrentDictionary<int, ImageSource?> _mem = new();
    private readonly ConcurrentQueue<int> _queue = new();
    private readonly ConcurrentDictionary<int, byte> _queued = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    public event Action? Loaded;

    public IconCache(ItemIcons index)
    {
        _index = index;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.3 (private build; item icons on demand, one request per 400 ms)");
    }

    /// <summary>The icon when it is on disk already (decoded at 56 px and frozen); null while it downloads or when there is none.</summary>
    public ImageSource? Get(int item)
    {
        if (_mem.TryGetValue(item, out var cached)) return cached;
        var rel = _index.Path(item);
        if (rel is null) { _mem[item] = null; return null; }
        var file = Path.Combine(_dir, item + Path.GetExtension(rel));
        if (File.Exists(file))
        {
            var img = Decode(file);
            _mem[item] = img;
            return img;
        }
        if (_queued.TryAdd(item, 0)) { _queue.Enqueue(item); _worker ??= Task.Run(WorkAsync); }
        return null;
    }

    private static ImageSource? Decode(string file)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.DecodePixelWidth = 56;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or FileFormatException or ArgumentException) { return null; }
    }

    private async Task WorkAsync()
    {
        Directory.CreateDirectory(_dir);
        while (!_cts.IsCancellationRequested)
        {
            if (!_queue.TryDequeue(out var item)) { await Task.Delay(500, _cts.Token).ConfigureAwait(false); continue; }
            var rel = _index.Path(item)!;
            var file = Path.Combine(_dir, item + Path.GetExtension(rel));
            try
            {
                var bytes = await _http.GetByteArrayAsync("https://aion2hub.me/media/icons/" + rel, _cts.Token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(file, bytes, _cts.Token).ConfigureAwait(false);
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
