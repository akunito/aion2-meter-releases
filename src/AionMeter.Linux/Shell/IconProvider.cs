using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AionMeter.Core.GameDb;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>
/// Item and skill icons as Avalonia bitmaps (A2M-48 phase 3b): the WPF IconCache + SkillIcons rules on Linux. Same disk cache as Windows
/// (`icons/` in the data dir: item icons as &lt;id&gt;.png, URL icons as &lt;prefix&gt;/&lt;stem&gt;.png, CDN skill icons as &lt;name&gt;.png), downloads
/// only while Settings → "Download icons" is on, one request per 400 ms for aion2hub/questlog, 64 px PNGs kept instead of the 256 px
/// originals. Lookups never block: a missing icon is queued and <see cref="Loaded"/> fires on the UI thread when it lands (coalesce redraws).
/// These files are the UI's own writes (the daemon never downloads icons).
/// </summary>
public sealed class IconProvider : IDisposable
{
    private const string SkillCdn = "https://assets.playnccdn.com/static-aion2-gamedata/resources/";
    private static readonly Dictionary<int, string> ClassCodes = new() { [11] = "GL", [12] = "TE", [13] = "AS", [14] = "RA", [15] = "SO", [16] = "EL", [17] = "CL", [18] = "CH", [19] = "GT" };

    private readonly string _dir;
    private readonly ItemIcons _items;
    private readonly Dictionary<int, string> _skillOverrides = new();
    private readonly ConcurrentDictionary<string, Bitmap?> _mem = new();
    private readonly ConcurrentQueue<(string Key, string Url, bool Paced)> _queue = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private int _loadedPosted;

    /// <summary>Settings → "Download icons": off = only what is on disk already.</summary>
    public bool Enabled { get; set; }
    /// <summary>Icon URL for ids item-icons.json lacks (questlog CDN, ItemNames.ExtraIconUrl of the viewer engine).</summary>
    public Func<int, string?>? ExtraUrl { get; set; }
    /// <summary>On the UI thread, at most once per 300 ms however many icons landed.</summary>
    public event Action? Loaded;

    public IconProvider(string dataDirectory, bool enabled)
    {
        Enabled = enabled;
        _dir = Path.Combine(AppSettings.AppDataDir, "icons");
        _items = ItemIcons.Load(Path.Combine(dataDirectory, "gamedb", "item-icons.json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 AION2DpsMeter/0.3 (private build; icons on demand, one request per 400 ms)");
        try
        {
            var path = Path.Combine(dataDirectory, "skill_icons.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } map)
                foreach (var (k, v) in map) if (int.TryParse(k, out var key) && v.Length > 0) _skillOverrides[key] = v;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    /// <summary>Item icon (aion2hub path or questlog URL from item-icons.json, else <see cref="ExtraUrl"/>), 56 px.</summary>
    public Bitmap? Item(int item)
    {
        var key = item.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_mem.TryGetValue(key, out var cached)) return cached;
        var rel = _items.Path(item);
        if (rel is not null) return GetOrQueue(key, rel.StartsWith("http", StringComparison.Ordinal) ? rel : "https://aion2hub.me/media/icons/" + rel, 56, true);
        return ExtraUrl?.Invoke(item) is { } url ? GetOrQueue(key, url, 56, true) : null;
    }

    /// <summary>Any icon by URL under <paramref name="key"/> ("skill:&lt;stem&gt;", "pet:…"; ':' becomes the folder separator).</summary>
    public Bitmap? Url(string key, string url, int decodeWidth = 56) =>
        _mem.TryGetValue(key, out var cached) ? cached : GetOrQueue(key, url, decodeWidth, true);

    /// <summary>Combat skill icon from the official CDN (WPF SkillIcons.IconName rules), 48 px; null for mob skills and basic variants.</summary>
    public Bitmap? Skill(int skillCode) => SkillIconName(skillCode) is { } name ? (_mem.TryGetValue(name, out var c) ? c : GetOrQueue(name, SkillCdn + name + ".png", 48, false)) : null;

    public string? SkillIconName(int skillCode)
    {
        var text = skillCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (skillCode > 999 && int.TryParse(text.AsSpan(0, 4), out var key) && _skillOverrides.TryGetValue(key, out var icon)) return icon;
        if (skillCode is >= 30_000_000 and <= 30_999_999 && text.Length >= 7)
            return int.TryParse(text.AsSpan(5, 2), out var stone) && stone > 0 ? $"Icon_Item_Usable_Godstone_WP_r_{stone:x3}" : null;
        if (skillCode is < 10_000_000 or > 19_999_999) return null;
        if (skillCode / 100 % 10_000 == 0 && skillCode % 100 != 0) return null;
        if (!ClassCodes.TryGetValue(skillCode / 1_000_000, out var cls)) return null;
        var sub = skillCode / 10_000 % 100;
        var passive = sub >= 70;
        if (passive) sub -= 70;
        return $"ICON_{cls}_SKILL_{(passive ? "Passive_" : "")}{sub:D3}";
    }

    private string FileFor(string key) => Path.Combine(_dir, key.Replace(':', Path.DirectorySeparatorChar) + ".png");

    private Bitmap? GetOrQueue(string key, string url, int width, bool paced)
    {
        var file = FileFor(key);
        if (File.Exists(file))
        {
            var img = Decode(file, width);
            _mem[key] = img;
            return img;
        }
        if (Enabled && _queued.TryAdd(key, 0)) { _queue.Enqueue((key, url, paced)); _worker ??= Task.Run(WorkAsync); }
        return null;
    }

    private static Bitmap? Decode(string file, int width)
    {
        try
        {
            using var s = File.OpenRead(file);
            return Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A 64 px PNG of the download (75 KB originals → ~5 KB); null when Skia cannot decode it.</summary>
    private static byte[]? Shrink(byte[] original)
    {
        try
        {
            using var ms = new MemoryStream(original);
            using var bmp = Bitmap.DecodeToWidth(ms, 64, BitmapInterpolationMode.HighQuality);
            using var outMs = new MemoryStream();
            bmp.Save(outMs);
            return outMs.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException) { return null; }
    }

    private async Task WorkAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!_queue.TryDequeue(out var job))
            {
                try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); } catch (TaskCanceledException) { return; }
                continue;
            }
            var file = FileFor(job.Key);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var bytes = await _http.GetByteArrayAsync(job.Url, _cts.Token).ConfigureAwait(false);
                var small = job.Paced ? Shrink(bytes) : bytes;   // CDN skill icons are small already (WPF keeps them as they come)
                if (small is null) { _mem[job.Key] = null; continue; }
                var tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, small, _cts.Token).ConfigureAwait(false);
                File.Move(tmp, file, overwrite: true);
                _mem.TryRemove(job.Key, out _);   // decoded on the next lookup
                if (Interlocked.Exchange(ref _loadedPosted, 1) == 0)
                    Dispatcher.UIThread.Post(async () => { await Task.Delay(300); _loadedPosted = 0; Loaded?.Invoke(); });
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                _mem[job.Key] = null;   // not retried this session
                if (e is not TaskCanceledException) Log.Info($"Icon {job.Key} not fetched: {e.Message}");
            }
            if (job.Paced) try { await Task.Delay(400, _cts.Token).ConfigureAwait(false); } catch (TaskCanceledException) { return; }
        }
    }

    public void Dispose() { _cts.Cancel(); _http.Dispose(); }
}
