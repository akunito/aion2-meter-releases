using AionMeter.Overlay.Shell;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>
/// The GTK application, the card's window, drag/resize, click-through and hide; A2M-50: the size per tab and the <see cref="AppShell"/> the
/// hosted views get (it outlives card rebuilds). Two modes: the window mode (default) is a plain xdg toplevel with app_id aionmeter that Sway
/// places, moves and resizes like any window (Alt+drag, sticky/floating toggles; the patched Sway's above_fullscreen rule keeps it over the
/// game), its rect saved through Sway IPC (<see cref="SwayWindow"/>); --layer is the layer-shell surface on the OVERLAY layer placed by anchor
/// and margins, with the keyboard mode per tab and the drag across outputs. The card itself (widgets, CSS, snapshots, tab host) is <see cref="Card"/>.
/// </summary>
internal sealed class OverlayHost : ICardHost
{
    /// <summary>The card's window title: SwayWindow tells it from its dialogs by it (same pid, same app_id).</summary>
    public const string Title = "AION 2 meter";
    private readonly OverlaySettings _s;
    private readonly string _socket;
    private readonly bool _layer;
    private SwayWindow? _sway;
    private SwayNode? _node;
    private bool _moveClaimed;
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
    private (int X, int Y) _edge;
    private bool _edgeClaimed;
    private int _edgeFloorW;
    private bool _hidden;

    private readonly string _uiJson;
    private readonly bool _scaleFixed;
    private DateTime _uiJsonStamp;

    /// <param name="uiJson">ui.json: the shared "UI scale" (unless <paramref name="scaleFixed"/>: --scale for this run).</param>
    private readonly string? _startTab, _startHelp, _startWindow;

    /// <param name="uiJson">ui.json: the shared "UI scale" (unless <paramref name="scaleFixed"/>: --scale for this run) and LastTab.</param>
    /// <param name="startTab">--tab: a tab to open once the card is up; <paramref name="startHelp"/> --help-page, <paramref name="startWindow"/> --window.</param>
    /// <param name="defaultMonitor">--monitor: the output when overlay.json names none, or when the saved one is not connected.</param>
    /// <param name="layer">--layer: the layer-shell card; otherwise the window mode.</param>
    public OverlayHost(OverlaySettings settings, string socket, string uiJson, bool scaleFixed, string? startTab = null, string? startHelp = null, string? startWindow = null,
        string? defaultMonitor = null, bool layer = false)
    {
        _s = settings;
        _layer = layer;
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
    public bool Layer => _layer;
    public Gtk.Window? Window => _win;
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
        // Window mode: GTK takes the Wayland app_id from the application id, else from the program name, and an application id needs a dot
        // (g_application_id_is_valid = a D-Bus name), so "aionmeter" (the Sway rules' app_id) can only come from prgname with no id.
        // (GirCore resolves the GLib library name only after a module init: SetPrgname first threw DllNotFoundException 'GLib')
        if (!_layer) { Gtk.Module.Initialize(); GLib.Functions.SetPrgname("aionmeter"); }
        _app = Gtk.Application.New(_layer ? "com.akunito.aionmeter.overlay" : null, Gio.ApplicationFlags.NonUnique);
        _app.OnActivate += (_, _) => Activate();
        var rc = _app.RunWithSynchronizationContext(null);
        _sway?.Dispose();
        _client?.Dispose();
        _shell?.Dispose();
        return rc;
    }

    /// <summary>Runs <paramref name="a"/> on the GTK main loop (from the socket thread or a signal handler).</summary>
    public void Post(Action a) => GLib.Functions.IdleAdd(0, () => { a(); return false; });

