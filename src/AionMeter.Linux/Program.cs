using System.Runtime.InteropServices;
using Avalonia;
using AionMeter.Linux;
using AionMeter.Linux.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

// A2M-48 phase 3: the overlay card on Linux. The daemon (CAP_NET_RAW service) captures; this process only reads its socket.
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        AionMeter.Linux [--socket PATH] [--data-dir DIR] [--tab KEY] [--fight ID] [--help-page KEY] [--popout] [--view key=value …] [--window report|restore|import] [--hidden] [--hide] [--quit]
          --socket    the daemon's UI socket (default $XDG_RUNTIME_DIR/aionmeter.sock, else /tmp/aionmeter-<uid>.sock)
          --data-dir  the meter's data dir: ui.json, and what the viewer engine reads (default $XDG_DATA_HOME/AionMeter)
          --window    open a dialog at start: report (Report a problem), restore (Restore from code), import (the Build tab's questlog import)
          --tab KEY   open a tab at start (settings, build, …); --fight ID opens the breakdown ("live", "s:<guid>", "h:<file>"); --help-page KEY a help page; --popout moves that tab into its own window
          --view      start state of the opened tab: items search=, open=bag,wh, list=bag, char=; craft search=, profit=1, scope=; prices expand=<id>, search=, add=<id>:<max>, check=1
          --hidden    start with the window hidden (tray icon only)
          Single instance (without --data-dir): when a tools window already runs, `--tab KEY` makes it show that tab, `--hide` hides it,
          `--quit` ends it, anything else shows and raises it; this process then exits. Commands go to $XDG_RUNTIME_DIR/aionmeter-ui.sock
          (newline JSON {"cmd":"show","tab":"items"} / hide / raise / toggle / quit).
        SIGUSR1 toggles click-through; the pid is in $XDG_RUNTIME_DIR/aionmeter-ui.pid (Sway: exec kill -USR1 $(cat $XDG_RUNTIME_DIR/aionmeter-ui.pid)).
        AVALONIA_SCREEN_SCALE_FACTORS / AVALONIA_GLOBAL_SCALE_FACTOR win; otherwise, under Sway with xwayland_zero_scaling, each output's scale.
        """);
    return 0;
}

// A2M-48 UX: one tools window. A test instance (--data-dir) neither joins nor takes the socket and the pid file.
var primary = Opt("--data-dir") is not { Length: > 0 };
var uiSocket = UiChannel.DefaultPath();
if (primary)
{
    var cmd = Array.IndexOf(args, "--quit") >= 0 ? new UiCommand(UiCommandKind.Quit)
        : Array.IndexOf(args, "--hide") >= 0 ? new UiCommand(UiCommandKind.Hide)
        : Opt("--tab") is { } t && UiTabs.IsKnown(t) ? new UiCommand(UiCommandKind.Show, t)
        : new UiCommand(UiCommandKind.Show);
    // --hidden (autostart) next to a running one: leave it as it is
    if (Array.IndexOf(args, "--hidden") >= 0 ? UiChannel.TrySend(uiSocket, null) : UiChannel.TrySend(uiSocket, cmd)) return 0;
    if (cmd.Kind is UiCommandKind.Quit or UiCommandKind.Hide) return 0;   // nothing running: nothing to hide or quit
}
App.Primary = primary;
App.UiSocket = uiSocket;
App.StartHidden = Array.IndexOf(args, "--hidden") >= 0;
ScreenScales.Apply();

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
if (primary) try { File.WriteAllText(pidFile, Environment.ProcessId.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
using var usr1 = PosixSignalRegistration.Create((PosixSignal)10, c => { c.Cancel = true; App.ToggleClickThroughFromSignal(); });

var rc = AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .With(new X11PlatformOptions { WmClass = "aionmeter" })
    .WithInterFont()
    .StartWithClassicDesktopLifetime(args);
if (primary && FrontEnds.LivePid(pidFile, FrontEnds.UiDll) == Environment.ProcessId) try { File.Delete(pidFile); } catch (IOException) { }
return rc;
