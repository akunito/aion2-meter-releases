using System.Collections.Concurrent;
using System.Text.Json;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>
/// A2M-50 window mode: follows the card's own container in Sway (two IPC connections, two background threads, no GTK). A fresh map (start,
/// SIGUSR2 show) gets the saved geometry back at once (<see cref="WindowGeometry.Restore"/>); after that every rect change is reported to the
/// main loop, which saves it.
/// Measured in Sway 1.11's source (2026-10-14): `window` events cover new/close/focus/floating/move-to-workspace, but an interactive floating
/// move or resize (Alt+drag, xdg move/resize) and `move position` emit nothing, so the tree is also read once a second (get_tree on the
/// existing socket, no swaymsg process); a `window` event for this pid reads it at once.
/// </summary>
internal sealed class SwayWindow : IDisposable
{
    private readonly string _path, _title;
    private readonly int _pid = Environment.ProcessId;
    private readonly Action<Action> _post;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<string> _commands = new();
    private SwayIpc.Connection? _events;
    private volatile bool _stop;
    private long _conId;

    /// <summary>What a fresh map is restored to (written by the main loop, read by the poll thread).</summary>
    public WinGeom Saved { get => _saved; set { lock (_commands) _saved = value; } }
    private WinGeom _saved;

    /// <summary>Main loop: the card's container appeared (already restored) or changed; null = unmapped (hidden).</summary>
    public event Action<SwayNode?>? Changed;

    public long ConId => Interlocked.Read(ref _conId);

    private SwayWindow(string path, string title, Action<Action> post)
    {
        _path = path;
        _title = title;
        _post = post;
    }

    /// <summary>Null outside Sway ($SWAYSOCK unset or gone): the window then opens wherever the compositor puts it.</summary>
    public static SwayWindow? Start(string title, Action<Action> post, WinGeom saved)
    {
        if (SwayIpc.SocketPath is not { } path) return null;
        var w = new SwayWindow(path, title, post) { _saved = saved };
        new Thread(w.Poll) { IsBackground = true, Name = "sway-poll" }.Start();
        new Thread(w.Events) { IsBackground = true, Name = "sway-events" }.Start();
        return w;
    }

    /// <summary>Runs a command on the poll thread; the tree is read right after.</summary>
    public void Command(string cmd)
    {
        _commands.Enqueue(cmd);
        _wake.Set();
    }

    /// <summary>Reads the tree now (after Present, a tab change).</summary>
    public void Poke() => _wake.Set();

    private void Poll()
    {
        using var c = SwayIpc.Connection.Open(_path);
        if (c is null) { Console.Error.WriteLine("overlay: sway IPC connect failed: " + _path); return; }
        SwayNode? last = null;
        while (!_stop)
        {
            while (_commands.TryDequeue(out var cmd)) Run(c, cmd);
            if (c.Request(SwayIpc.GetTree) is not { } tree) break;
            var node = WindowGeometry.FindOwn(tree, _pid, _title);
            if (node is { } n && n.Id != last?.Id)
            {
                Interlocked.Exchange(ref _conId, n.Id);
                // a fresh map (start or show after a hide): Sway put it at its own place (the for_window rules make it floating centred)
                WinGeom g;
                lock (_commands) g = _saved;
                if (n.Floating && c.Request(SwayIpc.GetOutputs) is { } outs && WindowGeometry.Restore(n.Id, g, WindowGeometry.Outputs(outs)) is { } cmd)
                {
                    Run(c, cmd);
                    Console.Error.WriteLine($"overlay: mapped con {n.Id} at {n.X},{n.Y} {n.W}x{n.H} on {n.Output}; restore: {cmd}");
                    if (c.Request(SwayIpc.GetTree) is { } t2) node = WindowGeometry.FindOwn(t2, _pid, _title);
                }
                else Console.Error.WriteLine($"overlay: mapped con {n.Id} at {n.X},{n.Y} {n.W}x{n.H} on {n.Output}{(n.Floating ? "" : " (tiled)")}");
            }
            if (node != last)
            {
                last = node;
                if (node is null) Interlocked.Exchange(ref _conId, 0);
                _post(() => Changed?.Invoke(node));
            }
            _wake.WaitOne(1000);
        }
    }

    private static void Run(SwayIpc.Connection c, string cmd)
    {
        if (c.Request(SwayIpc.RunCommand, cmd) is not { } reply) return;
        // [{"success":true},…]: a refused command (no such container, not floating) is logged, never fatal
        try
        {
            using var doc = JsonDocument.Parse(reply);
            foreach (var r in doc.RootElement.EnumerateArray())
                if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                    Console.Error.WriteLine($"overlay: sway refused '{cmd}': {(r.TryGetProperty("error", out var e) ? e.GetString() : "?")}");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
    }

    private void Events()
    {
        try
        {
            using var c = SwayIpc.Connection.Open(_path);
            if (c is null) return;
            _events = c;
            if (c.Request(SwayIpc.Subscribe, "[\"window\"]") is null) return;
            while (!_stop && c.Read() is { } m)
            {
                if (m.Type != SwayIpc.WindowEvent) continue;
                if (OwnEvent(m.Payload)) _wake.Set();
            }
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ObjectDisposedException) { }
    }

    /// <summary>A `window` event about a view of this process (the card or one of its dialogs).</summary>
    private bool OwnEvent(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("container", out var con) && con.TryGetProperty("pid", out var p)
                && p.ValueKind == JsonValueKind.Number && p.GetInt32() == _pid;
        }
        catch (JsonException) { return false; }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _events?.Dispose();
    }
}
