using AionMeter.Overlay.Shell;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>
/// The GTK application, the layer surface and its placement (margins = position), drag/resize, click-through and hide; A2M-50: the keyboard
/// mode and the size per tab, and the <see cref="AppShell"/> the hosted views get (it outlives card rebuilds).
/// The card itself (widgets, CSS, snapshots, tab host) is <see cref="Card"/>.
/// </summary>
internal sealed class OverlayHost : ICardHost
{
    private readonly OverlaySettings _s;
    private readonly string _socket;
    private Gtk.Application? _app;
    private Gtk.Window? _win;
    private IntPtr _w;
    private Card? _card;
    private OverlayClient? _client;
    private AppShell? _shell;
    private IntPtr _monitor;
    private int _outW, _outH;
    private double _outScale;
    private readonly string _defaultMonitor;
    private int _dragH, _dragV;
    // the drag in layout coordinates (A2M-50, moving to another output): outputs at the press, the one the card is on, press point, grab point
    private LayerShell.Monitor[] _dragMons = [];
    private OutputRect[] _dragRects = [];
    private int _dragOut = -1;
    private double _pressX, _pressY, _grabX, _grabY;
    private bool _remapping, _dragClaimed;
    private string _dragMonitor = "";
    private (int W, int H, int MH, int MV) _resizeStart;
    private bool _hidden;

    private readonly string _uiJson;
    private readonly bool _scaleFixed;
    private DateTime _uiJsonStamp;

    /// <param name="uiJson">ui.json: the shared "UI scale" (unless <paramref name="scaleFixed"/>: --scale for this run).</param>
    private readonly string? _startTab, _startHelp, _startWindow;

    /// <param name="uiJson">ui.json: the shared "UI scale" (unless <paramref name="scaleFixed"/>: --scale for this run) and LastTab.</param>
    /// <param name="startTab">--tab: a tab to open once the card is up; <paramref name="startHelp"/> --help-page, <paramref name="startWindow"/> --window.</param>
    /// <param name="defaultMonitor">--monitor: the output when overlay.json names none, or when the saved one is not connected.</param>
    public OverlayHost(OverlaySettings settings, string socket, string uiJson, bool scaleFixed, string? startTab = null, string? startHelp = null, string? startWindow = null,
        string? defaultMonitor = null)
    {
        _s = settings;
        _defaultMonitor = defaultMonitor ?? "";
        _socket = socket;
        _uiJson = uiJson;
        _scaleFixed = scaleFixed;
        _startTab = startTab;
        _startHelp = startHelp;
        _startWindow = startWindow;
    }

    // ---- ICardHost (what the shell and the views see)
    public Gtk.Application App => _app!;
    public IntPtr Monitor => _monitor;
    public double Scale => _s.Scale;
    public double Opacity => _s.Opacity;