    private void Activate()
    {
        if (_layer && !LayerShell.IsSupported())
        {
            Console.Error.WriteLine("overlay: the compositor has no wlr-layer-shell (or GDK is not on Wayland: unset GDK_BACKEND=x11)");
            _app!.Quit();
            return;
        }
        var win = Gtk.Window.New();
        _win = win;
        win.Application = _app;
        win.Title = Title;
        win.Decorated = false;
        win.AddCssClass("aionmeter");
        _w = win.Handle.DangerousGetHandle();

        if (_layer)
        {
            LayerShell.InitForWindow(_w);
            LayerShell.SetNamespace(_w, "aionmeter");
            LayerShell.SetLayer(_w, LayerShell.Layer.Overlay);
            // Never take the keyboard from the game; popovers still get clicks.
            LayerShell.SetKeyboardMode(_w, LayerShell.KeyboardMode.None);
            LayerShell.SetExclusiveZone(_w, 0);
            PickMonitor();
        }
        // the kit restyles entries, drop-downs and checks; dark Adwaita underneath keeps what it does not cover (popover arrows, selection) dark too
        if (Gtk.Settings.GetDefault() is { } gs) gs.GtkApplicationPreferDarkTheme = true;

        _client = new OverlayClient(_socket, Post);
        _shell = new AppShell(_client, this, _uiJson);
        BuildCard(null);
        if (_layer)
        {
            ApplyPlacement();
            win.OnRealize += (_, _) => Console.Error.WriteLine($"overlay: layer surface on {(_monitor != IntPtr.Zero ? LayerShell.Monitors().Find(m => m.Handle == _monitor).Connector : "(compositor's output)")} {_outW}x{_outH}, anchor {Placement.Name(_s.Anchor)} margins {_s.MarginH},{_s.MarginV}, card {Px(_s.Width)}x{Px(_s.Height)}, scale {_s.Scale}");
        }
        else StartWindowMode(win);
        win.Present();
        if (_s.ClickThrough) SetClickThrough(true);
        _client.Start();
        _shell.StartEngine();
        // Esc on a tab goes back (the layer surface only has the keyboard on a tab: ON_DEMAND, after a click; a window has it once focused)
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
        var ratio = scale / _s.Scale;
        _s.Scale = scale;
        if (save && !_scaleFixed) { AionMeter.Service.Overlay.UiScale.Write(_uiJson, scale); _uiJsonStamp = Stamp(_uiJson); }
        BuildCard(_card.Tab);
        if (_layer) ApplyPlacement();
        else ScaleWindow(ratio);
        Console.Error.WriteLine($"overlay: scale {scale:0.##}");
        _shell?.RaiseUiScaleChanged(scale);
    }

    /// <summary>A new card (start, rescale) reopening <paramref name="tab"/>: views are recreated, the shell and the socket stay.</summary>
    private void BuildCard(string? tab)
    {
        var win = _win!;
        var old = _card;
        old?.Detach();
        _card = new Card(_s, _shell!, ToggleHidden, () => SetClickThrough(true), OnTabChanged, !_layer);
        win.Child = _card.Root;
        _card.Seed(old?.Last);
        if (_s.ClickThrough) _card.SetClickThrough(true);
        ApplySize();
        if (tab is not null && tab != TabState.Meter) _card.SelectTab(tab);
        if (!_layer) { WindowGestures(); return; }

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

        // Right-button drag anywhere = resize from the nearest edge/corner, what Sway's Alt+right-drag does for windows (Diego kept trying it).
        // No modifier check: with keyboard mode NONE GDK often does not know the modifiers. Like the move, it claims the press only past 4 px,
        // so a right click (Atlas node naming, pet card, unequip in Build) stays a click; a control that claims its right press keeps it.
        // Measured in a nested Sway 1.11 (2026-10-14, HEADLESS-1 1600×900 at 1.5, screenshots): the 4 edges and 4 corners on DPS (top-right
        // anchor) and on Settings (bottom-left) move the grabbed edges by the drag to the pixel and leave the opposite edges where they were,
        // also with Alt held (Sway's floating_modifier Mod1 does not catch a layer surface) and at UI scale 1.1 (surface edge exact, the frame
        // inside rendered ±1 physical px); floors 560×540 on a tab, the pill lines (403) and the rows (260 high) on DPS; edges stop at the
        // output. A 3 px right press resizes nothing; a right click on an Atlas node opens the naming panel; a right drag on the map is the map's.
        var edge = Gtk.GestureDrag.New();
        edge.SetButton(3);
        edge.OnDragBegin += (_, e) =>
        {
            _edgeClaimed = false;
            // the surface as it is: the content can hold it wider than the saved size (a pill whose countdown text grew), measured 16 px,
            // and the opposite edge is computed from it
            var (cw, ch) = CardSize();
            var (ww, wh) = (_win!.GetWidth(), _win.GetHeight());
            _resizeStart = (Math.Max(cw, ww), Math.Max(ch, wh), _s.MarginH, _s.MarginV);
            _edge = Placement.EdgeAt(e.StartX, e.StartY, ww, wh);
            _edgeFloorW = (int)Math.Ceiling(_card!.PillMinWidth() / _s.Scale);
        };
        edge.OnDragUpdate += (g, e) =>
        {
            if (!_edgeClaimed && (e.OffsetX * e.OffsetX + e.OffsetY * e.OffsetY <= 16 || !(_edgeClaimed = g.SetState(Gtk.EventSequenceState.Claimed))))
                return;
            var (minW, minH) = TabState.MinSize(_card?.Tab ?? TabState.Meter);
            minW = Math.Max(minW, _edgeFloorW);
            EdgeResize(e.OffsetX, e.OffsetY, minW, minH);
            ApplySize();
            // The content can refuse the size (measured: before the pill floor, a card resized back from 530 to 470 stayed 487 wide; the DPS
            // rows refuse less than 260 high): the surface then keeps its size and, anchored on the moving side, the opposite edge moved
            // instead. Stop the edge at the content's minimum.
            var (fw, fh) = _card!.ContentMin();
            int cw = (int)Math.Ceiling(fw / _s.Scale), ch = (int)Math.Ceiling(fh / _s.Scale);
            if (cw > _s.Width || ch > _s.Height)
            {
                EdgeResize(e.OffsetX, e.OffsetY, Math.Max(minW, cw), Math.Max(minH, ch));
                ApplySize();
            }
            ApplyMargins();
        };
        edge.OnDragEnd += (_, _) => { if (_edgeClaimed) _s.Save(); };
        _card.Root.AddController(edge);

        void EdgeResize(double dx, double dy, int minW, int minH) =>
            (_s.Width, _s.Height, _s.MarginH, _s.MarginV) = Placement.EdgeResize(_s.Anchor, _edge.X, _edge.Y, _resizeStart.W, _resizeStart.H,
                _resizeStart.MH, _resizeStart.MV, dx, dy, minW, minH, _outW, _outH, _s.Scale);

        ZoomControllers();
    }

    /// <summary>Zoom: the wheel over the resize corner, or Ctrl+wheel anywhere when GDK knows the modifiers (keyboard mode NONE: often it does not).</summary>
    private void ZoomControllers()
    {
        var wheel = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical);
        wheel.OnScroll += (c, e) =>
        {
            var onGrip = c.GetWidget() == _card?.Grip;
            if (!onGrip && (c.GetCurrentEventState() & Gdk.ModifierType.ControlMask) == 0) return false;
            Rescale(_s.Scale + (e.Dy < 0 ? 1 : -1) * AionMeter.Service.Overlay.UiScale.Step, save: true);
            return true;
        };
        _card!.Root.AddController(wheel);
        var gripWheel = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical);
        gripWheel.OnScroll += (_, e) => { Rescale(_s.Scale + (e.Dy < 0 ? 1 : -1) * AionMeter.Service.Overlay.UiScale.Step, save: true); return true; };
        _card.Grip.AddController(gripWheel);
    }

