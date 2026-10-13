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
    private int _dragH, _dragV;
    private (int W, int H, int MH, int MV) _resizeStart;
    private bool _hidden;

    private readonly string _uiJson;
    private readonly bool _scaleFixed;
    private DateTime _uiJsonStamp;

    /// <param name="uiJson">ui.json: the shared "UI scale" (unless <paramref name="scaleFixed"/>: --scale for this run).</param>
    private readonly string? _startTab, _startHelp, _startWindow;

    /// <param name="uiJson">ui.json: the shared "UI scale" (unless <paramref name="scaleFixed"/>: --scale for this run) and LastTab.</param>
    /// <param name="startTab">--tab: a tab to open once the card is up; <paramref name="startHelp"/> --help-page, <paramref name="startWindow"/> --window.</param>
    public OverlayHost(OverlaySettings settings, string socket, string uiJson, bool scaleFixed, string? startTab = null, string? startHelp = null, string? startWindow = null)
    {
        _s = settings;
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
        win.OnRealize += (_, _) => Console.Error.WriteLine($"overlay: layer surface on {(_s.Monitor.Length > 0 ? _s.Monitor : "(compositor's output)")} {_outW}x{_outH}, anchor {Placement.Name(_s.Anchor)} margins {_s.MarginH},{_s.MarginV}, card {Px(_s.Width)}x{Px(_s.Height)}, scale {_s.Scale}");
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

        var drag = Gtk.GestureDrag.New();
        drag.OnDragBegin += (_, _) => (_dragH, _dragV) = (_s.MarginH, _s.MarginV);
        drag.OnDragUpdate += (g, e) =>
        {
            (_s.MarginH, _s.MarginV) = Placement.Drag(_s.Anchor, _dragH, _dragV, e.OffsetX, e.OffsetY, _outW, _outH, win.GetWidth(), win.GetHeight());
            ApplyMargins();
        };
        drag.OnDragEnd += (_, _) => { if ((_dragH, _dragV) != (_s.MarginH, _s.MarginV)) _s.Save(); };
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

    private void PickMonitor()
    {
        var mons = LayerShell.Monitors();
        foreach (var m in mons)
        {
            if (_s.Monitor.Length == 0 || m.Connector != _s.Monitor) continue;
            LayerShell.SetMonitor(_w, m.Handle);
            _monitor = m.Handle;
            (_outW, _outH) = (m.Width, m.Height);
            Console.Error.WriteLine($"overlay: output {m.Connector} {m.Width}x{m.Height} logical, scale {m.Scale}");
            return;
        }
        if (_s.Monitor.Length > 0)
            Console.Error.WriteLine($"overlay: no output {_s.Monitor} (have {string.Join(", ", mons.Select(m => m.Connector))}); the compositor picks one");
        // unknown output: clamp against the largest one so a saved place is not thrown away
        foreach (var m in mons) { _outW = Math.Max(_outW, m.Width); _outH = Math.Max(_outH, m.Height); }
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
