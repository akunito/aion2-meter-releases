using System.IO;

namespace AionMeter.Service;

/// <summary>Tiny append-only log in %AppData%\AionMeter\logs (Linux: ~/.local/share/AionMeter/logs) — enough to diagnose capture problems from user reports.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _filePath;
    // Fixed on first use (AppSettings.UseAppDataDir and Prefix must come before the first line).
    private static string FilePath => _filePath ??= Path.Combine(AppSettings.AppDataDir, "logs", $"{Prefix}_{DateTime.Now:yyyyMMdd}.log");

    /// <summary>AppSettings.UseAppDataDir: the next line goes under the new data dir. A line logged before it (another test class in the
    /// same process, A2M-50) had fixed the old dir and the daemon smoke test found no logs/ in its own dir.</summary>
    internal static void ResetPath() { lock (Gate) _filePath = null; }

    /// <summary>File name stem: "aionmeter" (App, daemon); the Linux card sets "aionmeter-overlay" so two processes never append to one file.</summary>
    public static string Prefix { get; set; } = "aionmeter";

    public static string Directory => Path.GetDirectoryName(FilePath)!;

    /// <summary>Off for the render tool: its sample fight must not end up in the log of the meter the player runs.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static readonly HashSet<string> SeenLists = new();

    /// <summary>
    /// Every distinct in-game boss list once per run, decoded or not, into logs\field-boss-lists.log: the record to
    /// look at when timers do not show up for a map.
    /// </summary>
    public static void FieldBossList(Core.Events.FieldBossListEvent list)
    {
        if (!Enabled) return;
        var hex = Convert.ToHexString(list.Raw);
        try
        {
            lock (Gate)
            {
                if (SeenLists.Count > 2_000 || !SeenLists.Add(hex)) return;
                var path = Path.Combine(Directory, "field-boss-lists.log");
                if (File.Exists(path) && new FileInfo(path).Length > 4_000_000) return;
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} map {list.MapId} slots {list.Slots.Count}/{list.Count}" +
                                         $"{(list.Complete ? "" : " INCOMPLETE")} {hex}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a log dir another user owns (the daemon run as root in docker) must never take the caller down
        }
    }

    /// <summary>Daemon --verbose: every line also goes here (level, message).</summary>
    public static Action<string, string>? Mirror { get; set; }

    private static void Write(string level, string message)
    {
        if (!Enabled) return;
        Mirror?.Invoke(level, message);

        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a log dir another user owns (the daemon run as root in docker) must never take the caller down
        }
    }
}
