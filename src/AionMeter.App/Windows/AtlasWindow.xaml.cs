using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AionMeter.App.Services;
using AionMeter.Core.Atlas;

namespace AionMeter.App.Windows;

/// <summary>
/// The private node atlas: the calibrated zone map with the player, the gathering spots the meter has seen (live ones
/// with units left, remembered ones with their respawn ETA) and the field bosses. Redrawn twice a second; a few
/// hundred shapes at most, so the canvas is simply rebuilt.
/// </summary>
public partial class AtlasWindow : Window
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

    public AtlasWindow(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this, canDrag: () => !Viewport.IsMouseOver); // the map pans itself
        FollowBox.IsChecked = true;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => Refresh();
        var s = meter.Settings;
        if (!double.IsNaN(s.AtlasLeft) && !double.IsNaN(s.AtlasTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.AtlasLeft; Top = s.AtlasTop; Width = Math.Max(500, s.AtlasWidth); Height = Math.Max(400, s.AtlasHeight);
        }
        Loaded += (_, _) => { Refresh(); _clock.Start(); };
        Closed += (_, _) =>
        {
            _clock.Stop();
            if (WindowState == WindowState.Normal) { s.AtlasLeft = Left; s.AtlasTop = Top; s.AtlasWidth = Width; s.AtlasHeight = Height; s.Save(); }
        };
    }

    private void Refresh()
    {
        var atlas = _meter.Atlas;
        var map = atlas.MapId;
        if (map != _mapShown)
        {
            _mapShown = map;
            _cal = _meter.Calibration(map);
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
            FitWholeMap();
        }
        Overlay.Children.Clear();
        if (_cal is null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (++_ticks % 60 == 1) Log.Info($"Atlas view: scale {_scale:0.000} pan ({_tx:0},{_ty:0}) viewport {Viewport.ActualWidth:0}x{Viewport.ActualHeight:0} image {_imgW}x{_imgH} self {atlas.SelfId} at ({atlas.SelfX:0},{atlas.SelfY:0}) age {(atlas.SelfAtMs > 0 ? (now - atlas.SelfAtMs) / 1000 : -1)}s estimate {atlas.Estimate(now)} spots {atlas.Count(map)}");

        foreach (var (code, name, px, py) in _cal.Bosses)
        {
            var timer = _meter.Timers.ForServer(_meter.Timers.CurrentServer).FirstOrDefault(t => t.NpcCode == code);
            var up = timer?.AliveNow == true;
            var label = up ? name : timer?.NextSpawn is { } next ? $"{name} · {Countdown(next)}" : name;
            Dot(px, py, 7, up ? BossUp : Gone, label, up ? BossUp : Gone, skull: true);
        }

        foreach (var s in atlas.Spots(map))
        {
            var (px, py) = _cal.ToPixel(s.X, s.Y);
            var name = _meter.Data.Objects.TryGetValue(s.Code, out var known) ? known + " " : ""; // unknown kinds show no id
            if (s.Alive && s.InView) Dot(px, py, 5, Live, $"{name}{s.UnitsLeft}", Live);
            else if (s.Alive) Dot(px, py, 4, Live, $"{name}{s.UnitsLeft}?", Gone);
            else
            {
                var eta = s.NextSpawnMs > 0 ? Countdown(DateTimeOffset.FromUnixTimeMilliseconds(s.NextSpawnMs), now) : "";
                Dot(px, py, 4, Gone, (name + eta).Trim(), Gone);
            }
        }

        // The player: the last reported position (casts, gathers, state records) when fresh, else the centroid of
        // what is in view (hollow dot), else the last report however old.
        var age = atlas.SelfAtMs > 0 ? (now - atlas.SelfAtMs) / 1000 : long.MaxValue;
        var (ex, ey, en) = atlas.Estimate(now);
        float wx, wy; bool exact;
        if (age <= 60) { wx = atlas.SelfX; wy = atlas.SelfY; exact = true; } // an action's position holds a minute
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
        return left.TotalSeconds <= 0 ? "now" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}" : $"{left.Minutes}:{left.Seconds:00}";
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
        if (label.Length == 0 || _scale * _f < 0.12) return; // labels only once zoomed in enough to read the map
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
        // screen → calibration pixel → world (the affine is scale + offset: invert through two known points)
        var px = (p.X - _tx) / (_f * _scale); var py = (p.Y - _ty) / (_f * _scale);
        var (ox, oy) = _cal.ToPixel(0, 0); var (ux, uy) = _cal.ToPixel(1000, 1000);
        var wx = (float)((px - ox) / (ux - ox) * 1000); var wy = (float)((py - oy) / (uy - oy) * 1000);
        var spot = _meter.Atlas.Nearest(_mapShown, wx, wy, maxCm: (float)(14 / (_f * _scale) / _cal.Scale));
        if (spot is null) return;
        AppHost.Current.ShowNameNode(spot.Code);
        e.Handled = true;
    }

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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
