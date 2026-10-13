namespace AionMeter.Overlay.Shell;

/// <summary>
/// The GTK main loop for code off it (A2M-50): sockets, Tasks and signal handlers marshal here, as OverlayHost.Post does. GTK widgets must
/// only be touched on this loop.
/// </summary>
internal static class UiLoop
{
    /// <summary>Runs <paramref name="a"/> on the GTK main loop (g_idle_add, default priority).</summary>
    public static void Post(Action a) => GLib.Functions.IdleAdd(0, () => { a(); return false; });

    /// <summary>Runs <paramref name="a"/> once on the main loop after <paramref name="ms"/> milliseconds; returns the source id (<see cref="Cancel"/>).</summary>
    public static uint Delay(uint ms, Action a) => GLib.Functions.TimeoutAdd(0, ms, () => { a(); return false; });

    public static void Cancel(ref uint source)
    {
        if (source != 0) GLib.Functions.SourceRemove(source);
        source = 0;
    }

    /// <summary>Awaitable from a background Task: the result of <paramref name="f"/> computed on the main loop.</summary>
    public static Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }
}

/// <summary>Avalonia DispatcherTimer on GLib (g_timeout_add): <see cref="Tick"/> on the main loop every <see cref="Interval"/> while started.</summary>
internal sealed class UiTimer
{
    private uint _source;
    public TimeSpan Interval { get; set; }
    public event Action? Tick;
    public bool IsEnabled => _source != 0;

    public UiTimer(TimeSpan interval) => Interval = interval;

    public void Start()
    {
        if (_source != 0) return;
        _source = GLib.Functions.TimeoutAdd(0, (uint)Math.Max(1, Interval.TotalMilliseconds), () =>
        {
            Tick?.Invoke();
            return _source != 0;   // Stop() inside Tick ends the source here
        });
    }

    public void Stop()
    {
        if (_source == 0) return;
        GLib.Functions.SourceRemove(_source);
        _source = 0;
    }

    /// <summary>Stop + Start: the next tick a full interval from now (debounce).</summary>
    public void Restart() { Stop(); Start(); }
}

/// <summary>Service code that edits UI-owned state runs on the GTK loop (WPF installs a Dispatcher host the same way).</summary>
internal sealed class GtkUiHost : AionMeter.Service.IUiHost
{
    public Task Invoke(Action action) => UiLoop.Invoke(() => { action(); return true; });
    public Task<T> Invoke<T>(Func<T> func) => UiLoop.Invoke(func);
}
