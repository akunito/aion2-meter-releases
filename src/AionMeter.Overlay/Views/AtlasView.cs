using AionMeter.Core.Atlas;
using AionMeter.Core.GameDb;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay.Views;

/// <summary>
/// The private node atlas on the layer-shell card (A2M-50, port of the Avalonia AtlasView, behaviour unchanged): the calibrated zone map under
/// zoom/pan, seeded and seen gathering spots with their icons (clustered zoomed out), the 5 densest areas of up to two kinds, field bosses with
/// their timers, the spawns of pets below level 3, and the player with a 10 s trail and the heading of the last move. Spots and timers are the
/// viewer engine's snapshot plus the daemon's `position` message (≤ 2 Hz) and the map/timer files re-read by mtime. Right-click a node to name
/// its kind (atlas/objects.json, UI-owned; the daemon re-reads it), a pet marker for its card. One DrawingArea paints everything
/// (AtlasView.Paint.cs); the naming panel is the only widget over the map.
/// </summary>
internal sealed partial class AtlasView : HostedView
{
    private static UiText T => UiText.Current;
    /// <summary>Kind toggles per line: 14 × 31 px stays inside the ~490 px bar of a 560 px card (a wider row grows the layer surface).</summary>
    private const int KindsPerLine = 14;

    private readonly Gtk.DrawingArea _area;
    private readonly Gtk.Box _kindBar, _namePanel, _nameCatalog;
    private readonly Gtk.CheckButton _pets, _follow;
    private readonly Gtk.Scale _zoom;
    private readonly Gtk.Label _zone, _hint, _nameQuestion, _nameTimer;
    private readonly Gtk.Entry _nameOther;
    private readonly UiTimer _clock = new(TimeSpan.FromMilliseconds(500)), _nameClock = new(TimeSpan.FromSeconds(1));
    private readonly double _u;   // the card's UI scale: the map is laid out in unscaled px

    private MapCalibration? _cal;
    private int _mapShown = -1, _mapLoading = -1;
    private int _imgW, _imgH;
    private double _scale = 0.5, _tx, _ty, _f = 1;
    private bool _followZoomed, _syncingSlider, _sized;
    private double _dragTx, _dragTy;
    private double _pointerX, _pointerY;

    private PositionModel? _pos;
    private long _posAt;   // Environment.TickCount64 when _pos arrived

    private List<NodeSpot> _spots = new();
    private List<MonsterTemplate> _monsters = new();
    private List<(PetInfo Pet, int Level, MonsterTemplate Template)>? _petLayer;
    private long _petLayerKey = -1;
    private int _toggleSpots = -1;
    private int _nameCode;
    private long _nameAskedAt;
    private List<CatalogNode>? _catalog;

