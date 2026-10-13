using AionMeter.Core.Armory;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// What the Top DPS and Boss Timers tabs need beyond UiKit (A2M-50): their stylesheet (k-tt-*; a provider of its own so KitCss stays as the
/// reference port left it, scaled like the card's on every rebuild), the WPF TabToggle, the Avalonia MenuFlyout as a popover of choices, a
/// wrap panel that never widens the card, and the bell icon.
/// </summary>
internal static class TopTimersKit
{
    private static Gtk.CssProvider? _css;
    private static double _cssScale;

    public static readonly Icon Bell = new("M4 11 L4 7 C4 4.5 5.8 2.8 8 2.8 C10.2 2.8 12 4.5 12 7 L12 11 L13.5 12.5 L2.5 12.5 Z M6.5 14.2 L9.5 14.2");

    public static string Sheet() => $$"""
        .k-tt-frame { background-color: rgba(255,255,255,0.051); border: 1px solid transparent; border-radius: 9px; padding: 8px 10px; margin-bottom: 7px; }
        .k-tt-frame.k-tt-hot { border-color: {{Palette.Hex(Palette.GoldDim)}}; }
        .k-tt-tab { all: unset; padding: 4px 10px 4px 10px; margin-right: 2px; border-bottom: 2px solid transparent; }
        .k-tt-tab.k-tt-on { border-bottom-color: {{Palette.Hex(Palette.Gold)}}; }
        .k-root .k-tt-tab label { font-size: 12.5px; color: {{Palette.Hex(Palette.TextDim)}}; }
        .k-root .k-tt-tab.k-tt-on label { color: {{Palette.Hex(Palette.GoldBright)}}; font-weight: 600; }
        .k-root .k-tt-tab:disabled label { color: {{Palette.Hex(Palette.TextMute)}}; }
        .k-root label.k-tt-num { font-feature-settings: "tnum"; }
        .k-tt-portrait { min-width: 44px; min-height: 44px; border-radius: 23px; border: 1px solid {{Palette.Hex(Palette.GoldDim)}}; background-color: {{Palette.Hex(Palette.Bg2)}}; }
        .k-tt-bell { all: unset; min-width: 26px; min-height: 24px; border-radius: 6px; margin-right: 4px; }
        .k-tt-bell.k-tt-on { background-color: rgba(201,164,92,0.15); }
        .k-tt-detail { border-top: 1px solid rgba(255,255,255,0.133); padding-top: 8px; }
        .k-tt-loading { background-color: rgba(20,26,38,0.85); border: 1px solid rgba(255,255,255,0.2); border-radius: 10px; padding: 14px 22px; margin-top: 70px; }
        .k-root popover.k-tt-menu button { all: unset; padding: 4px 10px; border-radius: 5px; }
        .k-root popover.k-tt-menu button:hover { background-color: rgba(201,164,92,0.18); }
        .k-root popover.k-tt-menu label, .k-root .k-chip popover.k-tt-menu label { font-size: 12.5px; font-weight: 400; color: {{Palette.Hex(Palette.Text)}}; }
        """;

    /// <summary>Loads (or rescales) this kit's provider; the views call it on construction (they are rebuilt with the card on a rescale).</summary>
    public static void EnsureCss(double scale)
    {
        if (_css is not null && Math.Abs(_cssScale - scale) < 0.001) return;
        if (_css is null)
        {
            _css = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, _css, 800);   // GTK_STYLE_PROVIDER_PRIORITY_USER, as the card's
        }
        _cssScale = scale;
        _css.LoadFromString(CardText.ScaleCss(Sheet(), scale));
    }

    /// <summary>WPF TabToggle: text with a gold underline when selected (<see cref="Paint"/>).</summary>
    public static Gtk.Button TabToggle(string text, Action click)
    {
        var b = Gtk.Button.New();
        b.AddCssClass("k-tt-tab");
        b.Child = Gtk.Label.New(text);
        b.SetCursorFromName("pointer");
        b.FocusOnClick = false;
        b.Valign = Gtk.Align.Center;
        b.OnClicked += (_, _) => click();
        return b;
    }

    public static void SetToggleText(Gtk.Button b, string text)
    {
        if (b.GetChild() is Gtk.Label l) l.SetText(text);
    }

    public static void Paint(Gtk.Button b, bool on)
    {
        if (on) b.AddCssClass("k-tt-on"); else b.RemoveCssClass("k-tt-on");
    }

    /// <summary>
    /// Avalonia MenuFlyout under <paramref name="anchor"/>: one line per choice, a click runs it and closes. A popover is an xdg_popup on the
    /// card's layer surface (as the kit's drop-downs); it is unparented once closed, so nothing accumulates on the chip.
    /// </summary>
    public static void Menu(Gtk.Widget anchor, IReadOnlyList<(string Label, Action Pick)> items)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        var pop = Gtk.Popover.New();
        pop.AddCssClass("k-tt-menu");
        pop.SetHasArrow(false);
        pop.SetPosition(Gtk.PositionType.Bottom);
        for (var i = 0; i < items.Count; i++)
        {
            var (label, pick) = items[i];
            var l = Gtk.Label.New(label);
            l.Xalign = 0;
            var b = Gtk.Button.New();
            b.Child = l;
            b.FocusOnClick = false;
            b.OnClicked += (_, _) => { pop.Popdown(); pick(); };
            box.Append(b);
        }
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.PropagateNaturalHeight = true;
        scroll.PropagateNaturalWidth = true;
        scroll.MaxContentHeight = UiKit.Px(360);
        scroll.Child = box;
        pop.Child = scroll;
        pop.SetParent(anchor);
        pop.OnClosed += (_, _) => UiLoop.Post(() => { if (pop.GetParent() is not null) pop.Unparent(); });
        pop.Popup();
    }
}

