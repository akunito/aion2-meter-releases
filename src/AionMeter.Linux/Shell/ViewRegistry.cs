using Avalonia.Media;
using AionMeter.Linux.Views;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>One pill of the card's tab strip, WPF order. <see cref="Tip"/> reads the current UiText (language switches without a restart).</summary>
public sealed record TabSpec(string Key, Geometry Icon, Func<UiText, string> Tip);

/// <summary>
/// A2M-48 phase 3b: which hosted view each tab key opens. A view agent adds ONE line to <see cref="Factories"/> (e.g.
/// <c>["atlas"] = s =&gt; new AtlasView(s),</c>) and its pill in the strip turns on; nothing else in the shell changes.
/// </summary>
public static class ViewRegistry
{
    /// <summary>The strip, left to right (the DPS card is "meter"; "settings" and "fight" are reached from the gear / a row and are not here).</summary>
    public static readonly TabSpec[] Strip =
    [
        new("meter", Visuals.IconMeter, _ => "DPS"),
        new("build", Visuals.IconBuild, t => t.TrayBuild),
        new("items", Visuals.IconItems, t => t.TrayItems),
        new("timers", Visuals.IconClock, t => t.TipTimers),
        new("atlas", Visuals.IconAtlas, t => t.TrayAtlas),
        new("top", Visuals.IconTop, t => t.TrayTop),
        new("prices", Visuals.IconPrices, t => t.TrayPrices),
        new("craft", Visuals.IconCraft, t => t.TrayCraft),
    ];

    public static readonly Dictionary<string, Func<AppShell, IHostedView>> Factories = new()
    {
        ["settings"] = s => new SettingsView(s),
        ["fight"] = s => new FightView(s),
        ["atlas"] = s => new AtlasView(s),
        ["timers"] = s => new BossTimersView(s),
        ["top"] = s => new TopDpsView(s),
        ["items"] = s => new ItemsView(s),
        ["craft"] = s => new CraftView(s),
        ["prices"] = s => new PricesView(s),
        // next view agents: ["build"] = s => new BuildView(s), ["items"] = …, ["timers"] = …, ["atlas"] = …, ["top"] = …, ["prices"] = …, ["craft"] = …
    };

    public static bool Has(string key) => Factories.ContainsKey(key);

    public static IHostedView? Create(string key, AppShell shell) => Factories.TryGetValue(key, out var f) ? f(shell) : null;
}
