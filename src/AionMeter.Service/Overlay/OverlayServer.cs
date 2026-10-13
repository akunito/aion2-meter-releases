using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AionMeter.Service.Overlay;

/// <summary>A command a client sent: `{"cmd":"reset"}`, `{"cmd":"show-fight","id":"…"}`, `{"cmd":"set-measure","mode":"boss|all"}`,
/// `{"cmd":"fights","max":40}`, `{"cmd":"fight-detail","id":"live|s:…|h:…"}`, `{"cmd":"reload-settings"}`, `{"cmd":"price-poll"[,"mode":"added"]}`, `{"cmd":"ping"}`,
/// `{"cmd":"timer","id":"killed|watch|unwatch|respawn|remove","server":N,"npc":N,"value":minutes}`.</summary>
public sealed record OverlayCommand(OverlayServer.Client From, string Cmd, string? Arg, int Max, int Server = 0, int Npc = 0, int Value = 0);

/// <summary>
/// A2M-48 phase 3: the daemon's Unix domain socket for the (unprivileged) Linux UI. Newline-delimited JSON both ways; any number of clients.
/// The server only moves bytes: commands queue up for the meter loop (<see cref="Commands"/>), which owns the engine and the selection.
/// A client that stops reading loses its oldest lines (8 queued at most), never blocks the meter.
/// </summary>
public sealed class OverlayServer : IDisposable
{
    private readonly Socket _listener;
    private readonly List<Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;   // under lock (_clients)

    public string Path { get; }
    public ConcurrentQueue<OverlayCommand> Commands { get; } = new();
    public int ClientCount { get { lock (_clients) return _clients.Count; } }
    /// <summary>Set when a client connected since the last check: the meter sends it `status` and an `overlay` right away.</summary>
    public bool TakeNewClient() => Interlocked.Exchange(ref _newClient, 0) != 0;
    private int _newClient;

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint uid, uint gid);

    /// <summary>$XDG_RUNTIME_DIR/aionmeter.sock, else /tmp/aionmeter-&lt;uid&gt;.sock (a system service has no XDG_RUNTIME_DIR unless the unit sets it).</summary>
    public static string DefaultPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg)) return System.IO.Path.Combine(xdg, "aionmeter.sock");
        return $"/tmp/aionmeter-{(OperatingSystem.IsWindows() ? 0 : GetEuid())}.sock";
    }

    public OverlayServer(string path)
    {
        Path = path;
        // A socket file left by a killed daemon refuses the bind (EADDRINUSE): remove it.
        if (File.Exists(path)) File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // build/daemon.sh runs the daemon as root in docker: AIONMETER_SOCKET_OWNER=uid:gid hands the socket to the desktop user.
            if (Environment.GetEnvironmentVariable("AIONMETER_SOCKET_OWNER") is { } owner && owner.Split(':') is [var u, var g]
                && uint.TryParse(u, out var uid) && uint.TryParse(g, out var gid))
                Chown(path, uid, gid);
        }
        _listener.Listen(8);
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            var c = new Client(this, s);
            bool disposed;
            lock (_clients) { disposed = _disposed; if (!disposed) _clients.Add(c); }
            if (disposed) { c.Close(); return; }   // accepted while Dispose ran: it already closed the list
            Interlocked.Exchange(ref _newClient, 1);
            c.Start(_cts.Token);
        }
    }

    /// <summary>One line (ending in '\n') to every client; the same array is queued for each.</summary>
    public void Broadcast(byte[] line)
    {
        lock (_clients)
            for (var i = 0; i < _clients.Count; i++) _clients[i].Send(line);
    }

    private void Drop(Client c)
    {
        lock (_clients) _clients.Remove(c);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Dispose(); } catch (SocketException) { }
        // Client.Close calls Drop, which re-enters this (reentrant) lock and removes from the list being enumerated:
        // "Collection was modified" in DaemonSmokeTests whenever a client was still open here. Close outside the lock.
        Client[] open;
        lock (_clients)
        {
            _disposed = true;
            open = _clients.ToArray();
            _clients.Clear();
        }
        for (var i = 0; i < open.Length; i++) open[i].Close();
        try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public sealed class Client
    {
        private readonly OverlayServer _server;
        private readonly Socket _socket;
        private readonly Channel<byte[]> _out = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true,
        });

        internal Client(OverlayServer server, Socket socket)
        {
            _server = server;
            _socket = socket;
        }

        public void Send(byte[] line) { if (line.Length > 0) _out.Writer.TryWrite(line); }

        internal void Start(CancellationToken ct)
        {
            _ = WriteLoop(ct);
            _ = ReadLoop(ct);
        }

        private async Task WriteLoop(CancellationToken ct)
        {
            try
            {
                await foreach (var line in _out.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await _socket.SendAsync(line, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
            Close();
        }

        private async Task ReadLoop(CancellationToken ct)
        {
            var buf = new byte[4096];
            var filled = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (filled == buf.Length) { filled = 0; continue; }   // a 4 KB line is not a command: drop it
                    var n = await _socket.ReceiveAsync(buf.AsMemory(filled), SocketFlags.None, ct).ConfigureAwait(false);
                    if (n == 0) break;
                    filled += n;
                    int start = 0, nl;
                    while ((nl = Array.IndexOf(buf, (byte)'\n', start, filled - start)) >= 0)
                    {
                        Parse(buf.AsSpan(start, nl - start));
                        start = nl + 1;
                    }
                    if (start > 0) { Buffer.BlockCopy(buf, start, buf, 0, filled - start); filled -= start; }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
            Close();
        }

        private void Parse(ReadOnlySpan<byte> line)
        {
            if (line.IsEmpty) return;
            string? cmd = null, arg = null;
            int max = 0, server = 0, npc = 0, value = 0;
            try
            {
                var r = new Utf8JsonReader(line);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("cmd"u8)) { r.Read(); cmd = r.GetString(); }
                    else if (r.ValueTextEquals("id"u8) || r.ValueTextEquals("mode"u8)) { r.Read(); arg = r.GetString(); }
                    else if (r.ValueTextEquals("max"u8)) { r.Read(); max = r.TokenType == JsonTokenType.Number ? r.GetInt32() : 0; }
                    else if (r.ValueTextEquals("server"u8)) { r.Read(); server = r.TokenType == JsonTokenType.Number ? r.GetInt32() : 0; }
                    else if (r.ValueTextEquals("npc"u8)) { r.Read(); npc = r.TokenType == JsonTokenType.Number ? r.GetInt32() : 0; }
                    else if (r.ValueTextEquals("value"u8)) { r.Read(); value = r.TokenType == JsonTokenType.Number ? r.GetInt32() : 0; }
                    else { r.Read(); r.Skip(); }
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
            if (cmd is null)
            {
                Send(Encoding.UTF8.GetBytes("{\"type\":\"error\",\"message\":\"expected {\\\"cmd\\\":…}\"}\n"));
                return;
            }
            _server.Commands.Enqueue(new OverlayCommand(this, cmd, arg, max, server, npc, value));
        }

        private int _closed;

        internal void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _out.Writer.TryComplete();
            try { _socket.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            _socket.Dispose();
            _server.Drop(this);
        }
    }
}
