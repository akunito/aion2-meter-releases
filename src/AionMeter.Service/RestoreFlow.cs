using System.Text.Json;
using AionMeter.Core.Builds;

namespace AionMeter.Service;

/// <summary>
/// A2M-30 "Restore from code" without the window (A2M-48 phase 3c): parse the code, ask the server, parse the answer, apply it, and the
/// status line the WPF RestoreWindow shows. The Linux dialog and Settings → Backup run it; fetch and apply are injected for the tests.
/// </summary>
public static class RestoreFlow
{
    public enum Outcome { BadCode, Failed, Restored }

    public readonly record struct Result(Outcome Outcome, string Text, MeterEngine.RestoreSummary? Summary = null);

    public static Task<Result> RunAsync(string codeText, MeterEngine engine) =>
        RunAsync(codeText, engine.Settings.ShareBackup, Telemetry.FetchRestoreAsync, engine.ApplyRestore);

    public static async Task<Result> RunAsync(string codeText, bool shareBackup, Func<string, Task<(int Status, string Body, string? Error)>> fetch,
                                              Func<string, RestorePayload, MeterEngine.RestoreSummary> apply)
    {
        var T = UiText.Current;
        if (BackupCode.Parse(codeText) is not { } code) return new(Outcome.BadCode, T.RestoreBadCode);
        var (status, body, error) = await fetch(code).ConfigureAwait(true);
        switch (status)
        {
            case 0: return new(Outcome.Failed, string.Format(T.RestoreFailed, error));
            case 404: return new(Outcome.Failed, T.RestoreNotFound);
            case 429: return new(Outcome.Failed, T.RestoreLimited);
            case < 200 or >= 300: return new(Outcome.Failed, string.Format(T.RestoreRefused, status));
        }
        RestorePayload payload;
        try { payload = await Task.Run(() => BuildTelemetryJson.ParseRestore(body)).ConfigureAwait(true); }
        catch (JsonException ex) { Log.Info($"Restore: unreadable answer: {ex.Message}"); return new(Outcome.Failed, string.Format(T.RestoreBadAnswer, ex.Message)); }
        MeterEngine.RestoreSummary r;
        try { r = apply(code, payload); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Restore: files not written", ex); return new(Outcome.Failed, ex.Message); }
        return new(Outcome.Restored, Describe(r, shareBackup), r);
    }

    /// <summary>The WPF RestoreWindow's success line.</summary>
    public static string Describe(MeterEngine.RestoreSummary r, bool shareBackup)
    {
        var T = UiText.Current;
        var text = string.Format(T.RestoreDone, r.Builds, r.Placed, r.Waiting);
        if (r.KeptLocal > 0) text += "; " + string.Format(T.RestoreKeptNewer, r.KeptLocal);
        text += "; " + (!r.StateFound ? T.RestoreNoState : r.WatchRestored ? T.RestoreWatchRestored : T.RestoreWatchKept);
        if (r.FavouritesRestored) text += "; " + T.RestoreFavRestored;
        text += ".";
        if (!shareBackup) text += " " + T.RestoreNoTelemetry;
        return text;
    }
}
