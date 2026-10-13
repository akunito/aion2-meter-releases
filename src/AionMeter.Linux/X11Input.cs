using System.Runtime.InteropServices;

namespace AionMeter.Linux;

/// <summary>
/// Click-through on X11 (Xwayland under Sway): an empty input shape (XShape ShapeInput) makes the pointer fall through to whatever is
/// below; Xwayland turns it into the surface's input region. Avalonia has no API for it. Own display connection: the shape is
/// server-side state of the window, so it does not need Avalonia's.
/// </summary>
internal static class X11Input
{
    private const int ShapeInput = 2, ShapeSet = 0, Unsorted = 0;
    private static IntPtr _display;

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr rects, int n, int op, int ordering);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineMask(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr pixmap, int op);

    /// <summary>False when there is no X display (native Wayland backend) or libXext is missing.</summary>
    public static bool SetClickThrough(IntPtr window, bool through)
    {
        try
        {
            if (_display == IntPtr.Zero) _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero || window == IntPtr.Zero) return false;
            if (through) XShapeCombineRectangles(_display, window, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, Unsorted);
            else XShapeCombineMask(_display, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);   // None mask = the default (whole window)
            XFlush(_display);
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
