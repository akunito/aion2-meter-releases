using AionMeter.Core.Events;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>
/// Vertical sizes of a DPS row and the column header (unscaled px, Css.Sheet and RowWidget). Diego 2026-10-13: 25 % more compact than the
/// Avalonia card (row 42 + gap 6 = 48 px pitch → 32 + 4 = 36), fonts unchanged, so a third more players fit; widths untouched (the header's
/// slots in Card.cs are 30 + 32 px).
/// Measured in a nested sway (14 fake players, 560×540 card): 100 % row 42 → 32 px, pitch 48 → 36, column header (party border to first row)
/// 25.3 → 20.7, rows visible 7.8 → 10.6; 125 % (700×675) row 52.5 → 40, pitch 60 → 45, header 31.3 → 24.7, rows 8.0 → 10.8; card width 540 / 676
/// visible before and after.
/// </summary>
internal static class RowMetrics
{
    public const int Height = 32, Gap = 4, Pitch = Height + Gap;
    /// <summary>The bar's top gloss band (19 of 42 before).</summary>
    public const int Gloss = 14;
    public const int Rank = 22, Coin = 26, CoinInset = 1, Emblem = 22, Unknown = 20;
    /// <summary>Top/bottom inset of the GS/CP | DPS rule inside the row.</summary>
    public const int RuleInset = 6;
    public const int ColsGap = 2, PartyPadV = 4;
}

/// <summary>Column size groups shared by the column labels and every row (Avalonia SharedSizeGroup).</summary>
internal sealed class Columns
{
    public readonly Gtk.SizeGroup Name = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal), Gs = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal),
        Cp = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal), Dps = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal),
        Damage = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal), Share = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal);
}

/// <summary>One player (WPF RowTemplate): a full-height class-coloured bar (cairo) under rank medal, emblem, name, GS/CP, DPS, damage, share.
/// Built once and updated in place; the bar redraws only when its fill, class, self or hover state changes.</summary>
internal sealed class RowWidget
{
    private readonly Gtk.DrawingArea _bar;
    private readonly Gtk.Label _rank, _name, _gs, _cp, _dps, _damage, _share;
    private readonly Gtk.Box _rule, _coin;
    private readonly Gtk.Image _emblem;
    private readonly IconView _unknown;
    private double _fill = -1;
    private GameClass _class = (GameClass)255;
    private int _rankValue = -1;
    private bool _self, _hover, _hasGear = true;
    private string _tip = "";
    private uint _actor;
    public Gtk.Overlay Root { get; }
    /// <summary>A click on the row (not a drag: the card's GestureDrag still moves it): the row's actor id, never 0.</summary>
    public event Action<uint>? Clicked;

    public RowWidget(Columns cols)
    {
        Root = Gtk.Overlay.New();
        Root.AddCssClass("row");
        _bar = Gtk.DrawingArea.New();
        _bar.SetDrawFunc(DrawBar);
        Root.SetChild(_bar);

        var c = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        c.AddCssClass("rowc");
        var rankSlot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        rankSlot.SetSizeRequest(Icons.Px(30), -1);
        _rank = Gtk.Label.New("");
        _rank.AddCssClass("rank");
        _rank.Valign = Gtk.Align.Center;
        _rank.Halign = Gtk.Align.Start;
        rankSlot.Append(_rank);
        c.Append(rankSlot);

        var coinSlot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        coinSlot.SetSizeRequest(Icons.Px(32), -1);
        _coin = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _coin.AddCssClass("coin");
        _coin.Valign = Gtk.Align.Center;
        _coin.Halign = Gtk.Align.Start;
        // GtkImage at a pixel size, not GtkPicture: a picture is height-for-width and inflated the 42 px row to 112 (measured)
        _emblem = Gtk.Image.New();
        _emblem.PixelSize = Icons.Px(RowMetrics.Emblem);
        _emblem.Halign = Gtk.Align.Center;
        _emblem.Valign = Gtk.Align.Center;
        _coin.Append(_emblem);
        _unknown = Icons.View(Icons.Unknown, 0x94A3B8, RowMetrics.Unknown, 2);
        _coin.Append(_unknown.Widget);
        coinSlot.Append(_coin);
        c.Append(coinSlot);

        _name = Gtk.Label.New("");
        _name.AddCssClass("name");
        _name.Xalign = 0;
        _name.Ellipsize = Pango.EllipsizeMode.End;
        cols.Name.AddWidget(_name);
        c.Append(_name);
        c.Append(_gs = Num("gear", cols.Gs));
        c.Append(_cp = Num("gear", cols.Cp));
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.Hexpand = true;
        c.Append(spacer);
        _rule = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _rule.AddCssClass("rule");
        c.Append(_rule);
        c.Append(_dps = Num(null, cols.Dps));
        c.Append(_damage = Num(null, cols.Damage));
        c.Append(_share = Num(null, cols.Share));
        Root.AddOverlay(c);
        Root.SetMeasureOverlay(c, true);

        var motion = Gtk.EventControllerMotion.New();
        motion.OnEnter += (_, _) => { _hover = true; _bar.QueueDraw(); };
        motion.OnLeave += (_, _) => { _hover = false; _bar.QueueDraw(); };
        Root.AddController(motion);
        var click = Gtk.GestureClick.New();
        click.OnReleased += (_, e) => { if (e.NPress == 1 && _actor != 0) Clicked?.Invoke(_actor); };
        Root.AddController(click);
        Root.SetCursorFromName("pointer");
    }

