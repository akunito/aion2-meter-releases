using System.Net.Sockets;
using System.Text;
using Avalonia.Threading;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux.Shell;

/// <summary>
/// A2M-48 UX: the tools window's command socket ($XDG_RUNTIME_DIR/aionmeter-ui.sock, UiChannel): the layer-shell card's pills and a second
/// `aionmeter-ui` send show/hide/raise/toggle/quit here. Each command runs on the UI thread. Mode 0600; the file goes with the process.
/// </summary>
public sealed class UiCommandServer : IDisposable
{
    private readonly string _path;
    private readonly Socket _listener;
    private readonly Action<UiCommand> _handle;
    private volatile bool _disposed;

    private UiCommandServer(string path, Socket listener, Action<UiCommand> handle)
    {
        _path = path;
        _listener = listener;
        _handle = handle;
    }

    /// <summary>Listens on <paramref name="path"/>; null if it cannot (logged). Call only after <see cref="UiChannel.TrySend"/> found nobody there.</summary>
    public static UiCommandServer? Start(string path, Action<UiCommand> handle)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);   // stale: nobody answered on it
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            s.Listen(4);
            var server = new UiCommandServer(path, s, handle);
            _ = Task.Run(server.AcceptLoop);
            return server;
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            Log.Error("UI command socket " + path, e);
            return null;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_disposed)
        {
            Socket c;
            try { c = await _listener.AcceptAsync().ConfigureAwait(false); }
            catch (Exception) when (_disposed) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private void Serve(Socket c)
    {
        using var _ = c;
        try
        {
            c.ReceiveTimeout = 2000;
            using var stream = new NetworkStream(c);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // one short-lived client per command in practice; a few lines at most
            for (var i = 0; i < 8 && reader.ReadLine() is { } line; i++)
                if (UiChannel.TryParse(line, out var cmd)) Dispatcher.UIThread.Post(() => _handle(cmd));
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _listener.Dispose(); } catch (SocketException) { }
        try { File.Delete(_path); } catch (IOException) { }
    }
}

/// <summary>`swaymsg` for what X11 cannot ask Sway (raise past focus_on_window_activation urgent, move to an output). No-op outside Sway.</summary>
public static class SwayIpc
{
    public static bool Available => Environment.GetEnvironmentVariable("SWAYSOCK") is { Length: > 0 } s && File.Exists(s);

    /// <summary>Runs swaymsg with <paramref name="args"/>; stdout, or null (not Sway, not found, timeout 1.5 s, non-zero exit).</summary>
    public static string? Run(params string[] args)
    {
        if (!Available) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("swaymsg") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(1500)) { try { p.Kill(); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? stdout.Result : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>A command on one X11 window (`[id=…]` matches the X11 window id of an Xwayland view).</summary>
    public static void OnWindow(IntPtr xid, string command)
    {
        if (xid == IntPtr.Zero) return;
        Run($"[id={(long)xid}] {command}");
    }
}

/// <summary>
/// A2M-48 UX, measured 2026-10-13: with Sway's xwayland_zero_scaling (DESK) X11 windows get physical pixels and Avalonia 11.3 renders at
/// scale 1 (it does not read Xft.dpi): sharp but 2/3 size on the 4K scale-1.5 output. Each output's scale goes to
/// AVALONIA_SCREEN_SCALE_FACTORS (matched by RandR name = Sway connector) before the platform starts. Without zero scaling Sway already
/// scales X11 (blurred) and a factor here would apply twice.
/// </summary>
public static class ScreenScales
{
    public static void Apply()
    {
        if (Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS") is { Length: > 0 } || Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") is { Length: > 0 }) return;
        if (SwayIpc.Run("-t", "get_config", "-r") is not { } cfgJson) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(cfgJson);
            if (!doc.RootElement.TryGetProperty("config", out var c) || !UiScale.ZeroScaling(c.GetString() ?? "")) return;
        }
        catch (System.Text.Json.JsonException) { return; }
        if (SwayIpc.Run("-t", "get_outputs", "-r") is not { } outs || UiScale.ScreenFactors(outs) is not { } factors) return;
        Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", factors);
        Console.Error.WriteLine("xwayland_zero_scaling: AVALONIA_SCREEN_SCALE_FACTORS=" + factors);
    }
}
