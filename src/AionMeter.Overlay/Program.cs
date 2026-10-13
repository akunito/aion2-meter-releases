using System.Runtime.InteropServices;
using AionMeter.Overlay;
using AionMeter.Service;
using AionMeter.Service.Overlay;

// A2M-48: the overlay card as a wlr-layer-shell surface (GTK4) on the OVERLAY layer: Sway composites it above fullscreen windows
// (gamescope running AION 2 on DP-1) at the output's scale. The daemon captures; this process only reads its socket.
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        AionMeter.Overlay [--socket PATH] [--monitor NAME] [--anchor top-right|top-left|bottom-right|bottom-left] [--scale F] [--data-dir DIR]
          --socket    the daemon's UI socket (default $XDG_RUNTIME_DIR/aionmeter.sock, else /tmp/aionmeter-<uid>.sock)
          --monitor   output connector (DP-1); saved in overlay.json, default: the compositor's choice
          --anchor    the corner the card hangs from (resets the margins to that corner's default)
          --scale     card size factor on top of the output scale (0.5 – 3)
          --data-dir  where overlay.json lives (default $XDG_DATA_HOME/AionMeter)
        Signals (pid in $XDG_RUNTIME_DIR/aionmeter-overlay.pid): SIGUSR1 toggles click-through, SIGUSR2 hides/shows the card.
        Needs libgtk-4, libgtk4-layer-shell and a wlroots compositor (Sway); no Xwayland.
        """);
    return 0;
}

if (LayerShell.Preload() is { } err)
{
    Console.Error.WriteLine($"overlay: cannot load libgtk4-layer-shell ({err}); put gtk4-layer-shell's lib/ on LD_LIBRARY_PATH");
    return 3;
}

if (Opt("--data-dir") is { Length: > 0 } dir) AppSettings.UseAppDataDir(dir);
var settings = OverlaySettings.Load(Path.Combine(AppSettings.AppDataDir, "overlay.json"));
if (Opt("--monitor") is { } mon) settings.Monitor = mon;
if (Opt("--anchor") is { } an)
{
    if (!Placement.TryParse(an, out var anchor)) { Console.Error.WriteLine($"overlay: unknown --anchor {an}"); return 2; }
    if (anchor != settings.Anchor) { settings.Anchor = anchor; settings.MarginH = 24; settings.MarginV = Placement.IsBottom(anchor) ? 120 : 140; }
}
if (Opt("--scale") is { } sc)
{
    if (!double.TryParse(sc, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) || scale is < 0.5 or > 3)
    { Console.Error.WriteLine($"overlay: --scale wants 0.5 – 3, got {sc}"); return 2; }
    settings.Scale = scale;
}
var socket = Opt("--socket") ?? OverlayServer.DefaultPath();
// The game shares the box: a background UI must not compete for CPU with it.
System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;

var runDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } rd && Directory.Exists(rd) ? rd : Path.GetTempPath();
var pidFile = Path.Combine(runDir, "aionmeter-overlay.pid");
try { File.WriteAllText(pidFile, Environment.ProcessId.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }

var host = new OverlayHost(settings, socket);
// SIGUSR1/SIGUSR2 are 10/12 on Linux; raw numbers are accepted cast to PosixSignal. The handler runs on a pool thread: marshal to GTK.
using var usr1 = PosixSignalRegistration.Create((PosixSignal)10, c => { c.Cancel = true; host.Post(host.ToggleClickThrough); });
using var usr2 = PosixSignalRegistration.Create((PosixSignal)12, c => { c.Cancel = true; host.Post(host.ToggleHidden); });
var rc = host.Run();
try { File.Delete(pidFile); } catch (IOException) { }
return rc;
