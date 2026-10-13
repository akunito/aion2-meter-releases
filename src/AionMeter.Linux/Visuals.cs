using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using AionMeter.Core.Events;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux;

/// <summary>The WPF overlay's palette (Theme.xaml + OverlayWindow.xaml resources), as Avalonia brushes.</summary>
internal static class Visuals
{
    public static readonly FontFamily Ui = new("fonts:Inter#Inter");
    public static readonly FontFamily Number = Ui;   // Bahnschrift on Windows; Inter's tabular-ish digits read close enough
    public static readonly FontFamily Title = new("Noto Serif, DejaVu Serif, Liberation Serif, serif");

    public static readonly IBrush Cream = Solid(0xFFF3E6C8), Gold = Solid(0xFFC9A45C), GoldBright = Solid(0xFFECCB82), GoldDim = Solid(0xFF6E5A33);
    public static readonly IBrush Text = Solid(0xFFF1E9D8), TextDim = Solid(0xFFA3ACBF), TextMute = Solid(0xFF6B7590), Line = Solid(0xFF2A3350);
    public static readonly IBrush Red = Solid(0xFFEF4D56), Green = Solid(0xFF3DD68C), Amber = Solid(0xFFF5B544);
    public static readonly IBrush LiveRed = Solid(0xFFEF4D56), SavedGold = Solid(0xFFC9A45C), Dim = Solid(0xFF8A93A8), LimitAmber = Solid(0xFFF0C060);
    public static readonly IBrush ColHeader = Solid(0xFFC9D0DC), FooterText = Solid(0xFFB4BCCB), FooterLink = Solid(0xFFD9B56A), GearText = Solid(0xFFE8ECF4);

    public static readonly IBrush CardFrame = VGradient((0xFF6E5B36, 0), (0xFF2E2A26, 0.35), (0xFF24252C, 1));
    public static readonly IBrush CardGloss = VGradient((0x0DFFFFFF, 0), (0x00FFFFFF, 0.4));
    public static readonly IBrush GoldTopLine = HGradient((0x00C9A45C, 0), (0xFFE7C67F, 0.5), (0x00C9A45C, 1));
    public static readonly IBrush RowTrack = VGradient((0xFF2A3046, 0), (0xFF20253A, 1));
    public static readonly IBrush RowGloss = VGradient((0x22FFFFFF, 0), (0x05FFFFFF, 1));
    public static readonly IBrush HpFill = VGradient((0xFFF67A6C, 0), (0xFFE0473F, 0.5), (0xFFB8202A, 1));
    public static readonly IBrush TimersPill = VGradient((0xFFF3D58F, 0), (0xFFC99A45, 1));

    // EventPill: grey-blue (calm), grey-yellow (< 20 min), orange (< 10), red (< 5 or running) — by eye against the game's own timers.
    private static readonly (IBrush Fill, IBrush Stroke)[] Pills =
    [
        (Solid(0xFF3A4A66), Solid(0xFF8FA3C7)), (Solid(0xFF7E7036), Solid(0xFFD9C56A)),
        (Solid(0xFFA8612A), Solid(0xFFFFB071)), (Solid(0xFFA8362B), Solid(0xFFFF8A7A)),
    ];

    public static (IBrush Fill, IBrush Stroke) Pill(PillTone tone) => Pills[(int)tone];

    // Rank badges: gold / silver / bronze for the top three, ringed number for the rest (RankBrushes).
    private static readonly (IBrush Bg, IBrush Fg, IBrush Border)[] Medals =
    [
        (VGradient((0xFFF6DB8E, 0), (0xFFC99A45, 1)), Solid(0xFF1A140A), Solid(0xFFFFEBB0)),
        (VGradient((0xFFEEF2F7, 0), (0xFFA7B2C1, 1)), Solid(0xFF12161F), Solid(0xFFFFFFFF)),
        (VGradient((0xFFE9A87A, 0), (0xFFA8633B, 1)), Solid(0xFF1A0F08), Solid(0xFFF5C39E)),
    ];
    private static readonly (IBrush Bg, IBrush Fg, IBrush Border) Plain = (Solid(0xFF1A2030), Solid(0xFFF3E6C8), Solid(0xFF6B7590));

