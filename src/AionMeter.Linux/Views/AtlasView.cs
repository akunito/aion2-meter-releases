using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using AionMeter.Core.Atlas;
using AionMeter.Core.GameDb;
using AionMeter.Linux.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux.Views;

/// <summary>
/// The private node atlas (WPF AtlasView, A2M-48 phase 3c): the calibrated zone map under zoom/pan, seeded and seen gathering spots with
/// their icons (clustered zoomed out), the 5 densest areas of up to two kinds, field bosses with their timers, the spawns of pets below
/// level 3, and the player with a 10 s trail and the heading of the last move. Spots and timers are the viewer engine's snapshot plus the
/// daemon's `position` message (≤ 2 Hz: drawn point, heading, spots in view) and the map/timer files re-read by mtime. Right-click a node
/// to name its kind (atlas/objects.json, UI-owned; the daemon re-reads it), a pet marker for its card.
/// Everything is painted by one control per frame (<see cref="MapSurface"/>): no element per marker, so panning never rebuilds a tree.
/// </summary>
public sealed class AtlasView : HostedView
{
    private static UiText T => UiText.Current;
    private const double MinScale = 0.08, MaxScale = 12;

    private readonly MapSurface _surface;
    private readonly WrapPanel _kindBar;
    private readonly CheckBox _pets, _follow;
    private readonly Slider _zoom;
    private readonly Border _petCard, _namePanel;
    private readonly TextBlock _petTitle, _petBody, _zone, _hint, _nameQuestion, _nameTimer;
    private readonly WrapPanel _nameCatalog, _footerPills;
    private readonly TextBox _nameOther;
    private readonly DispatcherTimer _clock, _redraw, _nameClock;

    private MapCalibration? _cal;
    private int _mapShown = -1;
    private Bitmap? _map;
    private int _imgW, _imgH;
    private double _scale = 0.5, _tx, _ty, _f = 1;
    private bool _followZoomed, _shown, _syncingSlider;
    private IBrush _edge = NoMapBg;
    private Point? _dragFrom;
    private double _dragTx, _dragTy;
    private int _frames;
    private long _frameTicks;

    private PositionModel? _pos;
    private long _posAt;   // Environment.TickCount64 when _pos arrived
    private readonly List<(long Ms, float X, float Y)> _trail = new();

    private List<NodeSpot> _spots = new();
    private List<MonsterTemplate> _monsters = new();
    private List<(PetInfo Pet, int Level, MonsterTemplate Template)>? _petLayer;
    private long _petLayerKey = -1;
    private readonly List<(double X, double Y, int Index)> _petMarkers = new();
    private int _toggleSpots = -1;
    private readonly LabelSpacing _labels = new();
    private readonly Dictionary<string, Bitmap?> _icons = new();
    private readonly Dictionary<string, IBrush> _iconBrush = new();
    private readonly Dictionary<(string, IBrush), FormattedText> _text = new();
    private readonly Dictionary<(string, int, int), (double Px, double Py, int N, int Alive)> _cells = new();
    private int _nameCode;
    private long _nameAskedAt;

    private static readonly IBrush Live = Visuals.Solid(0xFF5EE07A), Gone = Visuals.Solid(0xFF8A909C), BossUp = Visuals.Solid(0xFFE04A4A);
    private static readonly IBrush Self = Visuals.Solid(0xFFF2C96B), Ink = Visuals.Solid(0xC0000000), NoMapBg = Visuals.Solid(0xFF0B0F18);
    private static readonly IBrush SeededDim = Visuals.Solid(0x609AA0B0), PetInk = Visuals.Solid(0xA010141C);
    // soft, so the markers never compete with the nodes: red = not owned / level 0, orange = 1, yellow = 2; level 3 draws nothing
    private static readonly IBrush[] PetLevel = [Visuals.Solid(0x8CE0473F), Visuals.Solid(0x8CF08A24), Visuals.Solid(0x8CF2C25A)];
    private static readonly IPen InkPen = new ImmutablePen(Ink.ToImmutable(), 1.5), PetPen = new ImmutablePen(PetInk.ToImmutable(), 1), White = new ImmutablePen(Brushes.White.ToImmutable(), 1.4);
    private static readonly Typeface Face = new(Visuals.Ui), Bold = new(Visuals.Ui, FontStyle.Normal, FontWeight.Bold);

