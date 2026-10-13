using System.Runtime.InteropServices;
using Avalonia;
using AionMeter.Linux;
using AionMeter.Service;
using AionMeter.Service.Overlay;

// A2M-48 phase 3: the overlay card on Linux. The daemon (CAP_NET_RAW service) captures; this process only reads its socket.
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        AionMeter.Linux [--socket PATH] [--data-dir DIR] [--tab KEY] [--fight ID] [--help-page KEY] [--popout] [--view key=value …] [--window report|restore|import]
          --socket    the daemon's UI socket (default $XDG_RUNTIME_DIR/aionmeter.sock, else /tmp/aionmeter-<uid>.sock)
          --data-dir  the meter's data dir: ui.json, and what the viewer engine reads (default $XDG_DATA_HOME/AionMeter)
          --window    open a dialog at start: report (Report a problem), restore (Restore from code), import (the Build tab's questlog import)
          --tab KEY   open a tab at start (settings, build, …); --fight ID opens the breakdown ("live", "s:<guid>", "h:<file>"); --help-page KEY a help page; --popout moves that tab into its own window
          --view      start state of the opened tab: items search=, open=bag,wh, list=bag, char=; craft search=, profit=1, scope=; prices expand=<id>, search=, add=<id>:<max>, check=1
        SIGUSR1 toggles click-through; the pid is in $XDG_RUNTIME_DIR/aionmeter-ui.pid (Sway: exec kill -USR1 $(cat $XDG_RUNTIME_DIR/aionmeter-ui.pid)).
        """);
    return 0;
}

if (Opt("--data-dir") is { Length: > 0 } dir) AppSettings.UseAppDataDir(dir);
// before the first log line: two processes (daemon, UI) never append to the same file
Log.Prefix = "aionmeter-ui";
App.StartTab = Opt("--tab");
App.StartFight = Opt("--fight");
App.StartHelp = Opt("--help-page");
App.StartPopOut = Array.IndexOf(args, "--popout") >= 0;
for (var i = 0; i + 1 < args.Length; i++)
    if (args[i] == "--view" && args[i + 1].IndexOf('=') is var eq and > 0) App.ViewArgs[args[i + 1][..eq]] = args[i + 1][(eq + 1)..];
App.StartWindow = Opt("--window");
App.SocketPath = Opt("--socket") ?? OverlayServer.DefaultPath();
App.Ui = UiSettings.Load(Path.Combine(AppSettings.AppDataDir, "ui.json"));
// The game shares the box: a background UI must not compete for CPU with it.
System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;

// SIGUSR1 (10 on Linux): raw signal numbers are accepted cast to PosixSignal. The hotkey signals the pid file's process, not `pkill -f`:
// that would also hit steam-run's bwrap and the shell around it, whose default USR1 action is to die (taking the UI with them).
var runDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } rd && Directory.Exists(rd) ? rd : Path.GetTempPath();
var pidFile = Path.Combine(runDir, "aionmeter-ui.pid");
try { File.WriteAllText(pidFile, Environment.ProcessId.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
using var usr1 = PosixSignalRegistration.Create((PosixSignal)10, c => { c.Cancel = true; App.ToggleClickThroughFromSignal(); });

var rc = AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .With(new X11PlatformOptions { WmClass = "aionmeter" })
    .WithInterFont()
    .StartWithClassicDesktopLifetime(args);
try { File.Delete(pidFile); } catch (IOException) { }
return rc;