    public static (IBrush Bg, IBrush Fg, IBrush Border) Rank(int rank) => rank is >= 1 and <= 3 ? Medals[rank - 1] : Plain;

    private static readonly Dictionary<GameClass, IBrush> Fills = new();

    /// <summary>Full-height row fill: saturated class colour that eases off towards the end of the bar.</summary>
    public static IBrush FillOf(GameClass c)
    {
        if (Fills.TryGetValue(c, out var b)) return b;
        var v = Rgb(OverlayPalette.BarColor(c));
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Mix(v, Colors.White, 0.08), 0),
                new GradientStop(v, 0.55),
                new GradientStop(Color.FromArgb(0xC8, (byte)(v.R * 0.72), (byte)(v.G * 0.72), (byte)(v.B * 0.72)), 1),
            },
        }.ToImmutable();
        Fills[c] = brush;
        return brush;
    }

    private static readonly Dictionary<GameClass, Bitmap?> Emblems = new();

    public static Bitmap? EmblemOf(GameClass c)
    {
        if (c == GameClass.Unknown) return null;
        if (Emblems.TryGetValue(c, out var img)) return img;
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://AionMeter.Linux/Assets/Classes/{c.ToString().ToLowerInvariant()}.png"));
            img = Bitmap.DecodeToWidth(s, 96, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or ArgumentException) { img = null; }
        Emblems[c] = img;
        return img;
    }

    public static Color Rgb(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public static IImmutableSolidColorBrush Solid(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));

    public static IBrush VGradient(params (uint Argb, double Offset)[] stops) => Gradient(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(0, 1, RelativeUnit.Relative), stops);

    public static IBrush HGradient(params (uint Argb, double Offset)[] stops) => Gradient(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative), stops);

    private static IBrush Gradient(RelativePoint start, RelativePoint end, (uint Argb, double Offset)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (argb, offset) in stops) b.GradientStops.Add(new GradientStop(Color.FromUInt32(argb), offset));
        return b.ToImmutable();
    }

    /// <summary>Dense dark halo under every text so it stays readable over any game scene (WPF TextShadow).</summary>
    public static IEffect TextShadow() => new DropShadowDirectionEffect { BlurRadius = 5, ShadowDepth = 1.5, Direction = 300, Opacity = 1, Color = Colors.Black };

    // Stroked 16×16 icons standing in for Segoe Fluent Icons (not on Linux).
    public static readonly Geometry IconMeter = Geometry.Parse("M3 14 V9 M8 14 V4 M13 14 V1.5");
    public static readonly Geometry IconAtlas = Geometry.Parse("M1.5 3.5 L5.5 1.5 L10.5 3.5 L14.5 1.5 L14.5 12.5 L10.5 14.5 L5.5 12.5 L1.5 14.5 Z M5.5 1.5 L5.5 12.5 M10.5 3.5 L10.5 14.5");
    public static readonly Geometry IconTop = Geometry.Parse("M4.5 1.5 L11.5 1.5 L11.5 5 C11.5 9 4.5 9 4.5 5 Z M8 8.5 L8 12 M5 14 L11 14 M4.5 3 L1.5 3 C1.5 5.5 3 6.5 4.8 6.5 M11.5 3 L14.5 3 C14.5 5.5 13 6.5 11.2 6.5");
    public static readonly Geometry IconPrices = Geometry.Parse("M1.5 1.5 L7.5 1.5 L14.5 8.5 L8.5 14.5 L1.5 7.5 Z M4.5 4.2 L5 4.2");
    public static readonly Geometry IconCraft = Geometry.Parse("M2 14 L9 7 M7 3.5 L10 1.5 L14.5 6 L12.5 9 Z");
    public static readonly Geometry IconBuild = Geometry.Parse("M14.5 1.5 L6.5 9.5 M4 7.5 L8.5 12 M2 14 L5 11 M14.5 1.5 L14.5 4.5 M14.5 1.5 L11.5 1.5");
    public static readonly Geometry IconItems = Geometry.Parse("M2.5 5.5 L13.5 5.5 L12.5 14.5 L3.5 14.5 Z M5.5 5.5 L5.5 4 C5.5 0.8 10.5 0.8 10.5 4 L10.5 5.5");
    public static readonly Geometry IconClock = Geometry.Parse("M8 1.5 A6.5 6.5 0 1 1 7.99 1.5 Z M8 4.5 L8 8 L10.5 10");
    public static readonly Geometry IconStopwatch = Geometry.Parse("M8 3.5 A5.5 5.5 0 1 1 7.99 3.5 Z M8 6 L8 9.2 M6 1 L10 1");
    public static readonly Geometry IconReset = Geometry.Parse("M13.5 8 A5.5 5.5 0 1 1 11.9 4.1 M13.5 1.5 L13.5 5 L10 5");
    public static readonly Geometry IconPointer = Geometry.Parse("M3.5 2 L12.5 7 L8.5 8 L7 12 Z M1.5 14.5 L14.5 1.5");
    public static readonly Geometry IconClose = Geometry.Parse("M3.5 3.5 L12.5 12.5 M12.5 3.5 L3.5 12.5");
    public static readonly Geometry IconChevron = Geometry.Parse("M3 6 L8 11 L13 6");
    public static readonly Geometry IconDownload = Geometry.Parse("M8 2 L8 11 M4 7 L8 11 L12 7 M2.5 14 L13.5 14");
    public static readonly Geometry IconGrip = Geometry.Parse("M12 2 L2 12 M12 6 L6 12 M12 10 L10 12");

    public static Avalonia.Controls.Shapes.Path Icon(Geometry g, IBrush stroke, double size = 13, double thickness = 1.5) => new()
    {
        Data = g, Stroke = stroke, StrokeThickness = thickness, Width = size, Height = size, Stretch = Stretch.Uniform,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false,
    };

    /// <summary>WPF has no letter-spacing: spaces between letters give the card label its airy caps (kept identical on Linux).</summary>
    public static string Spaced(string text) => string.Join(' ', text.ToUpperInvariant().ToCharArray());
}

