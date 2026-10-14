using System.Globalization;

namespace AionMeter.Service;

/// <summary>The Items / Craft / Prices number formats and the price parser, of the GTK card (A2M-50).</summary>
public static class ItemFormat
{
    /// <summary>"12,847" → "13k", 1,234,567 → "1.2M" (mock kfmt: thousands from 10,000, one decimal below 10 M).</summary>
    public static string Short(long n) => n >= 1_000_000 ? (n / 1e6).ToString(n >= 10_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M"
        : n >= 10_000 ? Math.Round(n / 1e3).ToString(CultureInfo.InvariantCulture) + "k" : n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Craft's compact kinah: 1.25M, 850.5k, 120.</summary>
    public static string Kinah(long v) => v >= 1_000_000 ? (v / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M"
        : v >= 1_000 ? (v / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k" : v.ToString(CultureInfo.InvariantCulture);

    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"1200000", "1,200,000", "1.2m", "850k" → Kinah (WPF PriceWatchView.TryParsePrice).</summary>
    public static bool TryParsePrice(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant().Replace(",", "").Replace(" ", "").Replace("_", "");
        double mul = 1;
        if (t.EndsWith('k')) { mul = 1_000; t = t[..^1]; }
        else if (t.EndsWith('m')) { mul = 1_000_000; t = t[..^1]; }
        else if (t.EndsWith('b')) { mul = 1_000_000_000; t = t[..^1]; }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0) return false;
        value = (long)Math.Round(d * mul);
        return true;
    }
}
