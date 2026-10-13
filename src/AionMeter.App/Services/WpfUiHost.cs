using System.Windows.Threading;

namespace AionMeter.App.Services;

/// <summary>Runs on the WPF UI thread (inline when already there), as Telemetry did with Application.Current.Dispatcher before A2M-48.</summary>
public sealed class WpfUiHost(Dispatcher dispatcher) : IUiHost
{
    public Task Invoke(Action action)
    {
        if (dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action).Task;
    }

    public Task<T> Invoke<T>(Func<T> func) => dispatcher.CheckAccess() ? Task.FromResult(func()) : dispatcher.InvokeAsync(func).Task;
}