/// <summary>The official class emblem on a dark coin with a faint rim, so it never blends into a bar of the same hue (ClassGlyph OnBar).</summary>
internal sealed class ClassGlyph : Control
{
    private static readonly IBrush Disc = Visuals.Solid(0xB4070910);
    private static readonly IPen Ring = new ImmutablePen(Visuals.Solid(0x55FFFFFF), 1);
    private GameClass _class = (GameClass)255;
    private Bitmap? _emblem;
    private IPen? _unknownPen;
    private static readonly Geometry Unknown = Geometry.Parse("M12 3.5 A8.5 8.5 0 1 0 12.01 3.5 Z M9.5 9.5 C9.5 6.5 14.5 6.5 14.5 9.5 C14.5 11.5 12 11.5 12 14 M12 17 L12 17.5");

    public GameClass Class
    {
        get => _class;
        set
        {
            if (_class == value) return;
            _class = value;
            _emblem = Visuals.EmblemOf(value);
            _unknownPen = new ImmutablePen(new ImmutableSolidColorBrush(Visuals.Rgb(OverlayPalette.ClassColor(value))), 2, lineCap: PenLineCap.Round);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext dc)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        dc.DrawEllipse(Disc, Ring, center, size / 2, size / 2);
        var inner = size * 0.84;
        if (_emblem is not null)
        {
            dc.DrawImage(_emblem, new Rect(center.X - inner / 2, center.Y - inner / 2, inner, inner));
            return;
        }
        var scale = inner / 24.0;
        using (dc.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X - 12 * scale, center.Y - 12 * scale)))
            dc.DrawGeometry(null, _unknownPen, Unknown);
    }
}
