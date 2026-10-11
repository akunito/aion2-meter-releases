using System.Windows;
using System.Windows.Media;
using AionMeter.Core.Builds;

namespace AionMeter.App.Controls;

/// <summary>
/// One Daevanion node, 32 px, drawn in a single OnRender as the mock's .nd: grade tint when open, the sprite (disabled look when
/// closed), the skill icon inset 5 px for Skill Level Up nodes (greyed at half strength while closed) and a 1.5 px green inner
/// outline when planned but not open yet.
/// </summary>
public sealed class NodeTile : FrameworkElement
{
    public const double Size = 32;
    private static readonly Brush Hit = SkillTile.Frozen(0, 0, 0, 0), TintCommon = SkillTile.Frozen(128, 128, 128, 0x4D), TintUnique = SkillTile.Frozen(211, 173, 15, 0x4D),
        TintRare = SkillTile.Frozen(60, 120, 200, 0x4D), TintLegend = SkillTile.Frozen(160, 80, 200, 0x4D);
    private static readonly Pen PlanPen = MakePen(), PlanOpenPen = MakeOpenPen(), OffPen = MakeOffPen();
    private static readonly Rect Full = new(0, 0, Size, Size), Inset = new(5, 5, Size - 10, Size - 10);
    private static readonly Geometry IconClip = Freeze(new RectangleGeometry(Inset, 3, 3));

    public DaevNode Node { get; }
    public bool Open { get; private set; }
    public bool Planned { get; private set; }
    /// <summary>Open on the character but not in the build's plan: points spent off the plan (red outline; Diego 2026-10-11).</summary>
    public bool OffPlan { get; private set; }
    private ImageSource? _sprite, _icon;

    public NodeTile(DaevNode node) { Node = node; Width = Size; Height = Size; SnapsToDevicePixels = true; Cursor = System.Windows.Input.Cursors.Hand; }

    public void Set(bool open, bool planned, bool offPlan, bool dim, ImageSource? sprite, ImageSource? icon)
    {
        var changed = open != Open || planned != Planned || offPlan != OffPlan || !ReferenceEquals(sprite, _sprite) || !ReferenceEquals(icon, _icon);
        Open = open; Planned = planned; OffPlan = offPlan; _sprite = sprite; _icon = icon;
        Opacity = dim ? 0.22 : 1;
        if (changed) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(Open ? Node.Grade switch { "Unique" => TintUnique, "Rare" => TintRare, "Legend" => TintLegend, _ => TintCommon } : Hit, null, Full, 4, 4);
        if (_sprite is { } s) dc.DrawImage(s, Full);
        if (_icon is { } icon)
        {
            dc.PushClip(IconClip);
            if (Open) dc.DrawImage(icon, Inset);
            else
            {
                // mock: grayscale(1) brightness(.5)
                dc.PushOpacity(0.5);
                dc.DrawImage(SkillTile.Grey(icon), Inset);
                dc.Pop();
            }
            dc.Pop();
        }
        if (Planned) dc.DrawRoundedRectangle(null, Open ? PlanOpenPen : PlanPen, new Rect(0.75, 0.75, Size - 1.5, Size - 1.5), 3.25, 3.25);
        else if (OffPlan) dc.DrawRoundedRectangle(null, OffPen, new Rect(0.75, 0.75, Size - 1.5, Size - 1.5), 3.25, 3.25);
    }

    private static Pen MakePen() { var p = new Pen(SkillTile.Frozen(61, 220, 132, 0xBF), 1.5); p.Freeze(); return p; }
    private static Pen MakeOffPen() { var p = new Pen(SkillTile.Frozen(0xFF, 0x5C, 0x5C, 0xB3), 1.5); p.Freeze(); return p; }
    private static Pen MakeOpenPen() { var p = new Pen(SkillTile.Frozen(61, 220, 132, 0x66), 1.0); p.Freeze(); return p; }
    private static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
}