    public AtlasView(AppShell shell) : base(shell, "atlas", UiText.Current.AtlasTitle)
    {
        _u = shell.UiScale;
        _zone = UiKit.Line("", 12, Fg.Dim);
        _zone.Hexpand = false;
        _zone.MaxWidthChars = 28;   // an ellipsized label still asks for its whole text as natural width: capped, or a long zone name widens the card
        _zone.Xalign = 1;
        HeaderRight.Append(_zone);

        // bar: the kind toggles on their own lines, then Pets / Follow / Zoom / Fit (the WPF WrapPanel did not fit 560 px on one line)
        var bar = UiKit.VBox();
        UiKit.Margin(bar, 14, 0, 10, 6);
        _kindBar = UiKit.VBox(3);
        bar.Append(_kindBar);
        var row = UiKit.HBox();
        _pets = Check(T.AtlasPets, shell.Settings.AtlasShowPets);
        _pets.OnToggled += (_, _) =>
        {
            Shell.RefreshSettings();
            Shell.Settings.AtlasShowPets = _pets.Active;
            Shell.SaveSettings();
            HidePetCard();
            _area!.QueueDraw();
        };
        row.Append(_pets);
        _follow = Check(T.AtlasFollow, true);
        _follow.OnToggled += (_, _) => { if (_follow.Active) { _followZoomed = false; Refresh(); } };
        row.Append(_follow);
        row.Append(UiKit.Spacer());
        var zl = UiKit.Text(T.AtlasZoom, 11.5, Fg.Dim);
        zl.Hexpand = false;
        zl.MaxWidthChars = -1;
        zl.Wrap = false;
        zl.MarginEnd = UiKit.Px(6);
        row.Append(zl);
        _zoom = UiKit.Slider(Math.Log(AtlasMath.MinScale), Math.Log(AtlasMath.MaxScale), 0, 0, 120);
        _zoom.MarginEnd = UiKit.Px(10);
        _zoom.OnValueChanged += (_, _) => ZoomSlider(_zoom.GetValue());
        row.Append(_zoom);
        row.Append(UiKit.Chip(T.AtlasFit, () => { _follow.Active = false; FitWholeMap(); Refresh(); }));
        bar.Append(row);

        _area = Gtk.DrawingArea.New();
        _area.Hexpand = true;
        _area.Vexpand = true;
        _area.Focusable = false;
        _area.SetDrawFunc(Draw);
        _area.OnResize += (_, e) =>
        {
            // first allocation: the whole zone in view until Follow takes over (WPF SizeChanged from 0)
            if (!_sized && e.Width > 0) { _sized = true; if (_mapShown > 0 && !_followZoomed) FitWholeMap(); }
        };
        AddInput(_area);

        // node naming (WPF NameNodeWindow): the catalogue's kinds as icon chips, a free name as fallback; prompts from a gather close after 15 s
        var np = UiKit.VBox();
        var npHead = UiKit.HBox();
        npHead.MarginBottom = UiKit.Px(6);
        _nameQuestion = UiKit.Text("", 12.5, Fg.Text, 600);
        npHead.Append(_nameQuestion);
        var close = UiKit.IconButton(Icons.Close, T.Cancel, HideNamePanel);
        close.Valign = Gtk.Align.Start;
        close.MarginStart = UiKit.Px(6);
        npHead.Append(close);
        np.Append(npHead);
        _nameTimer = UiKit.Text("", 11, Fg.Amber);
        _nameTimer.MarginBottom = UiKit.Px(4);
        _nameTimer.Visible = false;
        np.Append(_nameTimer);
        _nameCatalog = UiKit.VBox(5);
        var catScroll = Gtk.ScrolledWindow.New();
        catScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        catScroll.PropagateNaturalHeight = true;
        catScroll.MaxContentHeight = UiKit.Px(230);
        catScroll.Child = _nameCatalog;
        np.Append(catScroll);
        var other = UiKit.HBox();
        other.MarginTop = UiKit.Px(6);
        _nameOther = UiKit.Input("", 180);
        _nameOther.PlaceholderText = "Other…";
        _nameOther.OnActivate += (_, _) => NameNode(_nameOther.GetText(), "");
        other.Append(_nameOther);
        var ok = UiKit.Chip("OK", () => NameNode(_nameOther.GetText(), ""));
        ok.MarginStart = UiKit.Px(8);
        other.Append(ok);
        np.Append(other);
        var inner = UiKit.VBox();
        inner.AddCssClass("k-dinner");
        inner.Append(np);
        _namePanel = UiKit.VBox();
        _namePanel.AddCssClass("k-dialog");
        _namePanel.Append(inner);
        _namePanel.Valign = Gtk.Align.Start;
        _namePanel.Halign = Gtk.Align.Fill;
        UiKit.Margin(_namePanel, 4, 4, 4, 4);
        _namePanel.Visible = false;

        // an overlay child is not measured by Gtk.Overlay: the panel can never widen the card
        var map = Gtk.Overlay.New();
        map.SetChild(_area);
        map.AddOverlay(_namePanel);
        map.Vexpand = true;
        map.MarginStart = map.MarginEnd = UiKit.Px(1);
        var body = UiKit.VBox();
        body.Append(bar);
        body.Append(map);
        SetBody(body);

        _hint = UiKit.Text(T.AtlasHint, 11, Fg.Mute);
        SetFooter(_hint);

        _clock.Tick += Refresh;
        _nameClock.Tick += NameTick;
        OnDetach(_nameClock.Stop);
        OnDetach(DisposeResources);

        Listen(() => shell.Daemon.Position += OnPosition, () => shell.Daemon.Position -= OnPosition);
        Listen(() => shell.Daemon.NodeNameWanted += OnNameWanted, () => shell.Daemon.NodeNameWanted -= OnNameWanted);
        Listen(() => shell.Daemon.TimersChanged += OnTimersChanged, () => shell.Daemon.TimersChanged -= OnTimersChanged);
        Listen(() => shell.EngineReady += Refresh, () => shell.EngineReady -= Refresh);
        _pos = shell.Daemon.LastPosition;
        _posAt = Environment.TickCount64;
        Refresh();
    }