    // ------------------------------------------------------------ window mode (A2M-50: a Sway window instead of a layer surface)

    private void StartWindowMode(Gtk.Window win)
    {
        // a Wayland client cannot place itself: this is only the size of the first map (Sway's rect is restored right after it) and the
        // size outside Sway
        var g = _s.Win;
        var (w, h) = g.Saved ? (g.W, g.H) : CardSize();
        win.SetDefaultSize(w, h);
        win.OnRealize += (_, _) =>
        {
            // the pills are laid out from the window's real width (the layer card's: from the requested width), on every configure
            if (win.GetSurface() is { } surface) surface.OnLayout += (_, e) => _card?.SetLayoutWidth(e.Width);
            Console.Error.WriteLine($"overlay: window mode, app_id aionmeter, {w}x{h}, saved {(g.Saved ? $"{g.W}x{g.H}+{g.X}+{g.Y} on {g.Output}" : "none")}, scale {_s.Scale}" +
                (_defaultMonitor.Length > 0 ? $" (--monitor {_defaultMonitor} ignored: Sway places windows)" : ""));
        };
        _card!.SetLayoutWidth(w);
        _sway = SwayWindow.Start(Title, Post, g);
        if (_sway is null) Console.Error.WriteLine("overlay: no SWAYSOCK: the compositor places the window, its place is not saved");
        else _sway.Changed += OnSwayChanged;
    }

