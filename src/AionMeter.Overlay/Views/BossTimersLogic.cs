using System.Globalization;
using System.Text.Json;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>What a timer row shows on the right: the big status, the line under it, its colour and size, and whether the frame is outlined.</summary>
internal readonly record struct TimerStatus(string Text, string Detail, Fg Fg, double Size, bool Hot);

/// <summary>Order of the list (Avalonia BossTimersView.Reload): alive or due first, then counting down, then unknown; soonest first, newest kill next.
/// One instance per view, <see cref="Now"/> set before each sort (no closure per tick).</summary>
internal sealed class TimerOrder : IComparer<BossTimer>
{
    public DateTimeOffset Now;

    public int Compare(BossTimer? a, BossTimer? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var c = BossTimersLogic.Group(a, Now).CompareTo(BossTimersLogic.Group(b, Now));
        if (c != 0) return c;
        c = (a.NextSpawn ?? DateTimeOffset.MaxValue).CompareTo(b.NextSpawn ?? DateTimeOffset.MaxValue);
        return c != 0 ? c : (b.LastKill ?? DateTimeOffset.MinValue).CompareTo(a.LastKill ?? DateTimeOffset.MinValue);
    }
}

/// <summary>The display-free part of the Boss Timers tab (A2M-50 port of the Avalonia BossTimersView): texts, order, menus, portrait names.</summary>
internal static class BossTimersLogic
{
    public static readonly int[] Presets = [0, 15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440];
    public static readonly int[] AlertChoices = [0, 1, 2, 5, 10];
    public const string PortraitBase = "https://metabot.gg/web/aion2/npcs/";
    /// <summary>WPF Compact under 700 px: the actions drop to a second line.</summary>
    public const double CompactBelow = 700;

    public static int Group(BossTimer t, DateTimeOffset now) => t.AliveNow || t.NextSpawn <= now ? 0 : t.NextSpawn is not null ? 1 : 2;

    /// <summary>Slots whose boss is not named yet stay out of sight until it is; "Watched" keeps the belled ones.</summary>
    public static bool Listed(BossTimer t, bool watchedOnly) => t.NpcCode > 0 && (!watchedOnly || t.Watch);

    /// <summary>"14:05", or "05.10 14:05" when not today.</summary>
    public static string When(DateTimeOffset t, DateTimeOffset now) =>
        t.LocalDateTime.Date == now.LocalDateTime.Date ? t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    public static string Countdown(TimeSpan left) =>
        left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : left.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    public static TimerStatus Status(BossTimer t, DateTimeOffset now, int alertMinutes, UiText T)
    {
        if (t.AliveNow)
            return new(T.TimerAlive, t.AliveSince is { } since ? string.Format(T.TimerAliveSince, When(since, now)) : "", Fg.Green, 16, true);
        if (t.NextSpawn is { } next && next > now)
        {
            var left = next - now;
            return new(Countdown(left), string.Format(T.TimerAt, When(next, now)), left.TotalMinutes <= Math.Max(1, alertMinutes) ? Fg.Amber : Fg.GoldBright, 19, false);
        }
        if (t.NextSpawn is { } due)
            return new(T.TimerDue, string.Format(T.TimerSince, When(due, now)), Fg.Green, 14, true);
        return new("—", t.FromGame && t.ListedTime is null ? T.TimerNoTime : T.TimerNeedsInterval, Fg.Mute, 19, false);
    }

    /// <summary>"Zone · killed 14:05" (either part may be missing); <paramref name="zone"/> is the map name the engine found.</summary>
    public static string Meta(string? zone, BossTimer t, DateTimeOffset now, UiText T)
    {
        if (zone == "Open world") zone = T.OpenWorld;
        var death = t.LastKill is { } k ? string.Format(T.TimerKilled, When(k, now))
            : t.LastSeenDead is { } d ? string.Format(T.TimerFoundDead, When(d, now)) : null;
        return string.IsNullOrEmpty(zone) ? death ?? "" : death is null ? zone : zone + " · " + death;
    }

    public static string RespawnLabel(BossTimer t, UiText T) =>
        t.RespawnMinutes > 0 ? (t.RespawnLearned ? "≈ " : "") + T.Duration(t.RespawnMinutes) : t.FromGame ? T.RespawnFromGame : T.RespawnUnknown;

    public static string SourceTip(BossTimer t, DateTimeOffset now, UiText T) =>
        t.FromGame && t.ListedAt is { } listed ? string.Format(T.SourceGame, When(listed, now)) : T.SourceMeter;

    /// <summary>The respawn menu: the presets, plus the current interval in order when it is not one of them.</summary>
    public static int[] RespawnChoices(int current)
    {
        if (Array.IndexOf(Presets, current) >= 0) return Presets;
        var r = new int[Presets.Length + 1];
        var j = 0;
        var added = false;
        for (var i = 0; i < Presets.Length; i++)
        {
            if (!added && current < Presets[i]) { r[j++] = current; added = true; }
            r[j++] = Presets[i];
        }
        if (!added) r[j] = current;
        return r;
    }

    public static string AlertText(int minutes, UiText T) => minutes == 0 ? T.AlertOff : T.Duration(minutes);

    /// <summary>A menu line: "▸ " on the current value, three spaces otherwise (the Avalonia flyout's marks).</summary>
    public static string MenuLine(string label, bool current) => (current ? "▸ " : "   ") + label;

    /// <summary>data/boss_portraits.json: {"portraits": {"&lt;npc code&gt;": "&lt;file name without .webp&gt;"}} (dungeon bosses); others go by code.</summary>
    public static Dictionary<int, string> ParsePortraitFiles(string json)
    {
        var map = new Dictionary<int, string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("portraits", out var p) && p.ValueKind == JsonValueKind.Object)
            foreach (var kv in p.EnumerateObject())
                if (int.TryParse(kv.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && kv.Value.ValueKind == JsonValueKind.String && kv.Value.GetString() is { Length: > 0 } f)
                    map[code] = f;
        return map;
    }

    public static Dictionary<int, string> LoadPortraitFiles(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "boss_portraits.json");
        try { return File.Exists(path) ? ParsePortraitFiles(File.ReadAllText(path)) : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log.Info($"boss_portraits.json ignored: {ex.Message}"); return new(); }
    }

    /// <summary>The icon-cache key and download URL of a boss portrait (MetaBot.GG, game art © NCSOFT).</summary>
    public static (string Key, string Url) Portrait(int code, Dictionary<int, string> files)
    {
        var name = files.TryGetValue(code, out var file) ? file : code.ToString(CultureInfo.InvariantCulture);
        return ("portrait:" + name.Replace('/', '_'), PortraitBase + name + ".webp");
    }
}