    /// <summary>A one-line check box (UiKit.Check wraps at 1 char of natural width; these are short and sit in one row).</summary>
    private static Gtk.CheckButton Check(string text, bool value)
    {
        var c = UiKit.Check(text, value);
        if (c.GetChild() is Gtk.Label l) { l.Wrap = false; l.MaxWidthChars = -1; l.Hexpand = false; }
        c.Hexpand = false;
        c.MarginEnd = UiKit.Px(12);
        return c;
    }

    public override void Shown()
    {
        Refresh();
        _clock.Start();
    }

    public override void Hidden()
    {
        _clock.Stop();
        HidePetCard();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.AtlasTitle;
        _hint.SetText(T.AtlasHint);
        _toggleSpots = -1;
        Refresh();
    }

    private void OnPosition(PositionModel p)
    {
        _pos = p;
        _posAt = Environment.TickCount64;
        if (Shell.Engine is { } e) e.Atlas.MergeLive(p.Map, p.Live, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (IsShown) Refresh();
    }

    private void OnNameWanted(int code) => ShowNamePanel(code, prompt: true);
    private void OnTimersChanged() => Shell.Engine?.Timers.ReloadIfChanged();

    // ------------------------------------------------------------ data

    private void Refresh()
    {
        if (Shell.Engine is not { } e) { SetZone(T.FightLoading); return; }
        var map = _pos is { Map: > 0 } p ? p.Map : e.Atlas.MapId;
        if (map != _mapShown) LoadMap(e, map);
        if (!IsShown) return;
        if (map > 0) e.Atlas.ReloadIfChanged(map);
        e.Timers.ReloadIfChanged();
        _spots = e.Atlas.Spots(map);
        _timers = e.Timers.ForServer(e.Timers.CurrentServer);
        PrepareSpots(e);
        RefreshKinds();
        RefreshPets(e);
        Follow();
        _area.QueueDraw();
    }

    private void SetZone(string text)
    {
        _zone.SetText(text);
        _zone.SetTooltipText(text);
    }

    private void LoadMap(MeterEngine e, int map)
    {
        _mapShown = map;
        _cal = e.Calibration(map);
        _monsters = MonsterSpawns.Load(Path.Combine(MeterEngine.DataDirectory, "atlas"), map);
        _petLayer = null;
        _petLayerKey = -1;
        _toggleSpots = -1;
        _trail.Clear();
        _map?.Dispose();
        _map = null;
        InvalidateBase();
        SetZone(_cal is not null ? _cal.Name : string.Format(T.AtlasNoMap, map));
        _imgW = _imgH = 0; _f = 1; _edge = NoMapBg;
        if (_cal is not null && File.Exists(_cal.BackgroundPath))
        {
            // the header only: size and factor now, so Fit/Follow are right before the 4096² decode lands (background thread)
            if (GdkPixbuf.Pixbuf.GetFileInfo(_cal.BackgroundPath, out var w, out var h) is not null && w > 0 && h > 0)
            {
                _imgW = w; _imgH = h;
                _f = _cal.Width > 0 ? (double)w / _cal.Width : 1;   // calibration pixels → decoded image pixels
            }
            _mapLoading = map;
            var path = _cal.BackgroundPath;
            var started = Environment.TickCount64;
            AppShell.Run(() => MapImage.Decode(path), img =>
            {
                if (img is null) return;
                if (_mapShown != map || _disposed) { img.Dispose(); return; }
                _mapLoading = -1;
                _map = img;
                _imgW = img.Width; _imgH = img.Height;
                _f = _cal is { Width: > 0 } c ? (double)img.Width / c.Width : 1;
                _edge = img.Edge;
                InvalidateBase();
                Log.Info($"UI atlas map {map}: {path} decoded {img.Width}x{img.Height} + {img.Levels - 1} halvings in {Environment.TickCount64 - started} ms (background), calibration {_cal?.Width}x{_cal?.Height}, factor {_f:0.000}");
                _area.QueueDraw();
            }, "atlas map decode");
        }
        if (_follow.Active && map > 0) _followZoomed = false; else FitWholeMap();
    }

    /// <summary>The kinds known on this map, one icon toggle each (A2M-3, at most two on); rebuilt when the spot count changes.</summary>
    private void RefreshKinds()
    {
        if (_spots.Count == _toggleSpots) return;
        _toggleSpots = _spots.Count;
        var kinds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _kinds) if (k.Used) kinds.Add(k.Name);
        var on = Shell.Settings.AtlasZoneKinds;
        while (_kindBar.GetFirstChild() is { } c) _kindBar.Remove(c);
        Gtk.Box? line = null;
        var n = 0;
        foreach (var name in kinds)
        {
            if (n++ % KindsPerLine == 0) { line = UiKit.HBox(3); _kindBar.Append(line); }
            line!.Append(KindToggle(name, on.Contains(AtlasMath.KindKey(name))));
        }
        _kindBar.Visible = kinds.Count > 0;
    }

