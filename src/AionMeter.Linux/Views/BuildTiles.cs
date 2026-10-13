using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using AionMeter.Core.Builds;

namespace AionMeter.Linux.Views;

/// <summary>The Build tab's questlog greys (WPF BuildView resources: docs/mockups/build-tab, questlog's planner CSS).</summary>
internal static class BuildColors
{
    public static readonly IBrush Text = Visuals.Solid(0xFFFAFAFA), Dim = Visuals.Solid(0xFFAFAFAF), Mute = Visuals.Solid(0xFF7D7D7D), Blue = Visuals.Solid(0xFF65B0FC),
        Line = Visuals.Solid(0xFF2B2B2B), Line2 = Visuals.Solid(0xFF373737), Btn = Visuals.Solid(0xFF2B2B2B), Card = Visuals.Solid(0xCC1F1F1F), Band = Visuals.Solid(0xFF1F1F1F),
        Amber = Visuals.Solid(0xFFF5B544), Green = Visuals.Solid(0xFF3DDC84), Bad = Visuals.Solid(0xFFFF5C5C), Num = Visuals.Solid(0xFFFCC78B), Purple = Visuals.Solid(0xFFA979CB),
        KeyBack = Visuals.Solid(0xFF1F1F1F), KeyOn = Visuals.Solid(0xFF2F5F8F), KeyText = Visuals.Solid(0xFFDCDCDC), Cell = Visuals.Solid(0xFF1A1A1A),
        SpecOnBack = Visuals.Solid(0x1A65B0FC), SpecOnLine = Visuals.Solid(0x5965B0FC), Marker = Visuals.Solid(0xFF3A3A3A), MarkerText = Visuals.Solid(0xFFCCCCCC),
        TagBack = Visuals.Solid(0xFF2B2B2B), CmpBack = Visuals.Solid(0xFF1F1F1F), Gt = Visuals.Solid(0xFF555555), AddLine = Visuals.Solid(0xFF444444), AddText = Visuals.Solid(0xFF888888),
        TipBack = Visuals.Solid(0xFF141414), Field = Visuals.Solid(0xFF1A1A1A), Hover = Visuals.Solid(0xFF5A5A5A), White = Visuals.Solid(0xFFFFFFFF), Black = Visuals.Solid(0xFF000000),
        BoardBack = Visuals.Solid(0xFF111111), RowBack = Visuals.Solid(0xFF141414), BdOnBack = Visuals.Solid(0xFF333333), BdOnLine = Visuals.Solid(0xFF555555);

    private static readonly ConditionalWeakTable<Bitmap, Bitmap> Greys = new();

    /// <summary>The icon in grey (the mock's grayscale(1)): BT.601 luma per pixel into a new bitmap, cached per source (48 px icons: ~9 KB each).</summary>
    public static Bitmap Grey(Bitmap src)
    {
        if (Greys.TryGetValue(src, out var g)) return g;
        var size = src.PixelSize;
        var grey = src;
        try
        {
            var wb = new WriteableBitmap(size, src.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                var len = fb.RowBytes * size.Height;
                src.CopyPixels(new PixelRect(size), fb.Address, len, fb.RowBytes);
                var buf = new byte[len];
                Marshal.Copy(fb.Address, buf, 0, len);
                for (var i = 0; i + 3 < len; i += 4)
                {
                    var y = (byte)((buf[i] * 29 + buf[i + 1] * 150 + buf[i + 2] * 77) >> 8);   // B,G,R weights; premultiplied stays premultiplied
                    buf[i] = buf[i + 1] = buf[i + 2] = y;
                }
                Marshal.Copy(buf, 0, fb.Address, len);
            }
            grey = wb;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { }
        Greys.AddOrUpdate(src, grey);
        return grey;
    }

    public static IPen Pen(uint argb, double thickness) => new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(argb)), thickness);
}

/// <summary>
/// One skill icon of the Build tab (WPF Controls/SkillTile) in a single Render: questlog's 48 px tile with the type-coloured frame, the build
/// level bottom-right, the character's level bottom-left, one diamond per chosen spec and the selection outline. Any size: laid out at 48 and scaled.
/// </summary>
internal sealed class SkillTile : Control
{
    public enum Look { Normal, Off, Done }

