using System.Diagnostics;
using System.IO;

namespace AionMeter.App.Services;

/// <summary>
/// "Start and stop with AION 2": registers (or removes) the per-user scheduled task that runs data\meter-autostart.ps1 at logon.
/// No administrator is needed for a task of the current user. The script finds the exe next to its own folder, so the same
/// file works from a clanmate's unzipped folder and from Diego's deploy folder (A2M-18 → clan option, 2026-10-10).
/// </summary>
public static class GameHook
{
    public const string TaskName = "AION2 meter autostart";

    /// <summary>Registers or removes the task; null on success, else the error text for the settings window.</summary>
    public static string? Set(bool enabled)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "data", "meter-autostart.ps1");
        if (enabled && !File.Exists(script)) return "data\\meter-autostart.ps1 is missing";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {(enabled ? "-Register" : "-Unregister")}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            Log.Info($"Game hook {(enabled ? "registered" : "removed")}: exit {p.ExitCode} {output.Trim()}");
            return p.ExitCode == 0 ? null : output.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Error("Game hook", ex);
            return ex.Message;
        }
    }
}
