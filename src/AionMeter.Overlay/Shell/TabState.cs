namespace AionMeter.Overlay.Shell;

/// <summary>
/// The card's tab state machine (A2M-50, the Avalonia OverlayWindow.SelectTab rules): which tab is in front, where Settings returns to, what
/// the keyboard mode and the minimum size are. Display-free so LayerOverlayTests can drive it; the Card applies the result to widgets.
/// </summary>
internal sealed class TabState
{
    public const string Meter = "meter", Settings = "settings", Fight = "fight";

    /// <summary>Minimum card content while a tab is open (unscaled px): the Avalonia card's default size, which every tab was laid out and
    /// screenshotted at ("items-card.png" etc., 2026-10-13); its MinWidth/MinHeight (340×220) are the DPS rows' and cut the tab views' headers.</summary>
    public const int MinTabWidth = 560, MinTabHeight = 540;

    private readonly Func<string, bool> _has;

    /// <param name="has">Whether a view is registered for a key (ViewRegistry.Has); "meter" never needs one.</param>
    public TabState(Func<string, bool> has, string start = Meter)
    {
        _has = has;
        Current = Meter;
        if (start != Meter) Select(start);
    }

    public string Current { get; private set; }
    /// <summary>Where Settings goes back to (the tab in front when the gear was pressed).</summary>
    public string BeforeSettings { get; private set; } = Meter;
    /// <summary>The fight breakdown view exists (a row click or OpenFight made it): only then "fight" can be selected.</summary>
    public bool FightOpen { get; set; }

    /// <summary>Brings <paramref name="tab"/> to front; false when nothing changed (unknown key, no view yet, already in front).</summary>
    public bool Select(string tab)
    {
        if (tab != Meter && !_has(tab)) return false;
        if (tab == Fight && !FightOpen) tab = Meter;   // reached from a row / the fight list only
        if (tab == Current) return false;
        if (tab == Settings) BeforeSettings = Current;
        Current = tab;
        return true;
    }

    /// <summary>A strip pill (WPF TabPill): another tab → that tab; the selected one again → back to DPS (Settings → where it came from).</summary>
    public string PillTarget(string key) => key != Current ? key : Back();

    /// <summary>Back chip, Esc, a view's CloseRequested: Settings returns to the tab before it, everything else to DPS.</summary>
    public string Back() => Current == Settings ? BeforeSettings : Meter;

    /// <summary>
    /// Whether a pill shows in the strip with <paramref name="current"/> in front: the breakdown's only while it is in front (WPF TabFight);
    /// the gear is the Settings pill and always shows (Diego 2026-10-13: a separate gear button plus a Settings pill that appeared next to
    /// it showed two gold icons). Its click is <see cref="PillTarget"/> like every pill: Settings, again → back where it came from.
    /// </summary>
    public static bool PillVisible(string key, string current) => key != Fight || current == Fight;

    /// <summary>The tab saved as ui.json LastTab (the tools window's start tab): not DPS, Settings or the breakdown.</summary>
    public static bool Persisted(string tab) => tab is not (Meter or Settings or Fight);

    /// <summary>
    /// The layer surface's keyboard mode: NONE on the DPS rows so the game never loses the keyboard, ON_DEMAND while a tab is in front so an
    /// entry can be typed into after a click (the compositor gives focus on click and takes it back with NONE).
    /// </summary>
    public static LayerShell.KeyboardMode Keyboard(string tab) => tab == Meter ? LayerShell.KeyboardMode.None : LayerShell.KeyboardMode.OnDemand;

    /// <summary>The card's size for <paramref name="tab"/>: the saved DPS size, grown to the tab minimum while a tab is open (Diego 2026-10-10:
    /// the card keeps its size whatever the tab, as WPF; the minimum only makes the views usable).</summary>
    public static (int W, int H) Size(string tab, int width, int height) =>
        tab == Meter ? (width, height) : (Math.Max(width, MinTabWidth), Math.Max(height, MinTabHeight));

    /// <summary>The resize grip's floor for <paramref name="tab"/> (unscaled px).</summary>
    public static (int W, int H) MinSize(string tab) =>
        tab == Meter ? (OverlaySettings.MinWidth, OverlaySettings.MinHeight) : (MinTabWidth, MinTabHeight);
}