    private static readonly IBrush Back = Visuals.Solid(0xFF0D0D0D), BadgeBack = Visuals.Solid(0xCC000000), CurBack = Visuals.Solid(0xD9000000);
    private static readonly IPen ActivePen = BuildColors.Pen(0xB365B0FC, 0.8), StigmaPen = BuildColors.Pen(0xB3A979CB, 0.8), PassivePen = BuildColors.Pen(0xB3EA584C, 0.8),
        OffPen = BuildColors.Pen(0xFF3A3A3A, 0.8), SelectedPen = BuildColors.Pen(0xFF65B0FC, 2), ShadowPen = BuildColors.Pen(0xB0000000, 0.8);
    private static readonly Typeface Bold = new(Visuals.Ui, FontStyle.Normal, FontWeight.Bold);
    private static readonly RoundedRect TileClip = new(new Rect(0, 0, 48, 48), 6);
    private static readonly Geometry[] Diamonds = MakeDiamonds();

    public int SkillId { get; private set; }
    private Bitmap? _icon;
    private string _sub = "active";
    private Look _look;
    private int _diamonds;
    private bool _selected, _badges = true;
    private string? _level, _current;
    private IBrush? _currentBrush;
    private FormattedText? _levelText, _currentText;
    private Geometry? _levelGeo, _currentGeo;

    public SkillTile() { Width = 48; Height = 48; }

    public void Set(int skillId, Bitmap? icon, string sub, Look look, int diamonds, bool selected, string? level, string? current, IBrush? currentBrush, bool badges = true)
    {
        SkillId = skillId; _icon = icon; _sub = sub; _look = look; _diamonds = Math.Min(diamonds, Diamonds.Length); _selected = selected; _badges = badges;
        if (_level != level) { _level = level; _levelText = null; _levelGeo = null; }
        if (_current != current || !ReferenceEquals(_currentBrush, currentBrush)) { _current = current; _currentBrush = currentBrush; _currentText = null; _currentGeo = null; }
        Opacity = look == Look.Done ? 0.28 : 1;
        InvalidateVisual();
    }

    public void SetIcon(Bitmap? icon) { if (!ReferenceEquals(icon, _icon)) { _icon = icon; InvalidateVisual(); } }

    public void SetSelected(bool selected) { if (selected != _selected) { _selected = selected; InvalidateVisual(); } }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        if (Math.Abs(w - 48) > 0.01) { using (dc.PushTransform(Matrix.CreateScale(w / 48, w / 48))) Draw(dc); }
        else Draw(dc);
    }

    private void Draw(DrawingContext dc)
    {
        using (dc.PushClip(TileClip))
        {
            dc.FillRectangle(Back, new Rect(0, 0, 48, 48));
            if (_icon is { } icon)
            {
                if (_look == Look.Normal) dc.DrawImage(icon, new Rect(0, 0, 48, 48));
                else
                {
                    // mock CSS: grayscale(1) brightness(.55) for "not in the build", grayscale at .28 opacity (whole tile) for "done"
                    if (_look == Look.Off) { using (dc.PushOpacity(0.55)) dc.DrawImage(BuildColors.Grey(icon), new Rect(0, 0, 48, 48)); }
                    else dc.DrawImage(BuildColors.Grey(icon), new Rect(0, 0, 48, 48));
                }
            }
        }
        dc.DrawRectangle(null, _look == Look.Off ? OffPen : _sub == "stigma" ? StigmaPen : _sub == "passive" ? PassivePen : ActivePen, new RoundedRect(new Rect(0.4, 0.4, 47.2, 47.2), 5.6));
        for (var i = 0; i < _diamonds; i++) dc.DrawGeometry(BuildColors.Blue, ShadowPen, Diamonds[i]);
        if (_badges && _look != Look.Off)
        {
            if (_level is { Length: > 0 })
            {
                _levelText ??= new FormattedText(_level, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 11, BuildColors.White);
                var bw = Math.Max(21, _levelText.Width + 6);
                _levelGeo ??= Corner(49 - bw, 33, bw, 16, topLeft: true);
                dc.DrawGeometry(BadgeBack, null, _levelGeo);
                dc.DrawText(_levelText, new Point(49 - bw + (bw - _levelText.Width) / 2, 33 + (16 - _levelText.Height) / 2));
            }
            if (_current is { Length: > 0 } && _currentBrush is not null)
            {
                _currentText ??= new FormattedText(_current, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Bold, 10, _currentBrush);
                var bw = Math.Max(16, _currentText.Width + 6);
                _currentGeo ??= Corner(-1, 35, bw, 14, topLeft: false);
                dc.DrawGeometry(CurBack, null, _currentGeo);
                dc.DrawText(_currentText, new Point(-1 + (bw - _currentText.Width) / 2, 35 + (14 - _currentText.Height) / 2));
            }
        }
        if (_selected) dc.DrawRectangle(null, SelectedPen, new RoundedRect(new Rect(-2, -2, 52, 52), 8));
    }

    /// <summary>Badge with one rounded corner (6 px): top-left for the build level, top-right for the current level.</summary>
    private static Geometry Corner(double x, double y, double w, double h, bool topLeft)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (topLeft)
            {
                c.BeginFigure(new Point(x, y + 6), true);
                c.ArcTo(new Point(x + 6, y), new Size(6, 6), 0, false, SweepDirection.Clockwise);
                c.LineTo(new Point(x + w, y));
                c.LineTo(new Point(x + w, y + h));
                c.LineTo(new Point(x, y + h));
            }
            else
            {
                c.BeginFigure(new Point(x, y), true);
                c.LineTo(new Point(x + w - 6, y));
                c.ArcTo(new Point(x + w, y + 6), new Size(6, 6), 0, false, SweepDirection.Clockwise);
                c.LineTo(new Point(x + w, y + h));
                c.LineTo(new Point(x, y + h));
            }
            c.EndFigure(true);
        }
        return g;
    }

    // 7 px squares rotated 45° (half diagonal 4.95) at left 3 / top 3, centres 9 px apart (the mock's flex row)
    private static Geometry[] MakeDiamonds()
    {
        var list = new Geometry[4];
        for (var i = 0; i < list.Length; i++)
        {
            double cx = 6.5 + i * 9, cy = 6.5, r = 4.95;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx, cy - r), true);
                c.LineTo(new Point(cx + r, cy));
                c.LineTo(new Point(cx, cy + r));
                c.LineTo(new Point(cx - r, cy));
                c.EndFigure(true);
            }
            list[i] = g;
        }
        return list;
    }
}

