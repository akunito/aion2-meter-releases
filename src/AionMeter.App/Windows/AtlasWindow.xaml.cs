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
    private double _scale = 0.5, _tx, _ty;
    private Point? _dragFrom;
    private int _ticks;
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
        Controls.DragAnywhere.Attach(this);
        FollowBox.IsChecked = true;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _clock.Start(); };
        Closed += (_, _) => _clock.Stop();
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
                MapImage.Width = _cal.Width > 0 ? _cal.Width : bs.PixelWidth;
                MapImage.Height = _cal.Height > 0 ? _cal.Height : bs.PixelHeight;
                Log.Info($"Atlas map {map}: {_cal.BackgroundPath} decoded {bs.PixelWidth}x{bs.PixelHeight}, logical {MapImage.Width}x{MapImage.Height}, dpi {bs.DpiX:0}");
            }
            else Log.Info($"Atlas map {map}: no background ({_cal?.BackgroundPath ?? "no calibration"})");
            FitWholeMap();
        }
        Overlay.Children.Clear();
        if (_cal is null) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
            var name = _meter.Data.ObjectName(s.Code);
            if (s.Alive && s.InView) Dot(px, py, 5, Live, $"{name} {s.UnitsLeft}", Live);
            else if (s.Alive) Dot(px, py, 4, Live, $"{name} {s.UnitsLeft}?", Gone);
            else
            {
                var eta = s.NextSpawnMs > 0 ? Countdown(DateTimeOffset.FromUnixTimeMilliseconds(s.NextSpawnMs), now) : "";
                Dot(px, py, 4, Gone, eta.Length > 0 ? $"{name} · {eta}" : name, Gone);
            }
        }

        if (atlas.SelfId != 0 && now - atlas.SelfAtMs < 120_000)
        {
            var (sx, sy) = _cal.ToPixel(atlas.SelfX, atlas.SelfY);
            Dot(sx, sy, 8, Self, "", Self);
            if (FollowBox.IsChecked == true && sx >= 0 && sy >= 0 && sx <= MapImage.Width && sy <= MapImage.Height && Viewport.ActualWidth > 0)
            {
                _tx = Viewport.ActualWidth / 2 - sx * _scale;
                _ty = Viewport.ActualHeight / 2 - sy * _scale;
                Apply();
            }
            if (++_ticks % 60 == 0) Log.Info($"Atlas view: scale {_scale:0.000} pan ({_tx:0},{_ty:0}) viewport {Viewport.ActualWidth:0}x{Viewport.ActualHeight:0} self px ({sx:0},{sy:0}) spots {atlas.Count(map)}");
        }
    }

    /// <summary>First view of a map: the whole zone in the window, centred; Follow then takes over when the player's position is known.</summary>
    private void FitWholeMap()
    {
        var w = MapImage.Width > 0 ? MapImage.Width : 1000;
        var h = MapImage.Height > 0 ? MapImage.Height : 1000;
        var vw = Math.Max(Viewport.ActualWidth, 100);
        var vh = Math.Max(Viewport.ActualHeight, 100);
        _scale = Math.Clamp(Math.Min(vw / w, vh / h), 0.08, 4);
        _tx = (vw - w * _scale) / 2; _ty = (vh - h * _scale) / 2;
        Apply();
    }

    private static string Countdown(DateTimeOffset at, long? nowMs = null)
    {
        var left = at - (nowMs is { } n ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.UtcNow);
        return left.TotalSeconds <= 0 ? "now" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}" : $"{left.Minutes}:{left.Seconds:00}";
    }

    private void Dot(double px, double py, double r, Brush fill, string label, Brush textBrush, bool skull = false)
    {
        // Shapes are drawn in map pixels but kept readable at any zoom: size divided by the current scale.
        var k = 1 / Math.Max(_scale, 0.05);
        var shape = new Ellipse { Width = 2 * r * k, Height = 2 * r * k, Fill = fill, Stroke = Ink, StrokeThickness = 1.5 * k };
        Canvas.SetLeft(shape, px - r * k); Canvas.SetTop(shape, py - r * k);
        Overlay.Children.Add(shape);
        if (skull)
        {
            var mark = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9 * k, Foreground = Brushes.White };
            Canvas.SetLeft(mark, px - 4.5 * k); Canvas.SetTop(mark, py - 5.5 * k);
            Overlay.Children.Add(mark);
        }
        if (label.Length == 0) return;
        var text = new TextBlock { Text = label, FontSize = 11 * k, Foreground = textBrush, Background = Ink, Padding = new Thickness(3 * k, 0, 3 * k, 0) };
        Canvas.SetLeft(text, px + (r + 2) * k); Canvas.SetTop(text, py - 7 * k);
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
        var next = Math.Clamp(_scale * k, 0.08, 4);
        k = next / _scale; _scale = next;
        _tx = p.X - (p.X - _tx) * k; _ty = p.Y - (p.Y - _ty) * k;
        FollowBox.IsChecked = false;
        Apply(); Refresh();
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
        Apply();
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