    public void SetOpacity(double opacity)
    {
        opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0.2, 1) : 0.92;
        if (Math.Abs(opacity - _s.Opacity) < 0.001) return;
        _s.Opacity = opacity;
        _s.Save();
        Card.LoadCss(_s);
    }

    public int Run()
    {
        _app = Gtk.Application.New("com.akunito.aionmeter.overlay", Gio.ApplicationFlags.NonUnique);
        _app.OnActivate += (_, _) => Activate();
        var rc = _app.RunWithSynchronizationContext(null);
        _client?.Dispose();
        _shell?.Dispose();
        return rc;
    }

    /// <summary>Runs <paramref name="a"/> on the GTK main loop (from the socket thread or a signal handler).</summary>
    public void Post(Action a) => GLib.Functions.IdleAdd(0, () => { a(); return false; });

    private void Activate()
    {
        if (!LayerShell.IsSupported())
        {
            Console.Error.WriteLine("overlay: the compositor has no wlr-layer-shell (or GDK is not on Wayland: unset GDK_BACKEND=x11)");
            _app!.Quit();
            return;
        }
        var win = Gtk.Window.New();
        _win = win;
        win.Application = _app;
        win.Title = "AION 2 meter";
        win.Decorated = false;
        win.AddCssClass("aionmeter");
        _w = win.Handle.DangerousGetHandle();

        LayerShell.InitForWindow(_w);
        LayerShell.SetNamespace(_w, "aionmeter");
        LayerShell.SetLayer(_w, LayerShell.Layer.Overlay);
        // Never take the keyboard from the game; popovers still get clicks.
        LayerShell.SetKeyboardMode(_w, LayerShell.KeyboardMode.None);
        LayerShell.SetExclusiveZone(_w, 0);
        PickMonitor();
        // the kit restyles entries, drop-downs and checks; dark Adwaita underneath keeps what it does not cover (popover arrows, selection) dark too
        if (Gtk.Settings.GetDefault() is { } gs) gs.GtkApplicationPreferDarkTheme = true;

        _client = new OverlayClient(_socket, Post);
        _shell = new AppShell(_client, this, _uiJson);
        BuildCard(null);
        ApplyPlacement();
        win.OnRealize += (_, _) => Console.Error.WriteLine($"overlay: layer surface on {(_monitor != IntPtr.Zero ? LayerShell.Monitors().Find(m => m.Handle == _monitor).Connector : "(compositor's output)")} {_outW}x{_outH}, anchor {Placement.Name(_s.Anchor)} margins {_s.MarginH},{_s.MarginV}, card {Px(_s.Width)}x{Px(_s.Height)}, scale {_s.Scale}");
        win.Present();
        if (_s.ClickThrough) SetClickThrough(true);
        _client.Start();
        _shell.StartEngine();
        // Esc on a tab goes back (the surface only has the keyboard on a tab: ON_DEMAND, after a click)
        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) => e.Keyval == 0xff1b && _card is not null && _card.Escape();   // GDK_KEY_Escape
        win.AddController(keys);
        if (_startTab is { } tab) Post(() => _card?.SelectTab(tab));
        if (_startHelp is { } help) Post(() => _shell.ShowHelp(help));
        if (_startWindow is { } dialog) GLib.Functions.TimeoutAdd(0, 800, () => { _shell.ShowWindow(dialog); return false; });
        // the shared UI scale: the tools window's Settings / Ctrl+wheel write ui.json; a 2 s mtime poll is enough for a setting
        _uiJsonStamp = Stamp(_uiJson);
        if (!_scaleFixed)
            GLib.Functions.TimeoutAddSeconds(0, 2, () =>
            {
                var st = Stamp(_uiJson);
                if (st != _uiJsonStamp) { _uiJsonStamp = st; Rescale(AionMeter.Service.Overlay.UiScale.Read(_uiJson), save: false); }
                return true;
            });
    }

    private static DateTime Stamp(string path) { try { return File.GetLastWriteTimeUtc(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; } }

    /// <summary>A new card at the new scale (GTK has no layout transform; CSS px, icon and slot sizes are rebuilt), same place and state.</summary>
    public void Rescale(double scale, bool save)
    {
        scale = AionMeter.Service.Overlay.UiScale.Clamp(scale);
        if (_card is null || Math.Abs(scale - _s.Scale) < 0.001) return;
        _s.Scale = scale;
        if (save && !_scaleFixed) { AionMeter.Service.Overlay.UiScale.Write(_uiJson, scale); _uiJsonStamp = Stamp(_uiJson); }
        BuildCard(_card.Tab);
        ApplyPlacement();
        Console.Error.WriteLine($"overlay: scale {scale:0.##}");
        _shell?.RaiseUiScaleChanged(scale);
    }

    /// <summary>A new card (start, rescale) reopening <paramref name="tab"/>: views are recreated, the shell and the socket stay.</summary>
    private void BuildCard(string? tab)
    {
        var win = _win!;
        var old = _card;
        old?.Detach();
        _card = new Card(_s, _shell!, ToggleHidden, () => SetClickThrough(true), OnTabChanged);
        win.Child = _card.Root;
        _card.Seed(old?.Last);
        if (_s.ClickThrough) _card.SetClickThrough(true);
        ApplySize();
        if (tab is not null && tab != TabState.Meter) _card.SelectTab(tab);

        // on the card's root, not the window: on the window the grip's claim did not deny it, and both gestures fought over the margins
        // during a resize (WAYLAND_DEBUG, nested sway 2026-10-13); a drag ends at a re-map on another output either way
        var drag = Gtk.GestureDrag.New();
        drag.OnDragBegin += (_, e) => DragBegin(e.StartX, e.StartY);
        drag.OnDragUpdate += (g, e) =>
        {
            // Measured in the nested sway (2026-10-13): a pill, the BOSS chip or a check pressed and dragged moved the card AND clicked on
            // release (the pointer stays over the widget, which moves with it): DPS pill dragged on Settings switched to DPS. Past a few px
            // the move claims the press, which cancels the children's click gestures.
            if (!_dragClaimed && e.OffsetX * e.OffsetX + e.OffsetY * e.OffsetY > 16) _dragClaimed = g.SetState(Gtk.EventSequenceState.Claimed);
            DragUpdate(e.OffsetX, e.OffsetY);
        };
        drag.OnDragEnd += (_, _) => DragEnd();
        _card.Root.AddController(drag);

        var resize = Gtk.GestureDrag.New();
        resize.OnDragBegin += (g, _) =>
        {
            g.SetState(Gtk.EventSequenceState.Claimed);   // the move gesture on the card must not see this press
            var (cw, ch) = CardSize();
            _resizeStart = (cw, ch, _s.MarginH, _s.MarginV);
        };
        resize.OnDragUpdate += (_, e) =>
        {
            var (minW, minH) = TabState.MinSize(_card?.Tab ?? TabState.Meter);
            var (w, h, mh, mv) = Placement.Resize(_s.Anchor, _resizeStart.W, _resizeStart.H, _resizeStart.MH, _resizeStart.MV, e.OffsetX, e.OffsetY,
                Px(minW), Px(minH), _outW, _outH);
            _s.Width = (int)Math.Round(w / _s.Scale);
            _s.Height = (int)Math.Round(h / _s.Scale);
            (_s.MarginH, _s.MarginV) = (mh, mv);
            ApplySize();
            ApplyMargins();
        };
        resize.OnDragEnd += (_, _) => _s.Save();
        _card.Grip.AddController(resize);

        // Zoom: the wheel over the resize corner, or Ctrl+wheel anywhere when GDK knows the modifiers (keyboard mode NONE: often it does not)
        var wheel = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical);
        wheel.OnScroll += (c, e) =>
        {
            var onGrip = c.GetWidget() == _card?.Grip;
            if (!onGrip && (c.GetCurrentEventState() & Gdk.ModifierType.ControlMask) == 0) return false;
            Rescale(_s.Scale + (e.Dy < 0 ? 1 : -1) * AionMeter.Service.Overlay.UiScale.Step, save: true);
            return true;
        };
        _card.Root.AddController(wheel);
        var gripWheel = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical);
        gripWheel.OnScroll += (_, e) => { Rescale(_s.Scale + (e.Dy < 0 ? 1 : -1) * AionMeter.Service.Overlay.UiScale.Step, save: true); return true; };
        _card.Grip.AddController(gripWheel);
    }

    private int Px(int v) => (int)Math.Round(v * _s.Scale);

    /// <summary>The saved output (overlay.json Monitor), else --monitor, else the compositor's pick. A missing saved output is kept in
    /// overlay.json, so the card returns to it once it is connected again.</summary>
    private void PickMonitor()
    {
        var mons = LayerShell.Monitors();
        foreach (var want in (ReadOnlySpan<string>)[_s.Monitor, _defaultMonitor])
        {
            if (want.Length == 0) continue;
            foreach (var m in mons)
            {
                if (m.Connector != want) continue;
                LayerShell.SetMonitor(_w, m.Handle);
                UseMonitor(m);
                Console.Error.WriteLine($"overlay: output {m.Connector} {m.Width}x{m.Height}+{m.X}+{m.Y} logical, scale {m.Scale}");
                return;
            }
            Console.Error.WriteLine($"overlay: no output {want} (have {string.Join(", ", mons.Select(m => m.Connector))})");
        }
        Console.Error.WriteLine("overlay: the compositor picks the output");
        // unknown output: clamp against the largest one so a saved place is not thrown away
        foreach (var m in mons) { _outW = Math.Max(_outW, m.Width); _outH = Math.Max(_outH, m.Height); }
    }

    private void UseMonitor(LayerShell.Monitor m)
    {
        _monitor = m.Handle;
        (_outW, _outH, _outScale) = (m.Width, m.Height, m.Scale);
    }

    private void DragBegin(double x, double y)
    {
        (_dragH, _dragV, _dragMonitor, _dragClaimed) = (_s.MarginH, _s.MarginV, _s.Monitor, false);
        _dragMons = LayerShell.Monitors().ToArray();
        _dragRects = Array.ConvertAll(_dragMons, m => m.Rect);
        // the compositor's pick (no saved output, or it was missing at start): ask GDK which output the surface entered
        if (_monitor == IntPtr.Zero && LayerShell.MonitorOf(_w) is var on && on != IntPtr.Zero)
            foreach (var m in _dragMons) if (m.Handle == on) UseMonitor(m);
        _dragOut = Array.FindIndex(_dragMons, m => m.Handle == _monitor);
        if (_dragOut < 0) return;
        // the gesture is on the card's root, which fills the window: its coordinates are the surface's
        var (ox, oy) = Placement.Origin(_s.Anchor, _dragRects[_dragOut], _s.MarginH, _s.MarginV, _win!.GetWidth(), _win.GetHeight());
        (_grabX, _grabY) = (x, y);
        (_pressX, _pressY) = (ox + x, oy + y);
    }

    private void DragUpdate(double dx, double dy)
    {
        if (_remapping) return;
        var (w, h) = (_win!.GetWidth(), _win.GetHeight());
        if (_dragOut < 0)
        {
            (_s.MarginH, _s.MarginV) = Placement.Drag(_s.Anchor, _dragH, _dragV, dx, dy, _outW, _outH, w, h);
            ApplyMargins();
            return;
        }
        if (Placement.Cross(_s.Anchor, _dragRects, _dragOut, _pressX, _pressY, dx, dy, _grabX, _grabY, w, h) is { } c)
        {
            // the surface is re-created on the other output: not inside the gesture's own event dispatch
            _remapping = true;
            _dragOut = c.Output;
            var m = _dragMons[c.Output];
            Post(() => { MoveToMonitor(m, c.H, c.V); _remapping = false; });
            return;
        }
        // in layout space (= Drag on the output of the press): Sway keeps sending motion to the same wl_surface after the layer surface
        // is re-created, relative to its position at the press (WAYLAND_DEBUG), so the offsets would still be valid if GTK kept the drag
        (_s.MarginH, _s.MarginV) = Placement.MarginsAt(_s.Anchor, _dragRects[_dragOut], (int)Math.Round(_pressX + dx - _grabX),
            (int)Math.Round(_pressY + dy - _grabY), w, h);
        ApplyMargins();
    }

    private void DragEnd()
    {
        if ((_dragH, _dragV, _dragMonitor) != (_s.MarginH, _s.MarginV, _s.Monitor)) _s.Save();
    }

    /// <summary>
    /// The card onto another output. gtk4-layer-shell 1.2 re-creates the layer surface itself when the monitor of a mapped window changes
    /// (WAYLAND_DEBUG: zwlr_layer_surface_v1.destroy + get_layer_surface on the same wl_surface); an explicit hide/show around it did the
    /// same plus a "Broken accounting of active state" GTK warning. A different output scale rebuilds the card as a UI-scale change does.
    /// Measured in a nested Sway 1.11 (2026-10-13, HEADLESS-1 1600×900 at 1.5 + HEADLESS-2 at 1 and at 1.5): the drag ends at the re-map in
    /// every variant (no update after it with or without a rebuild, one with hide/show), so the card stays where it crossed (the grab point
    /// under the pointer, clamped to the new output) and a second drag continues from there; the place and the output are saved then.
    /// </summary>
    private void MoveToMonitor(LayerShell.Monitor m, int marginH, int marginV)
    {
        if (_win is null || _card is null) return;
        var rescale = Math.Abs(m.Scale - _outScale) > 0.001;
        LayerShell.SetMonitor(_w, m.Handle);
        UseMonitor(m);
        _s.Monitor = m.Connector;
        (_s.MarginH, _s.MarginV) = (marginH, marginV);
        if (rescale) BuildCard(_card.Tab);
        ApplyPlacement();
        if (_s.ClickThrough) LayerShell.SetClickThrough(_w, true);
        _s.Save();
        Console.Error.WriteLine($"overlay: moved to {m.Connector} ({m.Width}x{m.Height}+{m.X}+{m.Y}, scale {m.Scale}{(rescale ? ", card rebuilt" : "")}), margins {_s.MarginH},{_s.MarginV}");
    }

    private void ApplyPlacement()
    {
        LayerShell.SetAnchor(_w, LayerShell.Edge.Top, !Placement.IsBottom(_s.Anchor));
        LayerShell.SetAnchor(_w, LayerShell.Edge.Bottom, Placement.IsBottom(_s.Anchor));
        LayerShell.SetAnchor(_w, LayerShell.Edge.Left, !Placement.IsRight(_s.Anchor));
        LayerShell.SetAnchor(_w, LayerShell.Edge.Right, Placement.IsRight(_s.Anchor));
        var (w, h) = CardSize();
        (_s.MarginH, _s.MarginV) = Placement.Fit(_s.MarginH, _s.MarginV, _outW, _outH, w, h);
        ApplyMargins();
    }

    /// <summary>The card's size in px now: the saved DPS size, grown to the tab minimum while a tab is open (TabState.Size).</summary>
    private (int W, int H) CardSize()
    {
        var (w, h) = TabState.Size(_card?.Tab ?? TabState.Meter, _s.Width, _s.Height);
        return (Px(w), Px(h));
    }

    /// <summary>
    /// The card switched tabs: keyboard ON_DEMAND on a tab (entries can be typed into after a click), NONE on the DPS rows (the game gets the
    /// keyboard back), and the size for the tab. Measured in a nested Sway 1.11 (2026-10-13, alacritty as the focused app): with Settings in
    /// front, keys still went to alacritty until the card was clicked; a click in an entry gave the card the keyboard (typing landed in the
    /// entry); clicking the DPS pill (mode NONE) handed it back to alacritty with no extra click. Esc on a tab only works after such a click.
    /// </summary>
    private void OnTabChanged(string tab)
    {
        LayerShell.SetKeyboardMode(_w, TabState.Keyboard(tab));
        ApplySize();
        ApplyPlacement();
        Console.Error.WriteLine($"overlay: tab {tab}, keyboard {TabState.Keyboard(tab)}, card {CardSize().W}x{CardSize().H}");
    }

    private void ApplyMargins()
    {
        LayerShell.SetMargin(_w, Placement.IsRight(_s.Anchor) ? LayerShell.Edge.Right : LayerShell.Edge.Left, _s.MarginH);
        LayerShell.SetMargin(_w, Placement.IsBottom(_s.Anchor) ? LayerShell.Edge.Bottom : LayerShell.Edge.Top, _s.MarginV);
    }

    private void ApplySize()
    {
        var (w, h) = CardSize();
        _card!.SetSize(w, h);
        _win!.SetDefaultSize(w, h);
    }

    public void ToggleClickThrough() => SetClickThrough(!_s.ClickThrough);

    private void SetClickThrough(bool on)
    {
        if (_win is null) return;
        LayerShell.SetClickThrough(_w, on);
        _s.ClickThrough = on;
        _s.Save();
        _card?.SetClickThrough(on);
        Console.Error.WriteLine($"overlay: click-through {(on ? "on" : "off")}");
    }

    public void ToggleHidden()
    {
        if (_win is null) return;
        _hidden = !_hidden;
        _win.SetVisible(!_hidden);
        // a re-mapped surface starts with the default input region again
        if (!_hidden && _s.ClickThrough) LayerShell.SetClickThrough(_w, true);
    }
}
