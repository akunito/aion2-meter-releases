using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AionMeter.App.Services;

/// <summary>
/// A2M-42: keeps every meter window over the game, and on all virtual desktops or on one (Settings → Overlay).
/// </summary>
/// <remarks>
/// Why secondary windows went behind the game on Diego's desk (AkuWM source, 2026-10-13): AkuWM manages every
/// unowned, titled, non-tool top-level window, and on a monitor where a game is fullscreen it takes those windows out
/// of the always-on-top band and places them behind the game (Desk.Compute: WantBanded false + WantBehind). The
/// overlay escaped because WPF owns a ShowInTaskbar=False window by a hidden window and the overlay is a tool window;
/// Settings, History and the breakdown were not even Topmost. Owning every secondary window by the overlay puts it out
/// of any window manager's reach (AkuWM skips GA_ROOTOWNER != hwnd) and Windows keeps an owned window above its owner.
///
/// Why the overlay shows on every virtual desktop: it is WS_EX_TOOLWINDOW, and the shell's virtual desktop manager does
/// not track tool windows: they show on all desktops. One desktop = drop the tool bit (the WPF hidden owner keeps it off
/// the taskbar and Alt+Tab). Secondary windows keep their taskbar button (WS_EX_APPWINDOW), so the shell tracks them per
/// desktop; all desktops = pin them through the undocumented IVirtualDesktopPinnedApps.
/// </remarks>
internal static unsafe partial class WindowPinning
{
    private static nint _anchor;

    /// <summary>Settings.AllDesktops as last applied.</summary>
    public static bool AllDesktops { get; private set; } = true;

    /// <summary>At startup, before the overlay is created (its tool bit is set from this).</summary>
    public static void Configure(bool allDesktops) => AllDesktops = allDesktops;

    /// <summary>The main overlay's handle (it must exist): owner of every secondary window from now on.</summary>
    public static void Anchor(nint overlay) => _anchor = overlay;

    /// <summary>Ex-style bits of an overlay card (main or popped out) for the current setting.</summary>
    public static long OverlayExStyle => NativeMethods.WS_EX_NOACTIVATE | (AllDesktops ? NativeMethods.WS_EX_TOOLWINDOW : 0);

    /// <summary>
    /// Before Show() of every secondary window: topmost, owned by the overlay (unless it already has an owner), taskbar
    /// button kept, pinned to all desktops while the setting is on.
    /// </summary>
    public static void KeepAboveGame(Window w)
    {
        // WPF creates a Topmost window with WS_EX_TOPMOST in CreateWindowEx, so no SetWindowPos is needed at creation.
        w.Topmost = true;
        var helper = new WindowInteropHelper(w);
        // Set before the handle exists: WPF passes it as the parent of CreateWindowEx. A Window.Owner (NameNode over the
        // overlay, Restore over Settings) already chains to the overlay.
        if (w.Owner is null && helper.Owner == 0 && _anchor != 0) helper.Owner = _anchor;
        if (w.ShowInTaskbar)
            w.SourceInitialized += static (s, _) => NativeMethods.SetExStyle(new WindowInteropHelper((Window)s!).Handle, NativeMethods.WS_EX_APPWINDOW);
        w.IsVisibleChanged += static (s, e) => { if ((bool)e.NewValue) PinSoon((Window)s!); };
    }

