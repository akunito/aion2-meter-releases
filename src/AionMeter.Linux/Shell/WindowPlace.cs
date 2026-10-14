using Avalonia;
using Avalonia.Controls;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux.Shell;

/// <summary>
/// A2M-48 UX: window places kept per output (connector name as Xwayland's RandR has it, "DP-2") + offset from its top-left, never as absolute
/// X11 coordinates. Measured 2026-10-13: ui.json held Left 4882 Top 2405, which was on DP-2 in the logical X11 layout; after
/// xwayland_zero_scaling (X11 space = layout × 1.5) the same numbers are on DP-1, and the Opened handler moved the window back there right after
/// Sway's `move container to output` rule had put it on DP-2.
/// </summary>
public static class WindowPlace
{
    public static (string Output, double X, double Y)? Capture(Window w)
    {
        var scr = w.Screens.ScreenFromWindow(w);
        if (scr is null || scr.DisplayName is not { Length: > 0 } name) return null;
        return (name, w.Position.X - scr.Bounds.X, w.Position.Y - scr.Bounds.Y);
    }

    /// <summary>Puts <paramref name="w"/> at the saved offset on its saved output; false when that output is gone or nothing was saved
    /// (then the window manager's placement — Sway's rule — stays).</summary>
    public static bool Restore(Window w, string output, double x, double y)
    {
        if (output.Length == 0 || double.IsNaN(x) || double.IsNaN(y)) return false;
        foreach (var scr in w.Screens.All)
        {
            if (scr.DisplayName != output) continue;
            var b = scr.Bounds;
            var px = (int)Math.Clamp(x, 0, Math.Max(0, b.Width - 80));
            var py = (int)Math.Clamp(y, 0, Math.Max(0, b.Height - 80));
            w.Position = new PixelPoint(b.X + px, b.Y + py);
            return true;
        }
        return false;
    }

    /// <summary>The window's output now (null: unknown).</summary>
    public static string? OutputOf(Window w) => w.Screens.ScreenFromWindow(w)?.DisplayName;

    /// <summary>
    /// Fallback when the window is not on <paramref name="output"/> after mapping (a rule that did not match, a place that failed): ask Sway
    /// to move it, by its X11 window id.
    /// </summary>
    public static void EnsureOutput(Window w, string output)
    {
        if (output.Length == 0 || OutputOf(w) == output) return;
        var xid = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        SwayIpc.OnWindow(xid, $"move container to output {output}");
    }
}
