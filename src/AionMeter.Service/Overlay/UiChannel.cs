using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AionMeter.Service.Overlay;

/// <summary>
/// A2M-48: the card strip, left to right, as WPF OverlayWindow.xaml has it (TabMeter, TabAtlas, … TabTimers). The GTK card builds its strip
/// from this list; CardCommandTests checks it against the XAML.
/// </summary>
public static class UiTabs
{
    public static readonly string[] Strip = ["meter", "atlas", "top", "prices", "craft", "build", "items", "timers"];

    public static bool IsKnown(string key) => key == "settings" || Array.IndexOf(Strip, key) >= 0;
}

/// <summary>A2M-48: "UI scale" (Settings → Overlay, the wheel over the card's resize corner): the card's zoom, kept in ui.json.</summary>
public static class UiScale
{
    public const double Min = 0.8, Max = 2.0, Step = 0.1;

    /// <summary>Clamped to 80–200 % on a 5 % grid (wheel steps never drift); NaN/∞ → 100 %.</summary>
    public static double Clamp(double v) => double.IsFinite(v) ? Math.Round(Math.Clamp(v, Min, Max) * 20) / 20 : 1;

    /// <summary>ui.json's "UiScale" (1 when missing or unreadable).</summary>
    public static double Read(string uiJson)
    {
        try
        {
            if (!File.Exists(uiJson)) return 1;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(uiJson));
            return doc.RootElement.TryGetProperty("UiScale", out var p) && p.TryGetDouble(out var v) ? Clamp(v) : 1;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { return 1; }
    }

    /// <summary>Sets only "UiScale" in ui.json (other keys stay, ui.json predates the card), aside + rename.</summary>
    public static void Write(string uiJson, double scale) => UiJson.Write(uiJson, "UiScale", Clamp(scale));
}

