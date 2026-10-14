using System.Runtime.InteropServices;
using AionMeter.Overlay;
using AionMeter.Service;
using AionMeter.Service.Overlay;

// A2M-48: the overlay card as a wlr-layer-shell surface (GTK4) on the OVERLAY layer: Sway composites it above fullscreen windows
// (gamescope running AION 2 on DP-1) at the output's scale. The daemon captures; this process only reads its socket.
// A2M-50: by default a plain Sway window (app_id aionmeter) instead; DESK's patched Sway draws it above the fullscreen game with the
// above_fullscreen rule. --layer keeps the layer-shell card.
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        AionMeter.Overlay [--layer] [--socket PATH] [--monitor NAME] [--anchor top-right|top-left|bottom-right|bottom-left] [--scale F] [--data-dir DIR] [--tab KEY] [--help-page KEY] [--window report|restore|import] [--show|--hide|--toggle|--quit]
          --layer     the layer-shell card (OVERLAY layer, placed by anchor + margins, dragged across outputs by itself). Default: a normal
                      window, app_id aionmeter, that Sway places, moves (Alt+drag) and resizes (Alt+right-drag) like any floating window;
                      its rect is saved in overlay.json through Sway IPC and restored on every start/show. --monitor/--anchor are layer-only
          --socket    the daemon's UI socket (default $XDG_RUNTIME_DIR/aionmeter.sock, else /tmp/aionmeter-<uid>.sock)
          --monitor   output connector (DP-1) when overlay.json names none (or the saved one is not connected); dragging the card onto
                      another output saves that one in overlay.json (Monitor). Neither: the compositor's choice
          --anchor    the corner the card hangs from (resets the margins to that corner's default)
          --scale     card size factor on top of the output scale (0.5 – 3) for this run; otherwise the shared "UI scale" of ui.json
                      (Settings → Overlay, or the wheel over the resize corner)
          --data-dir  the meter's data dir: overlay.json, ui.json, and what the viewer engine reads (default $XDG_DATA_HOME/AionMeter)
          --tab KEY   open a tab at start (settings, or a ported strip tab); --help-page KEY a help page; --window report|restore|import a dialog
        Mouse: drag the card (its header on every tab, or any part that is not a control) to move it, also onto another output;
        right-button drag anywhere resizes it from the nearest edge or corner (like Sway's Alt+right-drag; Alt optional), and so does the gold
        bottom-right corner; the wheel there zooms. In window mode these hand the move/resize to Sway; with --layer the card does them itself
        (Sway's floating_modifier does not apply to layer surfaces).
        Single instance: when a card already listens on $XDG_RUNTIME_DIR/aionmeter-overlay.sock, this process hands it a command and exits:
        --show (or nothing) shows and raises it (on --tab KEY when given), --hide, --toggle, --quit. With --data-dir it never hands over and
        runs as a card of its own (taking the socket and the pid file only when no card holds them). Socket lines: newline JSON
        {"cmd":"show","tab":"items"} / hide / raise / toggle / quit.
        Signals (pid in $XDG_RUNTIME_DIR/aionmeter-overlay.pid): SIGUSR1 toggles click-through, SIGUSR2 hides/shows the card, SIGTERM quits it
        cleanly (pid file and socket removed).
        The tab pills switch the card in place (A2M-50); Settings → Daemon restarts the daemon or quits the card.
        Needs libgtk-4 on Wayland (no Xwayland); --layer also libgtk4-layer-shell and a wlroots compositor (Sway).
        """);
    return 0;
}

// A2M-50: one card; a second start is a command to it (Hyper+Shift+M = show and raise, the dotfiles helpers' --quit / --toggle).
var testInstance = Opt("--data-dir") is { Length: > 0 };
var uiSocket = UiChannel.DefaultPath();
UiCommand? asked = Array.IndexOf(args, "--quit") >= 0 ? new UiCommand(UiCommandKind.Quit)
    : Array.IndexOf(args, "--hide") >= 0 ? new UiCommand(UiCommandKind.Hide)
    : Array.IndexOf(args, "--toggle") >= 0 ? new UiCommand(UiCommandKind.Toggle)
    : Array.IndexOf(args, "--show") >= 0 || !testInstance ? new UiCommand(UiCommandKind.Show, Opt("--tab") is { } st && UiTabs.IsKnown(st) ? st : null)
    : null;
if (asked is { } cmd)
{
    if (UiChannel.TrySend(uiSocket, cmd)) return 0;
    // starting up: the pid file is written before the socket listens
    if (CardProcess.LivePid(CardProcess.PidFile, CardProcess.Dll) is var starting and > 0 && starting != Environment.ProcessId)
    {
        for (var i = 0; i < 20; i++) { Thread.Sleep(250); if (UiChannel.TrySend(uiSocket, cmd)) return 0; }
        Console.Error.WriteLine($"overlay: card pid {starting} does not answer on {uiSocket}");
        return 1;
    }
    // nothing running: nothing to hide or quit; --show / --toggle start the card
    if (cmd.Kind is UiCommandKind.Quit or UiCommandKind.Hide) return 0;
}

var layer = Array.IndexOf(args, "--layer") >= 0;
if (layer && LayerShell.Preload() is { } err)
{
    Console.Error.WriteLine($"overlay: cannot load libgtk4-layer-shell ({err}); put gtk4-layer-shell's lib/ on LD_LIBRARY_PATH");
    return 3;
}

if (Opt("--data-dir") is { Length: > 0 } dir) AppSettings.UseAppDataDir(dir);
// before the first log line: the daemon and this card never append to the same file
Log.Prefix = "aionmeter-overlay";
// settings dictionaries the build telemetry records into are edited on the UI thread, as in WPF
UiHost.Current = new AionMeter.Overlay.Shell.GtkUiHost();
var settings = OverlaySettings.Load(Path.Combine(AppSettings.AppDataDir, "overlay.json"));
// --monitor (the wrapper's DP-1) is only the default: the output the card was dragged to (overlay.json Monitor) wins
var defaultMonitor = Opt("--monitor");
if (layer && settings.Monitor.Length == 0 && defaultMonitor is { Length: > 0 }) settings.Monitor = defaultMonitor;
if (Opt("--anchor") is { } an)
{
    if (!Placement.TryParse(an, out var anchor)) { Console.Error.WriteLine($"overlay: unknown --anchor {an}"); return 2; }
    if (anchor != settings.Anchor) { settings.Anchor = anchor; settings.MarginH = 24; settings.MarginV = Placement.IsBottom(anchor) ? 120 : 140; }
}
settings.Scale = UiScale.Read(Path.Combine(AppSettings.AppDataDir, "ui.json"));
var scaleFixed = false;
if (Opt("--scale") is { } sc)
{
    if (!double.TryParse(sc, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) || scale is < 0.5 or > 3)
    { Console.Error.WriteLine($"overlay: --scale wants 0.5 – 3, got {sc}"); return 2; }
    settings.Scale = scale;
    scaleFixed = true;
}
var socket = Opt("--socket") ?? OverlayServer.DefaultPath();
// The game shares the box: a background UI must not compete for CPU with it.
System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;

// the pid file and the command socket go together: a --data-dir card next to a running one takes neither (its helpers keep the real card)
var pidFile = CardProcess.PidFile;
var owner = !UiChannel.TrySend(uiSocket, null);
if (owner) try { File.WriteAllText(pidFile, Environment.ProcessId.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }

var host = new OverlayHost(settings, socket, Path.Combine(AppSettings.AppDataDir, "ui.json"), scaleFixed, Opt("--tab"), Opt("--help-page"), Opt("--window"), defaultMonitor, layer);
using var commands = owner ? UiCommandServer.Start(uiSocket, host.Command, host.Post) : null;
// SIGUSR1/SIGUSR2 are 10/12 on Linux; raw numbers are accepted cast to PosixSignal. The handler runs on a pool thread: marshal to GTK.
using var usr1 = PosixSignalRegistration.Create((PosixSignal)10, c => { c.Cancel = true; host.Post(host.ToggleClickThrough); });
using var usr2 = PosixSignalRegistration.Create((PosixSignal)12, c => { c.Cancel = true; host.Post(host.ToggleHidden); });
// deploy.sh stops the card with TERM: quit through GTK so the pid file and the socket are removed (the default action skips both)
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; host.Post(host.Quit); });
var rc = host.Run();
commands?.Dispose();
if (owner && CardProcess.LivePid(pidFile, CardProcess.Dll) == Environment.ProcessId) try { File.Delete(pidFile); } catch (IOException) { }
return rc;