    /// <summary>A 28 px icon toggle (WPF: icon at 55 % when off, full under the mouse, a border in the kind's colour when on).</summary>
    private Gtk.DrawingArea KindToggle(string name, bool active)
    {
        var key = AtlasMath.KindKey(name);
        var icon = Icon(name);
        var hover = false;
        var d = Gtk.DrawingArea.New();
        d.SetSizeRequest(UiKit.Px(28), UiKit.Px(28));
        d.SetCursorFromName("pointer");
        d.SetTooltipText(string.Format(T.AtlasZonesTip, name));
        d.SetDrawFunc((_, cr, w, h) =>
        {
            var s = Math.Min(w, h);
            Paint.RoundRect(cr, 1, 1, s - 2, s - 2, s * 6 / 28.0);
            Paint.Argb(cr, active ? 0x44FFFFFFu : 0x22FFFFFFu);
            cr.FillPreserve();
            if (active)
            {
                Paint.Rgb(cr, icon?.Colour ?? SelfRgb);
                cr.LineWidth = 2 * s / 28.0;
                cr.Stroke();
            }
            else cr.NewPath();
            var ic = s * 20 / 28.0;
            if (icon is not null) DrawIcon(cr, icon, (s - ic) / 2, (s - ic) / 2, ic, !(active || hover) ? 0.55 : 1);
            else
            {
                // no icon: the first letter (WPF)
                var t = TextOf(name[..1], FontLetter);
                Paint.Rgb(cr, Palette.Text);
                cr.MoveTo((s - t.W) / 2.0, (s - t.H) / 2.0);
                PangoCairo.Functions.ShowLayout(cr, t.Layout);
            }
        });
        var motion = Gtk.EventControllerMotion.New();
        motion.OnEnter += (_, _) => { hover = true; d.QueueDraw(); };
        motion.OnLeave += (_, _) => { hover = false; d.QueueDraw(); };
        d.AddController(motion);
        var click = Gtk.GestureClick.New();
        click.OnPressed += (g, _) => { g.SetState(Gtk.EventSequenceState.Claimed); ToggleKind(key); };
        d.AddController(click);
        return d;
    }