    /// <summary>Sway moved, resized, mapped or unmapped the card: the rect goes to overlay.json (tiled: not saved, the floating rect stays).</summary>
    private void OnSwayChanged(SwayNode? node)
    {
        _node = node;
        if (node is not { Floating: true } n || _card is null) return;
        var meter = _card.Tab == TabState.Meter;
        var (w, h) = WindowGeometry.SizeToSave(meter, _s.WinW, _s.WinH, n.W, n.H, Px(TabState.MinTabWidth), Px(TabState.MinTabHeight));
        var g = new WinGeom(n.Output, n.X, n.Y, w, h);
        if (g == _s.Win) return;
        _s.Win = g;
        _s.Save();
        _sway!.Saved = g;
    }

    /// <summary>
    /// Window mode tab switch: the size request is the tab's minimum (Sway keeps a floating window at least at its min size on resize); a
    /// window smaller than it is grown explicitly, and back on DPS it returns to the saved DPS size, as the layer card does.
    /// </summary>
    private void WindowTabSize(string tab)
    {
        if (_sway is null || _node is not { Floating: true } n) return;
        int w, h;
        if (tab == TabState.Meter)
        {
            if (!_s.Win.Saved) return;
            (w, h) = (_s.WinW, _s.WinH);
        }
        else (w, h) = (Math.Max(n.W, Px(TabState.MinTabWidth)), Math.Max(n.H, Px(TabState.MinTabHeight)));
        if ((w, h) != (n.W, n.H)) _sway.Command(WindowGeometry.ResizeAt(n.Id, n.X, n.Y, w, h));
    }

    /// <summary>A UI-scale change grows or shrinks the window with its content (the layer card's size is unscaled × scale).</summary>
    private void ScaleWindow(double ratio)
    {
        if (_s.Win.Saved)
        {
            _s.WinW = (int)Math.Round(_s.WinW * ratio);
            _s.WinH = (int)Math.Round(_s.WinH * ratio);
            _s.Save();
            if (_sway is not null) _sway.Saved = _s.Win;
        }
        if (_sway is not null && _node is { Floating: true } n)
            _sway.Command(WindowGeometry.ResizeAt(n.Id, n.X, n.Y, (int)Math.Round(n.W * ratio), (int)Math.Round(n.H * ratio)));
    }

