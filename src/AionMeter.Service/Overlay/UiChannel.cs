using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AionMeter.Service.Overlay;

/// <summary>
/// A2M-48: the card strip, left to right, as WPF OverlayWindow.xaml has it (TabMeter, TabAtlas, … TabTimers). Both Linux front-ends build
/// their strip from this list; LinuxUxTests checks it against the XAML.
/// </summary>
public static class UiTabs
{
    public static readonly string[] Strip = ["meter", "atlas", "top", "prices", "craft", "build", "items", "timers"];

    /// <summary>What the tools window opens on when nothing was used before: the DPS tab is the overlay's job.</summary>
    public const string DefaultToolTab = "items";

    public static bool IsKnown(string key) => key == "settings" || Array.IndexOf(Strip, key) >= 0;

    /// <summary>The tab the tools window starts on: the last one used unless it was DPS / Settings / unknown.</summary>
    public static string StartTab(string? last) => last is { Length: > 0 } && last != "meter" && last != "settings" && IsKnown(last) ? last : DefaultToolTab;
}

/// <summary>A2M-48: "UI scale" (Settings → Overlay, Ctrl+wheel): one zoom for the Avalonia window and the layer-shell card, kept in ui.json.</summary>
public static class UiScale
{
    public const double Min = 0.8, Max = 2.0, Step = 0.1;

    /// <summary>Clamped to 80–200 % on a 5 % grid (Ctrl+wheel steps never drift); NaN/∞ → 100 %.</summary>
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

    /// <summary>
    /// AVALONIA_SCREEN_SCALE_FACTORS for Sway with xwayland_zero_scaling (X11 sees physical pixels, so Avalonia must render at each output's
    /// scale itself): "DP-1=1.5;DP-2=1.25" from `swaymsg -t get_outputs -r` (active outputs only); null when there is nothing to set.
    /// </summary>
    public static string? ScreenFactors(string outputsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var sb = new StringBuilder();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False) continue;
                if (!o.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                if (!o.TryGetProperty("scale", out var sc) || !sc.TryGetDouble(out var scale) || scale <= 0) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(name).Append('=').Append(scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether Sway's config turns xwayland_zero_scaling on (launch-time only, so the loaded config is the truth).</summary>
    public static bool ZeroScaling(string swayConfig) =>
        System.Text.RegularExpressions.Regex.IsMatch(swayConfig, @"^\s*xwayland_zero_scaling\s+(yes|enable|true|on)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Sets only "UiScale" in ui.json (the Avalonia UI owns the rest of the file), aside + rename.</summary>
    public static void Write(string uiJson, double scale)
    {
        try
        {
            JsonObject root;
            try { root = File.Exists(uiJson) ? JsonNode.Parse(File.ReadAllBytes(uiJson)) as JsonObject ?? new JsonObject() : new JsonObject(); }
            catch (JsonException) { root = new JsonObject(); }
            root["UiScale"] = Clamp(scale);
            Directory.CreateDirectory(Path.GetDirectoryName(uiJson)!);
            var tmp = uiJson + ".tmp-ov";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, uiJson, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public enum UiCommandKind { Show, Hide, Raise, Toggle, Quit }

/// <summary>One line on the tools window's socket. <see cref="Tab"/> only with Show (null = keep the tab in front).</summary>
public readonly record struct UiCommand(UiCommandKind Kind, string? Tab = null);

/// <summary>
/// A2M-48: the Avalonia tools window listens on $XDG_RUNTIME_DIR/aionmeter-ui.sock (newline JSON: {"cmd":"show","tab":"items"},
/// {"cmd":"hide"}, {"cmd":"raise"}, {"cmd":"toggle"}, {"cmd":"quit"}). The layer-shell card's tab pills and a second `aionmeter-ui`
/// (single instance) talk to it.
/// </summary>
public static class UiChannel
{
    public static string RunDir() =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } rd && Directory.Exists(rd) ? rd : Path.GetTempPath();

    public static string DefaultPath() => Path.Combine(RunDir(), "aionmeter-ui.sock");

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

    /// <summary>Delivers one command to a running tools window (null: only checks that one listens); false when none listens (no socket, refused, timeout).</summary>
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

/// <summary>A2M-48: the two Linux front-ends find, signal and start each other (pid files in $XDG_RUNTIME_DIR, never `pkill -f`).</summary>
public static class FrontEnds
{
    public const int SigUsr1 = 10, SigUsr2 = 12;
    public static string OverlayPidFile => Path.Combine(UiChannel.RunDir(), "aionmeter-overlay.pid");
    public static string UiPidFile => Path.Combine(UiChannel.RunDir(), "aionmeter-ui.pid");
    public const string OverlayDll = "AionMeter.Overlay.dll", UiDll = "AionMeter.Linux.dll";

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

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

    public static bool Signal(int pid, int sig) => pid > 1 && Kill(pid, sig) == 0;

    /// <summary>Starts <paramref name="command"/> detached from this process (setsid -f), PATH lookup; false if it could not start.</summary>
    public static bool Launch(string command, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("setsid") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = false };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(command);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.StandardInput.Close();
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>The tools window on <paramref name="tab"/>: tell the running one, else start `aionmeter-ui --tab` ($AIONMETER_UI_LAUNCH overrides the command).</summary>
    public static void OpenUi(string? tab)
    {
        var cmd = new UiCommand(UiCommandKind.Show, tab);
        if (UiChannel.TrySend(UiChannel.DefaultPath(), cmd)) return;
        // starting up: the pid file is written before the socket listens
        if (LivePid(UiPidFile, UiDll) != 0)
        {
            for (var i = 0; i < 20; i++) { Thread.Sleep(250); if (UiChannel.TrySend(UiChannel.DefaultPath(), cmd)) return; }
            return;
        }
        var launch = Environment.GetEnvironmentVariable("AIONMETER_UI_LAUNCH") is { Length: > 0 } l ? l : "aionmeter-ui";
        if (tab is null) Launch(launch);
        else Launch(launch, "--tab", tab);
    }

    /// <summary>The layer-shell card: SIGUSR2 (hide/show) if it runs, else start `aionmeter-overlay`. False when neither worked.</summary>
    public static bool ToggleOverlay()
    {
        var pid = LivePid(OverlayPidFile, OverlayDll);
        return pid != 0 ? Signal(pid, SigUsr2) : Launch(Environment.GetEnvironmentVariable("AIONMETER_OVERLAY_LAUNCH") is { Length: > 0 } l ? l : "aionmeter-overlay");
    }

    /// <summary>SIGUSR1 (click-through) to the layer-shell card; false when it is not running.</summary>
    public static bool OverlayClickThrough() => Signal(LivePid(OverlayPidFile, OverlayDll), SigUsr1);
}
