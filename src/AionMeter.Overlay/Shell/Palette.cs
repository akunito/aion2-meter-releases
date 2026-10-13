using AionMeter.Core.GameDb;

namespace AionMeter.Overlay.Shell;

/// <summary>Text colours of the kit: each is a CSS class (<see cref="Palette.Class"/>), set with <see cref="UiKit.SetFg"/>.</summary>
public enum Fg : byte { Text, Dim, Mute, Gold, GoldBright, GoldDim, Cream, Red, Green, Amber, White, Dark }

/// <summary>
/// The WPF Theme.xaml palette as the Avalonia Visuals has it (A2M-50): 0xRRGGBB / 0xAARRGGBB values for cairo (<see cref="Paint"/>), the
/// CSS colour each <see cref="Fg"/> class carries, and the item-grade colours (Diego 2026-10-11).
/// </summary>
public static class Palette
{
    public const uint Cream = 0xF3E6C8, Gold = 0xC9A45C, GoldBright = 0xECCB82, GoldDim = 0x6E5A33;
    public const uint Text = 0xF1E9D8, TextDim = 0xA3ACBF, TextMute = 0x6B7590, Line = 0x2A3350;
    public const uint Red = 0xEF4D56, Green = 0x3DD68C, Amber = 0xF5B544, Dim = 0x8A93A8, LimitAmber = 0xF0C060, Dark = 0x1A140A;
    public const uint Bg0 = 0x0B0F17, Bg1 = 0x111726, Bg2 = 0x19203A;
    /// <summary>ARGB overlays: hover fill, section card, stat tile.</summary>
    public const uint HoverFill = 0x14FFFFFF, SectionFill = 0x0DFFFFFF, SectionLine = 0x1AFFFFFF, TileFill = 0x12FFFFFF, TileLine = 0x1FFFFFFF;

    private static readonly uint[] GradeLines = [0x373737, 0x8C8C8C, 0x4CB05A, 0x4F8FE0, 0xD3AD0F, 0xE0663A, 0x3FB6B0];
    private static readonly uint[] GradeTexts = [0xFAFAFA, 0xCFCFCF, 0x7ED38A, 0x7FB0F0, 0xE7C74A, 0xF08A5D, 0x6FD3CD];
    public static uint GradeLine(Grade g) => GradeLines[(int)g < GradeLines.Length ? (int)g : 0];
    public static uint GradeText(Grade g) => GradeTexts[(int)g < GradeTexts.Length ? (int)g : 0];
    public static int GradeCount => GradeLines.Length;

    private static readonly uint[] FgRgb = [Text, TextDim, TextMute, Gold, GoldBright, GoldDim, Cream, Red, Green, Amber, 0xFFFFFF, Dark];
    private static readonly string[] FgClass = ["k-fg-text", "k-fg-dim", "k-fg-mute", "k-fg-gold", "k-fg-goldbright", "k-fg-golddim", "k-fg-cream", "k-fg-red", "k-fg-green", "k-fg-amber", "k-fg-white", "k-fg-dark"];
    public static uint Rgb(Fg fg) => FgRgb[(int)fg];
    public static string Class(Fg fg) => FgClass[(int)fg];
    internal static ReadOnlySpan<string> Classes => FgClass;

    /// <summary>"#C9A45C" for a stylesheet.</summary>
    public static string Hex(uint rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"rgba(255,255,255,0.08)" for an ARGB value.</summary>
    public static string Rgba(uint argb) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"rgba({(argb >> 16) & 0xFF},{(argb >> 8) & 0xFF},{argb & 0xFF},{(argb >> 24) / 255.0:0.###})");
}