    private void ToggleKind(string key)
    {
        Shell.RefreshSettings();   // never write back over a save the daemon made (set-measure) since this process read the file
        AtlasMath.ToggleKind(Shell.Settings.AtlasZoneKinds, key);
        Shell.SaveSettings();
        _toggleSpots = -1;
        // the toggles are rebuilt by Refresh: let the click that asked finish on its own widget first
        UiLoop.Post(Refresh);
    }

    /// <summary>Every spawn of a species whose pet is below level 3 (A2M-11); rebuilt when the pet list or a level changes.</summary>
    private void RefreshPets(MeterEngine e)
    {
        var pets = e.Character.Pets;
        long key = pets.Count * 1_000_003L + _monsters.Count;
        foreach (var kv in pets) key += kv.Key * 7L + kv.Value;
        if (_petLayer is not null && key == _petLayerKey) return;
        _petLayerKey = key;
        _petLayer = new();
        var below = 0;
        var species = new HashSet<int>();
        foreach (var pet in e.Pets.All)
        {
            var level = pets.GetValueOrDefault(pet.Id, 0);
            if (level >= 3 || pet.Souls.Length == 0) continue;   // no soul item = not farmable from monsters (shop/event pet)
            below++;
            foreach (var t in _monsters)
                if (MonsterSpawns.Drops(pet, t)) { _petLayer.Add((pet, level, t)); species.Add(pet.Id); }
        }
        _pets.SetTooltipText(string.Format(T.AtlasPetsTip, below, species.Count));
    }

    private static string? KindOf(MeterEngine e, NodeSpot s) => s.Seeded ? s.Name : e.Data.Objects.TryGetValue(s.Code, out var n) ? n : null;

    // ------------------------------------------------------------ view transform

    /// <summary>The map's size in unscaled px (all drawing and pointer maths run in those).</summary>
    private (double W, double H) Viewport => (Math.Max(_area.GetWidth() / _u, 1), Math.Max(_area.GetHeight() / _u, 1));

    /// <summary>The drawn player point (world cm), null when there is none.</summary>
    private (float X, float Y)? SelfPoint => _pos is { } p && (p.X != 0 || p.Y != 0) && p.Map == _mapShown ? (p.X, p.Y) : null;

    private void Follow()
    {
        if (!_follow.Active || _cal is null || SelfPoint is not { } me || _area.GetWidth() < 1) return;
        var (px, py) = _cal.ToPixel(me.X, me.Y);
        if (px < 0 || py < 0 || px > _cal.Width || py > _cal.Height) return;
        if (!_followZoomed) { _scale = AtlasMath.FollowScale; _followZoomed = true; }
        var (w, h) = Viewport;
        (_tx, _ty) = AtlasMath.Centre(px, py, _f * _scale, w, h);
        SyncSlider();
    }

    /// <summary>First view of a map: the whole zone in the window, centred; Follow then takes over once the position is known.</summary>
    private void FitWholeMap()
    {
        var (vw, vh) = (_area.GetWidth() / _u, _area.GetHeight() / _u);
        (_scale, _tx, _ty) = AtlasMath.Fit(_imgW, _imgH, vw, vh);
        SyncSlider();
        _area.QueueDraw();
    }

    private void SyncSlider()
    {
        _syncingSlider = true;
        _zoom.SetValue(Math.Log(_scale));
        _syncingSlider = false;
    }

    private void ZoomSlider(double value)
    {
        if (_syncingSlider || _cal is null) return;
        var (w, h) = Viewport;
        (_scale, _tx, _ty) = AtlasMath.ZoomTo(_scale, _tx, _ty, w, h, value);
        _area.QueueDraw();
    }

