using System.Text.Json;

namespace AionMeter.Core.Atlas;

/// <summary>A recurring server event (Shugo Festival, Spacetime Rift, …) from data/atlas/events.json, in the server's time zone.</summary>
public sealed record ScheduledEvent(string Key, string Name, TimeZoneInfo Zone, IReadOnlyList<TimeOnly> Times, int DurationMinutes, int AlertMinutes, IReadOnlySet<DayOfWeek>? Days = null, int EveryMinutes = 0, bool Boss = false, bool Hidden = false)
{
    /// <summary>
    /// The occurrence running now (start, end) or the next one. A periodic event (every_minutes) takes an <paramref name="anchor"/>:
    /// a start the player confirmed in game, which re-phases the cycle (the server's hourly events drift after a restart or
    /// maintenance; measured 2026-10-09, Shugo ran at :03 instead of :00).
    /// </summary>
    public (DateTimeOffset Start, DateTimeOffset End, bool Running) Next(DateTimeOffset now, DateTimeOffset? anchor = null, double? periodMinutes = null)
    {
        if (anchor is { } a && EveryMinutes > 0)
        {
            // periodMinutes: the cycle measured from two of the player's marks (Diego 2026-10-10: Shugo starts a few minutes later every hour on EU)
            var period = TimeSpan.FromMinutes(periodMinutes is > 0 ? periodMinutes.Value : EveryMinutes);
            var elapsed = now - a;
            var cycles = Math.Floor(elapsed / period);
            var start = a + period * cycles;
            var end = start.AddMinutes(DurationMinutes);
            if (end <= now) { start += period; end = start.AddMinutes(DurationMinutes); }
            return (start, end, start <= now);
        }
        var local = TimeZoneInfo.ConvertTime(now, Zone);
        var day = DateOnly.FromDateTime(local.DateTime);
        for (var d = -1; d <= 7; d++)
        {
            if (Days is not null && !Days.Contains(day.AddDays(d).DayOfWeek)) continue;
            foreach (var t in Times)
            {
                var start = new DateTimeOffset(day.AddDays(d).ToDateTime(t), Zone.GetUtcOffset(day.AddDays(d).ToDateTime(t)));
                var end = start.AddMinutes(DurationMinutes);
                if (end > now) return (start, end, start <= now);
            }
        }
        return (now, now, false);
    }

    /// <summary>The next start strictly after <paramref name="now"/> (a running occurrence is skipped): the Items "Today" card's reset times.</summary>
    public DateTimeOffset NextStart(DateTimeOffset now)
    {
        var (start, end, _) = Next(now);
        return start > now ? start : Next(end).Start;
    }

    /// <summary>Footer pill countdown: "1d 22h" from a day up (the weekly reset), "5h03" from an hour, else "39m".</summary>
    public static string Countdown(TimeSpan left) =>
        left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h"
        : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}"
        : $"{Math.Max(0, (int)Math.Ceiling(left.TotalMinutes))}m";

    /// <summary>The EU server clock events.json is written in.</summary>
    public const string EuZoneId = "Europe/Berlin";

    /// <summary>
    /// A2M-40: the events on another shard's clock. shugo.gg timers.json (2026-10-09) gives every Global shard the same local
    /// hours (Rift, Shugo, Invasion, Kaira, siege) and only a different timeZone, so the events written in the EU clock move to
    /// <paramref name="zone"/>; the resets (Asia/Seoul, one moment on every Global server) stay. The EU zone returns the same list.
    /// </summary>
    public static IReadOnlyList<ScheduledEvent> ForZone(IReadOnlyList<ScheduledEvent> events, TimeZoneInfo zone)
    {
        if (IsEu(zone)) return events;
        var list = new List<ScheduledEvent>(events.Count);
        for (var i = 0; i < events.Count; i++) list.Add(IsEu(events[i].Zone) ? events[i] with { Zone = zone } : events[i]);
        return list;
    }

    // Windows can hand back the Windows id for an IANA lookup depending on the ICU mode; both name the Berlin clock.
    private static bool IsEu(TimeZoneInfo z) => z.Id is EuZoneId or "W. Europe Standard Time";

    public static List<ScheduledEvent> Load(string path)
    {
        var list = new List<ScheduledEvent>();
        try
        {
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("events").EnumerateArray())
            {
                var zone = Game.TimeZones.Find(e.TryGetProperty("zone", out var z) ? z.GetString() ?? "Europe/Berlin" : "Europe/Berlin");
                var times = new List<TimeOnly>();
                var every = 0;
                if (e.TryGetProperty("times", out var ts))
                    foreach (var t in ts.EnumerateArray()) if (TimeOnly.TryParse(t.GetString(), out var to)) times.Add(to);
                if (e.TryGetProperty("every_minutes", out var ev))
                {
                    var step = ev.GetInt32(); var at = e.TryGetProperty("at_minute", out var am) ? am.GetInt32() : 0;
                    every = step;
                    for (var m = at; m < 24 * 60; m += step) times.Add(new TimeOnly(m / 60, m % 60));
                }
                times.Sort();
                HashSet<DayOfWeek>? days = null;
                if (e.TryGetProperty("days", out var ds))
                {
                    days = new();
                    foreach (var dn in ds.EnumerateArray()) if (Enum.TryParse<DayOfWeek>(dn.GetString(), true, out var dow)) days.Add(dow);
                }
                list.Add(new ScheduledEvent(e.GetProperty("key").GetString() ?? "", e.GetProperty("name").GetString() ?? "", zone, times,
                    e.TryGetProperty("duration_minutes", out var du) ? du.GetInt32() : 10, e.TryGetProperty("alert_minutes", out var al) ? al.GetInt32() : 5, days, every,
                    e.TryGetProperty("boss", out var bo) && bo.GetBoolean(), e.TryGetProperty("hidden", out var hi) && hi.GetBoolean()));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
        }
        return list;
    }
}
