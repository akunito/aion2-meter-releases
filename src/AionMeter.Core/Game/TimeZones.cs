namespace AionMeter.Core.Game;

/// <summary>
/// IANA time zone lookup that also works where .NET cannot map IANA ids. Measured under Wine 10.0 (A2M-27, 2026-10-14):
/// FindSystemTimeZoneById("Europe/Berlin") throws TimeZoneNotFoundException (no ICU in the prefix), so events.json loaded no
/// event at all and the footer lost every event pill; the Windows ids are in Wine's registry and resolve. A Windows without
/// ICU (pre-1903, some LTSC/Server builds) takes the same path.
/// </summary>
public static class TimeZones
{
    public static TimeZoneInfo Find(string id) => Find(id, TimeZoneInfo.FindSystemTimeZoneById);

    public static TimeZoneInfo Find(string id, Func<string, TimeZoneInfo> lookup)
    {
        try { return lookup(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (WindowsId(id) is { } win) return lookup(win);
            throw;
        }
    }

    /// <summary>The Windows ids of the zones the meter uses (ServerContext regions, events.json); null for any other id.</summary>
    public static string? WindowsId(string ianaId) => ianaId switch
    {
        "Europe/Berlin" => "W. Europe Standard Time",
        "Asia/Seoul" => "Korea Standard Time",
        "America/New_York" => "Eastern Standard Time",
        "America/Los_Angeles" => "Pacific Standard Time",
        "America/Sao_Paulo" => "E. South America Standard Time",
        _ => null,
    };
}