    private void AddInput(Gtk.DrawingArea area)
    {
        var motion = Gtk.EventControllerMotion.New();
        motion.OnMotion += (_, e) => { _pointerX = e.X / _u; _pointerY = e.Y / _u; };
        area.AddController(motion);

        var wheel = Gtk.EventControllerScroll.New(Gtk.EventControllerScrollFlags.Vertical);
        wheel.OnScroll += (_, e) =>
        {
            if (e.Dy == 0) return false;
            HidePetCard();
            (_scale, _tx, _ty) = AtlasMath.ZoomAt(_scale, _tx, _ty, _pointerX, _pointerY, e.Dy < 0 ? AtlasMath.WheelStep : 1 / AtlasMath.WheelStep);
            SyncSlider();
            _area.QueueDraw();   // zooming keeps Follow: the next tick re-centres at the new zoom
            return true;
        };
        area.AddController(wheel);

        var drag = Gtk.GestureDrag.New();
        drag.SetButton(1);
        drag.OnDragBegin += (g, _) =>
        {
            g.SetState(Gtk.EventSequenceState.Claimed);   // never the card's move drag: the map pans itself
            HidePetCard();
            (_dragTx, _dragTy) = (_tx, _ty);
        };
        drag.OnDragUpdate += (_, e) =>
        {
            double ox = e.OffsetX / _u, oy = e.OffsetY / _u;
            _tx = _dragTx + ox; _ty = _dragTy + oy;
            if (_follow.Active && (Math.Abs(ox) > 3 || Math.Abs(oy) > 3)) _follow.Active = false;
            _area.QueueDraw();
        };
        area.AddController(drag);

        var right = Gtk.GestureClick.New();
        right.SetButton(3);
        right.OnPressed += (g, e) =>
        {
            g.SetState(Gtk.EventSequenceState.Claimed);
            RightClick(e.X / _u, e.Y / _u);
        };
        area.AddController(right);
    }

    // ------------------------------------------------------------ right-click: pet card, node naming

    private void RightClick(double x, double y)
    {
        if (_cal is null || Shell.Engine is not { } e) return;
        if (_pets.Active && _petLayer is not null)
        {
            var best = AtlasMath.Nearest(_petMarkers, x, y);
            if (best >= 0) { ShowPetCard(best, x, y); return; }
        }
        HidePetCard();
        var k = _f * _scale;
        var (wx, wy) = AtlasMath.ScreenToWorld(_cal, x, y, _tx, _ty, k);
        var spot = e.Atlas.Nearest(_mapShown, wx, wy, AtlasMath.PickCm(_cal, k));
        if (spot is null || spot.Seeded) return;   // a seeded spot already has its kind from the map data
        ShowNamePanel(spot.Code, prompt: false);
    }

    private void ShowPetCard(int index, double x, double y)
    {
        var (pet, level, t) = _petLayer![index];
        static string E(string s) => GLib.Functions.MarkupEscapeText(s, -1);
        static string Label(string s) => "<span weight=\"600\" foreground=\"#FFFFFF\">" + E(s) + " </span>";
        var sb = new System.Text.StringBuilder(256);
        sb.Append(E((t.Sub.Length > 0 ? t.Sub : t.Category) + " · " + string.Format(T.AtlasPetSpawns, t.Points.Length))).Append('\n');
        sb.Append(Label(T.AtlasPets + ":")).Append(E(pet.Name + " · "));
        sb.Append("<span weight=\"bold\" foreground=\"").Append(Palette.Hex(PetLevel[Math.Clamp(level, 0, 2)] & 0xFFFFFF)).Append("\">")
          .Append(E(string.Format(T.AtlasPetLevel, level))).Append("</span>");
        sb.Append(E(" · " + string.Format(T.AtlasPetNeed, pet.NeedSoul > 0 ? pet.NeedSoul : 5)));
        var others = new System.Text.StringBuilder(); var n = 0;
        for (var i = 0; i < pet.Droppers.Length; i++)
        {
            if (pet.Droppers[i].Equals(t.Name, StringComparison.OrdinalIgnoreCase) || (i < pet.DropperIds.Length && t.HasId(pet.DropperIds[i]))) continue;
            if (n > 0) others.Append(", ");
            others.Append(pet.Droppers[i]);
            if (++n == 6) { others.Append('…'); break; }
        }
        if (n > 0) sb.Append('\n').Append(Label(T.AtlasPetAlso)).Append(E(others.ToString()));
        if (pet.Zones.Count > 0) sb.Append('\n').Append(Label(T.AtlasPetZones)).Append(E(string.Join(", ", pet.Zones.Keys)));
        SetPetCard(t.Name, sb.ToString(), x, y);
    }

