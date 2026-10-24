namespace AionMeter.Core.Diagnostics;

/// <summary>The parts of a "Report a problem" zip. The meter itself sends nothing anywhere: the player saves the zip and attaches it where they choose.</summary>
public enum ShareKind
{
    /// <summary>Bug report: logs, crash files, settings, BUILD-INFO. Always in a report.</summary>
    ReportLogs,
    /// <summary>Bug report: state summary + character folders (names, server, Kina, progress, builds).</summary>
    ReportSummary,
    /// <summary>Bug report: top-dps.json (other players' names).</summary>
    ReportTopDps,
    /// <summary>Bug report: inventories, craftable lists, the account warehouse.</summary>
    ReportInventory,
}

/// <summary>
/// A2M-45: the player's choice of what goes into a bug-report zip (the Report-a-problem dialog, remembered in settings). The zip is built
/// through <see cref="BugReportFiles"/>.
/// </summary>
public sealed record SharePolicy(bool ReportSummary, bool ReportTopDps, bool ReportInventory)
{
    /// <summary>New installs: summary + Top DPS in reports, inventory not.</summary>
    public static readonly SharePolicy Default = new(true, true, false);

    public bool Filter(ShareKind kind) => kind switch
    {
        ShareKind.ReportLogs => true,
        ShareKind.ReportSummary => ReportSummary,
        ShareKind.ReportTopDps => ReportTopDps,
        ShareKind.ReportInventory => ReportInventory,
        _ => false,
    };
}
