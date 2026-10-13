using AionMeter.Service.Overlay;

namespace AionMeter.Overlay.Shell;

/// <summary>Pango markup for a tooltip (<see cref="UiKit.TipBody"/>); a plain string tooltip is text.</summary>
internal readonly record struct Markup(string Value);

/// <summary>
/// The WPF Theme.xaml / view styles as GTK widgets (A2M-50, the Avalonia UiKit with the same names and arguments where GTK allows): section
/// cards, labels, hints, fields, check boxes, entries, drop-downs, sliders, chips, buttons, stat tiles, grids, tooltips, scroll bodies and bars.
/// Look = CSS classes of <see cref="KitCss"/> (scaled with the card); fixed pixel sizes go through <see cref="Px"/> (the card's scale), so a
/// view built from these is right at any UI scale after the card's rebuild. Main loop only.
/// </summary>
internal static class UiKit
{
    /// <summary>A fixed size in unscaled px at the card's scale (Icons.Px).</summary>
    public static int Px(double v) => Icons.Px(v);

    public static Gtk.Box VBox(int spacing = 0) => Gtk.Box.New(Gtk.Orientation.Vertical, spacing == 0 ? 0 : Px(spacing));
    public static Gtk.Box HBox(int spacing = 0) => Gtk.Box.New(Gtk.Orientation.Horizontal, spacing == 0 ? 0 : Px(spacing));

    /// <summary>A box that takes the remaining width (Avalonia "*" column / DockPanel fill).</summary>
    public static Gtk.Box Spacer()
    {
        var b = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        b.Hexpand = true;
        return b;
    }

    /// <summary>Wrapping row of children (Avalonia WrapPanel); GTK's FlowBox lines children up in columns of the widest one.</summary>
    public static Gtk.FlowBox Wrap()
    {
        var f = Gtk.FlowBox.New();
        f.SelectionMode = Gtk.SelectionMode.None;
        f.Homogeneous = false;
        f.MaxChildrenPerLine = 32;
        f.ColumnSpacing = (uint)Px(6);
        f.RowSpacing = (uint)Px(4);
        return f;
    }

    /// <summary>Replaces the widget's text colour class (k-fg-*).</summary>
    public static T SetFg<T>(T w, Fg fg) where T : Gtk.Widget
    {
        foreach (var c in Palette.Classes) w.RemoveCssClass(c);
        w.AddCssClass(Palette.Class(fg));
        return w;
    }

    public static T Margin<T>(T w, double left, double top, double right, double bottom) where T : Gtk.Widget
    {
        w.MarginStart = Px(left); w.MarginTop = Px(top); w.MarginEnd = Px(right); w.MarginBottom = Px(bottom);
        return w;
    }

    /// <summary>Section (SettingsView "Section"): faint card with a rounded hairline, 16×12 padding, 12 below; optional gold caps label.</summary>
    public static Gtk.Box Section(Gtk.Widget child, string? label = null)
    {
        var b = VBox();
        b.AddCssClass("k-section");
        if (label is not null)
        {
            var l = SectionLabel(label);
            l.MarginBottom = Px(8);
            b.Append(l);
        }
        b.Append(child);
        return b;
    }

    /// <summary>SectionLabel: gold caps, 11 px, spaced.</summary>
    public static Gtk.Label SectionLabel(string text)
    {
        var l = Gtk.Label.New(text.ToUpperInvariant());
        l.AddCssClass("k-seclabel");
        l.Xalign = 0;
        return l;
    }

    /// <summary>Hint: muted wrapped 11.5 px text, indented under a check box by default.</summary>
    public static Gtk.Label Hint(string text, double indent = 24, Fg fg = Fg.Mute)
    {
        var l = Wrapped(text);
        l.AddCssClass("k-hint");
        if (fg != Fg.Mute) SetFg(l, fg);
        l.MarginStart = Px(indent);
        return l;
    }

    /// <summary>Wrapped text in <paramref name="size"/> px (nearest half pixel class), colour and weight (400, 500, 600, 700).</summary>
    public static Gtk.Label Text(string text, double size = 12.5, Fg fg = Fg.Text, int weight = 400)
    {
        var l = Wrapped(text);
        l.Valign = Gtk.Align.Center;
        if (Math.Abs(size - 12.5) > 0.01) l.AddCssClass(KitCss.SizeClass(size));
        if (fg != Fg.Text) SetFg(l, fg);
        if (weight >= 500) l.AddCssClass(weight >= 700 ? "k-w7" : weight >= 600 ? "k-w6" : "k-w5");
        return l;
    }

