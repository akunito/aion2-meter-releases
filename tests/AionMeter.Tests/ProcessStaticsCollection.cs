namespace AionMeter.Tests;

/// <summary>A2M-50: test classes that write process-wide statics (AppSettings.AppDataDir, Log's file, MeterEngine.DataDirectory,
/// UiText.Current, CloudflareHttp.SolverUrl) run alone, after the parallel collections: under load another class read or overwrote them
/// mid-test (DaemonSmokeTests.LoginReplay found no logs/ in its data dir, 1 in 4 full runs, 2026-10-13).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStaticsCollection
{
    public const string Name = "Process-wide statics";
}