    /// <summary>Settings saved: overlay cards change their tool bit, secondary windows are pinned or unpinned.</summary>
    public static void Apply(bool allDesktops)
    {
        if (allDesktops == AllDesktops) return;
        AllDesktops = allDesktops;
        foreach (Window w in Application.Current.Windows)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == 0) continue;
            if (w is Windows.OverlayWindow)
            {
                // The shell reads the tool bit when a window is shown; a style change on a visible window is not picked up.
                var visible = w.IsVisible;
                if (visible) w.Hide();
                if (allDesktops) NativeMethods.SetExStyle(hwnd, NativeMethods.WS_EX_TOOLWINDOW);
                else NativeMethods.SetExStyle(hwnd, 0, NativeMethods.WS_EX_TOOLWINDOW);
                if (visible) w.Show();
            }
            else if (w.IsVisible && w.ShowInTaskbar && (allDesktops || _everPinned)) Pin(hwnd, allDesktops);
        }
    }

    private static void PinSoon(Window w)
    {
        if (!w.ShowInTaskbar || (!AllDesktops && !_everPinned)) return;
        // The shell creates the window's application view after it is shown, asynchronously.
        w.Dispatcher.InvokeAsync(() =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != 0 && w.IsVisible && !Pin(hwnd, AllDesktops))
            {
                var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                retry.Tick += (_, _) => { retry.Stop(); if (w.IsVisible) Pin(hwnd, AllDesktops); };
                retry.Start();
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    // ------------------------------------------------------------ above a game that makes itself topmost

    /// <summary>
    /// The foreground window when it belongs to another process, is itself always-on-top and covers its monitor (a
    /// borderless game that asserts HWND_TOPMOST); 0 otherwise. A non-topmost window can never cover a topmost one.
    /// </summary>
    public static nint TopmostGameInFront()
    {
        var fg = GetForegroundWindow();
        if (fg == 0) return 0;
        if (((long)NativeMethods.GetWindowLongPtr(fg, NativeMethods.GWL_EXSTYLE) & WS_EX_TOPMOST) == 0) return 0;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid == (uint)Environment.ProcessId) return 0;
        if (!GetWindowRect(fg, out var r)) return 0;
        var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        if (mon == 0 || !GetMonitorInfoW(mon, ref mi)) return 0;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom ? fg : 0;
    }

    /// <summary>
    /// Puts <paramref name="hwnd"/> back on top of the topmost band when <paramref name="game"/> sits above it. Owned
    /// windows (every secondary window) move with it. The walk is short: only topmost windows are above a topmost one.
    /// </summary>
    public static void KeepAbove(nint hwnd, nint game)
    {
        if (hwnd == 0) return;
        var above = hwnd;
        for (var i = 0; i < 64; i++)
        {
            above = GetWindow(above, GW_HWNDPREV);
            if (above == 0) return;
            if (above == game)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                return;
            }
        }
    }

    // ------------------------------------------------------------ virtual desktops (undocumented shell COM)

    // Interface ids as used by MScholtes/VirtualDesktop; these two have not changed from Windows 10 to 11 24H2, unlike
    // IVirtualDesktopManagerInternal. Any failure is logged once and the windows keep the shell's default (one desktop).
    private const uint CLSCTX_LOCAL_SERVER = 4;
    private static nint _pinnedApps, _views;
    private static bool _comBroken, _everPinned, _viewMissLogged;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint ppv);

    private static bool EnsureCom()
    {
        if (_pinnedApps != 0) return true;
        if (_comBroken) return false;
        nint shell = 0;
        try
        {
            var clsidShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
            var iidServiceProvider = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
            var hr = CoCreateInstance(clsidShell, 0, CLSCTX_LOCAL_SERVER, iidServiceProvider, out shell);
            if (hr < 0) return ComFailed("ImmersiveShell", hr);
            var queryService = (delegate* unmanaged<nint, Guid*, Guid*, nint*, int>)(*(nint**)shell)[3];
            var clsidPinned = new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
            var iidPinned = new Guid("4CE81583-1E4C-4632-A621-07A53543148F");
            var iidViews = new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
            nint pinned = 0, views = 0;
            hr = queryService(shell, &clsidPinned, &iidPinned, &pinned);
            if (hr < 0) return ComFailed("IVirtualDesktopPinnedApps", hr);
            hr = queryService(shell, &iidViews, &iidViews, &views);
            if (hr < 0) { Marshal.Release(pinned); return ComFailed("IApplicationViewCollection", hr); }
            _pinnedApps = pinned;
            _views = views;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Virtual desktop pinning unavailable", ex);
            _comBroken = true;
            return false;
        }
        finally
        {
            if (shell != 0) Marshal.Release(shell);
        }
    }

    private static bool ComFailed(string what, int hr)
    {
        Log.Info($"Virtual desktop pinning unavailable: {what} 0x{hr:X8}; windows stay on one desktop");
        _comBroken = true;
        return false;
    }

    /// <summary>Pins or unpins one window's application view; false when the shell has no view for it (yet).</summary>
    private static bool Pin(nint hwnd, bool pin)
    {
        if (!EnsureCom()) return true; // nothing to retry
        nint view = 0;
        try
        {
            // IApplicationViewCollection: IUnknown(3), GetViews, GetViewsByZOrder, GetViewsByAppUserModelId, GetViewForHwnd = 6.
            var getView = (delegate* unmanaged<nint, nint, nint*, int>)(*(nint**)_views)[6];
            var hr = getView(_views, hwnd, &view);
            if (hr < 0 || view == 0)
            {
                if (!_viewMissLogged) { _viewMissLogged = true; Log.Info($"Virtual desktop pinning: no view for a window yet (0x{hr:X8})"); }
                return false;
            }
            // IVirtualDesktopPinnedApps: IUnknown(3), IsAppIdPinned, PinAppID, UnpinAppID, IsViewPinned = 6, PinView = 7, UnpinView = 8.
            var isPinned = (delegate* unmanaged<nint, nint, int*, int>)(*(nint**)_pinnedApps)[6];
            int already;
            if (isPinned(_pinnedApps, view, &already) >= 0 && (already != 0) == pin) return true;
            var change = (delegate* unmanaged<nint, nint, int>)(*(nint**)_pinnedApps)[pin ? 7 : 8];
            hr = change(_pinnedApps, view);
            if (hr < 0) return ComFailed(pin ? "PinView" : "UnpinView", hr);
            if (pin) _everPinned = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Virtual desktop pinning failed", ex);
            _comBroken = true;
            return true;
        }
        finally
        {
            if (view != 0) Marshal.Release(view);
        }
    }

    // ------------------------------------------------------------ user32

    private const long WS_EX_TOPMOST = 0x00000008;
    private const uint GW_HWNDPREV = 3;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const nint HWND_TOPMOST = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hWnd, uint cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hWnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
