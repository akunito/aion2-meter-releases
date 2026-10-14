using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AionMeter.App.Services;

namespace AionMeter.App.Windows;

/// <summary>
/// Asked once per unknown object code when the player starts gathering it (or by right-click in the atlas): pick the
/// kind from the catalogue (data/atlas/gather-catalog.json, icons in data/atlas/icons), typing only as a fallback.
/// </summary>
public partial class NameNodeWindow : Window
{
    private readonly MeterService _meter;
    private readonly int _code;
    private string? _current;
    private bool _currentShown;
    private readonly DateTimeOffset _askedAt = DateTimeOffset.UtcNow;
    private readonly bool _autoClose;
    private readonly DispatcherTimer _clock;
    private const int AutoCloseSeconds = 15;

    /// <param name="autoClose">True for the prompt raised by a gather: it shows how long ago that gather was and closes itself
    /// after 15 s, so several gathers in a row cannot be confused (Diego). False from the atlas right-click.</param>
    public NameNodeWindow(MeterService meter, int code, bool autoClose = false)
    {
        _meter = meter;
        _code = code;
        _autoClose = autoClose;
        InitializeComponent();
        Question.Text = string.Format(UiText.Current.NameNodeQuestion, code);
        meter.Data.Objects.TryGetValue(code, out _current); // the kind already chosen for this code: its button is highlighted
        BuildCatalog();

        TimerChip.Visibility = autoClose ? Visibility.Visible : Visibility.Collapsed;
        if (autoClose) System.Media.SystemSounds.Asterisk.Play(); // the prompt lives 15 s on the other monitor: say so
        Tick();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
        Closed += (_, _) => _clock.Stop();
    }

    public int Code => _code;

    private void Tick()
    {
        var age = (int)(DateTimeOffset.UtcNow - _askedAt).TotalSeconds;
        if (!_autoClose) return;
        TimerText.Text = string.Format(UiText.Current.NameNodeTimer, age, Math.Max(0, AutoCloseSeconds - age));
        if (age >= AutoCloseSeconds) Close();
    }

    private void BuildCatalog()
    {
        var dir = Path.Combine(MeterService.DataDirectory, "atlas");
        var path = Path.Combine(dir, "gather-catalog.json");
        if (!File.Exists(path)) return;
        // A2M-47: labels in the names language (Settings → Names); objects.json and the map keep the English name
        var lang = _meter.Settings.ResolvedNamesLanguage;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var cat in doc.RootElement.GetProperty("categories").EnumerateArray())
            {
                var catKey = cat.GetProperty("key").GetString() ?? "";
                var catEn = (cat.TryGetProperty("en", out var cen) ? cen.GetString() : null) ?? catKey;
                var header = new TextBlock { Text = lang == "es" && cat.TryGetProperty("es", out var ces) && ces.GetString() is { Length: > 0 } catEs ? catEs : catEn, Foreground = (Brush)FindResource("TextMute"), FontSize = 11, Margin = new Thickness(0, 6, 0, 2) };
                Catalog.Children.Add(header);
                var row = new WrapPanel();
                foreach (var node in cat.GetProperty("nodes").EnumerateArray())
                {
                    var key = node.GetProperty("key").GetString() ?? "";
                    var name = (node.TryGetProperty("en", out var en) ? en.GetString() : key) ?? key;
                    var label = lang == "es" && node.TryGetProperty("es", out var nes) && nes.GetString() is { Length: > 0 } es ? es : name;
                    var icon = Path.Combine(dir, "icons", key + ".png");
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    if (File.Exists(icon))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(icon); bmp.DecodePixelWidth = 40; bmp.EndInit(); bmp.Freeze();
                        content.Children.Add(new Image { Source = bmp, Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0) });
                    }
                    content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                    var selected = _current is not null && string.Equals(_current.Trim(), name, StringComparison.OrdinalIgnoreCase);
                    if (selected) _currentShown = true;
                    // The kind already chosen for this code stands out in the theme's gold (right-click rename case).
                    object inner = selected
                        ? new Border { Child = content, BorderBrush = (Brush)FindResource("Gold"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xC9, 0xA4, 0x5C)), Padding = new Thickness(6, 2, 8, 2), Margin = new Thickness(-6, -2, -8, -2) }
                        : content;
                    var button = new Button { Style = (Style)FindResource("Chip"), Height = 32, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 0, 10, 0), Content = inner, Tag = (name, catKey) };
                    if (selected) button.FontWeight = FontWeights.SemiBold;
                    button.Click += Pick_Click;
                    row.Children.Add(button);
                }
                Catalog.Children.Add(row);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        {
            Log.Info($"gather-catalog.json ignored: {ex.Message}");
        }
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: (string name, string category) })
        {
            _meter.NameObject(_code, name, category);
            Close();
        }
    }

    private void Other_Click(object sender, RoutedEventArgs e) => SaveOther();

    private void OtherBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SaveOther();
    }

    private void SaveOther()
    {
        var name = OtherBox.Text.Trim();
        if (name.Length == 0) { OtherBox.Focus(); return; }
        _meter.NameObject(_code, name, Core.Atlas.AtlasShare.OtherCategory);
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();
}
