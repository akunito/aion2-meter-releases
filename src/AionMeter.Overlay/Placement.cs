namespace AionMeter.Overlay;

/// <summary>The corner of the output the card is anchored to; margins count from that corner's two edges.</summary>
public enum Anchor : byte { TopRight, TopLeft, BottomRight, BottomLeft }

/// <summary>
/// Layer surfaces cannot be moved by the compositor: the card moves by changing its margins. Pure math, tested without a display.
/// All values are logical pixels of the output (what layer-shell margins and GTK sizes are in).
/// </summary>
public static class Placement
{
    public static bool IsRight(Anchor a) => a is Anchor.TopRight or Anchor.BottomRight;
    public static bool IsBottom(Anchor a) => a is Anchor.BottomRight or Anchor.BottomLeft;

    public static bool TryParse(string? s, out Anchor anchor)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case "top-right" or "tr": anchor = Anchor.TopRight; return true;
            case "top-left" or "tl": anchor = Anchor.TopLeft; return true;
            case "bottom-right" or "br": anchor = Anchor.BottomRight; return true;
            case "bottom-left" or "bl": anchor = Anchor.BottomLeft; return true;
            default: anchor = Anchor.TopRight; return false;
        }
    }

    public static string Name(Anchor a) => a switch
    {
        Anchor.TopLeft => "top-left", Anchor.BottomRight => "bottom-right", Anchor.BottomLeft => "bottom-left", _ => "top-right",
    };

    /// <summary>
    /// The margins for a drag that moved the pointer (dx, dy) since the press, from the margins at the press: the margin from a
    /// right/bottom edge shrinks when the card moves towards it. Clamped so the card stays on the output.
    /// Measured on Sway 1.11 (2026-10-13, WAYLAND_DEBUG): while a button is held, seatop_down sends wl_pointer.motion relative to the
    /// surface position at the press, ignoring margin commits made since, so GestureDrag offsets are already totals. Adding each update's
    /// offset to the last margins instead overshot 30+60+…+300 = 1650 px for a 300 px drag.
    /// </summary>
    public static (int H, int V) Drag(Anchor a, int startH, int startV, double dx, double dy, int outW, int outH, int cardW, int cardH)
    {
        var h = startH + (int)Math.Round(IsRight(a) ? -dx : dx);
        var v = startV + (int)Math.Round(IsBottom(a) ? -dy : dy);
        return (Clamp(h, outW - cardW), Clamp(v, outH - cardH));
    }

    /// <summary>
    /// Resizing from the bottom-right grip keeps the top-left corner still: with a right/bottom anchor the margin on that side shrinks by
    /// what the card grows. <paramref name="dw"/>/<paramref name="dh"/> are relative to the size at the press (the surface origin does not move).
    /// </summary>
    public static (int W, int H, int MarginH, int MarginV) Resize(Anchor a, int startW, int startH, int startMarginH, int startMarginV,
        double dw, double dh, int minW, int minH, int outW, int outH)
    {
        var w = Math.Max(minW, startW + (int)Math.Round(dw));
        var h = Math.Max(minH, startH + (int)Math.Round(dh));
        // the left/top edge is fixed: the card may grow only up to the output's far edge
        var left = IsRight(a) ? outW - startMarginH - startW : startMarginH;
        var top = IsBottom(a) ? outH - startMarginV - startH : startMarginV;
        if (outW > 0) w = Math.Min(w, Math.Max(minW, outW - left));
        if (outH > 0) h = Math.Min(h, Math.Max(minH, outH - top));
        var mh = IsRight(a) ? Math.Max(0, outW - left - w) : startMarginH;
        var mv = IsBottom(a) ? Math.Max(0, outH - top - h) : startMarginV;
        return (w, h, mh, mv);
    }

    /// <summary>Keeps a saved place on screen after the output changed (another monitor, another resolution).</summary>
    public static (int H, int V) Fit(int marginH, int marginV, int outW, int outH, int cardW, int cardH) =>
        (Clamp(marginH, outW - cardW), Clamp(marginV, outH - cardH));

    private static int Clamp(int v, int max) => max <= 0 ? 0 : Math.Clamp(v, 0, max);
}
