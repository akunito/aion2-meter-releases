using System.Runtime.InteropServices;

namespace AionMeter.Overlay;

/// <summary>libgtk4-layer-shell (wlr-layer-shell-unstable-v1 for GTK4) and the few GDK/cairo calls GirCore does not wrap the way we need.</summary>
internal static partial class LayerShell
{
    private const string Lib = "libgtk4-layer-shell.so.0";

    public enum Layer { Background = 0, Bottom = 1, Top = 2, Overlay = 3 }
    public enum Edge { Left = 0, Right = 1, Top = 2, Bottom = 3 }
    public enum KeyboardMode { None = 0, Exclusive = 1, OnDemand = 2 }

    [LibraryImport("libc", EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr DlOpen(string file, int mode);

    [LibraryImport("libc", EntryPoint = "dlerror")]
    private static partial IntPtr DlError();

    /// <summary>
    /// gtk4-layer-shell 1.x interposes libwayland-client symbols, so it must be in the global scope before GTK pulls libwayland in
    /// ("linked before libwayland-client"; Python/GJS use LD_PRELOAD). .NET's own dlopen is RTLD_LOCAL, so this RTLD_GLOBAL load has
    /// to happen before the first GirCore call. Returns the loader's error, or null.
    /// </summary>
    public static string? Preload()
    {
        const int RTLD_NOW = 2, RTLD_GLOBAL = 0x100;
        if (DlOpen(Lib, RTLD_NOW | RTLD_GLOBAL) != IntPtr.Zero) return null;
        return Marshal.PtrToStringUTF8(DlError()) ?? "dlopen failed";
    }

    [LibraryImport(Lib, EntryPoint = "gtk_layer_is_supported")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial bool IsSupported();

    [LibraryImport(Lib, EntryPoint = "gtk_layer_init_for_window")]
    public static partial void InitForWindow(IntPtr window);

    [LibraryImport(Lib, EntryPoint = "gtk_layer_set_namespace", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SetNamespace(IntPtr window, string ns);

    [LibraryImport(Lib, EntryPoint = "gtk_layer_set_layer")]
    public static partial void SetLayer(IntPtr window, Layer layer);

    [LibraryImport(Lib, EntryPoint = "gtk_layer_set_monitor")]
    public static partial void SetMonitor(IntPtr window, IntPtr monitor);

    [LibraryImport(Lib, EntryPoint = "gtk_layer_set_anchor")]
    public static partial void SetAnchor(IntPtr window, Edge edge, [MarshalAs(UnmanagedType.I4)] bool anchor);

    [LibraryImport(Lib, EntryPoint = "gtk_layer_set_margin")]
    public static partial void SetMargin(IntPtr window, Edge edge, int margin);

    [LibraryImport(Lib, EntryPoint = "gtk_layer_set_exclusive_zone")]
    public static partial void SetExclusiveZone(IntPtr window, int zone);

    [LibraryImport(Lib, EntryPoint = "gtk_layer_set_keyboard_mode")]
    public static partial void SetKeyboardMode(IntPtr window, KeyboardMode mode);

    // ---- GDK / GLib / cairo
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_native_get_surface")]
    public static partial IntPtr NativeGetSurface(IntPtr native);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_native_get_surface_transform")]
    public static partial void NativeGetSurfaceTransform(IntPtr native, out double x, out double y);

    // window mode (A2M-50): the compositor moves / resizes the toplevel (xdg_toplevel.move / .resize with the press serial GDK keeps)
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_toplevel_begin_move")]
    public static partial void ToplevelBeginMove(IntPtr toplevel, IntPtr device, int button, double x, double y, uint timestamp);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_toplevel_begin_resize")]
    public static partial void ToplevelBeginResize(IntPtr toplevel, int edge, IntPtr device, int button, double x, double y, uint timestamp);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_surface_set_input_region")]
    public static partial void SurfaceSetInputRegion(IntPtr surface, IntPtr region);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_get_default")]
    private static partial IntPtr DisplayGetDefault();

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_get_monitors")]
    private static partial IntPtr DisplayGetMonitors(IntPtr display);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_monitor_get_connector")]
    private static partial IntPtr MonitorGetConnector(IntPtr monitor);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_monitor_get_geometry")]
    private static partial void MonitorGetGeometry(IntPtr monitor, out GdkRect rect);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_monitor_get_scale")]
    private static partial double MonitorGetScale(IntPtr monitor);

    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_list_model_get_n_items")]
    private static partial uint ListModelCount(IntPtr model);

    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_list_model_get_item")]
    private static partial IntPtr ListModelItem(IntPtr model, uint i);

    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
    private static partial void Unref(IntPtr obj);

    [LibraryImport("libcairo.so.2", EntryPoint = "cairo_region_create")]
    private static partial IntPtr RegionCreate();

    [LibraryImport("libcairo.so.2", EntryPoint = "cairo_region_destroy")]
    private static partial void RegionDestroy(IntPtr region);

    [StructLayout(LayoutKind.Sequential)]
    public struct GdkRect { public int X, Y, Width, Height; }

    public readonly record struct Monitor(IntPtr Handle, string Connector, int X, int Y, int Width, int Height, double Scale)
    {
        public OutputRect Rect => new(Connector, X, Y, Width, Height, Scale);
    }

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_display_get_monitor_at_surface")]
    private static partial IntPtr DisplayGetMonitorAtSurface(IntPtr display, IntPtr surface);

    /// <summary>The output the window's surface is on (the compositor's pick when no monitor was set), or zero before it is mapped.</summary>
    public static IntPtr MonitorOf(IntPtr window)
    {
        var display = DisplayGetDefault();
        var surface = NativeGetSurface(window);
        return display == IntPtr.Zero || surface == IntPtr.Zero ? IntPtr.Zero : DisplayGetMonitorAtSurface(display, surface);
    }

    /// <summary>The outputs GDK knows (connector name, logical position and size, scale). The monitor objects stay referenced by the display's list.</summary>
    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        var display = DisplayGetDefault();
        if (display == IntPtr.Zero) return list;
        var model = DisplayGetMonitors(display);
        var n = ListModelCount(model);
        for (uint i = 0; i < n; i++)
        {
            var m = ListModelItem(model, i);
            if (m == IntPtr.Zero) continue;
            MonitorGetGeometry(m, out var r);
            list.Add(new Monitor(m, Marshal.PtrToStringUTF8(MonitorGetConnector(m)) ?? "", r.X, r.Y, r.Width, r.Height, MonitorGetScale(m)));
            Unref(m);   // get_item returns a new reference; the list keeps its own
        }
        return list;
    }

    /// <summary>Click-through: an empty input region hands every pointer event to whatever is below (the game); null restores the default (all of it).</summary>
    public static void SetClickThrough(IntPtr window, bool on)
    {
        var surface = NativeGetSurface(window);
        if (surface == IntPtr.Zero) return;
        if (!on) { SurfaceSetInputRegion(surface, IntPtr.Zero); return; }
        var empty = RegionCreate();
        SurfaceSetInputRegion(surface, empty);   // GDK copies the region
        RegionDestroy(empty);
    }
}
