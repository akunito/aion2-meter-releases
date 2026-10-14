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

    /// <summary>
    /// Which edges a right-button drag pressed at (<paramref name="x"/>, <paramref name="y"/>) inside a <paramref name="w"/>×<paramref name="h"/>
    /// card moves: -1 = left/top, 1 = right/bottom, 0 = neither. Outer thirds pick that edge, a corner cell both; the centre cell takes the
    /// corner of its quadrant, which is Sway's own floating_modifier rule (seatop_default: always the corner of the half the press is in).
    /// </summary>
    public static (int X, int Y) EdgeAt(double x, double y, int w, int h)
    {
        int ex = x * 3 < w ? -1 : x * 3 >= 2 * w ? 1 : 0;
        int ey = y * 3 < h ? -1 : y * 3 >= 2 * h ? 1 : 0;
        if (ex == 0 && ey == 0) { ex = x * 2 < w ? -1 : 1; ey = y * 2 < h ? -1 : 1; }
        return (ex, ey);
    }

    /// <summary>
    /// A right-button drag resize: the edges picked by <see cref="EdgeAt"/> follow the pointer (<paramref name="dx"/>, <paramref name="dy"/>,
    /// totals since the press, see <see cref="Drag"/>), the opposite edges stay where they are on the output, so moving the left/top edge of a
    /// left/top-anchored card also moves its margin. Start size and margins in px, <paramref name="minW"/>/<paramref name="minH"/> and the
    /// returned W/H unscaled (what overlay.json keeps): the moving edge is placed from the width the card will really get (unscaled × scale,
    /// rounded), otherwise a 1.5 scale shifted the opposite edge by a pixel. The moving edges stop at the output's edges and at the minimum.
    /// </summary>
    public static (int W, int H, int MarginH, int MarginV) EdgeResize(Anchor a, int edgeX, int edgeY, int startW, int startH, int startMarginH,
        int startMarginV, double dx, double dy, int minW, int minH, int outW, int outH, double scale)
    {
        if (!(scale > 0)) scale = 1;
        var (w, l) = Span(IsRight(a) ? outW - startMarginH - startW : startMarginH, startW, edgeX, dx, minW, outW, scale);
        var (h, t) = Span(IsBottom(a) ? outH - startMarginV - startH : startMarginV, startH, edgeY, dy, minH, outH, scale);
        var mh = IsRight(a) ? outW - l - (int)Math.Round(w * scale) : l;
        var mv = IsBottom(a) ? outH - t - (int)Math.Round(h * scale) : t;
        return (w, h, Math.Max(0, mh), Math.Max(0, mv));
    }

    /// <summary>One axis of <see cref="EdgeResize"/>: the unscaled length and the new near edge (left/top) in px.</summary>
    private static (int Len, int Near) Span(int near, int len, int edge, double d, int min, int outLen, double scale)
    {
        var far = near + len;
        var minPx = (int)Math.Round(min * scale);
        int lo = outLen > 0 ? 0 : int.MinValue / 2, hi = outLen > 0 ? outLen : int.MaxValue / 2;
        var px = edge switch
        {
            < 0 => far - Math.Min(Math.Max(near + (int)Math.Round(d), lo), far - minPx),
            > 0 => Math.Max(Math.Min(far + (int)Math.Round(d), hi), near + minPx) - near,
            _ => len,
        };
        var u = Math.Max(min, (int)Math.Round(px / scale));
        return (u, edge < 0 ? far - (int)Math.Round(u * scale) : near);
    }

    /// <summary>Keeps a saved place on screen after the output changed (another monitor, another resolution).</summary>
    public static (int H, int V) Fit(int marginH, int marginV, int outW, int outH, int cardW, int cardH) =>
        (Clamp(marginH, outW - cardW), Clamp(marginV, outH - cardH));

    private static int Clamp(int v, int max) => max <= 0 ? 0 : Math.Clamp(v, 0, max);

    // ---- moving to another output (A2M-50): a layer surface lives on one output, so a drag past its edge re-creates the surface on the
    // output under the pointer. Coordinates here are the compositor's global layout (GdkMonitor geometry = xdg-output logical position).

    /// <summary>The card's top-left corner in layout coordinates.</summary>
    public static (int X, int Y) Origin(Anchor a, OutputRect o, int marginH, int marginV, int cardW, int cardH) =>
        (o.X + (IsRight(a) ? o.W - marginH - cardW : marginH), o.Y + (IsBottom(a) ? o.H - marginV - cardH : marginV));

    /// <summary>Margins on <paramref name="o"/> that put the card's top-left at (<paramref name="left"/>, <paramref name="top"/>) in layout
    /// coordinates, clamped so the card stays on <paramref name="o"/>.</summary>
    public static (int H, int V) MarginsAt(Anchor a, OutputRect o, int left, int top, int cardW, int cardH)
    {
        int x = left - o.X, y = top - o.Y;
        return Fit(IsRight(a) ? o.W - cardW - x : x, IsBottom(a) ? o.H - cardH - y : y, o.W, o.H, cardW, cardH);
    }

    /// <summary>The output containing the layout point, or -1 (the gap between outputs, or past the layout).</summary>
    public static int OutputAt(ReadOnlySpan<OutputRect> outputs, double x, double y)
    {
        for (var i = 0; i < outputs.Length; i++)
        {
            var o = outputs[i];
            if (x >= o.X && x < o.X + o.W && y >= o.Y && y < o.Y + o.H) return i;
        }
        return -1;
    }

    /// <summary>
    /// A drag update: the pointer is at the press point (<paramref name="pressX"/>, <paramref name="pressY"/>, layout coordinates) plus
    /// the GestureDrag offsets (totals since the press, see <see cref="Drag"/>). When it is over another output than
    /// <paramref name="current"/>, that output and the margins that keep the grab point (<paramref name="grabX"/>, <paramref name="grabY"/>,
    /// the press inside the card) under the pointer; null while it stays on the current output or is over no output.
    /// </summary>
    public static (int Output, int H, int V)? Cross(Anchor a, ReadOnlySpan<OutputRect> outputs, int current, double pressX, double pressY,
        double dx, double dy, double grabX, double grabY, int cardW, int cardH)
    {
        double px = pressX + dx, py = pressY + dy;
        var i = OutputAt(outputs, px, py);
        if (i < 0 || i == current) return null;
        var (h, v) = MarginsAt(a, outputs[i], (int)Math.Round(px - grabX), (int)Math.Round(py - grabY), cardW, cardH);
        return (i, h, v);
    }
}

/// <summary>An output as GDK reports it: connector, logical position in the layout and logical size, scale.</summary>
public readonly record struct OutputRect(string Connector, int X, int Y, int W, int H, double Scale = 1);