/// <summary>
/// A2M-50: single keys of ui.json for the card (the file was the retired Avalonia UI's, its other keys are kept): read one key, patch one key
/// (JsonNode, aside + rename).
/// </summary>
public static class UiJson
{
    /// <summary>A string key ("LastTab"); null when missing, not a string or the file is unreadable.</summary>
    public static string? ReadString(string uiJson, string key)
    {
        try
        {
            if (!File.Exists(uiJson)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(uiJson));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Sets <paramref name="key"/> only (a broken file is replaced by an object holding just this key), aside + rename.</summary>
    public static void Write(string uiJson, string key, JsonNode? value)
    {
        try
        {
            JsonObject root;
            try { root = File.Exists(uiJson) ? JsonNode.Parse(File.ReadAllBytes(uiJson)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (JsonException) { root = new JsonObject(); }
            root[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(uiJson)!);
            var tmp = uiJson + ".tmp-ov";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, uiJson, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public enum UiCommandKind { Show, Hide, Raise, Toggle, Quit }

/// <summary>One line on the card's command socket. <see cref="Tab"/> only with Show (null = keep the tab in front).</summary>
public readonly record struct UiCommand(UiCommandKind Kind, string? Tab = null);

/// <summary>
/// A2M-50: the card listens on $XDG_RUNTIME_DIR/aionmeter-overlay.sock (newline JSON: {"cmd":"show","tab":"items"}, {"cmd":"hide"},
/// {"cmd":"raise"}, {"cmd":"toggle"}, {"cmd":"quit"}); a second `aionmeter-overlay` (single instance, --show/--hide/--toggle/--quit) and the
/// dotfiles helpers talk to it (`printf '{"cmd":"raise"}\n' | socat - UNIX-CONNECT:…` works too). Was the Avalonia tools window's
/// aionmeter-ui.sock until 2026-10-14.
/// </summary>
public static class UiChannel
{
    public static string RunDir() =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } rd && Directory.Exists(rd) ? rd : Path.GetTempPath();

    public static string DefaultPath() => Path.Combine(RunDir(), "aionmeter-overlay.sock");

    public static bool TryParse(string line, out UiCommand cmd)
    {
        cmd = default;
        if (string.IsNullOrWhiteSpace(line) || line.Length > 512) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cmd", out var c) || c.ValueKind != JsonValueKind.String) return false;
            string? tab = null;
            if (root.TryGetProperty("tab", out var t))
            {
                if (t.ValueKind != JsonValueKind.String || t.GetString() is not { } ts || !UiTabs.IsKnown(ts)) return false;
                tab = ts;
            }
            UiCommandKind kind;
            switch (c.GetString())
            {
                case "show": kind = UiCommandKind.Show; break;
                case "hide": kind = UiCommandKind.Hide; break;
                case "raise": kind = UiCommandKind.Raise; break;
                case "toggle": kind = UiCommandKind.Toggle; break;
                case "quit": kind = UiCommandKind.Quit; break;
                default: return false;
            }
            if (tab is not null && kind != UiCommandKind.Show) return false;
            cmd = new UiCommand(kind, tab);
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static string Format(UiCommand c)
    {
        var name = c.Kind switch
        {
            UiCommandKind.Show => "show", UiCommandKind.Hide => "hide", UiCommandKind.Raise => "raise", UiCommandKind.Toggle => "toggle", _ => "quit",
        };
        return c.Tab is null ? $"{{\"cmd\":\"{name}\"}}\n" : $"{{\"cmd\":\"{name}\",\"tab\":\"{c.Tab}\"}}\n";
    }

    /// <summary>Delivers one command to a running card (null: only checks that one listens); false when none listens (no socket, refused, timeout).</summary>
    public static bool TrySend(string path, UiCommand? c, int timeoutMs = 600)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.SendTimeout = timeoutMs;
            var connect = s.ConnectAsync(new UnixDomainSocketEndPoint(path));
            if (!connect.Wait(timeoutMs) || !s.Connected) return false;
            if (c is { } cmd) s.Send(Encoding.UTF8.GetBytes(Format(cmd)));
            return true;
        }
        catch (Exception e) when (e is SocketException or IOException or AggregateException or ObjectDisposedException) { return false; }
    }
}

/// <summary>
/// A2M-50: the card's command socket (<see cref="UiChannel"/>). Mode 0600; the file goes with the process. Each command runs through
/// <c>post</c> (the GTK main loop); a client sends one line in practice, at most 8 are read per connection.
/// </summary>
public sealed class UiCommandServer : IDisposable
{
    private readonly string _path;
    private readonly Socket _listener;
    private readonly Action<UiCommand> _handle;
    private readonly Action<Action> _post;
    private volatile bool _disposed;

    private UiCommandServer(string path, Socket listener, Action<UiCommand> handle, Action<Action> post)
    {
        _path = path;
        _listener = listener;
        _handle = handle;
        _post = post;
    }

    /// <summary>Listens on <paramref name="path"/>; null if it cannot (logged). Call only after <see cref="UiChannel.TrySend"/> found nobody there.</summary>
    public static UiCommandServer? Start(string path, Action<UiCommand> handle, Action<Action> post)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);   // stale: nobody answered on it
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            s.Listen(4);
            var server = new UiCommandServer(path, s, handle, post);
            _ = Task.Run(server.AcceptLoop);
            return server;
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException)
        {
            Log.Error("card command socket " + path, e);
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
            for (var i = 0; i < 8 && reader.ReadLine() is { } line; i++)
                if (UiChannel.TryParse(line, out var cmd)) _post(() => _handle(cmd));
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

/// <summary>The card's pid file in $XDG_RUNTIME_DIR (the dotfiles helpers signal it; never `pkill -f`).</summary>
public static class CardProcess
{
    public const string Dll = "AionMeter.Overlay.dll";
    public static string PidFile => Path.Combine(UiChannel.RunDir(), "aionmeter-overlay.pid");

    /// <summary>The pid in <paramref name="pidFile"/> when that process is alive and its cmdline holds <paramref name="marker"/> (stale file, reused pid).</summary>
    public static int LivePid(string pidFile, string marker)
    {
        try
        {
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) || pid <= 1) return 0;
            var cmd = File.ReadAllBytes($"/proc/{pid}/cmdline");
            return Encoding.UTF8.GetString(cmd).Contains(marker, StringComparison.Ordinal) ? pid : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }
}

/// <summary>
/// Settings → Daemon "Restart the daemon" (was the Avalonia tray's): `systemctl restart` asks polkit (an agent may prompt); when that fails
/// and SUDO_ASKPASS is set (dotfiles sudoAskpassEnable) `sudo -A` asks through the GUI askpass, as tools/linux/deploy.sh does. Both failing,
/// the caller shows <see cref="FailedText"/> with the command to run in a terminal.
/// </summary>
public static class DaemonControl
{
    public const string Unit = "aion2-meter-daemon";
    public static string Command => "systemctl restart " + Unit;

    /// <summary>UiText.TrayRestartFailed with the command.</summary>
    public static string FailedText() => string.Format(UiText.Current.TrayRestartFailed, "sudo " + Command);

    /// <summary>Blocking (up to 2 min per attempt: a password prompt waits for the user): true when the unit was restarted.</summary>
    public static bool Restart()
    {
        if (Run("systemctl", ["restart", Unit])) return true;
        return Environment.GetEnvironmentVariable("SUDO_ASKPASS") is { Length: > 0 } ap && File.Exists(ap) && Run("sudo", ["-A", "--", "systemctl", "restart", Unit]);
    }

    /// <summary>Runs <paramref name="exe"/> (PATH lookup) with stdin closed; true on exit 0. A missing program, a timeout or a non-zero exit is logged.</summary>
    public static bool Run(string exe, string[] args, int timeoutMs = 120_000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (InvalidOperationException) { } Log.Info($"daemon restart: {exe} timed out"); return false; }
            if (p.ExitCode == 0) { Log.Info($"daemon restart: {exe} {string.Join(' ', args)} ok"); return true; }
            Log.Info($"daemon restart: {exe} exit {p.ExitCode}: {err.Result.Trim()}");
            return false;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Info($"daemon restart: {exe}: {e.Message}"); return false; }
    }

    /// <summary>A desktop notification (notify-send); stderr when there is none.</summary>
    public static void Notify(string text)
    {
        try
        {
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(AppInfo.Name);
            psi.ArgumentList.Add(AppInfo.Name);
            psi.ArgumentList.Add(text);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.Error.WriteLine(text); }
    }
}