    /// <summary>A one-line label cut with an ellipsis (TextTrimming).</summary>
    public static Gtk.Label Line(string text, double size = 12.5, Fg fg = Fg.Text, int weight = 400)
    {
        var l = Text(text, size, fg, weight);
        l.Wrap = false;
        l.Ellipsize = Pango.EllipsizeMode.End;
        return l;
    }

    private static Gtk.Label Wrapped(string text)
    {
        var l = Gtk.Label.New(text);
        l.Wrap = true;
        l.WrapMode = Pango.WrapMode.WordChar;
        l.Xalign = 0;
        // a wrapping label asks for its one-line width as natural width: without a cap it widens the whole view past the card
        l.MaxWidthChars = 1;
        l.Hexpand = true;
        return l;
    }

    /// <summary>The fixed-width label left of a field (SettingsView "Label": 190 px).</summary>
    public static Gtk.Label FieldLabel(string text, double width = 190)
    {
        var l = Gtk.Label.New(text);
        l.Wrap = true;
        l.Xalign = 0;
        l.Valign = Gtk.Align.Center;
        l.AddCssClass("k-fieldlabel");
        l.SetSizeRequest(Px(width), -1);
        l.MaxWidthChars = 1;
        return l;
    }

    /// <summary>[label][control] on one line.</summary>
    public static Gtk.Box Field(string label, Gtk.Widget control, double top = 8, double labelWidth = 190)
    {
        var b = HBox();
        b.MarginTop = Px(top);
        b.Append(FieldLabel(label, labelWidth));
        control.Valign = Gtk.Align.Center;
        b.Append(control);
        return b;
    }

    public static Gtk.CheckButton Check(string text, bool value, double top = 0)
    {
        var c = Gtk.CheckButton.New();
        var l = Wrapped(text);
        c.SetChild(l);
        c.Active = value;
        c.MarginTop = Px(top);
        return c;
    }

    /// <summary>Text entry, <paramref name="width"/> px (NaN = fill the row).</summary>
    public static Gtk.Entry Input(string text, double width = 140)
    {
        var e = Gtk.Entry.New();
        e.SetText(text);
        if (double.IsNaN(width)) e.Hexpand = true;
        else { e.SetSizeRequest(Px(width), -1); e.WidthChars = 1; }
        e.Valign = Gtk.Align.Center;
        return e;
    }

    /// <summary>Drop-down of strings (Avalonia ComboBox); <see cref="Selected"/> reads the index, <see cref="SetItems"/> refills.</summary>
    public static Gtk.DropDown Combo(IEnumerable<string> items, int selected, double minWidth = 160)
    {
        var d = Gtk.DropDown.New(Gtk.StringList.New(items.ToArray()), null);
        d.SetSizeRequest(Px(minWidth), -1);
        d.Valign = Gtk.Align.Center;
        if (selected >= 0) d.SetSelected((uint)selected);
        return d;
    }

    /// <summary>Selected index, -1 for none (GTK_INVALID_LIST_POSITION).</summary>
    public static int Selected(Gtk.DropDown d) => d.GetSelected() is var s && s == uint.MaxValue ? -1 : (int)s;

    public static int Count(Gtk.DropDown d) => d.GetModel() is { } m ? (int)m.GetNItems() : 0;

    /// <summary>New strings; selection <paramref name="selected"/> (-1 none). Raises the selection notify like a user pick: guard with a flag.</summary>
    public static void SetItems(Gtk.DropDown d, IEnumerable<string> items, int selected)
    {
        d.SetModel(Gtk.StringList.New(items.ToArray()));
        d.SetSelected(selected >= 0 ? (uint)selected : uint.MaxValue);
    }

    /// <summary>The user (or code) changed the selection.</summary>
    public static void OnSelected(Gtk.DropDown d, Action changed) => d.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") changed(); };

