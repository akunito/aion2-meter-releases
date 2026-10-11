using System.IO;

namespace AionMeter.App.Services;

/// <summary>Tiny append-only log in %AppData%\AionMeter\logs — enough to diagnose capture problems from user reports.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppSettings.AppDataDir, "logs", $"aionmeter_{DateTime.Now:yyyyMMdd}.log");

    public static string Directory => Path.GetDirectoryName(FilePath)!;

    /// <summary>Off for the render tool: its sample fight must not end up in the log of the meter the player runs.</summary>
    public static bool Enabled { get; set; } = true;

    public static void Info(string message) => Write("INFO", message);

    private static int _errors;
    /// <summary>Errors logged since the last telemetry record.</summary>
    public static int Errors => _errors;
    public static void ResetErrors() => Interlocked.Exchange(ref _errors, 0);
    public static void Error(string message, Exception? ex = null)
    {
        Interlocked.Increment(ref _errors);
        Write("ERROR", ex is null ? message : $"{message}: {ex}");
    }

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
        catch (IOException)
        {
        }
    }

    private static void Write(string level, string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }
}
