using AionMeter.Overlay.Views;
using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>One pill of the card's tab strip, WPF order. <see cref="Tip"/> reads the current UiText (language switches without a restart).</summary>
internal sealed record TabSpec(string Key, Icon Icon, Func<UiText, string> Tip);

/// <summary>
/// A2M-50: which hosted view each tab key opens on the layer-shell card. A view agent adds ONE line to <see cref="Factories"/> (e.g.
/// <c>["atlas"] = s =&gt; new AtlasView(s),</c>) and its pill in the strip turns on; nothing else in the shell changes.
/// </summary>
internal static class ViewRegistry
{
    /// <summary>The strip, left to right = WPF OverlayWindow.xaml (UiTabs.Strip: DPS, Atlas, Top, Prices, Craft, Build, Items, Timers; the gear
    /// follows). The DPS card is "meter"; "settings" and "fight" are reached from the gear / a row and are not here.</summary>
    public static readonly TabSpec[] Strip = Array.ConvertAll(AionMeter.Service.Overlay.UiTabs.Strip, Spec);

    private static TabSpec Spec(string key) => key switch
    {
        "meter" => new(key, Icons.Meter, _ => "DPS"),
        "atlas" => new(key, Icons.Atlas, t => t.TrayAtlas),
        "top" => new(key, Icons.Top, t => t.TrayTop),
        "prices" => new(key, Icons.Prices, t => t.TrayPrices),
        "craft" => new(key, Icons.Craft, t => t.TrayCraft),
        "build" => new(key, Icons.Build, t => t.TrayBuild),
        "items" => new(key, Icons.Items, t => t.TrayItems),
        "timers" => new(key, Icons.Clock, t => t.TipTimers),
        _ => throw new ArgumentException("no strip icon for " + key),
    };

    public static readonly Dictionary<string, Func<AppShell, IHostedView>> Factories = new()
    {
        ["settings"] = s => new SettingsView(s),
        ["top"] = s => new TopDpsView(s),
        ["timers"] = s => new BossTimersView(s),
        ["prices"] = s => new PricesView(s),
        ["craft"] = s => new CraftView(s),
        ["atlas"] = s => new AtlasView(s),
    };

    public static bool Has(string key) => Factories.ContainsKey(key);

    public static IHostedView? Create(string key, AppShell shell) => Factories.TryGetValue(key, out var f) ? f(shell) : null;
}