/// <summary>
/// Avalonia WrapPanel for a row of toggles: children go into lines by their natural widths against the width the panel is given. A layer
/// surface takes its content's natural width, so the lines sit in a scrolled window with an External policy (natural width ~0, cannot widen the
/// card) and the width is read from a zero-height sensor beside it; a new width relays the lines.
/// </summary>
internal sealed class WrapLines
{
    private readonly List<Gtk.Widget> _items = new();
    private readonly List<Gtk.Box> _lines = new();
    private readonly Gtk.Box _stack = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly int _fallback;
    private int _width;
    private int[] _lineOf = [];

    public Gtk.Box Widget { get; } = Gtk.Box.New(Gtk.Orientation.Vertical, 0);

    /// <param name="fallback">Width in unscaled px until the panel is allocated.</param>
    public WrapLines(double fallback)
    {
        _fallback = UiKit.Px(fallback);
        var sensor = Gtk.DrawingArea.New();
        sensor.Hexpand = true;
        sensor.CanTarget = false;
        sensor.OnResize += (_, e) => { if (e.Width != _width) { _width = e.Width; Layout(); } };
        Widget.Append(sensor);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        scroll.Child = _stack;
        scroll.Hexpand = true;
        Widget.Append(scroll);
    }

    public void Clear()
    {
        foreach (var l in _lines) _stack.Remove(l);
        _lines.Clear();
        _items.Clear();
    }

    public void Add(Gtk.Widget w) => _items.Add(w);

    /// <summary>Places the children added since <see cref="Clear"/>; children only move when one changes line.</summary>
    public void Layout()
    {
        var n = _items.Count;
        if (n == 0) return;
        var avail = _width > 0 ? _width : _fallback;
        if (_lines.Count == 0) _lines.Add(NewLine());
        // measured where they are (a parented widget has its CSS): new ones go on line 0 first
        for (var i = 0; i < n; i++) if (_items[i].GetParent() is null) _lines[0].Append(_items[i]);
        if (_lineOf.Length != n) _lineOf = new int[n];
        int x = 0, line = 0;
        var same = true;
        for (var i = 0; i < n; i++)
        {
            _items[i].Measure(Gtk.Orientation.Horizontal, -1, out _, out var nat, out _, out _);
            if (x > 0 && x + nat > avail) { line++; x = 0; }
            x += nat;
            same &= _lineOf[i] == line && ReferenceEquals(_items[i].GetParent(), line < _lines.Count ? _lines[line] : null);
            _lineOf[i] = line;
        }
        if (same) return;
        for (var i = 0; i < n; i++) if (_items[i].GetParent() is Gtk.Box from) from.Remove(_items[i]);
        while (_lines.Count <= line) _lines.Add(NewLine());
        for (var i = _lines.Count - 1; i > line; i--) { _stack.Remove(_lines[i]); _lines.RemoveAt(i); }
        for (var i = 0; i < n; i++) _lines[_lineOf[i]].Append(_items[i]);
    }

    private Gtk.Box NewLine()
    {
        var b = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _stack.Append(b);
        return b;
    }
}

/// <summary>
/// Optional on the "fight" view (Avalonia FightView.Card / UpdateCard / RefreshProfile): lets Top DPS show a profile the armory fetched after
/// the tab opened. IFightTab has no such members; without this the view re-sends SetFight / SetCard while the fight tab is still in front.
/// </summary>
internal interface IFightTabProfile
{
    PlayerCard? Card { get; }
    void UpdateCard(PlayerCard card, string? status);
    void RefreshProfile();
}