    /// <summary>
    /// The layer card's gestures, handed to the compositor: a left drag anywhere (past 4 px, so clicks still click) is xdg_toplevel.move, a
    /// right drag the resize from the nearest edges (<see cref="Placement.EdgeAt"/>, Sway's own rule), the gold corner the bottom-right resize.
    /// Sway starts them only for a floating view and only with the serial of the latest button press (xdg_shell.c handle_request_move/resize).
    /// </summary>
    private void WindowGestures()
    {
        var card = _card!;
        var drag = Gtk.GestureDrag.New();
        drag.OnDragBegin += (_, _) => _moveClaimed = false;
        drag.OnDragUpdate += (g, e) =>
        {
            if (_moveClaimed || e.OffsetX * e.OffsetX + e.OffsetY * e.OffsetY <= 16 || !g.GetStartPoint(out var x, out var y)) return;
            _moveClaimed = g.SetState(Gtk.EventSequenceState.Claimed);
            if (_moveClaimed) Begin(g, -1, x, y);
        };
        card.Root.AddController(drag);

        var edge = Gtk.GestureDrag.New();
        edge.SetButton(3);
        edge.OnDragBegin += (_, _) => _edgeClaimed = false;
        edge.OnDragUpdate += (g, e) =>
        {
            if (_edgeClaimed || e.OffsetX * e.OffsetX + e.OffsetY * e.OffsetY <= 16 || !g.GetStartPoint(out var x, out var y)) return;
            _edgeClaimed = g.SetState(Gtk.EventSequenceState.Claimed);
            var (ex, ey) = Placement.EdgeAt(x, y, card.Root.GetWidth(), card.Root.GetHeight());
            if (_edgeClaimed) Begin(g, WindowGeometry.SurfaceEdge(ex, ey), x, y);
        };
        card.Root.AddController(edge);

        var grip = Gtk.GestureDrag.New();
        grip.OnDragBegin += (g, e) =>
        {
            g.SetState(Gtk.EventSequenceState.Claimed);   // the move gesture on the card must not see this press
            Begin(g, WindowGeometry.SurfaceEdge(1, 1), e.StartX, e.StartY, card.Grip);
        };
        card.Grip.AddController(grip);
        ZoomControllers();
    }

    /// <summary>xdg_toplevel.move (<paramref name="edge"/> -1) or .resize from the press at (<paramref name="x"/>, <paramref name="y"/>) in
    /// <paramref name="on"/>'s coordinates (default the card's root); the gesture is reset, the compositor owns the pointer from here.</summary>
    private void Begin(Gtk.GestureDrag g, int edge, double x, double y, Gtk.Widget? on = null)
    {
        var win = _win!;
        if ((on ?? _card!.Root).TranslateCoordinates(win, x, y, out var wx, out var wy) is false) return;
        LayerShell.NativeGetSurfaceTransform(_w, out var nx, out var ny);
        var surface = LayerShell.NativeGetSurface(_w);
        var device = g.GetDevice()?.Handle.DangerousGetHandle() ?? IntPtr.Zero;
        var button = (int)g.GetCurrentButton();
        var time = g.GetCurrentEventTime();
        if (surface == IntPtr.Zero || device == IntPtr.Zero) return;
        if (edge < 0) LayerShell.ToplevelBeginMove(surface, device, button, wx + nx, wy + ny, time);
        else LayerShell.ToplevelBeginResize(surface, edge, device, button, wx + nx, wy + ny, time);
        g.Reset();
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
        if (!_layer)
        {
            ApplySize();
            WindowTabSize(tab);
            Console.Error.WriteLine($"overlay: tab {tab}, window {_node?.W}x{_node?.H}");
            return;
        }
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
        if (!_layer)
        {
            // window mode: the size is Sway's; the request is only the floor (DPS rows, 560×540 on a tab; the card adds the pill floor)
            var (mw, mh) = TabState.MinSize(_card?.Tab ?? TabState.Meter);
            _card!.SetMinimum(Px(mw), Px(mh));
            return;
        }
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
        // window mode: a shown toplevel is a new Sway container placed by Sway; SwayWindow sees the new con id and restores the saved rect
        if (!_hidden && !_layer) { _win.Present(); _sway?.Poke(); }
        // a re-mapped surface starts with the default input region again
        if (!_hidden && _s.ClickThrough) LayerShell.SetClickThrough(_w, true);
    }
}