    /// <summary>Horizontal slider (Avalonia Slider with ticks): <paramref name="step"/> &gt; 0 snaps (0 = continuous), no value drawn (the views put a label beside it).</summary>
    public static Gtk.Scale Slider(double min, double max, double step, double value, double width = 180)
    {
        // gtk_scale_new_with_range rejects step 0 (critical + an invalid widget, measured): continuous = 1/100 of the range for keys
        var s = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, min, max, step > 0 ? step : (max - min) / 100);
        s.DrawValue = false;
        s.SetValue(value);
        s.SetSizeRequest(Px(width), -1);
        s.Valign = Gtk.Align.Center;
        if (step > 0) s.OnChangeValue += (r, e) =>
        {
            // snap like IsSnapToTickEnabled: GTK only rounds to `digits`
            var v = Math.Clamp(Math.Round(e.Value / step) * step, min, max);
            if (Math.Abs(r.GetValue() - v) > 1e-9) r.SetValue(v);
            return true;
        };
        return s;
    }

    /// <summary>Chip (Theme "Chip"): gold outline pill; brighter under the mouse; optional leading icon.</summary>
    public static Gtk.Button Chip(string text, Action click, string? tip = null, Icon? icon = null)
    {
        var row = HBox();
        row.Valign = Gtk.Align.Center;
        IconView? glyph = null;
        if (icon is not null) { glyph = Icons.View(icon, Palette.Gold, 10, 1.6); row.Append(glyph.Widget); }
        var label = Gtk.Label.New(text);
        row.Append(label);
        var b = Button(row, "k-chip", click);
        b.Valign = Gtk.Align.Center;
        if (glyph is not null) Hover(b, glyph, Palette.Gold, Palette.GoldBright);
        if (tip is not null) b.SetTooltipText(tip);
        return b;
    }

    /// <summary>Chip text (the Back chip's language switch).</summary>
    public static void SetChipText(Gtk.Button chip, string text)
    {
        if (chip.GetChild() is Gtk.Box row && row.GetLastChild() is Gtk.Label l) l.SetText(text);
    }

    /// <summary>GhostButton: Bg2 fill, hairline.</summary>
    public static Gtk.Button Ghost(string text, Action click) => Button(Gtk.Label.New(text), "k-btn", click);

    /// <summary>PrimaryButton: gold fill, dark text.</summary>
    public static Gtk.Button Primary(string text, Action click)
    {
        var b = Button(Gtk.Label.New(text), "k-btn", click);
        b.AddCssClass("k-primary");
        return b;
    }

    /// <summary>The text of a Ghost / Primary button.</summary>
    public static void SetText(Gtk.Button b, string text)
    {
        if (b.GetChild() is Gtk.Label l) l.SetText(text);
    }

    /// <summary>Icon-only button (ToolIcon): 22×22, the stroke turns gold under the mouse.</summary>
    public static Gtk.Button IconButton(Icon icon, string tip, Action click, double size = 22)
    {
        var v = Icons.View(icon, Palette.TextDim, 12, 1.5);
        var b = Button(v.Widget, "k-icon", click);
        b.SetSizeRequest(Px(size), Px(size));
        b.Valign = Gtk.Align.Center;
        b.SetTooltipText(tip);
        Hover(b, v, Palette.TextDim, Palette.GoldBright);
        return b;
    }

    private static Gtk.Button Button(Gtk.Widget child, string css, Action click)
    {
        var b = Gtk.Button.New();
        b.AddCssClass(css);
        b.Child = child;
        b.SetCursorFromName("pointer");
        b.FocusOnClick = false;
        b.OnClicked += (_, _) => click();
        return b;
    }

    private static void Hover(Gtk.Widget w, IconView v, uint normal, uint hover)
    {
        var motion = Gtk.EventControllerMotion.New();
        motion.OnEnter += (_, _) => v.Rgb = hover;
        motion.OnLeave += (_, _) => v.Rgb = normal;
        w.AddController(motion);
    }

    /// <summary>Pill: rounded badge in an event-pill tone (the card's p0–p3 classes).</summary>
    public static Gtk.Box Pill(string text, PillTone tone)
    {
        var b = HBox();
        b.AddCssClass("pill");
        b.AddCssClass(CardText.PillClass(tone));
        b.Append(Gtk.Label.New(text));
        b.Valign = Gtk.Align.Center;
        return b;
    }

    /// <summary>StatTile (BreakdownView): label caps above a big number, optional caption.</summary>
    public static Gtk.Box StatTile(string label, string value, string? caption = null, Fg valueFg = Fg.Text, double size = 17, double minWidth = 80)
    {
        var b = VBox();
        b.AddCssClass("k-tile");
        b.SetSizeRequest(Px(minWidth), -1);
        var l = Gtk.Label.New(label.ToUpperInvariant());
        l.AddCssClass("k-tilelabel");
        l.Xalign = 0;
        b.Append(l);
        var v = Gtk.Label.New(value);
        v.AddCssClass("k-tilevalue");
        v.AddCssClass(KitCss.SizeClass(size));
        if (valueFg != Fg.Text) SetFg(v, valueFg);
        v.Xalign = 0;
        b.Append(v);
        if (!string.IsNullOrEmpty(caption))
        {
            var c = Gtk.Label.New(caption);
            c.AddCssClass("k-tilecap");
            c.Xalign = 0;
            c.Ellipsize = Pango.EllipsizeMode.End;
            b.Append(c);
        }
        return b;
    }

    /// <summary>A grid row from a column spec ("Auto,*,64"): "*" columns take the spare width, numbers are fixed px, Auto is natural.</summary>
    public static Gtk.Grid Row(string columns, params Gtk.Widget?[] cells)
    {
        var g = Gtk.Grid.New();
        var spec = columns.Split(',');
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } c) continue;
            var col = i < spec.Length ? spec[i].Trim() : "Auto";
            if (col.EndsWith('*')) c.Hexpand = true;
            else if (double.TryParse(col, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)) c.SetSizeRequest(Px(px), -1);
            g.Attach(c, i, 0, 1, 1);
        }
        return g;
    }

    /// <summary>Puts <paramref name="c"/> at (row, column) of <paramref name="g"/> and returns it.</summary>
    public static T Place<T>(this Gtk.Grid g, T c, int row, int column, int columnSpan = 1) where T : Gtk.Widget
    {
        g.Attach(c, column, row, columnSpan, 1);
        return c;
    }

    /// <summary>Plain-text tooltip (GTK4 has no per-widget delay: the toolkit's own ~500 ms, not WPF's 150).</summary>
    public static T Tip<T>(T c, string? tip) where T : Gtk.Widget
    {
        c.SetTooltipText(string.IsNullOrEmpty(tip) ? null : tip);
        return c;
    }

    public static T Tip<T>(T c, Markup tip) where T : Gtk.Widget
    {
        c.SetTooltipMarkup(tip.Value);
        return c;
    }

    /// <summary>A multi-line tooltip body: title in bold, the rest below (escaped Pango markup).</summary>
    public static Markup TipBody(string title, string? body) =>
        new("<b>" + GLib.Functions.MarkupEscapeText(title, -1) + "</b>" + (string.IsNullOrEmpty(body) ? "" : "\n<small>" + GLib.Functions.MarkupEscapeText(body, -1) + "</small>"));

    /// <summary>The hosted views' scroll body: vertical only, the WPF 16 px gutters (k-scroll).</summary>
    public static Gtk.ScrolledWindow Scroll(Gtk.Widget content, bool gutters = true)
    {
        var s = Gtk.ScrolledWindow.New();
        s.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        s.Child = content;
        s.Vexpand = true;
        s.Hexpand = true;
        if (gutters) s.AddCssClass("k-scroll");
        return s;
    }

    /// <summary>A thin progress bar (gold on a dark track), 0..1; set <see cref="BarView.Fraction"/> to move it.</summary>
    public static BarView Bar(double fraction, double height = 4) => new(fraction, height);
}

/// <summary>UiKit.Bar: cairo, redraws only when the fraction changes.</summary>
internal sealed class BarView
{
    private double _fraction;
    public Gtk.DrawingArea Widget { get; }

    public BarView(double fraction, double height)
    {
        _fraction = Math.Clamp(fraction, 0, 1);
        Widget = Gtk.DrawingArea.New();
        Widget.SetSizeRequest(-1, Math.Max(1, UiKit.Px(height)));
        Widget.Hexpand = true;
        Widget.Valign = Gtk.Align.Center;
        Widget.SetDrawFunc((_, cr, w, h) =>
        {
            var r = h / 2.0;
            Paint.RoundRect(cr, 0, 0, w, h, r);
            Paint.Argb(cr, 0x22FFFFFF);
            cr.Fill();
            var fw = _fraction * w;
            if (fw < 0.5) return;
            Paint.RoundRect(cr, 0, 0, fw, h, Math.Min(r, fw / 2));
            Paint.Rgb(cr, Palette.Gold);
            cr.Fill();
        });
    }

    public double Fraction
    {
        get => _fraction;
        set { var v = Math.Clamp(value, 0, 1); if (v == _fraction) return; _fraction = v; Widget.QueueDraw(); }
    }
}
