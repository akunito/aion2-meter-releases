using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AionMeter.App.Services;
using AionMeter.Core.Atlas;

namespace AionMeter.App.Views;

/// <summary>
/// The private node atlas: the calibrated zone map with the player, the gathering spots the meter has seen (live ones
/// with units left, remembered ones with their respawn ETA) and the field bosses. Redrawn twice a second; a few
/// hundred shapes at most, so the canvas is simply rebuilt.
/// </summary>
public partial class AtlasView : UserControl
{
    private static UiText T => UiText.Current;
    private readonly MeterService _meter;
    private readonly DispatcherTimer _clock;
    private MapCalibration? _cal;
    private int _mapShown = -1;
    private double _scale = 0.5, _tx, _ty, _f = 1;
    private int _imgW, _imgH;
    private Point? _dragFrom;
    private int _ticks;
    private bool _followZoomed;
    private double _dragTx, _dragTy;
    // pan and scale the two drawn layers were painted at; Apply() translates them by the difference instead of redrawing
    private double _ovTx, _ovTy, _staticTx, _staticTy, _staticScale = double.NaN;
    private static readonly Brush Live = new SolidColorBrush(Color.FromRgb(0x5E, 0xE0, 0x7A));
    private static readonly Brush Gone = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9C));
    private static readonly Brush BossUp = new SolidColorBrush(Color.FromRgb(0xE0, 0x4A, 0x4A));
    private static readonly Brush Self = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x6B));
    private static readonly Brush Ink = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0));

    /// <summary>One kind toggle: icon, its dominant colour (zone outline), on/off.</summary>
    public sealed class ZoneToggle(string key, string name, ImageSource? icon, Brush brush, bool on, string tip)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public ImageSource? Icon { get; } = icon;
        public Brush Brush { get; } = brush;
        public bool On { get; set; } = on;
        public string Tip { get; } = tip;
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<ZoneToggle> _zoneToggles = new();
    private readonly Dictionary<string, Brush> _iconBrush = new();
    private int _toggleSpots = -1;

    public AtlasView(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        StaticLayer.Draw = DrawStatic;
        FollowBox.IsChecked = true;
        PetsBox.IsChecked = meter.Settings.AtlasShowPets;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Refresh(); };
    }

    // render tool (--window atlas): drawn although never shown, framed on a world point at a fixed zoom, with sample pet levels
    private bool _sample;
    private (float X, float Y, double Scale)? _sampleFrame;
    private Dictionary<int, int>? _samplePets;
    public void ShowSample(float worldX, float worldY, double scale, Dictionary<int, int> pets)
    {
        _sample = true; _sampleFrame = (worldX, worldY, scale); _samplePets = pets;
        FollowBox.IsChecked = false; PetsBox.IsChecked = true;
    }
    public void RenderNow()
    {
        Refresh();
        if (_cal is null || _sampleFrame is not { } f || Viewport.ActualWidth <= 0) return;
        var (px, py) = _cal.ToPixel(f.X, f.Y);
        _scale = f.Scale;
        _tx = Viewport.ActualWidth / 2 - px * _f * _scale; _ty = Viewport.ActualHeight / 2 - py * _f * _scale;
        Apply();
        Refresh();
        StaticLayer.InvalidateVisual();
    }

    private void Refresh()
    {
        var atlas = _meter.Atlas;
        var map = atlas.MapId;
        if (map != _mapShown)
        {
            _mapShown = map;
            _cal = _meter.Calibration(map);
            _monsters = Core.Atlas.MonsterSpawns.Load(System.IO.Path.Combine(MeterService.DataDirectory, "atlas"), map);
            _petLayer = null;
            ZoneText.Text = _cal is not null ? _cal.Name : string.Format(T.AtlasNoMap, map);
            MapImage.Source = _cal is not null && File.Exists(_cal.BackgroundPath) ? LoadBitmap(_cal.BackgroundPath) : null;
            // The bitmap is decoded small (a 34-Mpx JPEG is a 135 MB texture: WPF showed it black); the element keeps the
            // calibration's logical size, so pixel coordinates stay those of calibration.json.
            if (_cal is not null && MapImage.Source is BitmapSource bs)
            {
                _imgW = bs.PixelWidth; _imgH = bs.PixelHeight;
                _f = _cal.Width > 0 ? (double)bs.PixelWidth / _cal.Width : 1;  // calibration pixels → decoded image pixels
                // outside the map the viewport showed its near-black panel colour (A2M-8 "black corners"): paint the map's own edge colour
                Viewport.Background = EdgeBrush(bs);
                Log.Info($"Atlas map {map}: {_cal.BackgroundPath} decoded {bs.PixelWidth}x{bs.PixelHeight}, calibration {_cal.Width}x{_cal.Height}, factor {_f:0.000}");
            }
            else { _imgW = _imgH = 0; _f = 1; Viewport.Background = NoMapBg; Log.Info($"Atlas map {map}: no background ({_cal?.BackgroundPath ?? "no calibration"})"); }
            // Following: a map change (instance in and out) must not zoom out to the whole map; the next tick re-centres at the follow zoom.
            if (FollowBox.IsChecked == true && _mapShown > 0) _followZoomed = false; else FitWholeMap();
            StaticLayer.InvalidateVisual();
        }
        if (!IsVisible && !_sample) return;   // another tab is in front: the 500 ms clock must not rebuild hundreds of elements nobody sees
        Overlay.Children.Clear();
        _labelPts.Clear();
        _ovTx = _tx; _ovTy = _ty; OverlayPan.X = OverlayPan.Y = 0;
        if (_cal is null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (++_ticks % 60 == 1) Log.Info($"Atlas view: scale {_scale:0.000} pan ({_tx:0},{_ty:0}) viewport {Viewport.ActualWidth:0}x{Viewport.ActualHeight:0} image {_imgW}x{_imgH} self {atlas.SelfId} at ({atlas.SelfX:0},{atlas.SelfY:0}) age {(atlas.SelfAtMs > 0 ? (now - atlas.SelfAtMs) / 1000 : -1)}s estimate {atlas.Estimate(now)} positions {atlas.PositionsSeen}/{atlas.SelfPositions} self spots {atlas.Count(map)}");

        foreach (var (code, name, px, py) in _cal.Bosses)
        {
            var timer = _meter.Timers.ForServer(_meter.Timers.CurrentServer).FirstOrDefault(t => t.NpcCode == code);
            var up = timer?.AliveNow == true;
            var label = up ? name : timer?.NextSpawn is { } next ? $"{name} · {Countdown(next)}" : name;
            Dot(px, py, 7, up ? BossUp : Gone, label, up ? BossUp : Gone, skull: true);
        }

        var spots = atlas.Spots(map);
        RefreshToggles(spots);
        DrawZones(spots);
        if (PetsBox.IsChecked == true) DrawPets();
        // Zoomed out, icons of one kind pile up: group the spots of a kind per 28 px screen cell and draw one icon with the count
        // (same idea as the label clustering; Diego 2026-10-10). Zoomed in, every spot draws on its own as before.
        var cluster = _scale * _f < 0.16;
        // the static layer paints the seeded spots (and the pet spawns); it repaints when their set, the cluster mode or the view changes
        var seededCount = 0; foreach (var s0 in spots) if (s0.Seeded) seededCount++;
        _spotsCache = spots;
        if (cluster != _clusterNow || seededCount != _seededCount || PetsBox.IsChecked != _petsShown) { _clusterNow = cluster; _seededCount = seededCount; _petsShown = PetsBox.IsChecked; StaticLayer.InvalidateVisual(); }
        var cells = cluster ? new Dictionary<(string, int, int), (double Px, double Py, int N, int Alive)>() : null;
        foreach (var s in spots)
        {
            var (px, py) = _cal.ToPixel(s.X, s.Y);
            // Known kinds draw their icon at the spot (name kept for the tooltip-less reader: none); unknown kinds a plain dot.
            var kind = KindOf(s);
            if (cells is not null && kind is not null)
            {
                var sx = px * _f * _scale + _tx; var sy = py * _f * _scale + _ty;
                var key = (kind, (int)Math.Floor(sx / 28), (int)Math.Floor(sy / 28));
                cells[key] = cells.TryGetValue(key, out var c) ? (c.Px + px, c.Py + py, c.N + 1, c.Alive + (s.Alive && !s.Seeded ? 1 : 0)) : (px, py, 1, s.Alive && !s.Seeded ? 1 : 0);
                continue;
            }
            var icon = kind is { } kn ? Icon(kn) : null;
            if (s.Seeded) continue;   // painted by the static layer
            if (s.Alive && s.InView) Mark(px, py, 5, Live, icon, $"{s.UnitsLeft}", Live);
            else if (s.Alive) Mark(px, py, 4, Live, icon, $"{s.UnitsLeft}?", Gone);
            else
            {
                var eta = s.NextSpawnMs > 0 ? Countdown(DateTimeOffset.FromUnixTimeMilliseconds(s.NextSpawnMs), now) : "";
                Mark(px, py, 4, Gone, icon, eta, Gone, dim: true);
            }
        }
        if (cells is not null)
            foreach (var ((kind, _, _), c) in cells)
                Mark(c.Px / c.N, c.Py / c.N, 5, c.Alive > 0 ? Live : Gone, Icon(kind), c.N > 1 ? "×" + c.N : "", c.Alive > 0 ? Live : Gone, dim: c.Alive == 0, always: true);

        // The player: the last reported position (casts, gathers, state records) when fresh, else the centroid of
        // what is in view (hollow dot), else the last report however old.
        var age = atlas.SelfAtMs > 0 ? (now - atlas.SelfAtMs) / 1000 : long.MaxValue;
        var (ex, ey, en) = atlas.Estimate(now);
        float wx, wy; bool exact;
        // An action's position holds while the surroundings agree with it. Standing still, the centroid of what is in view
        // wanders as mobs and players move (Diego saw the dot drift while idle, 2026-10-09), so the centroid only takes
        // over when it has stayed more than 40 m away from the last fix for 15 s running: that is walking, not noise.
        var farNow = en >= 3 && (Math.Abs(ex - atlas.SelfX) > 4_000 || Math.Abs(ey - atlas.SelfY) > 4_000);
        if (!farNow) _driftSinceMs = 0; else if (_driftSinceMs == 0) _driftSinceMs = now;
        var drifted = farNow && now - _driftSinceMs >= 15_000;
        if (age <= 20 || !drifted) { wx = atlas.SelfX; wy = atlas.SelfY; exact = true; }
        else if (en >= 3) { wx = ex; wy = ey; exact = false; }
        else if (age < 30 * 60) { wx = atlas.SelfX; wy = atlas.SelfY; exact = true; }
        else { wx = wy = 0; exact = false; }
        if (wx != 0 || wy != 0)
        {
            var (sx, sy) = _cal.ToPixel(wx, wy);
            if (FollowBox.IsChecked == true && sx >= 0 && sy >= 0 && sx <= _cal.Width && sy <= _cal.Height && Viewport.ActualWidth > 0)
            {
                if (!_followZoomed) { _scale = 10; _followZoomed = true; } // ~420 px per 100 m: nodes a few metres apart stay separate (Diego's pick: closest)
                _tx = Viewport.ActualWidth / 2 - sx * _f * _scale;
                _ty = Viewport.ActualHeight / 2 - sy * _f * _scale;
                Apply(); _ovTx = _tx; _ovTy = _ty; OverlayPan.X = OverlayPan.Y = 0;
            }
            DrawTrail(now, wx, wy);
            var label = exact ? (age > 10 ? $"{age}s" : "") : $"~{en}";
            if (exact) Dot(sx, sy, 8, Self, label, Self);
            else
            {
                var x = sx * _f * _scale + _tx; var y = sy * _f * _scale + _ty;
                var ring = new Ellipse { Width = 18, Height = 18, Stroke = Self, StrokeThickness = 2.5, Fill = Brushes.Transparent };
                Canvas.SetLeft(ring, x - 9); Canvas.SetTop(ring, y - 9); Overlay.Children.Add(ring);
                var t = new TextBlock { Text = label, FontSize = 11, Foreground = Self, Background = Ink, Padding = new Thickness(3, 0, 3, 0) };
                Canvas.SetLeft(t, x + 12); Canvas.SetTop(t, y - 7); Overlay.Children.Add(t);
            }
        }
    }

    /// <summary>The kind name of a spot: from objects.json for seen codes, from the map data for seeded ones.</summary>
    private string? KindOf(NodeSpot s) => s.Seeded ? s.Name : _meter.Data.Objects.TryGetValue(s.Code, out var n) ? n : null;

    // ------------------------------------------------------------------ best areas per kind (A2M-3)

    /// <summary>The kinds known on this map, one toggle each; rebuilt when the spot count changes.</summary>
    private void RefreshToggles(List<NodeSpot> spots)
    {
        if (spots.Count == _toggleSpots) return;
        _toggleSpots = spots.Count;
        var kinds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in spots) if (KindOf(s) is { } n) kinds.Add(n);
        var on = _meter.Settings.AtlasZoneKinds;
        _zoneToggles.Clear();
        foreach (var name in kinds)
        {
            var key = name.Trim().ToLowerInvariant();
            _zoneToggles.Add(new ZoneToggle(key, name, Icon(name), IconBrush(name), on.Contains(key), string.Format(T.AtlasZonesTip, name)));
        }
        ZoneToggles.ItemsSource ??= _zoneToggles;
    }

    private void ZoneToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string key }) return;
        var on = _meter.Settings.AtlasZoneKinds;
        if (on.Remove(key)) { }
        else
        {
            on.Add(key);
            while (on.Count > 2) on.RemoveAt(0); // at most two kinds: the oldest drops
        }
        foreach (var t in _zoneToggles) t.On = on.Contains(t.Key);
        _toggleSpots = -1; // rebuild so the bindings (OneWay) reflect the new state
        _meter.Settings.Save();
        Refresh();
    }

    /// <summary>
    /// The 5 densest areas of each toggled kind as padded hull outlines in the kind's icon colour. Drawn on the screen-space
    /// overlay every refresh, so the stroke keeps its width and the shape stays anchored at any zoom or pan.
    /// </summary>
    private void DrawZones(List<NodeSpot> spots)
    {
        if (_cal is null || _meter.Settings.AtlasZoneKinds.Count == 0) return;
        foreach (var key in _meter.Settings.AtlasZoneKinds)
        {
            var mine = new List<NodeSpot>();
            foreach (var s in spots)
                if (KindOf(s) is { } n && string.Equals(n.Trim(), key, StringComparison.OrdinalIgnoreCase)) mine.Add(s);
            if (mine.Count == 0) continue;
            var brush = IconBrush(key);
            var fill = brush is SolidColorBrush sc ? new SolidColorBrush(Color.FromArgb(0x2A, sc.Color.R, sc.Color.G, sc.Color.B)) : Brushes.Transparent;
            var rank = 0;
            foreach (var z in NodeZones.Compute(mine))
            {
                rank++;
                var pts = new PointCollection();
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var (wx, wy) in z.Hull)
                {
                    var (px, py) = _cal.ToPixel(wx, wy);
                    var x = px * _f * _scale + _tx; var y = py * _f * _scale + _ty;
                    pts.Add(new Point(x, y));
                    if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
                if (maxX < -CullX - 100 || maxY < -CullY - 100 || minX > Viewport.ActualWidth + CullX + 100 || minY > Viewport.ActualHeight + CullY + 100) continue;
                var (cx, cy) = _cal.ToPixel(z.CenterX, z.CenterY);
                var sx = cx * _f * _scale + _tx; var sy = cy * _f * _scale + _ty;
                var dash = rank == 1 ? null : new DoubleCollection { 6, 3 };
                // Zoomed out, a 150 m area is a few pixels: keep a readable ring (min 48 px) so the best spots stay findable at any zoom.
                // A dark halo under the coloured stroke keeps it visible over light and dark terrain alike.
                if (Math.Max(maxX - minX, maxY - minY) < 48)
                {
                    const double r = 24;
                    Overlay.Children.Add(Place(new Ellipse { Width = 2 * r + 6, Height = 2 * r + 6, Stroke = Ink, StrokeThickness = 6, Fill = Brushes.Transparent }, sx - r - 3, sy - r - 3));
                    Overlay.Children.Add(Place(new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = brush, StrokeThickness = 3, Fill = fill, StrokeDashArray = dash }, sx - r, sy - r));
                }
                else
                {
                    Overlay.Children.Add(new Polygon { Points = pts, Stroke = Ink, StrokeThickness = 7, Fill = Brushes.Transparent, StrokeLineJoin = PenLineJoin.Round });
                    Overlay.Children.Add(new Polygon { Points = pts, Stroke = brush, StrokeThickness = 3, Fill = fill, StrokeLineJoin = PenLineJoin.Round, StrokeDashArray = dash });
                }
                var text = new TextBlock { Text = $"#{rank} · {z.Count}", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = brush, Background = Ink, Padding = new Thickness(4, 1, 4, 1) };
                Canvas.SetLeft(text, sx - 16); Canvas.SetTop(text, sy - 9);
                Overlay.Children.Add(text);
            }
        }
    }

    private static UIElement Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }

    /// <summary>The icon's dominant colour: the saturation-weighted mean of its opaque pixels (cached per kind); gold when there is no icon.</summary>
    private Brush IconBrush(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        if (_iconBrush.TryGetValue(key, out var b)) return b;
        Brush brush = Self;
        if (Icon(name) is BitmapSource bmp)
        {
            try
            {
                var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                var w = conv.PixelWidth; var h = conv.PixelHeight; var px = new byte[w * h * 4];
                conv.CopyPixels(px, w * 4, 0);
                double r = 0, g = 0, bl = 0, wsum = 0;
                for (var i = 0; i < px.Length; i += 4)
                {
                    if (px[i + 3] < 128) continue;
                    int bb = px[i], gg = px[i + 1], rr = px[i + 2];
                    var max = Math.Max(rr, Math.Max(gg, bb)); var min = Math.Min(rr, Math.Min(gg, bb));
                    var sat = max == 0 ? 0 : (max - min) / (double)max;
                    var wgt = 0.15 + sat * sat; // saturated pixels dominate, dark outlines barely count
                    r += rr * wgt; g += gg * wgt; bl += bb * wgt; wsum += wgt;
                }
                if (wsum > 0)
                {
                    var c = Color.FromRgb((byte)(r / wsum), (byte)(g / wsum), (byte)(bl / wsum));
                    // lift towards white so it stays readable on the dark map
                    c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));
                    var sb = new SolidColorBrush(c); sb.Freeze(); brush = sb;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { }
        }
        return _iconBrush[key] = brush;
    }

    private long _driftSinceMs;
    private readonly List<(long Ms, float X, float Y)> _trail = new();

    /// <summary>The last 10 s of the player's drawn position as a fading line (new point only when it moved ≥ 0.5 m).</summary>
    // ------------------------------------------------------------------ pets below level 3 (A2M-11)
    private List<Core.Atlas.MonsterTemplate> _monsters = new();
    private List<(Core.GameDb.PetInfo Pet, int Level, Core.Atlas.MonsterTemplate Template)>? _petLayer;
    private readonly List<(double X, double Y, int Index)> _petMarkers = new();   // screen positions drawn this frame, for the right-click card
    private long _petLayerKey = -1;
    // soft, so the markers never compete with the nodes: red = not owned / level 0, orange = 1, yellow = 2; level 3 draws nothing
    private static readonly Brush[] PetLevelBrush = [Soft(0xE0, 0x47, 0x3F), Soft(0xF0, 0x8A, 0x24), Soft(0xF2, 0xC2, 0x5A)];
    private static readonly Brush PetInk = Soft(0x10, 0x14, 0x1C, 0xA0);
    private static Brush Soft(byte r, byte g, byte b, byte a = 0x8C) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }

    private void Pets_Click(object sender, RoutedEventArgs e)
    {
        _meter.Settings.AtlasShowPets = PetsBox.IsChecked == true;
        _meter.Settings.Save();
        Refresh();
    }

    /// <summary>
    /// Every spawn of a species whose pet is below level 3: a small translucent dot per spawn in the level colour, and at the
    /// centre of each template one soft badge with the level (0/1/2) and the pet's name. The layer is rebuilt when the pet
    /// list or any level changes (Diego, 2026-10-10: icons only until level 3, 0/1/2 colour-coded, nothing once maxed).
    /// </summary>
    private void DrawPets()
    {
        if (_cal is null) return;
        var pets = _samplePets ?? _meter.Character.Pets;
        long key = pets.Count * 1_000_003L; foreach (var kv in pets) key += kv.Key * 7L + kv.Value;
        if (_petLayer is null || _petLayerKey != key)
        {
            _petLayerKey = key;
            _petLayer = new List<(Core.GameDb.PetInfo, int, Core.Atlas.MonsterTemplate)>();
            HidePetInfo();
            var below = 0;
            foreach (var pet in _meter.Pets.All)
            {
                var level = pets.GetValueOrDefault(pet.Id, 0);
                if (level >= 3 || pet.Souls.Length == 0) continue;   // no soul item = not farmable from monsters (shop/event pet)
                below++;
                foreach (var t in _monsters)
                    if (Core.Atlas.MonsterSpawns.Drops(pet, t)) _petLayer.Add((pet, level, t));
            }
            PetsBox.ToolTip = string.Format(T.AtlasPetsTip, below, _petLayer.Select(x => x.Pet.Id).Distinct().Count());
            StaticLayer.InvalidateVisual();
        }
    }

    private List<Core.Atlas.NodeSpot> _spotsCache = new();
    private bool _clusterNow; private int _seededCount = -1; private bool? _petsShown;
    private static readonly Typeface BadgeFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush SeededDim = Soft(0x9A, 0xA0, 0xB0, 0x60);

    /// <summary>Seeded nodes and pet spawns, painted in one pass; also the pet markers the right-click uses.</summary>
    private void DrawStatic(DrawingContext dc)
    {
        _staticTx = _tx; _staticTy = _ty; _staticScale = _scale; StaticPan.X = StaticPan.Y = 0;
        if (_cal is null) return;
        var w = Viewport.ActualWidth; var h = Viewport.ActualHeight; var k = _f * _scale;
        var mx = CullX; var my = CullY;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!_clusterNow && _spotsCache.Count > 0)
        {
            dc.PushOpacity(0.45);
            foreach (var s in _spotsCache)
            {
                if (!s.Seeded) continue;
                var (px, py) = _cal.ToPixel(s.X, s.Y);
                var x = px * k + _tx; var y = py * k + _ty;
                if (x < -mx || y < -my || x > w + mx || y > h + my) continue;
                var icon = KindOf(s) is { } kn ? Icon(kn) : null;
                if (icon is not null) dc.DrawImage(icon, new Rect(x - 11, y - 11, 22, 22));
                else dc.DrawEllipse(SeededDim, null, new Point(x, y), 3, 3);
            }
            dc.Pop();
        }
        _petMarkers.Clear();
        if (PetsBox.IsChecked != true || _petLayer is null) return;
        for (var li = 0; li < _petLayer.Count; li++)
        {
            var (pet, level, t) = _petLayer[li];
            var brush = PetLevelBrush[Math.Clamp(level, 0, 2)];
            double sx = 0, sy = 0; var n = 0;
            foreach (var (x0, y0) in t.Points)
            {
                var (px, py) = _cal.ToPixel(x0, y0);
                var vx = px * k + _tx; var vy = py * k + _ty;
                sx += vx; sy += vy; n++;
                if (vx < -mx || vy < -my || vx > w + mx || vy > h + my) continue;
                dc.DrawEllipse(brush, null, new Point(vx, vy), 3.5, 3.5);
                _petMarkers.Add((vx, vy, li));
            }
            if (n == 0) continue;
            var cx = sx / n; var cy = sy / n;
            if (cx < -mx || cy < -my || cx > w + mx || cy > h + my) continue;
            dc.DrawEllipse(brush, new Pen(PetInk, 1), new Point(cx, cy), 9, 9);
            var digit = new FormattedText(level.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, BadgeFace, 10, Brushes.White, dip);
            dc.DrawText(digit, new Point(cx - digit.Width / 2, cy - digit.Height / 2));
            _petMarkers.Add((cx, cy, li));
            if (k >= 0.12)
            {
                var label = new FormattedText(pet.Name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10.5, Brushes.White, dip);
                dc.DrawRectangle(PetInk, null, new Rect(cx + 11, cy - 8, label.Width + 6, label.Height));
                dc.DrawText(label, new Point(cx + 14, cy - 8));
            }
        }
    }

    private void DrawTrail(long now, float wx, float wy)
    {
        if (_cal is null) return;
        if (_trail.Count == 0 || Math.Abs(_trail[^1].X - wx) > 50 || Math.Abs(_trail[^1].Y - wy) > 50) _trail.Add((now, wx, wy));
        _trail.RemoveAll(p => now - p.Ms > 10_000);
        if (_trail.Count < 2) return;
        for (var i = 1; i < _trail.Count; i++)
        {
            var (ax, ay) = _cal.ToPixel(_trail[i - 1].X, _trail[i - 1].Y);
            var (bx, by) = _cal.ToPixel(_trail[i].X, _trail[i].Y);
            var age = (now - _trail[i].Ms) / 10_000.0; // 0 = fresh, 1 = ten seconds old
            Overlay.Children.Add(new Line
            {
                X1 = ax * _f * _scale + _tx, Y1 = ay * _f * _scale + _ty, X2 = bx * _f * _scale + _tx, Y2 = by * _f * _scale + _ty,
                Stroke = Self, StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0.65 * (1 - age) + 0.1,
            });
        }
    }

    /// <summary>First view of a map: the whole zone in the window, centred; Follow then takes over when the player's position is known.</summary>
    private void FitWholeMap()
    {
        var w = _imgW > 0 ? _imgW : 1000;
        var h = _imgH > 0 ? _imgH : 1000;
        var vw = Math.Max(Viewport.ActualWidth, 100);
        var vh = Math.Max(Viewport.ActualHeight, 100);
        _scale = Math.Clamp(Math.Min(vw / w, vh / h), 0.08, 12);
        _tx = (vw - w * _scale) / 2; _ty = (vh - h * _scale) / 2;
        Apply();
    }

    private static string Countdown(DateTimeOffset at, long? nowMs = null)
    {
        var left = at - (nowMs is { } n ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.UtcNow);
        return left.TotalSeconds <= 0 ? "soon" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}" : $"{left.Minutes}:{left.Seconds:00}";
    }

    private readonly Dictionary<string, ImageSource?> _icons = new();

    /// <summary>The catalogue icon of a kind (data/atlas/icons/&lt;name&gt;.png, English name lower-cased), cached; null when there is none.</summary>
    private ImageSource? Icon(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        if (_icons.TryGetValue(key, out var cached)) return cached;
        var path = System.IO.Path.Combine(MeterService.DataDirectory, "atlas", "icons", key + ".png");
        ImageSource? img = null;
        if (File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.DecodePixelWidth = 48; bmp.EndInit(); bmp.Freeze();
                img = bmp;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException) { }
        }
        return _icons[key] = img;
    }

    /// <summary>A node: its icon (when known) over the dot, the units left beside it.</summary>
    // label anchors drawn this frame (A2M-8: a label is skipped when another sits within 14 px)
    private readonly LabelSpacing _labelPts = new();
    // elements are kept half a viewport beyond each edge: panning is a translate, the layers are rebuilt only once the drift passes that
    private double CullX => Math.Max(200, Viewport.ActualWidth / 2);
    private double CullY => Math.Max(200, Viewport.ActualHeight / 2);
    private bool LabelRoom(double x, double y) => _labelPts.TryPlace(x, y);

    private void Mark(double px, double py, double r, Brush fill, ImageSource? icon, string label, Brush textBrush, bool dim = false, bool always = false)
    {
        if (icon is null) { Dot(px, py, r, fill, label, textBrush, always: always); return; }
        var x = px * _f * _scale + _tx;
        var y = py * _f * _scale + _ty;
        if (x < -CullX || y < -CullY || x > Viewport.ActualWidth + CullX || y > Viewport.ActualHeight + CullY) return;
        var size = 22.0;
        var image = new Image { Source = icon, Width = size, Height = size, Opacity = dim ? 0.45 : 1 };
        Canvas.SetLeft(image, x - size / 2); Canvas.SetTop(image, y - size / 2);
        Overlay.Children.Add(image);
        if (label.Length == 0 || (!always && (_scale * _f < 0.12 || !LabelRoom(x, y)))) return;
        var text = new TextBlock { Text = label, FontSize = 11, Foreground = textBrush, Background = Ink, Padding = new Thickness(3, 0, 3, 0) };
        Canvas.SetLeft(text, x + size / 2 + 1); Canvas.SetTop(text, y - 7);
        Overlay.Children.Add(text);
    }

    /// <summary>Draws at the screen position of a calibration pixel; shapes and text keep their size at any zoom.</summary>
    private void Dot(double px, double py, double r, Brush fill, string label, Brush textBrush, bool skull = false, bool always = false)
    {
        var x = px * _f * _scale + _tx;
        var y = py * _f * _scale + _ty;
        if (x < -CullX || y < -CullY || x > Viewport.ActualWidth + CullX || y > Viewport.ActualHeight + CullY) return;
        var shape = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = fill, Stroke = Ink, StrokeThickness = 1.5 };
        Canvas.SetLeft(shape, x - r); Canvas.SetTop(shape, y - r);
        Overlay.Children.Add(shape);
        if (skull)
        {
            var mark = new TextBlock { Text = "\uE91E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Brushes.White };
            Canvas.SetLeft(mark, x - 4.5); Canvas.SetTop(mark, y - 5.5);
            Overlay.Children.Add(mark);
        }
        if (label.Length == 0 || (!always && (_scale * _f < 0.12 || !LabelRoom(x, y)))) return; // labels only once zoomed in enough to read the map
        var text = new TextBlock { Text = label, FontSize = 11, Foreground = textBrush, Background = Ink, Padding = new Thickness(3, 0, 3, 0) };
        Canvas.SetLeft(text, x + r + 2); Canvas.SetTop(text, y - 7);
        Overlay.Children.Add(text);
    }

    private static readonly Brush NoMapBg = Soft(0x0B, 0x0F, 0x18, 0xFF);

    /// <summary>Mean colour of four 32 px corner patches and four edge-midpoint patches of the map (the shipped maps are parchment,
    /// #DAD1BC at every corner, measured 2026-10-13).</summary>
    private static Brush EdgeBrush(BitmapSource bs)
    {
        const int p = 32;
        if (bs.PixelWidth < 2 * p || bs.PixelHeight < 2 * p) return NoMapBg;
        var src = bs.Format == PixelFormats.Bgra32 || bs.Format == PixelFormats.Bgr32 ? bs : new FormatConvertedBitmap(bs, PixelFormats.Bgr32, null, 0);
        var buf = new byte[p * p * 4];
        long b = 0, g = 0, r = 0, n = 0;
        int w = bs.PixelWidth - p, h = bs.PixelHeight - p;
        ReadOnlySpan<(int X, int Y)> at = [(0, 0), (w, 0), (0, h), (w, h), (w / 2, 0), (w / 2, h), (0, h / 2), (w, h / 2)];
        foreach (var (x, y) in at)
        {
            src.CopyPixels(new Int32Rect(x, y, p, p), buf, p * 4, 0);
            for (var i = 0; i < buf.Length; i += 4) { b += buf[i]; g += buf[i + 1]; r += buf[i + 2]; }
            n += p * p;
        }
        return Soft((byte)(r / n), (byte)(g / n), (byte)(b / n), 0xFF);
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.DecodePixelWidth = 4096;   // the maps are 4096² JPEGs: native size = 64 MB texture, sharp at full zoom (3000 upscaled before, 2026-10-11)
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void Apply()
    {
        Zoom.ScaleX = Zoom.ScaleY = _scale;
        Pan.X = _tx; Pan.Y = _ty;
        var dx = _tx - _staticTx; var dy = _ty - _staticTy;
        if (_scale != _staticScale || Math.Abs(dx) > CullX || Math.Abs(dy) > CullY) StaticLayer.InvalidateVisual();
        else { StaticPan.X = dx; StaticPan.Y = dy; }
        OverlayPan.X = _tx - _ovTx; OverlayPan.Y = _ty - _ovTy;
        if (!_syncingSlider) { _syncingSlider = true; ZoomSlider.Value = Math.Log(_scale); _syncingSlider = false; }
    }

    // Wheel ticks and drag moves arrive faster than the data layer redraws (hundreds of icons at low zoom): the map transform
    // applies at once, the layer is redrawn at most every 40 ms (Diego 2026-10-10: "el zoom con la rueda va muy lento").
    private readonly System.Windows.Threading.DispatcherTimer _redraw = new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _redrawWired, _syncingSlider;
    private void RequestRefresh()
    {
        if (!_redrawWired) { _redrawWired = true; _redraw.Tick += (_, _) => { _redraw.Stop(); Refresh(); }; }
        if (!_redraw.IsEnabled) _redraw.Start();
    }

    private void Zoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || _cal is null) return;
        var next = Math.Clamp(Math.Exp(e.NewValue), 0.08, 12);
        var cx = Viewport.ActualWidth / 2; var cy = Viewport.ActualHeight / 2;   // zoom around the centre of the view
        _tx = cx - (cx - _tx) * next / _scale; _ty = cy - (cy - _ty) * next / _scale; _scale = next;
        _syncingSlider = true; Apply(); _syncingSlider = false;
        RequestRefresh();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        HidePetInfo();
        var p = e.GetPosition(Viewport);
        var k = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var next = Math.Clamp(_scale * k, 0.08, 12);
        k = next / _scale; _scale = next;
        _tx = p.X - (p.X - _tx) * k; _ty = p.Y - (p.Y - _ty) * k;
        Apply(); RequestRefresh(); // zooming keeps Follow: the next tick re-centres at the new zoom
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(Viewport); _dragTx = _tx; _dragTy = _ty;
        HidePetInfo();
        Viewport.CaptureMouse();
        e.Handled = true; // not the window-drag
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Viewport);
        _tx = _dragTx + (p.X - from.X); _ty = _dragTy + (p.Y - from.Y);
        if (FollowBox.IsChecked == true) FollowBox.IsChecked = false;
        Apply();
        if (Math.Abs(_tx - _ovTx) > CullX || Math.Abs(_ty - _ovTy) > CullY) RequestRefresh();
    }

    /// <summary>Right-click on a node: name its kind (also the way to name a node that was in view before the meter started).</summary>
    private void Viewport_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_cal is null) return;
        var p = e.GetPosition(Viewport);
        if (PetsBox.IsChecked == true && _petLayer is not null)
        {
            var best = -1; var bestD = 14.0 * 14.0;
            for (var i = 0; i < _petMarkers.Count; i++)
            {
                var dx = _petMarkers[i].X + StaticPan.X - p.X; var dy = _petMarkers[i].Y + StaticPan.Y - p.Y; var d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = _petMarkers[i].Index; }
            }
            if (best >= 0) { ShowPetInfo(best, p); e.Handled = true; return; }
        }
        HidePetInfo();
        // screen → calibration pixel → world (the affine is scale + offset: invert through two known points)
        var px = (p.X - _tx) / (_f * _scale); var py = (p.Y - _ty) / (_f * _scale);
        var (ox, oy) = _cal.ToPixel(0, 0); var (ux, uy) = _cal.ToPixel(1000, 1000);
        var wx = (float)((px - ox) / (ux - ox) * 1000); var wy = (float)((py - oy) / (uy - oy) * 1000);
        var spot = _meter.Atlas.Nearest(_mapShown, wx, wy, maxCm: (float)(14 / (_f * _scale) / _cal.Scale));
        if (spot is null) return;
        AppHost.Current.ShowNameNode(spot.Code);
        e.Handled = true;
    }

    /// <summary>Card for a right-clicked pet marker: the mob (name, level/family/Named, spawns on this map) and the pet (our level, souls per level, other droppers, zones).</summary>
    private void ShowPetInfo(int index, Point at)
    {
        var (pet, level, t) = _petLayer![index];
        PetInfoTitle.Text = t.Name;
        PetInfoTitle.Foreground = Brushes.White;
        var body = PetInfoBody.Inlines; body.Clear();
        void Label(string t) => body.Add(new System.Windows.Documents.Run(t + " ") { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        void Text(string t) => body.Add(new System.Windows.Documents.Run(t));
        void Br() => body.Add(new System.Windows.Documents.LineBreak());
        Text((t.Sub.Length > 0 ? t.Sub : t.Category) + " · " + string.Format(T.AtlasPetSpawns, t.Points.Length)); Br();
        Label(T.AtlasPets + ":"); Text(pet.Name + " · ");
        body.Add(new System.Windows.Documents.Run(string.Format(T.AtlasPetLevel, level)) { FontWeight = FontWeights.Bold, Foreground = PetLevelBrush[Math.Clamp(level, 0, 2)] });
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

        PetInfo.Visibility = Visibility.Visible;
        PetInfo.UpdateLayout();
        var w = PetInfo.ActualWidth > 0 ? PetInfo.ActualWidth : 300; var h = PetInfo.ActualHeight > 0 ? PetInfo.ActualHeight : 90;
        Canvas.SetLeft(PetInfo, Math.Max(4, Math.Min(at.X + 14, Viewport.ActualWidth - w - 4)));
        Canvas.SetTop(PetInfo, Math.Max(4, Math.Min(at.Y - 10, Viewport.ActualHeight - h - 4)));
    }

    private void HidePetInfo() { if (PetInfo.Visibility == Visibility.Visible) PetInfo.Visibility = Visibility.Collapsed; }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        Viewport.ReleaseMouseCapture();
        RequestRefresh();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        FollowBox.IsChecked = false;
        FitWholeMap();
        Refresh();
    }

    /// <summary>True while the pointer is over the map: the hosting window must not start its own drag (the map pans itself; broke again with the tab host, Diego 2026-10-10).</summary>
    public bool MapHover => Viewport.IsMouseOver;

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp(sender is FrameworkElement { Tag: string t } ? t : "meter");
    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