    private void ShowNamePanel(int code, bool prompt)
    {
        if (Shell.Engine is not { } e) return;
        _nameCode = code;
        _nameAskedAt = prompt ? Environment.TickCount64 : 0;
        _nameQuestion.SetText(string.Format(T.NameNodeQuestion, code));
        e.Data.Objects.TryGetValue(code, out var current);
        _nameOther.SetText("");
        BuildCatalog(current);
        _namePanel.Visible = true;
        _nameTimer.Visible = prompt;
        if (prompt) { NameTick(); _nameClock.Start(); } else _nameClock.Stop();
    }

    private void NameTick()
    {
        if (_nameAskedAt == 0) { _nameClock.Stop(); return; }
        var age = (int)((Environment.TickCount64 - _nameAskedAt) / 1000);
        _nameTimer.SetText(string.Format(T.NameNodeTimer, age, Math.Max(0, 15 - age)));
        if (age >= 15) HideNamePanel();
    }

    private void HideNamePanel()
    {
        _namePanel.Visible = false;
        _nameClock.Stop();
        _nameAskedAt = 0;
    }

    private void NameNode(string name, string category)
    {
        name = name.Trim();
        if (name.Length == 0 || Shell.Engine is not { } e) return;
        e.NameObject(_nameCode, name, category);   // atlas/objects.json (atomic); the daemon re-reads it within 2 s
        HideNamePanel();
        _toggleSpots = -1;
        UiLoop.Post(Refresh);   // the click came from a catalogue chip that the next panel rebuilds
    }

    /// <summary>data/atlas/gather-catalog.json: the 17 kinds by category, English names (Diego: English only in the atlas), the current one gold.</summary>
    private void BuildCatalog(string? current)
    {
        while (_nameCatalog.GetFirstChild() is { } c) _nameCatalog.Remove(c);
        _catalog ??= AtlasCatalog.Load(Path.Combine(MeterEngine.DataDirectory, "atlas", "gather-catalog.json"));
        // chips laid out in lines by hand at the panel's width (GTK has no wrap panel; a FlowBox lines them up in columns of the widest)
        var width = Math.Max(200, Viewport.W - 60);
        Gtk.Box? line = null;
        double used = 0;
        foreach (var node in _catalog)
        {
            var selected = AtlasCatalog.IsCurrent(current, node.Name);
            var chip = CatalogChip(node, selected, out var est);
            if (line is null || used + est > width) { line = UiKit.HBox(5); _nameCatalog.Append(line); used = 0; }
            line.Append(chip);
            used += est + 5;
        }
    }

    /// <summary>A catalogue chip: icon + English name, gold when it is the current name; <paramref name="width"/> = estimated px (Inter 12 ≈ 6.8 px a character).</summary>
    private Gtk.Button CatalogChip(CatalogNode node, bool selected, out double width)
    {
        var row = UiKit.HBox();
        row.Valign = Gtk.Align.Center;
        width = 18 + node.Name.Length * 6.8;
        if (Texture(node.Key) is { } tex)
        {
            var img = ItemKit.Image(tex, 22);
            img.MarginEnd = UiKit.Px(6);
            row.Append(img);
            width += 28;
        }
        var label = Gtk.Label.New(node.Name);
        UiKit.SetFg(label, selected ? Fg.GoldBright : Fg.Text);
        label.AddCssClass(KitCss.SizeClass(12));
        row.Append(label);
        var b = Gtk.Button.New();
        b.AddCssClass("k-chip");
        b.Child = row;
        b.SetCursorFromName("pointer");
        b.FocusOnClick = false;
        b.SetSizeRequest(-1, UiKit.Px(30));
        b.SetTooltipText(node.CategoryName);
        var (n, c) = (node.Name, node.Category);
        b.OnClicked += (_, _) => NameNode(n, c);
        return b;
    }
}
