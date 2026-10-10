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
        FollowBox.IsChecked = true;
        PetsBox.IsChecked = meter.Settings.AtlasShowPets;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
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
                Log.Info($"Atlas map {map}: {_cal.BackgroundPath} decoded {bs.PixelWidth}x{bs.PixelHeight}, calibration {_cal.Width}x{_cal.Height}, factor {_f:0.000}");
            }
            else { _imgW = _imgH = 0; _f = 1; Log.Info($"Atlas map {map}: no background ({_cal?.BackgroundPath ?? "no calibration"})"); }
            // Following: a map change (instance in and out) must not zoom out to the whole map; the next tick re-centres at the follow zoom.
            if (FollowBox.IsChecked == true && _mapShown > 0) _followZoomed = false; else FitWholeMap();
        }
        Overlay.Children.Clear();
        _labelPts.Clear();
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
        foreach (var s in spots)
        {
            var (px, py) = _cal.ToPixel(s.X, s.Y);
            // Known kinds draw their icon at the spot (name kept for the tooltip-less reader: none); unknown kinds a plain dot.
            var icon = KindOf(s) is { } kn ? Icon(kn) : null;
            if (s.Seeded) { Mark(px, py, 3, Gone, icon, "", Gone, dim: true); continue; }
            if (s.Alive && s.InView) Mark(px, py, 5, Live, icon, $"{s.UnitsLeft}", Live);
            else if (s.Alive) Mark(px, py, 4, Live, icon, $"{s.UnitsLeft}?", Gone);
            else
            {
                var eta = s.NextSpawnMs > 0 ? Countdown(DateTimeOffset.FromUnixTimeMilliseconds(s.NextSpawnMs), now) : "";
                Mark(px, py, 4, Gone, icon, eta, Gone, dim: true);
            }
        }

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
                Apply();
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
                if (maxX < -300 || maxY < -300 || minX > Viewport.ActualWidth + 300 || minY > Viewport.ActualHeight + 300) continue;
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
        var pets = _meter.Character.Pets;
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
                // Diego 2026-10-10: word matching ("Spider", "Slime", "Swarm"...) painted mobs that drop nothing → only aion2hub droppers of the soul item.
                // Pets with a soul but no hub drop data (Coradon, Young Tog…) fall back to the species as the LAST word(s) of the template name.
                foreach (var t in _monsters)
                {
                    var hit = false;
                    if (pet.Droppers.Length > 0) { for (var i = 0; i < pet.Droppers.Length && !hit; i++) hit = pet.Droppers[i].Equals(t.Name, StringComparison.OrdinalIgnoreCase); }
                    else hit = t.Name.EndsWith(pet.Name, StringComparison.OrdinalIgnoreCase) && (t.Name.Length == pet.Name.Length || t.Name[t.Name.Length - pet.Name.Length - 1] == ' ');
                    if (hit) _petLayer.Add((pet, level, t));
                }
            }
            PetsBox.ToolTip = string.Format(T.AtlasPetsTip, below, _petLayer.Select(x => x.Pet.Id).Distinct().Count());
        }
        _petMarkers.Clear();
        for (var li = 0; li < _petLayer.Count; li++)
        {
            var (pet, level, t) = _petLayer[li];
            var brush = PetLevelBrush[Math.Clamp(level, 0, 2)];
            double sx = 0, sy = 0; var n = 0;
            foreach (var (x, y) in t.Points)
            {
                var (px, py) = _cal.ToPixel(x, y);
                var vx = px * _f * _scale + _tx; var vy = py * _f * _scale + _ty;
                if (vx < -50 || vy < -50 || vx > Viewport.ActualWidth + 50 || vy > Viewport.ActualHeight + 50) { sx += vx; sy += vy; n++; continue; }
                var dot = new Ellipse { Width = 7, Height = 7, Fill = brush };
                Canvas.SetLeft(dot, vx - 3.5); Canvas.SetTop(dot, vy - 3.5);
                Overlay.Children.Add(dot); _petMarkers.Add((vx, vy, li));
                sx += vx; sy += vy; n++;
            }
            if (n == 0) continue;
            var cx = sx / n; var cy = sy / n;
            if (cx < -30 || cy < -30 || cx > Viewport.ActualWidth + 30 || cy > Viewport.ActualHeight + 30) continue;
            var badge = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = brush, BorderBrush = PetInk, BorderThickness = new Thickness(1),
                                     Child = new TextBlock { Text = level.ToString(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            Canvas.SetLeft(badge, cx - 9); Canvas.SetTop(badge, cy - 9);
            Overlay.Children.Add(badge); _petMarkers.Add((cx, cy, li));
            if (_scale * _f >= 0.12)
            {
                var text = new TextBlock { Text = pet.Name, FontSize = 10.5, Foreground = Brushes.White, Background = PetInk, Padding = new Thickness(3, 0, 3, 0), Opacity = 0.85 };
                Canvas.SetLeft(text, cx + 11); Canvas.SetTop(text, cy - 8);
                Overlay.Children.Add(text);
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
    // Label anchors drawn this frame: at low zoom neighbouring nodes overprint each other's counts, so a label is skipped when
    // another sits within 14 px (A2M-8 "label clustering"; the icon/dot still draws, the count comes back when zooming in).
    private readonly List<(double X, double Y)> _labelPts = new();
    private bool LabelRoom(double x, double y)
    {
        for (var i = 0; i < _labelPts.Count; i++) { var dx = _labelPts[i].X - x; var dy = _labelPts[i].Y - y; if (dx * dx + dy * dy < 14 * 14) return false; }
        _labelPts.Add((x, y)); return true;
    }

    private void Mark(double px, double py, double r, Brush fill, ImageSource? icon, string label, Brush textBrush, bool dim = false)
    {
        if (icon is null) { Dot(px, py, r, fill, label, textBrush); return; }
        var x = px * _f * _scale + _tx;
        var y = py * _f * _scale + _ty;
        if (x < -200 || y < -200 || x > Viewport.ActualWidth + 200 || y > Viewport.ActualHeight + 200) return;
        var size = 22.0;
        var image = new Image { Source = icon, Width = size, Height = size, Opacity = dim ? 0.45 : 1 };
        Canvas.SetLeft(image, x - size / 2); Canvas.SetTop(image, y - size / 2);
        Overlay.Children.Add(image);
        if (label.Length == 0 || _scale * _f < 0.12 || !LabelRoom(x, y)) return;
        var text = new TextBlock { Text = label, FontSize = 11, Foreground = textBrush, Background = Ink, Padding = new Thickness(3, 0, 3, 0) };
        Canvas.SetLeft(text, x + size / 2 + 1); Canvas.SetTop(text, y - 7);
        Overlay.Children.Add(text);
    }

    /// <summary>Draws at the screen position of a calibration pixel; shapes and text keep their size at any zoom.</summary>
    private void Dot(double px, double py, double r, Brush fill, string label, Brush textBrush, bool skull = false)
    {
        var x = px * _f * _scale + _tx;
        var y = py * _f * _scale + _ty;
        if (x < -200 || y < -200 || x > Viewport.ActualWidth + 200 || y > Viewport.ActualHeight + 200) return;
        var shape = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = fill, Stroke = Ink, StrokeThickness = 1.5 };
        Canvas.SetLeft(shape, x - r); Canvas.SetTop(shape, y - r);
        Overlay.Children.Add(shape);
        if (skull)
        {
            var mark = new TextBlock { Text = "\uE91E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Brushes.White };
            Canvas.SetLeft(mark, x - 4.5); Canvas.SetTop(mark, y - 5.5);
            Overlay.Children.Add(mark);
        }
        if (label.Length == 0 || _scale * _f < 0.12 || !LabelRoom(x, y)) return; // labels only once zoomed in enough to read the map
        var text = new TextBlock { Text = label, FontSize = 11, Foreground = textBrush, Background = Ink, Padding = new Thickness(3, 0, 3, 0) };
        Canvas.SetLeft(text, x + r + 2); Canvas.SetTop(text, y - 7);
        Overlay.Children.Add(text);
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.DecodePixelWidth = 3000;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void Apply()
    {
        Zoom.ScaleX = Zoom.ScaleY = _scale;
        Pan.X = _tx; Pan.Y = _ty;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        HidePetInfo();
        var p = e.GetPosition(Viewport);
        var k = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var next = Math.Clamp(_scale * k, 0.08, 12);
        k = next / _scale; _scale = next;
        _tx = p.X - (p.X - _tx) * k; _ty = p.Y - (p.Y - _ty) * k;
        Apply(); Refresh(); // zooming keeps Follow: the next tick re-centres at the new zoom
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
        FollowBox.IsChecked = false;
        Apply(); Refresh();
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
                var dx = _petMarkers[i].X - p.X; var dy = _petMarkers[i].Y - p.Y; var d = dx * dx + dy * dy;
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
        PetInfoTitle.Foreground = PetLevelBrush[Math.Clamp(level, 0, 2)];
        var sb = new System.Text.StringBuilder(256);
        sb.Append(t.Sub.Length > 0 ? t.Sub : t.Category).Append(" · ").AppendFormat(T.AtlasPetSpawns, t.Points.Length).Append('\n');
        sb.AppendFormat(T.AtlasPetLine, pet.Name, level, pet.NeedSoul > 0 ? pet.NeedSoul : 5).Append('\n');
        var others = 0;
        for (var i = 0; i < pet.Droppers.Length; i++)
        {
            if (pet.Droppers[i].Equals(t.Name, StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(others == 0 ? T.AtlasPetAlso + ' ' : ", ").Append(pet.Droppers[i]);
            if (++others == 6) { sb.Append('…'); break; }
        }
        if (others > 0) sb.Append('\n');
        if (pet.Zones.Count > 0)
        {
            sb.Append(T.AtlasPetZones).Append(' '); var first = true;
            foreach (var z in pet.Zones) { if (!first) sb.Append(", "); sb.Append(z.Key); first = false; }
        }
        PetInfoBody.Text = sb.ToString().TrimEnd('\n');
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
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        FollowBox.IsChecked = false;
        FitWholeMap();
        Refresh();
    }

    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