/// <summary>
/// One Daevanion node, 32 px (WPF Controls/NodeTile): grade tint when open, the sprite (disabled look when closed), the skill icon inset 5 px
/// for Skill Level Up nodes (grey at half strength while closed), a green inner outline when planned, red when open off the plan.
/// </summary>
internal sealed class NodeTile : Control
{
    public const double Size = 32;
    private static readonly IBrush TintCommon = Visuals.Solid(0x4D808080), TintUnique = Visuals.Solid(0x4DD3AD0F), TintRare = Visuals.Solid(0x4D3C78C8), TintLegend = Visuals.Solid(0x4DA050C8),
        Hit = Visuals.Solid(0x00000000);
    private static readonly IPen PlanPen = BuildColors.Pen(0xBF3DDC84, 1.5), PlanOpenPen = BuildColors.Pen(0x663DDC84, 1.0), OffPen = BuildColors.Pen(0xB3FF5C5C, 1.5);
    private static readonly Rect Full = new(0, 0, Size, Size), Inset = new(5, 5, Size - 10, Size - 10);
    private static readonly RoundedRect IconClip = new(Inset, 3);

    public DaevNode Node { get; }
    public bool Open { get; private set; }
    public bool Planned { get; private set; }
    /// <summary>Open on the character but not in the build's plan: points spent off the plan (red outline; Diego 2026-10-11).</summary>
    public bool OffPlan { get; private set; }
    private Bitmap? _sprite, _icon;

    public NodeTile(DaevNode node) { Node = node; Width = Size; Height = Size; Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand); }

    public void Set(bool open, bool planned, bool offPlan, bool dim, Bitmap? sprite, Bitmap? icon)
    {
        var changed = open != Open || planned != Planned || offPlan != OffPlan || !ReferenceEquals(sprite, _sprite) || !ReferenceEquals(icon, _icon);
        Open = open; Planned = planned; OffPlan = offPlan; _sprite = sprite; _icon = icon;
        Opacity = dim ? 0.22 : 1;
        if (changed) InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Open ? Node.Grade switch { "Unique" => TintUnique, "Rare" => TintRare, "Legend" => TintLegend, _ => TintCommon } : Hit, null, new RoundedRect(Full, 4));
        if (_sprite is { } s) dc.DrawImage(s, Full);
        if (_icon is { } icon)
        {
            using (dc.PushClip(IconClip))
            {
                if (Open) dc.DrawImage(icon, Inset);
                else
                {
                    using (dc.PushOpacity(0.5)) dc.DrawImage(BuildColors.Grey(icon), Inset);   // mock: grayscale(1) brightness(.5)
                }
            }
        }
        if (Planned) dc.DrawRectangle(null, Open ? PlanOpenPen : PlanPen, new RoundedRect(new Rect(0.75, 0.75, Size - 1.5, Size - 1.5), 3.25));
        else if (OffPlan) dc.DrawRectangle(null, OffPen, new RoundedRect(new Rect(0.75, 0.75, Size - 1.5, Size - 1.5), 3.25));
    }
}
