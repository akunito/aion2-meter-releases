using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.Service.Overlay;

namespace AionMeter.App.ViewModels;

/// <summary>A footer pill for one recurring event: text and colour by time left, plus the right-click alert toggles (saved to settings).</summary>
public sealed class EventPill(string key, string name, AppSettings.EventAlert alert, Action save, Action<string> syncNow, Action<string> resetSync) : ObservableObject
{
    public bool CanSync { get; init; } = true;
    public void SyncNow() => syncNow(key);
    public void ResetSync() => resetSync(key);
    private string _text = name;
    private Brush _fill = Calm, _stroke = CalmStroke;

    // Measured against the game's own timer colours only by eye: grey-blue (calm), grey-yellow (< 20 min), orange (< 10), red (< 5 or running).
    public static readonly Brush Calm = Solid("#3A4A66"), CalmStroke = Solid("#8FA3C7");
    public static readonly Brush Soon = Solid("#7E7036"), SoonStroke = Solid("#D9C56A");
    public static readonly Brush Close = Solid("#A8612A"), CloseStroke = Solid("#FFB071");
    public static readonly Brush Now = Solid("#A8362B"), NowStroke = Solid("#FF8A7A");

    private static SolidColorBrush Solid(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Text { get => _text; set => Set(ref _text, value); }
    public Brush Fill { get => _fill; set => Set(ref _fill, value); }
    public Brush Stroke { get => _stroke; set => Set(ref _stroke, value); }

    public bool Notify { get => alert.Notify; set { if (alert.Notify != value) { alert.Notify = value; save(); Raise(); } } }
    public bool Sound { get => alert.Sound; set { if (alert.Sound != value) { alert.Sound = value; save(); Raise(); } } }
    public bool At10 { get => alert.At10; set { if (alert.At10 != value) { alert.At10 = value; save(); Raise(); } } }
    public bool At5 { get => alert.At5; set { if (alert.At5 != value) { alert.At5 = value; save(); Raise(); } } }

    /// <summary>Colour step from <see cref="OverlayBuilder.ToneOf"/>.</summary>
    public void Paint(PillTone tone)
    {
        var (fill, stroke) = tone switch
        {
            PillTone.Now => (Now, NowStroke),
            PillTone.Close => (Close, CloseStroke),
            PillTone.Soon => (Soon, SoonStroke),
            _ => (Calm, CalmStroke),
        };
        if (!ReferenceEquals(fill, _fill)) { Fill = fill; Stroke = stroke; }
    }
}