    private static Gtk.Label Num(string? extra, Gtk.SizeGroup group)
    {
        var l = Gtk.Label.New("");
        l.AddCssClass("num");
        if (extra is not null) l.AddCssClass(extra);
        l.Xalign = 1;
        group.AddWidget(l);
        return l;
    }

    public void Update(OverlayRow r, bool hasGear)
    {
        _actor = r.ActorId;
        if (_rankValue != r.Rank)
        {
            if (_rankValue != -1) _rank.RemoveCssClass(CardText.RankClass(_rankValue));
            _rankValue = r.Rank;
            _rank.AddCssClass(CardText.RankClass(r.Rank));
            _rank.SetText(CardText.Rank(r.Rank));
        }
        if (_class != r.Class)
        {
            _class = r.Class;
            var path = Emblem(r.Class);
            _emblem.Visible = path is not null;
            _unknown.Widget.Visible = path is null;
            if (path is not null) _emblem.SetFromFile(path);
            _unknown.Rgb = OverlayPalette.ClassColor(r.Class);
            _bar.QueueDraw();
        }
        if (_self != r.IsSelf)
        {
            _self = r.IsSelf;
            if (_self) Root.AddCssClass("self"); else Root.RemoveCssClass("self");
            _bar.QueueDraw();
        }
        Set(_name, r.Name);
        Set(_gs, r.GearScore);
        _gs.Visible = r.GearScore.Length > 0;
        Set(_cp, r.CombatPower);
        _cp.Visible = r.CombatPower.Length > 0;
        if (_hasGear != hasGear) { _hasGear = hasGear; _rule.Visible = hasGear; }
        Set(_dps, r.Dps);
        Set(_damage, r.Damage);
        Set(_share, r.Share);
        if (_fill != r.Fill) { _fill = r.Fill; _bar.QueueDraw(); }
        if (!string.Equals(_tip, r.Tooltip, StringComparison.Ordinal)) { _tip = r.Tooltip; Root.SetTooltipText(r.Tooltip.Length > 0 ? r.Tooltip : null); }
    }

    /// <summary>GTK re-lays out a label on every set_text, equal or not: skip the unchanged ones (most cells at 5 Hz).</summary>
    internal static void Set(Gtk.Label l, string text)
    {
        if (!string.Equals(l.GetLabel(), text, StringComparison.Ordinal)) l.SetText(text);
    }

    private static readonly string?[] EmblemPaths = new string?[256];
    private static readonly bool[] EmblemLooked = new bool[256];

    private static string? Emblem(GameClass c)
    {
        if (c == GameClass.Unknown) return null;
        if (EmblemLooked[(byte)c]) return EmblemPaths[(byte)c];
        EmblemLooked[(byte)c] = true;
        var p = Path.Combine(AppContext.BaseDirectory, "Assets", "Classes", c.ToString().ToLowerInvariant() + ".png");
        return EmblemPaths[(byte)c] = File.Exists(p) ? p : null;
    }

    private void DrawBar(Gtk.DrawingArea area, Cairo.Context cr, int w, int h)
    {
        const double r = 7;
        Paint.RoundRect(cr, 0, 0, w, h, r);
        Paint.FillWith(cr, Paint.RowTrack, 0, 0, w, h);
        var fw = Math.Clamp(_fill, 0, 1) * w;
        if (fw >= 1)
        {
            Paint.RoundRect(cr, 0, 0, fw, h, Math.Min(r, fw / 2));
            Paint.FillWith(cr, Paint.FillOf(_class), 0, 0, fw, h);
        }
        var gloss = Math.Min(Icons.Px(RowMetrics.Gloss), h);
        Paint.RoundRect(cr, 0, 0, w, gloss, r, r, 0, 0);
        Paint.FillWith(cr, Paint.RowGloss, 0, 0, w, gloss);
        if (_self)
        {
            // gold frame with a soft inner glow (WPF: BoxShadow 0 0 8 #CCE7C67F outside; a layer surface clips at the row, so inside)
            Paint.RoundRect(cr, 1.5, 1.5, w - 3, h - 3, r - 1);
            Paint.Argb(cr, 0x55E7C67F);
            cr.LineWidth = 3;
            cr.Stroke();
            Paint.RoundRect(cr, 0.8, 0.8, w - 1.6, h - 1.6, r);
            Paint.Rgb(cr, 0xECCB82);
            cr.LineWidth = 1.6;
            cr.Stroke();
        }
        else if (_hover)
        {
            Paint.RoundRect(cr, 0.5, 0.5, w - 1, h - 1, r);
            Paint.Argb(cr, 0x66FFFFFF);
            cr.LineWidth = 1;
            cr.Stroke();
        }
    }
}
