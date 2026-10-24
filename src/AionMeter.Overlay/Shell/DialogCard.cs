namespace AionMeter.Overlay.Shell;

/// <summary>
/// A WPF dialog on the layer-shell card's output (A2M-50: Report a problem, the Build tab's questlog import): a
/// <see cref="LayerWindow"/> (centred, keyboard on demand, X / Esc close). Not modal; the owner talks to it through callbacks. One instance
/// per kind: <see cref="ShowSingle{T}"/>.
/// </summary>
internal abstract class DialogCard : LayerWindow
{
    private static readonly Dictionary<Type, DialogCard> Open = new();

    protected DialogCard(AppShell shell, string key, string title, double width, double height) : base(shell, "aionmeter-" + key, title, width, height) { }

    /// <summary>The open dialog of this kind (raised in window mode; a layer surface stays where it is: no raise), or a new one shown.</summary>
    public static T ShowSingle<T>(Func<T> make) where T : DialogCard
    {
        if (Open.TryGetValue(typeof(T), out var w) && w.IsOpen) { w.Raise(); return (T)w; }
        var n = make();
        Open[typeof(T)] = n;
        n.Closed += () => { if (Open.TryGetValue(typeof(T), out var cur) && ReferenceEquals(cur, n)) Open.Remove(typeof(T)); };
        n.Show();
        return n;
    }
}
