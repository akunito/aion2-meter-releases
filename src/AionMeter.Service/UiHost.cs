namespace AionMeter.Service;

/// <summary>
/// The UI thread, for state the App edits only there (settings dictionaries, craft favourites). The App installs a WPF Dispatcher host
/// at start-up; the daemon has no UI thread and runs the work inline (A2M-48).
/// </summary>
public interface IUiHost
{
    Task Invoke(Action action);
    Task<T> Invoke<T>(Func<T> func);
}

public static class UiHost
{
    public static IUiHost Current { get; set; } = Inline.Instance;

    private sealed class Inline : IUiHost
    {
        public static readonly Inline Instance = new();
        public Task Invoke(Action action) { action(); return Task.CompletedTask; }
        public Task<T> Invoke<T>(Func<T> func) => Task.FromResult(func());
    }
}
