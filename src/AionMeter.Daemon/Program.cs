using System.Runtime.InteropServices;
using AionMeter.Service;

// A2M-48: the meter without a window (Linux). Same files as the Windows build under $XDG_DATA_HOME/AionMeter (or --data-dir).
// Capture needs CAP_NET_RAW + CAP_NET_ADMIN (libpcap) and read access to the game's /proc/<pid>/fd (same uid, or CAP_SYS_PTRACE in a container).
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
bool Flag(string name) => Array.IndexOf(args, name) >= 0;

if (Flag("--help") || Flag("-h"))
{
    Console.WriteLine("""
        aionmeter-daemon [--data-dir DIR] [--device NAME] [--once SECONDS] [--json-events] [--offline] [--verbose]
                         [--replay FILE.pcap] [--game-data DIR] [--status-every SECONDS]
          --data-dir      state folder (default $XDG_DATA_HOME/AionMeter, ~/.local/share/AionMeter)
          --device        libpcap device (default: settings.json CaptureDevice, else the adapter carrying the game)
          --once N        stop after N seconds (default: until SIGTERM / Ctrl+C)
          --json-events   one JSON object per line on stdout (status, heartbeat, character, fight, boss-alert, price-hit, stopped); human lines go to stderr
          --offline       write state, but no network (profile sync, price polls, Abyss Logs, telemetry)
          --verbose       mirror the log file's lines to the human stream
          --replay FILE   feed a recording through the engine as if live (stores and state files written), stop at its end
        """);
    return 0;
}

int.TryParse(Opt("--once"), out var once);
var options = new HeadlessOptions
{
    DataDir = Opt("--data-dir"),
    GameDataDir = Opt("--game-data"),
    Device = Opt("--device"),
    Replay = Opt("--replay"),
    OnceSeconds = once,
    JsonEvents = Flag("--json-events"),
    Offline = Flag("--offline"),
    Verbose = Flag("--verbose"),
    StatusEverySeconds = int.TryParse(Opt("--status-every"), out var every) ? every : 10,
};

using var cts = new CancellationTokenSource();
// SIGTERM (systemd stop) and SIGINT: the loop ends, the running fight is closed and saved, state flushed. Cancel here keeps the runtime from exiting first.
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Error("Fatal exception", e.ExceptionObject as Exception); BugReport.WriteCrash("Fatal exception", e.ExceptionObject as Exception); };
TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

using var meter = new HeadlessMeter(options, options.JsonEvents ? Console.Error : Console.Out, Console.Out);
return await meter.RunAsync(cts.Token);
