using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>
/// The GTK application, the layer surface and its placement (margins = position), drag/resize, click-through and hide.
/// The card itself (widgets, CSS, snapshots) is <see cref="Card"/>.
/// </summary>
internal sealed class OverlayHost
{
    private readonly OverlaySettings _s;
    private readonly string _socket;
    private Gtk.Application? _app;
    private Gtk.Window? _win;
    private IntPtr _w;
    private Card? _card;
    private OverlayClient? _client;
    private int _outW, _outH;
    private int _dragH, _dragV;
    private (int W, int H, int MH, int MV) _resizeStart;
    private bool _hidden;

    private readonly string _uiJson;
    private readonly bool _scaleFixed;
    private DateTime _uiJsonStamp;

    /// <param name="uiJson">ui.json: the shared "UI scale" (unless <paramref name="scaleFixed"/>: --scale for this run).</param>
    public OverlayHost(OverlaySettings settings, string socket, string uiJson, bool scaleFixed)
    {
        _s = settings;
        _socket = socket;
        _uiJson = uiJson;
        _scaleFixed = scaleFixed;
    }

    public int Run()
    {
        _app = Gtk.Application.New("com.akunito.aionmeter.overlay", Gio.ApplicationFlags.NonUnique);
        _app.OnActivate += (_, _) => Activate();
        var rc = _app.RunWithSynchronizationContext(null);
        _client?.Dispose();
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

        _client = new OverlayClient(_socket, Post);
        BuildCard();
        ApplyPlacement();
        win.OnRealize += (_, _) => Console.Error.WriteLine($"overlay: layer surface on {(_s.Monitor.Length > 0 ? _s.Monitor : "(compositor's output)")} {_outW}x{_outH}, anchor {Placement.Name(_s.Anchor)} margins {_s.MarginH},{_s.MarginV}, card {Px(_s.Width)}x{Px(_s.Height)}, scale {_s.Scale}");
        win.Present();
        if (_s.ClickThrough) SetClickThrough(true);
        _client.Start();
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
        BuildCard();
        ApplyPlacement();
        Console.Error.WriteLine($"overlay: scale {scale:0.##}");
    }

    private void BuildCard()
    {
        var win = _win!;
        var old = _card;
        old?.Detach();
        _card = new Card(_s, _client!, ToggleHidden, () => SetClickThrough(true), key => Task.Run(() => FrontEnds.OpenUi(key)));
        win.Child = _card.Root;
        _card.Seed(old?.Last);
        if (_s.ClickThrough) _card.SetClickThrough(true);
        ApplySize();

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
            _resizeStart = (Px(_s.Width), Px(_s.Height), _s.MarginH, _s.MarginV);
        };
        resize.OnDragUpdate += (_, e) =>
        {
            var (w, h, mh, mv) = Placement.Resize(_s.Anchor, _resizeStart.W, _resizeStart.H, _resizeStart.MH, _resizeStart.MV, e.OffsetX, e.OffsetY,
                Px(OverlaySettings.MinWidth), Px(OverlaySettings.MinHeight), _outW, _outH);
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
        (_s.MarginH, _s.MarginV) = Placement.Fit(_s.MarginH, _s.MarginV, _outW, _outH, Px(_s.Width), Px(_s.Height));
        ApplyMargins();
    }

    private void ApplyMargins()
    {
        LayerShell.SetMargin(_w, Placement.IsRight(_s.Anchor) ? LayerShell.Edge.Right : LayerShell.Edge.Left, _s.MarginH);
        LayerShell.SetMargin(_w, Placement.IsBottom(_s.Anchor) ? LayerShell.Edge.Bottom : LayerShell.Edge.Top, _s.MarginV);
    }

    private void ApplySize()
    {
        int w = Px(_s.Width), h = Px(_s.Height);
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