    public AtlasView(AppShell shell) : base(shell, "atlas", T.AtlasTitle)
    {
        HeaderRight.Children.Add(_zone = new TextBlock { FontSize = 12, Foreground = Visuals.TextDim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });

        _surface = new MapSurface(this) { ClipToBounds = true, Focusable = false };
        RenderOptions.SetBitmapInterpolationMode(_surface, BitmapInterpolationMode.MediumQuality);
        var bar = new WrapPanel { Margin = new Thickness(14, 0, 10, 6) };
        _kindBar = new WrapPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(_kindBar);
        _pets = UiKit.Check(T.AtlasPets, shell.Settings.AtlasShowPets);
        _pets.Margin = new Thickness(0, 0, 12, 0);
        _pets.IsCheckedChanged += (_, _) => { Shell.RefreshSettings(); Shell.Settings.AtlasShowPets = _pets.IsChecked == true; Shell.SaveSettings(); HidePetCard(); _surface.InvalidateVisual(); };
        bar.Children.Add(_pets);
        _follow = UiKit.Check(T.AtlasFollow, true);
        _follow.Margin = new Thickness(0, 0, 12, 0);
        _follow.IsCheckedChanged += (_, _) => { if (_follow.IsChecked == true) { _followZoomed = false; Refresh(); } };
        bar.Children.Add(_follow);
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        zoomRow.Children.Add(new TextBlock { Text = T.AtlasZoom, FontSize = 11.5, Foreground = Visuals.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _zoom = new Slider { Width = 120, Minimum = Math.Log(MinScale), Maximum = Math.Log(MaxScale), Value = 0, VerticalAlignment = VerticalAlignment.Center };
        _zoom.ValueChanged += (_, e) => ZoomSlider(e.NewValue);
        zoomRow.Children.Add(_zoom);
        bar.Children.Add(zoomRow);
        bar.Children.Add(UiKit.Chip(T.AtlasFit, () => { _follow.IsChecked = false; FitWholeMap(); Refresh(); }));


        _petCard = new Border
        {
            IsVisible = false, Background = Visuals.Solid(0xE6121826), BorderBrush = Visuals.Solid(0x55FFFFFF), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
        };
        var pc = new StackPanel();
        pc.Children.Add(_petTitle = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        pc.Children.Add(_petBody = new TextBlock { FontSize = 11.5, Foreground = Visuals.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 17 });
        _petCard.Child = pc;

        // node naming (WPF NameNodeWindow): the catalogue's kinds as icon chips, a free name as fallback; prompts from a gather close after 15 s
        var np = new StackPanel();
        var npHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var close = UiKit.IconButton(Visuals.IconClose, T.Cancel, HideNamePanel);
        DockPanel.SetDock(close, Dock.Right);
        npHead.Children.Add(close);
        npHead.Children.Add(_nameQuestion = new TextBlock { FontSize = 12.5, Foreground = Visuals.Text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        np.Children.Add(npHead);
        np.Children.Add(_nameTimer = new TextBlock { FontSize = 11, Foreground = Visuals.Amber, Margin = new Thickness(0, 0, 0, 4), IsVisible = false });
        np.Children.Add(new ScrollViewer { Content = _nameCatalog = new WrapPanel(), MaxHeight = 230, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var other = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        other.Children.Add(_nameOther = UiKit.Input("", 180));
        _nameOther.Watermark = "Other…";
        _nameOther.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; NameNode(_nameOther.Text ?? "", ""); } };
        other.Children.Add(UiKit.Chip("OK", () => NameNode(_nameOther.Text ?? "", "")));
        ((Control)other.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        np.Children.Add(other);
        _namePanel = new Border
        {
            IsVisible = false, Background = Visuals.Solid(0xF2121826), BorderBrush = Visuals.GoldDim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10), Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Top, MaxWidth = 520, Child = np,
        };

        var map = new Grid { Margin = new Thickness(1, 0) };
        map.Children.Add(_surface);
        map.Children.Add(_petCard);
        map.Children.Add(_namePanel);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Place(bar, 0, 0);
        body.Place(map, 1, 0);
        SetBody(body);

        var foot = new StackPanel();
        foot.Children.Add(_footerPills = new WrapPanel { IsVisible = false, Margin = new Thickness(0, 0, 0, 4) });
        foot.Children.Add(_hint = new TextBlock { Text = T.AtlasHint, FontSize = 11, Foreground = Visuals.TextMute, TextWrapping = TextWrapping.Wrap });
        SetFooter(foot);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => Refresh();
        // wheel ticks and drag moves come faster than a full repaint is worth: the map transform applies at once, the data at most every 40 ms
        _redraw = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _redraw.Tick += (_, _) => { _redraw.Stop(); _surface.InvalidateVisual(); };
        _nameClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nameClock.Tick += (_, _) => NameTick();

        shell.Daemon.Position += OnPosition;
        shell.Daemon.NodeNameWanted += code => ShowNamePanel(code, prompt: true);
        shell.Daemon.TimersChanged += () => Shell.Engine?.Timers.ReloadIfChanged();
        shell.EngineReady += Refresh;
        _pos = shell.Daemon.LastPosition;
        _posAt = Environment.TickCount64;
        SizeChanged += (_, e) => { if (e.PreviousSize.Width < 1 && _mapShown > 0 && !_followZoomed) FitWholeMap(); };
    }

    public override void Shown()
    {
        _shown = true;
        Refresh();
        _clock.Start();
    }

    public override void Hidden()
    {
        _shown = false;
        _clock.Stop();
        HidePetCard();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.AtlasTitle;
        _hint.Text = T.AtlasHint;
        _toggleSpots = -1;
        Refresh();
    }

    private void OnPosition(PositionModel p)
    {
        _pos = p;
        _posAt = Environment.TickCount64;
        if (Shell.Engine is { } e) e.Atlas.MergeLive(p.Map, p.Live, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_shown) Refresh();
    }

    // ------------------------------------------------------------ data

    private void Refresh()
    {
        if (Shell.Engine is not { } e) { _zone.Text = T.FightLoading; return; }
        var map = _pos is { Map: > 0 } p ? p.Map : e.Atlas.MapId;
        if (map != _mapShown) LoadMap(e, map);
        if (!_shown) return;
        if (map > 0) e.Atlas.ReloadIfChanged(map);
        e.Timers.ReloadIfChanged();
        _spots = e.Atlas.Spots(map);
        RefreshKinds(e);
        RefreshPets(e);
        RefreshFooterPills();
        Follow();
        _surface.InvalidateVisual();
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
        _zone.Text = _cal is not null ? _cal.Name : string.Format(T.AtlasNoMap, map);
        ToolTip.SetTip(_zone, _zone.Text);
        if (_cal is not null && File.Exists(_cal.BackgroundPath))
        {
            var started = Environment.TickCount64;
            try
            {
                using var s = File.OpenRead(_cal.BackgroundPath);
                // the maps are 4096² JPEGs: native size, sharp at full zoom (WPF measured 3000 upscaled as soft, 2026-10-11)
                _map = Bitmap.DecodeToWidth(s, 4096, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { Log.Error($"Atlas map {map} background", ex); }
            if (_map is not null)
            {
                _imgW = _map.PixelSize.Width; _imgH = _map.PixelSize.Height;
                _f = _cal.Width > 0 ? (double)_imgW / _cal.Width : 1;   // calibration pixels → decoded image pixels
                // outside the map the panel colour showed as black corners (A2M-8): paint the map's own edge colour
                _edge = EdgeBrush(_map);
                Log.Info($"UI atlas map {map}: {_cal.BackgroundPath} decoded {_imgW}x{_imgH} in {Environment.TickCount64 - started} ms, calibration {_cal.Width}x{_cal.Height}, factor {_f:0.000}");
            }
        }
        if (_map is null) { _imgW = _imgH = 0; _f = 1; _edge = NoMapBg; }
        if (_follow.IsChecked == true && map > 0) _followZoomed = false; else FitWholeMap();
    }

    /// <summary>The kinds known on this map, one icon toggle each (A2M-3, at most two on); rebuilt when the spot count changes.</summary>
    private void RefreshKinds(MeterEngine e)
    {
        if (_spots.Count == _toggleSpots) return;
        _toggleSpots = _spots.Count;
        var kinds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _spots) if (KindOf(e, s) is { } n) kinds.Add(n);
        var on = Shell.Settings.AtlasZoneKinds;
        _kindBar.Children.Clear();
        foreach (var name in kinds)
        {
            var key = name.Trim().ToLowerInvariant();
            var active = on.Contains(key);
            var img = new Image { Source = Icon(name), Width = 20, Height = 20, Opacity = active ? 1 : 0.55 };
            var b = new Border
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 3, 3), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), Padding = new Thickness(2),
                BorderBrush = active ? IconBrush(name) : Brushes.Transparent, Background = Visuals.Solid(active ? 0x44FFFFFFu : 0x22FFFFFFu), Child = img, Cursor = new Cursor(StandardCursorType.Hand),
            };
            if (img.Source is null) b.Child = new TextBlock { Text = name[..1], FontSize = 12, Foreground = Visuals.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.PointerEntered += (_, _) => img.Opacity = 1;
            b.PointerExited += (_, _) => img.Opacity = on.Contains(key) ? 1 : 0.55;
            b.PointerPressed += (_, ev) => { ev.Handled = true; ToggleKind(key); };
            UiKit.Tip(b, string.Format(T.AtlasZonesTip, name));
            _kindBar.Children.Add(b);
        }
    }

    private void ToggleKind(string key)
    {
        Shell.RefreshSettings();   // never write back over a save the daemon made (set-measure) since this process read the file
        var on = Shell.Settings.AtlasZoneKinds;
        if (!on.Remove(key))
        {
            on.Add(key);
            while (on.Count > 2) on.RemoveAt(0);   // at most two kinds: the oldest drops
        }
        Shell.SaveSettings();
        _toggleSpots = -1;
        Refresh();
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
        UiKit.Tip(_pets, string.Format(T.AtlasPetsTip, below, species.Count));
    }

    private void RefreshFooterPills()
    {
        // in the card the card's own footer shows the event pills; a popped-out atlas shows them here
        _footerPills.IsVisible = !Hosted && Shell.LastOverlay is { Pills.Length: > 0 };
        if (!_footerPills.IsVisible) return;
        _footerPills.Children.Clear();
        foreach (var p in Shell.LastOverlay!.Pills)
        {
            var (fill, stroke) = Visuals.Pill(p.Tone);
            _footerPills.Children.Add(UiKit.Pill(p.Text, fill, stroke));
        }
    }

    private static string? KindOf(MeterEngine e, NodeSpot s) => s.Seeded ? s.Name : e.Data.Objects.TryGetValue(s.Code, out var n) ? n : null;

    // ------------------------------------------------------------ view transform

    private (double W, double H) Viewport => (Math.Max(_surface.Bounds.Width, 1), Math.Max(_surface.Bounds.Height, 1));

    /// <summary>The drawn player point (world cm), null when there is none.</summary>
    private (float X, float Y)? SelfPoint => _pos is { } p && (p.X != 0 || p.Y != 0) && p.Map == _mapShown ? (p.X, p.Y) : null;

    private void Follow()
    {
        if (_follow.IsChecked != true || _cal is null || SelfPoint is not { } me || _surface.Bounds.Width < 1) return;
        var (px, py) = _cal.ToPixel(me.X, me.Y);
        if (px < 0 || py < 0 || px > _cal.Width || py > _cal.Height) return;
        if (!_followZoomed) { _scale = 10; _followZoomed = true; }   // ~420 px per 100 m: nodes a few metres apart stay separate (Diego's pick: closest)
        var (w, h) = Viewport;
        _tx = w / 2 - px * _f * _scale;
        _ty = h / 2 - py * _f * _scale;
        SyncSlider();
    }

    /// <summary>First view of a map: the whole zone in the window, centred; Follow then takes over once the position is known.</summary>
    private void FitWholeMap()
    {
        var w = _imgW > 0 ? _imgW : 1000;
        var h = _imgH > 0 ? _imgH : 1000;
        var (vw, vh) = (Math.Max(_surface.Bounds.Width, 100), Math.Max(_surface.Bounds.Height, 100));
        _scale = Math.Clamp(Math.Min(vw / w, vh / h), MinScale, MaxScale);
        _tx = (vw - w * _scale) / 2; _ty = (vh - h * _scale) / 2;
        SyncSlider();
        _surface.InvalidateVisual();
    }

    private void SyncSlider()
    {
        _syncingSlider = true;
        _zoom.Value = Math.Log(_scale);
        _syncingSlider = false;
    }

    private void ZoomSlider(double value)
    {
        if (_syncingSlider || _cal is null) return;
        var next = Math.Clamp(Math.Exp(value), MinScale, MaxScale);
        var (w, h) = Viewport;
        var cx = w / 2; var cy = h / 2;   // around the centre of the view
        _tx = cx - (cx - _tx) * next / _scale; _ty = cy - (cy - _ty) * next / _scale; _scale = next;
        RequestRedraw();
    }

    private void RequestRedraw() { if (!_redraw.IsEnabled) _redraw.Start(); }

    internal void OnWheel(PointerWheelEventArgs e)
    {
        HidePetCard();
        var p = e.GetPosition(_surface);
        var k = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        var next = Math.Clamp(_scale * k, MinScale, MaxScale);
        k = next / _scale; _scale = next;
        _tx = p.X - (p.X - _tx) * k; _ty = p.Y - (p.Y - _ty) * k;
        SyncSlider();
        RequestRedraw();   // zooming keeps Follow: the next tick re-centres at the new zoom
        e.Handled = true;
    }

    internal void OnPress(PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_surface);
        e.Handled = true;   // never the card's window drag: the map pans itself
        if (pt.Properties.IsRightButtonPressed) { RightClick(pt.Position); return; }
        if (!pt.Properties.IsLeftButtonPressed) return;
        HidePetCard();
        _dragFrom = pt.Position; _dragTx = _tx; _dragTy = _ty;
        e.Pointer.Capture(_surface);
    }

    internal void OnMove(PointerEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var p = e.GetPosition(_surface);
        _tx = _dragTx + (p.X - from.X); _ty = _dragTy + (p.Y - from.Y);
        if (_follow.IsChecked == true && (Math.Abs(p.X - from.X) > 3 || Math.Abs(p.Y - from.Y) > 3)) _follow.IsChecked = false;
        _surface.InvalidateVisual();
    }

    internal void OnRelease(PointerReleasedEventArgs e)
    {
        if (_dragFrom is null) return;
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    // ------------------------------------------------------------ right-click: pet card, node naming

    private void RightClick(Point p)
    {
        if (_cal is null || Shell.Engine is not { } e) return;
        if (_pets.IsChecked == true && _petLayer is not null)
        {
            var best = -1; var bestD = 14.0 * 14.0;
            foreach (var (x, y, i) in _petMarkers)
            {
                var d = (x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) { ShowPetCard(best, p); return; }
        }
        HidePetCard();
        // screen → calibration pixel → world (the affine is scale + offset: invert through two known points)
        var px = (p.X - _tx) / (_f * _scale); var py = (p.Y - _ty) / (_f * _scale);
        var (ox, oy) = _cal.ToPixel(0, 0); var (ux, uy) = _cal.ToPixel(1000, 1000);
        var wx = (float)((px - ox) / (ux - ox) * 1000); var wy = (float)((py - oy) / (uy - oy) * 1000);
        var spot = e.Atlas.Nearest(_mapShown, wx, wy, maxCm: (float)(14 / (_f * _scale) / _cal.Scale));
        if (spot is null || spot.Seeded) return;   // a seeded spot already has its kind from the map data
        ShowNamePanel(spot.Code, prompt: false);
    }

    private void ShowPetCard(int index, Point at)
    {
        var (pet, level, t) = _petLayer![index];
        _petTitle.Text = t.Name;
        var inl = _petBody.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        inl.Clear();
        void Label(string s) => inl.Add(new Avalonia.Controls.Documents.Run(s + " ") { FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        void Text(string s) => inl.Add(new Avalonia.Controls.Documents.Run(s));
        void Br() => inl.Add(new Avalonia.Controls.Documents.LineBreak());
        Text((t.Sub.Length > 0 ? t.Sub : t.Category) + " · " + string.Format(T.AtlasPetSpawns, t.Points.Length)); Br();
        Label(T.AtlasPets + ":"); Text(pet.Name + " · ");
        inl.Add(new Avalonia.Controls.Documents.Run(string.Format(T.AtlasPetLevel, level)) { FontWeight = FontWeight.Bold, Foreground = PetLevel[Math.Clamp(level, 0, 2)] });
        Text(" · " + string.Format(T.AtlasPetNeed, pet.NeedSoul > 0 ? pet.NeedSoul : 5));
        var others = new System.Text.StringBuilder(); var n = 0;
        for (var i = 0; i < pet.Droppers.Length; i++)
        {
            if (pet.Droppers[i].Equals(t.Name, StringComparison.OrdinalIgnoreCase) || (i < pet.DropperIds.Length && t.HasId(pet.DropperIds[i]))) continue;
            if (n > 0) others.Append(", ");
            others.Append(pet.Droppers[i]);
            if (++n == 6) { others.Append('…'); break; }
        }
        if (n > 0) { Br(); Label(T.AtlasPetAlso); Text(others.ToString()); }
        if (pet.Zones.Count > 0) { Br(); Label(T.AtlasPetZones); Text(string.Join(", ", pet.Zones.Keys)); }
        _petCard.IsVisible = true;
        var (w, h) = Viewport;
        _petCard.Margin = new Thickness(Math.Max(4, Math.Min(at.X + 14, w - 360)), Math.Max(4, Math.Min(at.Y - 10, h - 120)), 0, 0);
    }

    private void HidePetCard() { if (_petCard.IsVisible) _petCard.IsVisible = false; }

    private void ShowNamePanel(int code, bool prompt)
    {
        if (Shell.Engine is not { } e) return;
        _nameCode = code;
        _nameAskedAt = prompt ? Environment.TickCount64 : 0;
        _nameQuestion.Text = string.Format(T.NameNodeQuestion, code);
        e.Data.Objects.TryGetValue(code, out var current);
        _nameOther.Text = "";
        BuildCatalog(current);
        _namePanel.IsVisible = true;
        _nameTimer.IsVisible = prompt;
        if (prompt) { NameTick(); _nameClock.Start(); } else _nameClock.Stop();
    }

    private void NameTick()
    {
        if (_nameAskedAt == 0) { _nameClock.Stop(); return; }
        var age = (int)((Environment.TickCount64 - _nameAskedAt) / 1000);
        _nameTimer.Text = string.Format(T.NameNodeTimer, age, Math.Max(0, 15 - age));
        if (age >= 15) HideNamePanel();
    }

    private void HideNamePanel()
    {
        _namePanel.IsVisible = false;
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
        Refresh();
    }

    /// <summary>data/atlas/gather-catalog.json: the 17 kinds by category, English names (Diego: English only in the atlas), the current one gold.</summary>
    private void BuildCatalog(string? current)
    {
        _nameCatalog.Children.Clear();
        var dir = Path.Combine(MeterEngine.DataDirectory, "atlas");
        var path = Path.Combine(dir, "gather-catalog.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var cat in doc.RootElement.GetProperty("categories").EnumerateArray())
            {
                var catKey = cat.GetProperty("key").GetString() ?? "";
                foreach (var node in cat.GetProperty("nodes").EnumerateArray())
                {
                    var key = node.GetProperty("key").GetString() ?? "";
                    var name = (node.TryGetProperty("en", out var en) ? en.GetString() : null) ?? key;
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    if (Icon(key) is { } bmp) row.Children.Add(new Image { Source = bmp, Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0) });
                    var selected = current is not null && string.Equals(current.Trim(), name, StringComparison.OrdinalIgnoreCase);
                    row.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = selected ? Visuals.GoldBright : Visuals.Text, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
                    var chip = new Border
                    {
                        Height = 30, Padding = new Thickness(6, 2, 9, 2), Margin = new Thickness(0, 0, 5, 5), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(selected ? 2 : 1),
                        BorderBrush = selected ? Visuals.Gold : Visuals.GoldDim, Background = Visuals.Solid(selected ? 0x40C9A45Cu : 0x1AC9A45Cu), Child = row, Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    UiKit.Tip(chip, (cat.TryGetProperty("en", out var cen) ? cen.GetString() : null) ?? catKey);
                    var (n, c) = (name, catKey);
                    chip.PointerPressed += (_, ev) => { ev.Handled = true; NameNode(n, c); };
                    _nameCatalog.Children.Add(chip);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { Log.Info($"gather-catalog.json ignored: {ex.Message}"); }
    }

    // ------------------------------------------------------------ painting

    private void Paint(DrawingContext dc)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var (w, h) = Viewport;
        dc.FillRectangle(_edge, new Rect(0, 0, w, h));
        _labels.Clear();
        _petLabels.Clear();
        _petMarkers.Clear();
        if (_map is not null) dc.DrawImage(_map, new Rect(0, 0, _imgW, _imgH), new Rect(_tx, _ty, _imgW * _scale, _imgH * _scale));
        if (_cal is null || Shell.Engine is not { } e) return;
        var k = _f * _scale;
        var cluster = k < 0.16;   // zoomed out, icons of one kind pile up: one icon per kind per 28 px cell with the count (Diego 2026-10-10)
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // seeded nodes (never seen by this meter) dim under everything
        if (!cluster)
            using (dc.PushOpacity(0.45))
                foreach (var s in _spots)
                {
                    if (!s.Seeded) continue;
                    var (x, y) = Screen(s.X, s.Y, k);
                    if (!InView(x, y, w, h)) continue;
                    if (KindOf(e, s) is { } kn && Icon(kn) is { } icon) dc.DrawImage(icon, new Rect(x - 11, y - 11, 22, 22));
                    else dc.DrawEllipse(SeededDim, null, new Point(x, y), 3, 3);
                }
        if (_pets.IsChecked == true) PaintPets(dc, k, w, h);
        PaintZones(dc, e, k, w, h);

        // field bosses first: their labels win the spacing
        var timers = e.Timers.ForServer(e.Timers.CurrentServer);
        foreach (var (code, name, bx, by) in _cal.Bosses)
        {
            BossTimer? timer = null;
            foreach (var t in timers) if (t.NpcCode == code) { timer = t; break; }
            var up = timer?.AliveNow == true;
            var label = up ? name : timer?.NextSpawn is { } next ? $"{name} · {Countdown(next.ToUnixTimeMilliseconds(), now)}" : name;
            var x = bx * k + _tx; var y = by * k + _ty;
            if (!InView(x, y, w, h)) continue;
            dc.DrawEllipse(up ? BossUp : Gone, InkPen, new Point(x, y), 7, 7);
            dc.DrawLine(White, new Point(x - 3, y - 3), new Point(x + 3, y + 3));   // a cross in place of WPF's skull glyph
            dc.DrawLine(White, new Point(x + 3, y - 3), new Point(x - 3, y + 3));
            LabelAt(dc, label, x, y, x + 9, up ? BossUp : Gone, false, k);
        }

        _cells.Clear();
        foreach (var s in _spots)
        {
            var kind = KindOf(e, s);
            var (x, y) = Screen(s.X, s.Y, k);
            if (cluster && kind is not null)
            {
                var key = (kind, (int)Math.Floor(x / 28), (int)Math.Floor(y / 28));
                var alive = s.Alive && !s.Seeded ? 1 : 0;
                _cells[key] = _cells.TryGetValue(key, out var c) ? (c.Px + x, c.Py + y, c.N + 1, c.Alive + alive) : (x, y, 1, alive);
                continue;
            }
            if (s.Seeded || !InView(x, y, w, h)) continue;
            var icon = kind is not null ? Icon(kind) : null;
            if (s.Alive && s.InView) Mark(dc, x, y, 5, Live, icon, s.UnitsLeft.ToString(CultureInfo.InvariantCulture), Live, false, false, k);
            else if (s.Alive) Mark(dc, x, y, 4, Live, icon, s.UnitsLeft + "?", Gone, false, false, k);
            else Mark(dc, x, y, 4, Gone, icon, s.NextSpawnMs > 0 ? Countdown(s.NextSpawnMs, now) : "", Gone, true, false, k);
        }
        if (_cells.Count > 0)
        {
            // biggest clusters first: when labels collide, the larger count keeps its label
            var order = new List<KeyValuePair<(string, int, int), (double Px, double Py, int N, int Alive)>>(_cells);
            order.Sort((a, b) => b.Value.N.CompareTo(a.Value.N));
            foreach (var ((kind, _, _), c) in order)
            {
                var x = c.Px / c.N; var y = c.Py / c.N;
                if (!InView(x, y, w, h)) continue;
                Mark(dc, x, y, 5, c.Alive > 0 ? Live : Gone, Icon(kind), c.N > 1 ? "×" + c.N : "", c.Alive > 0 ? Live : Gone, c.Alive == 0, true, k);
            }
        }

        PaintPetLabels(dc);
        PaintSelf(dc, k);
        var debug = Environment.GetEnvironmentVariable("AIONMETER_UI_DEBUG") is not null;
        if (++_frames % 120 == 1 || debug)
        {
            _frameTicks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            if (debug) Console.Error.WriteLine($"atlas frame {_frameTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms, scale {_scale:0.000}, {_spots.Count} spots, labels {_labels.Count}");
            Log.Info($"UI atlas frame: {_frameTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms, scale {_scale:0.000} viewport {w:0}x{h:0}, {_spots.Count} spots, {_petLayer?.Count ?? 0} pet templates, cluster {cluster}, labels {_labels.Count}, map {_mapShown}, self {(_pos is { } p ? $"({p.X:0},{p.Y:0}) exact {p.Exact} age {p.AgeS}s heading {p.Heading:0}" : "none")}");
        }
    }

    private (double X, double Y) Screen(float wx, float wy, double k)
    {
        var (px, py) = _cal!.ToPixel(wx, wy);
        return (px * k + _tx, py * k + _ty);
    }

    // drawn half a viewport beyond each edge is pointless with one surface: only what is on screen (+ a marker's radius)
    private static bool InView(double x, double y, double w, double h) => x > -30 && y > -30 && x < w + 30 && y < h + 30;

    private void PaintPets(DrawingContext dc, double k, double w, double h)
    {
        if (_petLayer is null) return;
        for (var li = 0; li < _petLayer.Count; li++)
        {
            var (pet, level, t) = _petLayer[li];
            var brush = PetLevel[Math.Clamp(level, 0, 2)];
            double sx = 0, sy = 0; var n = 0;
            foreach (var (x0, y0) in t.Points)
            {
                var (vx, vy) = Screen(x0, y0, k);
                sx += vx; sy += vy; n++;
                if (!InView(vx, vy, w, h)) continue;
                dc.DrawEllipse(brush, null, new Point(vx, vy), 3.5, 3.5);
                _petMarkers.Add((vx, vy, li));
            }
            if (n == 0) continue;
            var cx = sx / n; var cy = sy / n;
            if (!InView(cx, cy, w, h)) continue;
            dc.DrawEllipse(brush, PetPen, new Point(cx, cy), 9, 9);
            var digit = Text(level.ToString(CultureInfo.InvariantCulture), Brushes.White, true, 10);
            dc.DrawText(digit, new Point(cx - digit.Width / 2, cy - digit.Height / 2));
            _petMarkers.Add((cx, cy, li));
            if (k >= 0.12) _petLabels.Add((cx, cy, pet.Name));
        }
    }

    // pet names go through the label spacing after the bosses and nodes (WPF drew them all: at the Fit zoom of Verteron they overprinted
    // into an unreadable block, render of 2026-10-13); the badge with the level always draws
    private readonly List<(double X, double Y, string Name)> _petLabels = new();

    private void PaintPetLabels(DrawingContext dc)
    {
        foreach (var (cx, cy, name) in _petLabels)
        {
            if (!_labels.TryPlace(cx, cy, cx + 11, cy - 8, name.Length * 6 + 6, 15)) continue;
            var label = Text(name, Brushes.White, false, 10.5);
            dc.FillRectangle(PetInk, new Rect(cx + 11, cy - 8, label.Width + 6, label.Height));
            dc.DrawText(label, new Point(cx + 14, cy - 8));
        }
        _petLabels.Clear();
    }

    /// <summary>The 5 densest areas of each toggled kind as padded hull outlines in the kind's icon colour (A2M-3), #1 solid, the rest dashed.</summary>
    private void PaintZones(DrawingContext dc, MeterEngine e, double k, double w, double h)
    {
        var kinds = Shell.Settings.AtlasZoneKinds;
        if (kinds.Count == 0) return;
        foreach (var key in kinds)
        {
            var mine = new List<NodeSpot>();
            foreach (var s in _spots)
                if (KindOf(e, s) is { } n && string.Equals(n.Trim(), key, StringComparison.OrdinalIgnoreCase)) mine.Add(s);
            if (mine.Count == 0) continue;
            var brush = IconBrush(key);
            var col = brush is ISolidColorBrush sc ? sc.Color : Colors.Gold;
            var fill = new ImmutableSolidColorBrush(Color.FromArgb(0x2A, col.R, col.G, col.B));
            var rank = 0;
            foreach (var z in NodeZones.Compute(mine))
            {
                rank++;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var first = true;
                    foreach (var (wx, wy) in z.Hull)
                    {
                        var (x, y) = Screen(wx, wy, k);
                        if (first) { g.BeginFigure(new Point(x, y), true); first = false; } else g.LineTo(new Point(x, y));
                        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                    if (!first) g.EndFigure(true);
                }
                if (maxX < -100 || maxY < -100 || minX > w + 100 || minY > h + 100) continue;
                var (cx, cy) = Screen(z.CenterX, z.CenterY, k);
                var pen = new Pen(brush, 3, rank == 1 ? null : new DashStyle([2, 1], 0), lineJoin: PenLineJoin.Round);
                // zoomed out, a 150 m area is a few pixels: a readable ring (min 48 px); a dark halo keeps it visible on light and dark terrain
                if (Math.Max(maxX - minX, maxY - minY) < 48)
                {
                    dc.DrawEllipse(null, new Pen(Ink, 6), new Point(cx, cy), 27, 27);
                    dc.DrawEllipse(fill, pen, new Point(cx, cy), 24, 24);
                }
                else
                {
                    dc.DrawGeometry(null, new Pen(Ink, 7, lineJoin: PenLineJoin.Round), geo);
                    dc.DrawGeometry(fill, pen, geo);
                }
                var text = Text($"#{rank} · {z.Count}", brush, true, 12);
                dc.FillRectangle(Ink, new Rect(cx - 16, cy - 9, text.Width + 8, text.Height + 2));
                dc.DrawText(text, new Point(cx - 12, cy - 8));
            }
        }
    }

    /// <summary>The player: a gold dot (a fix) or a hollow ring (centroid, "~N"), the heading wedge, the 10 s trail, the age past 10 s.</summary>
    private void PaintSelf(DrawingContext dc, double k)
    {
        if (SelfPoint is not { } me || _pos is not { } p) return;
        var now = Environment.TickCount64;
        if (_trail.Count == 0 || Math.Abs(_trail[^1].X - me.X) > 50 || Math.Abs(_trail[^1].Y - me.Y) > 50) _trail.Add((now, me.X, me.Y));
        _trail.RemoveAll(t => now - t.Ms > 10_000);
        for (var i = 1; i < _trail.Count; i++)
        {
            var (ax, ay) = Screen(_trail[i - 1].X, _trail[i - 1].Y, k);
            var (bx, by) = Screen(_trail[i].X, _trail[i].Y, k);
            var age = (now - _trail[i].Ms) / 10_000.0;   // 0 = fresh, 1 = ten seconds old
            using (dc.PushOpacity(0.65 * (1 - age) + 0.1))
                dc.DrawLine(new Pen(Self, 2.5, lineCap: PenLineCap.Round), new Point(ax, ay), new Point(bx, by));
        }
        var (x, y) = Screen(me.X, me.Y, k);
        if (p.Heading is { } hd && _cal is not null)
        {
            // world direction → screen direction through the calibration (the map may be rotated or mirrored against world axes)
            var rad = hd * Math.PI / 180;
            var (hx, hy) = Screen(me.X + (float)(Math.Cos(rad) * 500), me.Y + (float)(Math.Sin(rad) * 500), k);
            var dx = hx - x; var dy = hy - y; var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0.001)
            {
                dx /= len; dy /= len;
                var tip = new Point(x + dx * 19, y + dy * 19);
                var l = new Point(x + dx * 9 - dy * 6, y + dy * 9 + dx * 6);
                var r = new Point(x + dx * 9 + dy * 6, y + dy * 9 - dx * 6);
                var geo = new StreamGeometry();
                using (var g = geo.Open()) { g.BeginFigure(tip, true); g.LineTo(l); g.LineTo(r); g.EndFigure(true); }
                dc.DrawGeometry(Self, InkPen, geo);
            }
        }
        var ageS = p.AgeS < 0 ? -1 : p.AgeS + (now - _posAt) / 1000;
        if (p.Exact)
        {
            dc.DrawEllipse(Self, InkPen, new Point(x, y), 8, 8);
            if (ageS > 10) LabelAt(dc, ageS + "s", x, y, x + 10, Self, true, k);
        }
        else
        {
            dc.DrawEllipse(null, new Pen(Self, 2.5), new Point(x, y), 9, 9);
            LabelAt(dc, "~" + p.Samples, x, y, x + 12, Self, true, k);
        }
    }

    /// <summary>A node: its icon (when known) or a dot, the label beside it when it has room (A2M-8 spacing).</summary>
    private void Mark(DrawingContext dc, double x, double y, double r, IBrush fill, Bitmap? icon, string label, IBrush textBrush, bool dim, bool always, double k)
    {
        if (icon is null) dc.DrawEllipse(fill, InkPen, new Point(x, y), r, r);
        else if (dim) using (dc.PushOpacity(0.45)) dc.DrawImage(icon, new Rect(x - 11, y - 11, 22, 22));
        else dc.DrawImage(icon, new Rect(x - 11, y - 11, 22, 22));
        if (label.Length > 0) LabelAt(dc, label, x, y, x + (icon is null ? r + 2 : 12), textBrush, always, k);
    }

    // label box estimated, not measured (Inter 11 px averages ~6.5 px a character plus 3+3 padding): nothing is built for a refused label.
    // always = shown at any zoom (cluster counts, the player); spacing still applies.
    private void LabelAt(DrawingContext dc, string label, double x, double y, double left, IBrush brush, bool always, double k)
    {
        if (!always && k < 0.12) return;   // labels only once zoomed in enough to read the map
        if (!_labels.TryPlace(x, y, left, y - 7, label.Length * 6.5 + 6, 15)) return;
        var t = Text(label, brush, false, 11);
        dc.FillRectangle(Ink, new Rect(left, y - 7, t.Width + 6, 15));
        dc.DrawText(t, new Point(left + 3, y - 7 + (15 - t.Height) / 2));
    }

    private FormattedText Text(string s, IBrush brush, bool bold, double size)
    {
        var key = (bold ? "\u0001" : "") + size.ToString(CultureInfo.InvariantCulture) + "\u0002" + s;
        if (_text.TryGetValue((key, brush), out var ft)) return ft;
        if (_text.Count > 4000) _text.Clear();   // countdown labels change every second
        return _text[(key, brush)] = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? Bold : Face, size, brush);
    }

    private static string Countdown(long atMs, long nowMs)
    {
        var left = TimeSpan.FromMilliseconds(atMs - nowMs);
        return left.TotalSeconds <= 0 ? "soon" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}" : $"{left.Minutes}:{left.Seconds:00}";
    }

    // ------------------------------------------------------------ icons and colours

    /// <summary>The catalogue icon of a kind (data/atlas/icons/&lt;name&gt;.png, English name lower-cased), cached; null when there is none.</summary>
    private Bitmap? Icon(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        if (_icons.TryGetValue(key, out var cached)) return cached;
        var path = Path.Combine(MeterEngine.DataDirectory, "atlas", "icons", key + ".png");
        Bitmap? img = null;
        if (File.Exists(path))
            try { using var s = File.OpenRead(path); img = Bitmap.DecodeToWidth(s, 48, BitmapInterpolationMode.HighQuality); }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { }
        return _icons[key] = img;
    }

    /// <summary>The icon's dominant colour: the saturation-weighted mean of its opaque pixels, lifted towards white (cached per kind); gold without an icon.</summary>
    private IBrush IconBrush(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        if (_iconBrush.TryGetValue(key, out var b)) return b;
        IBrush brush = Self;
        if (Icon(name) is { } bmp && Pixels(bmp, new PixelRect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height)) is { } px)
        {
            double r = 0, g = 0, bl = 0, wsum = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] < 128) continue;
                int bb = px[i], gg = px[i + 1], rr = px[i + 2];
                var max = Math.Max(rr, Math.Max(gg, bb)); var min = Math.Min(rr, Math.Min(gg, bb));
                var sat = max == 0 ? 0 : (max - min) / (double)max;
                var wgt = 0.15 + sat * sat;   // saturated pixels dominate, dark outlines barely count
                r += rr * wgt; g += gg * wgt; bl += bb * wgt; wsum += wgt;
            }
            if (wsum > 0)
            {
                byte Lift(double v) => (byte)(v / wsum + (255 - v / wsum) * 0.25);
                brush = new ImmutableSolidColorBrush(Color.FromRgb(Lift(r), Lift(g), Lift(bl)));
            }
        }
        return _iconBrush[key] = brush;
    }

    /// <summary>Mean colour of four 32 px corner patches and four edge-midpoint patches of the map (the shipped maps are parchment, #DAD1BC
    /// at every corner, measured 2026-10-13).</summary>
    private static IBrush EdgeBrush(Bitmap bmp)
    {
        const int p = 32;
        int w = bmp.PixelSize.Width - p, h = bmp.PixelSize.Height - p;
        if (w < p || h < p) return NoMapBg;
        long b = 0, g = 0, r = 0, n = 0;
        ReadOnlySpan<(int X, int Y)> at = [(0, 0), (w, 0), (0, h), (w, h), (w / 2, 0), (w / 2, h), (0, h / 2), (w, h / 2)];
        foreach (var (x, y) in at)
        {
            if (Pixels(bmp, new PixelRect(x, y, p, p)) is not { } px) return NoMapBg;
            for (var i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
            n += p * p;
        }
        return new ImmutableSolidColorBrush(Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n)));
    }

    /// <summary>BGRA bytes of a region (swapped from RGBA when the decoder produced that), null when the bitmap cannot be read back.</summary>
    private static byte[]? Pixels(Bitmap bmp, PixelRect rect)
    {
        var buf = new byte[rect.Width * rect.Height * 4];
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try { bmp.CopyPixels(rect, handle.AddrOfPinnedObject(), buf.Length, rect.Width * 4); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
        finally { handle.Free(); }
        if (bmp.Format == PixelFormat.Rgba8888)
            for (var i = 0; i < buf.Length; i += 4) (buf[i], buf[i + 2]) = (buf[i + 2], buf[i]);
        return buf;
    }

    /// <summary>The one control that paints the atlas; pointer input goes back to the view.</summary>
    private sealed class MapSurface(AtlasView owner) : Control, Avalonia.Rendering.ICustomHitTest
    {
        public bool HitTest(Point point) => true;
        public override void Render(DrawingContext context) => owner.Paint(context);
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e) => owner.OnWheel(e);
        protected override void OnPointerPressed(PointerPressedEventArgs e) => owner.OnPress(e);
        protected override void OnPointerMoved(PointerEventArgs e) => owner.OnMove(e);
        protected override void OnPointerReleased(PointerReleasedEventArgs e) => owner.OnRelease(e);
    }
}
