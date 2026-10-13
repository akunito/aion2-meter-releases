using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux;

/// <summary>
/// The daemon's socket (OverlayServer): newline-delimited JSON. Reconnects with backoff (0.5 s doubling to 5 s) while the daemon is away.
/// Events are raised on the UI thread; only the newest overlay snapshot is delivered when the UI falls behind.
/// </summary>
public sealed class DaemonClient : IDisposable
{
    private readonly string _path;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;
    private OverlayModel? _pending;
    private int _posted;

    public DaemonClient(string path) => _path = path;

    public string Path => _path;
    public bool Connected { get; private set; }
    public event Action<OverlayModel>? Overlay;
    public event Action<OverlayStatus>? Status;
    public event Action<OverlayFightItem[]>? Fights;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? FightClosed;

    public void Start() => _ = Task.Run(Loop);

    private async Task Loop()
    {
        var delay = 500;
        var buf = new byte[256 * 1024];
        while (!_cts.IsCancellationRequested)
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await s.ConnectAsync(new UnixDomainSocketEndPoint(_path), _cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                s.Dispose();
                try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                delay = Math.Min(delay * 2, 5_000);
                continue;
            }
            delay = 500;
            _socket = s;
            SetConnected(true);
            var filled = 0;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (filled == buf.Length) Array.Resize(ref buf, buf.Length * 2);   // a fights list of hundreds of entries
                    var n = await s.ReceiveAsync(buf.AsMemory(filled), SocketFlags.None, _cts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    filled += n;
                    int start = 0, nl;
                    while ((nl = Array.IndexOf(buf, (byte)'\n', start, filled - start)) >= 0)
                    {
                        Handle(buf.AsMemory(start, nl - start));
                        start = nl + 1;
                    }
                    if (start > 0) { Buffer.BlockCopy(buf, start, buf, 0, filled - start); filled -= start; }
                }
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException) { }
            _socket = null;
            s.Dispose();
            SetConnected(false);
        }
    }

    private void SetConnected(bool on)
    {
        Connected = on;
        Dispatcher.UIThread.Post(() => ConnectionChanged?.Invoke(on));
    }

    private void Handle(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            root.TryGetProperty("data", out var data);
            switch (type)
            {
                case "overlay":
                    var m = data.Deserialize(OverlayJson.Default.OverlayModel);
                    if (m is null) return;
                    Volatile.Write(ref _pending, m);
                    if (Interlocked.Exchange(ref _posted, 1) == 0)
                        Dispatcher.UIThread.Post(() =>
                        {
                            Interlocked.Exchange(ref _posted, 0);
                            if (Interlocked.Exchange(ref _pending, null) is { } latest) Overlay?.Invoke(latest);
                        });
                    break;
                case "status":
                    if (data.Deserialize(OverlayJson.Default.OverlayStatus) is { } st) Dispatcher.UIThread.Post(() => Status?.Invoke(st));
                    break;
                case "fights":
                    if (data.Deserialize(OverlayJson.Default.OverlayFightItemArray) is { } list) Dispatcher.UIThread.Post(() => Fights?.Invoke(list));
                    break;
                case "fight-closed":
                    var title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    Dispatcher.UIThread.Post(() => FightClosed?.Invoke(title));
                    break;
            }
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Fire-and-forget command: reset, ping, show-fight (id), set-measure (mode), fights (max).</summary>
    public void Send(string cmd) => Write($"{{\"cmd\":\"{cmd}\"}}\n");

    public void Send(string cmd, string key, string value) => Write($"{{\"cmd\":\"{cmd}\",\"{key}\":\"{JsonEncodedText.Encode(value)}\"}}\n");

    public void Send(string cmd, string key, int value) => Write($"{{\"cmd\":\"{cmd}\",\"{key}\":{value}}}\n");

    private void Write(string json)
    {
        if (_socket is not { } s) return;
        try { s.Send(Encoding.UTF8.GetBytes(json)); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Dispose();
    }
}
