using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AionMeter.Core.Combat;
using AionMeter.Core.Storage;

namespace AionMeter.Service.Overlay;

/// <summary>`fight-detail` reply: the concrete id ("s:…"/"h:…"), the id asked for, whether the fight still runs, the whole fight.</summary>
public sealed record FightDetailReply(string Id, string Asked, bool Live, FightRecord Record);

/// <summary>`price-polled`: whether the poll ran (else why: offline, empty, cooldown, busy), items read / failed, when Check now is allowed again (Unix ms, 0 = now).</summary>
public sealed record PricePollReply(bool Ran, string Reason, int Items, int Errors, long Next);

/// <summary>
/// The client side of <see cref="OverlayServer"/> without any UI toolkit (AionMeter.Linux has its own Avalonia copy, DaemonClient; A2M-50: this one
/// carries every message DaemonClient does, for the GTK card's hosted views).
/// Newline-delimited JSON; reconnects with backoff (0.5 s doubling to 5 s) while the daemon is away. Every event is raised through
/// <c>post</c> (the UI thread's dispatcher); only the newest overlay snapshot is delivered when the UI falls behind.
/// </summary>
public sealed class OverlayClient : IDisposable
{
    private readonly string _path;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action _deliverOverlay;
    private Socket? _socket;
    private OverlayModel? _pending;
    private int _posted;

    public OverlayClient(string path, Action<Action> post)
    {
        _path = path;
        _post = post;
        _deliverOverlay = () =>
        {
            Interlocked.Exchange(ref _posted, 0);
            if (Interlocked.Exchange(ref _pending, null) is { } latest) Overlay?.Invoke(latest);
        };
    }

    public string Path => _path;
    public bool Connected { get; private set; }
    public event Action<OverlayModel>? Overlay;
    public event Action<OverlayStatus>? Status;
    public event Action<OverlayFightItem[]>? Fights;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? FightClosed;
    /// <summary>`fight-detail` reply: the concrete id ("s:…"/"h:…"), the id asked for, whether the fight still runs, the whole fight.</summary>
    public event Action<FightDetailReply>? FightDetail;
    /// <summary>`settings-reloaded`: the daemon took settings.json (language it now uses).</summary>
    public event Action<string>? SettingsReloaded;
    public event Action<string>? Error;
    /// <summary>`position` (≤ 2 Hz): the atlas point, heading and the spots in view. The newest one is kept in <see cref="LastPosition"/>.</summary>
    public event Action<PositionModel>? Position;
    public PositionModel? LastPosition { get; private set; }
    /// <summary>`timers-changed`: a `timer` command rewrote boss-timers.json (reload it now instead of on the next mtime check).</summary>
    public event Action? TimersChanged;
    /// <summary>`boss-alert`: the alert text (also in the overlay `announcement` for 60 s).</summary>
    public event Action<string>? BossAlert;
    /// <summary>`node-name-wanted`: the player started gathering an object code nobody has named (the atlas asks).</summary>
    public event Action<int>? NodeNameWanted;
    /// <summary>`price-polled`: the answer to `price-poll`, sent once price-watch.json and price-history.json are written.</summary>
    public event Action<PricePollReply>? PricePolled;

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
        _post(() => ConnectionChanged?.Invoke(on));
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
                    if (Interlocked.Exchange(ref _posted, 1) == 0) _post(_deliverOverlay);
                    break;
                case "status":
                    if (data.Deserialize(OverlayJson.Default.OverlayStatus) is { } st) _post(() => Status?.Invoke(st));
                    break;
                case "fights":
                    if (data.Deserialize(OverlayJson.Default.OverlayFightItemArray) is { } list) _post(() => Fights?.Invoke(list));
                    break;
                case "fight-closed":
                    var title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    _post(() => FightClosed?.Invoke(title));
                    break;
                case "fight-detail":
                    var rec = data.GetProperty("record").Deserialize<FightRecord>(HistoryStore.JsonOptions);
                    if (rec is null) return;
                    var reply = new FightDetailReply(data.GetProperty("id").GetString() ?? "", data.TryGetProperty("asked", out var a) ? a.GetString() ?? "" : "",
                        data.TryGetProperty("live", out var lv) && lv.GetBoolean(), rec);
                    _post(() => FightDetail?.Invoke(reply));
                    break;
                case "settings-reloaded":
                    var lang = data.TryGetProperty("language", out var l) ? l.GetString() ?? "" : "";
                    _post(() => SettingsReloaded?.Invoke(lang));
                    break;
                case "price-polled":
                    var pp = new PricePollReply(data.GetProperty("ran").GetBoolean(), data.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "",
                        data.TryGetProperty("items", out var it) ? it.GetInt32() : 0, data.TryGetProperty("errors", out var er) ? er.GetInt32() : 0,
                        data.TryGetProperty("next", out var nx) ? nx.GetInt64() : 0);
                    _post(() => PricePolled?.Invoke(pp));
                    break;
                case "error":
                    var msg = root.TryGetProperty("data", out var ed) && ed.ValueKind == JsonValueKind.Object && ed.TryGetProperty("message", out var mm) ? mm.GetString() ?? ""
                            : root.TryGetProperty("message", out var m2) ? m2.GetString() ?? "" : "";
                    _post(() => Error?.Invoke(msg));
                    break;
                case "position":
                    if (data.Deserialize(OverlayJson.Default.PositionModel) is { } pos)
                        _post(() => { LastPosition = pos; Position?.Invoke(pos); });
                    break;
                case "timers-changed":
                    _post(() => TimersChanged?.Invoke());
                    break;
                case "boss-alert":
                    var alert = data.TryGetProperty("text", out var at) ? at.GetString() ?? "" : "";
                    _post(() => BossAlert?.Invoke(alert));
                    break;
                case "node-name-wanted":
                    var code = data.TryGetProperty("code", out var cd) ? cd.GetInt32() : 0;
                    if (code != 0) _post(() => NodeNameWanted?.Invoke(code));
                    break;
            }
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        catch (InvalidOperationException) { }
        catch (NotSupportedException) { }
    }

    /// <summary>Fire-and-forget command: reset, ping, show-fight (id), set-measure (mode), fights (max).</summary>
    public void Send(string cmd) => Write($"{{\"cmd\":\"{cmd}\"}}\n");

    public void Send(string cmd, string key, string value) => Write($"{{\"cmd\":\"{cmd}\",\"{key}\":\"{JsonEncodedText.Encode(value)}\"}}\n");

    public void Send(string cmd, string key, int value) => Write($"{{\"cmd\":\"{cmd}\",\"{key}\":{value}}}\n");

    /// <summary>`timer` command: killed / watch / unwatch / respawn (<paramref name="value"/> minutes) / remove on one server's boss.</summary>
    public void Timer(string action, int server, int npc, int value = 0) =>
        Write($"{{\"cmd\":\"timer\",\"id\":\"{action}\",\"server\":{server},\"npc\":{npc},\"value\":{value}}}\n");

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
