namespace AionMeter.Core.Diagnostics;

/// <summary>What leaves the PC for Diego's server. The three telemetry kinds go on their own; the report kinds only inside a bug report the player sends.</summary>
public enum ShareKind
{
    /// <summary>Daily usage record (build, OS, Npcap, capture health, crash/error counts, tabs used).</summary>
    Usage,
    /// <summary>Anonymous builds (webhook kind "build").</summary>
    Builds,
    /// <summary>The Restore-from-code copy: watch list + favourite recipes (webhook kind "watch"); the builds it restores are the <see cref="Builds"/> rows.</summary>
    Backup,
    /// <summary>Names the player gives to unknown world objects in the atlas (webhook kind "atlas": code, kind/name, maps).</summary>
    AtlasNames,
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
/// A2M-45: the player's per-kind choice of what the meter shares (Settings → Privacy, the Report-a-problem dialog). The telemetry client
/// asks <see cref="Filter(string)"/> before every POST and the bug-report zip is built through <see cref="BugReportFiles"/>.
/// </summary>
public sealed record SharePolicy(bool Usage, bool Builds, bool Backup, bool ReportSummary, bool ReportTopDps, bool ReportInventory)
{
    /// <summary>Atlas node names (kind "atlas"). Not positional so the report-side call sites keep their shape; off unless set (<see cref="Default"/> sets it).</summary>
    public bool AtlasNames { get; init; }

    /// <summary>New installs: the telemetry kinds on (as before A2M-45), summary + Top DPS in reports, inventory not.</summary>
    public static readonly SharePolicy Default = new(true, true, true, true, true, false) { AtlasNames = true };

    /// <summary>Settings files from before A2M-45 had one switch for all telemetry kinds: it becomes each of them.</summary>
    public static SharePolicy FromLegacyTelemetry(bool telemetry) => Default with { Usage = telemetry, Builds = telemetry, Backup = telemetry, AtlasNames = telemetry };

    /// <summary>A settings file written before the atlas switch existed: on unless the player had switched every other telemetry kind off.</summary>
    public static bool AtlasNamesForOldFile(bool usage, bool builds, bool backup) => usage || builds || backup;

    public bool Filter(ShareKind kind) => kind switch
    {
        ShareKind.Usage => Usage,
        ShareKind.Builds => Builds,
        ShareKind.Backup => Backup,
        ShareKind.AtlasNames => AtlasNames,
        ShareKind.ReportLogs => true,
        ShareKind.ReportSummary => ReportSummary,
        ShareKind.ReportTopDps => ReportTopDps,
        ShareKind.ReportInventory => ReportInventory,
        _ => false,
    };

    /// <summary>The telemetry webhook's kinds: "usage" (the daily record), "build", "watch", "atlas". Anything else is refused.</summary>
    public bool Filter(string telemetryKind) => telemetryKind switch
    {
        "usage" => Usage,
        "build" => Builds,
        "watch" => Backup,
        "atlas" => AtlasNames,
        _ => false,
    };

    /// <summary>At least one kind is sent without the player pressing anything (the first-start balloon is about these).</summary>
    public bool SendsAnything => Usage || Builds || Backup || AtlasNames;
}
