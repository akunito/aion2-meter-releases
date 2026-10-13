using System.Globalization;
using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Core.GameDb;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// A2M-44 player card pages on the GTK card (port of the Avalonia ProfilePages, A2M-50): Profile (equipment tiles, Arcana, other, attributes,
/// divine stats), Skills (actives, stigmas, passives by group) and Daevanion (boards, skill levels, stats) from a <see cref="CharacterProfile"/>.
/// Sections without data are left out; tiles whose icon is still downloading redraw on <see cref="RetryIcons"/>. The card is narrow (560 px):
/// one column, Daevanion stats in two (the Avalonia ≥ 900 / ≥ 700 px layouts never apply on the card).
/// </summary>
internal sealed class ProfilePages
{
    private static UiText T => UiText.Current;
    private const double Tile = 44;
    private readonly AppShell _shell;
    private readonly List<IconTile> _tiles = new();
    private readonly List<Func<bool>> _pending = new();

    public ProfilePages(AppShell shell) { _shell = shell; }

    /// <summary>New icon files arrived: tiles still missing theirs redraw (they ask on draw), images ask again. True when nothing is missing.</summary>
    public bool RetryIcons()
    {
        var missing = false;
        foreach (var t in _tiles) if (t.Missing) { t.Retry(); missing = true; }
        for (var i = _pending.Count - 1; i >= 0; i--) if (_pending[i]()) _pending.RemoveAt(i);
        return !missing && _pending.Count == 0;
    }

    public void Clear() { _tiles.Clear(); _pending.Clear(); }

    // ------------------------------------------------------------ Profile

    public Gtk.Widget Profile(PlayerCard card, CharacterProfile p)
    {
        var gear = ProfileLayout.Arrange(p.Equipment);
        var left = UiKit.VBox();
        var hasLeft = false;
        if (gear.HasGear || p.Wing is not null || p.Pet is not null)
        {
            left.Append(Label(T.ProfEquipment));
            var g = UiKit.VBox();
            var armour = new WrapBox();
            for (var i = 0; i < gear.Armour.Length; i++) armour.Add(GearTile(gear.Armour[i], ProfileLayout.ArmourSlots[i]));
            for (var i = 0; i < gear.Weapons.Length; i++) armour.Add(GearTile(gear.Weapons[i], ProfileLayout.WeaponSlots[i]));
            g.Append(armour.Widget);
            var acc = new WrapBox();
            acc.Widget.MarginTop = UiKit.Px(4);
            for (var i = 0; i < gear.Accessories.Length; i++) acc.Add(GearTile(gear.Accessories[i], ProfileLayout.AccessorySlots[i]));
            g.Append(acc.Widget);
            var chips = new WrapBox();
            chips.Widget.MarginTop = UiKit.Px(6);
            if (p.Wing is { } wing)
            {
                var (name, enchant) = ProfileLayout.SplitWing(wing);
                chips.Add(Companion(name + (enchant > 0 ? " +" + enchant.ToString(CultureInfo.InvariantCulture) : ""), T.ProfWing, ProfileLayout.GradeOf(p.WingGrade), p.WingIcon));
            }
            if (p.Pet is { } pet)
            {
                var (name, level) = ProfileLayout.SplitPet(pet);
                chips.Add(Companion(name + (level > 0 ? " · " + string.Format(T.ProfLevel, level) : ""), T.ProfPet, Grade.Unknown, p.PetIcon));
            }
            if (chips.Count > 0) g.Append(chips.Widget);
            left.Append(FightKit.Card(g));
            hasLeft = true;
        }
        if (gear.Arcana.Count > 0) { left.Append(Label(T.ProfArcana, true)); left.Append(FightKit.Card(TileRow(gear.Arcana))); hasLeft = true; }
        if (gear.Other.Count > 0) { left.Append(Label(T.ProfOther, true)); left.Append(FightKit.Card(TileRow(gear.Other))); hasLeft = true; }
        var (right, hasRight) = Attributes(card, p);
        var root = UiKit.VBox();
        root.Append(left);
        if (hasLeft && hasRight) right.MarginTop = UiKit.Px(14);
        root.Append(right);
        return Page(root, p);
    }

    private Gtk.Widget TileRow(List<EquipItem> items)
    {
        var w = new WrapBox();
        foreach (var it in items) w.Add(GearTile(it, it.Slot));
        return w.Widget;
    }

    private Gtk.Widget GearTile(EquipItem? it, string slot)
    {
        var grade = it is null ? Grade.Unknown : ProfileLayout.GradeOf(it.Grade);
        IconTile tile;
        if (it is null)
        {
            tile = new IconTile(Tile, null, Palette.Line, 1.5, null, false, ProfileLayout.SlotLabel(slot));
            tile.Widget.MarginEnd = tile.Widget.MarginBottom = UiKit.Px(6);
            return UiKit.Tip(tile.Widget, ProfileLayout.SlotLabel(slot));
        }
        Func<Gdk.Texture?>? fetch = it.Icon is { Length: > 0 } url ? () => _shell.Icons.Url("gear:" + KeyOf(url), url)
            : it.Id > 0 && it.Id < int.MaxValue ? () => _shell.Icons.Item((int)it.Id) : null;
        tile = new IconTile(Tile, fetch, Palette.GradeLine(grade), 1.5, ProfileLayout.EnchantText(it.Enchant), false, null);
        tile.Widget.MarginEnd = tile.Widget.MarginBottom = UiKit.Px(6);
        if (fetch is not null) _tiles.Add(tile);
        var line = ProfileLayout.SlotLabel(it.Slot) + (it.Exceed > 0 ? " · " + string.Format(T.ProfExceed, it.Exceed) : "");
        var name = it.Name + (it.Enchant > 0 ? " +" + it.Enchant.ToString(CultureInfo.InvariantCulture) : "");
        return UiKit.Tip(tile.Widget, new Markup(FightKit.Span(name, Palette.GradeText(grade), "weight=\"600\"") + "\n" + FightKit.Span(line, Palette.TextDim, "size=\"small\"")));
    }

    private Gtk.Widget Companion(string name, string kind, Grade grade, string? iconUrl)
    {
        var row = UiKit.HBox();
        if (iconUrl is { Length: > 0 })
        {
            var key = "gear:" + KeyOf(iconUrl);
            var image = ItemKit.Image(_shell.Icons.Url(key, iconUrl), 26);
            image.MarginEnd = UiKit.Px(8);
            if (image.GetPaintable() is null)
                _pending.Add(() => { if (_shell.Icons.Url(key, iconUrl) is not { } t) return false; image.SetFromPaintable(t); return true; });
            row.Append(image);
        }
        var text = UiKit.VBox();
        text.Valign = Gtk.Align.Center;
        var k = Gtk.Label.New(kind);
        k.Xalign = 0;
        k.AddCssClass(KitCss.SizeClass(10));
        UiKit.SetFg(k, Fg.Mute);
        text.Append(k);
        var n = Gtk.Label.New(name);
        n.Xalign = 0;
        n.AddCssClass(KitCss.SizeClass(12));
        n.AddCssClass("k-w6");
        // a long wing name must not widen the card: cut at ~30 chars
        n.MaxWidthChars = 30;
        n.Ellipsize = Pango.EllipsizeMode.End;
        if (grade != Grade.Unknown) ItemKit.GradeText(n, grade);
        text.Append(n);
        row.Append(text);
        var box = UiKit.HBox();
        box.AddCssClass("k-fight-companion");
        box.Append(row);
        return box;
    }

    private (Gtk.Box Stack, bool Any) Attributes(PlayerCard card, CharacterProfile p)
    {
        var stack = UiKit.VBox();
        var any = false;
        if (card.Fight is not null && (p.CombatPower > 0 || p.ItemLevel > 0))
        {
            var top = Uniform();
            top.MarginBottom = UiKit.Px(12);
            var col = 0;
            if (p.CombatPower > 0) top.Attach(StatCell(T.ProfCombatPower, Format.Grouped(p.CombatPower), null, Fg.GoldBright, 20), col++, 0, 1, 1);
            if (p.ItemLevel > 0) top.Attach(StatCell(T.ProfItemLevel, Format.Grouped(p.ItemLevel), null, Fg.Text, 20), col, 0, 1, 1);
            stack.Append(top);
            any = true;
        }
        var primary = ProfileLayout.Primary(p);
        if (primary.Count > 0)
        {
            stack.Append(Label(T.ProfAttributes));
            var grid = Uniform();
            for (var i = 0; i < primary.Count; i++) grid.Attach(StatCell(primary[i].Name, primary[i].Value, primary[i].Effects, Fg.Text, 17), i % 3, i / 3, 1, 1);
            stack.Append(FightKit.Card(grid, 6, 6, 6, 6));
            any = true;
        }
        var divine = ProfileLayout.Divine(p);
        if (divine.Count > 0)
        {
            stack.Append(Label(T.ProfDivine, primary.Count > 0));
            var max = 1;
            foreach (var s in divine) if (int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > max) max = v;
            var grid = Uniform();
            for (var i = 0; i < divine.Count; i++)
            {
                var s = divine[i];
                var (name, deity) = ProfileLayout.SplitDeity(s.Name);
                var g = Gtk.Grid.New();
                UiKit.Margin(g, 4, 3, 10, 5);
                var label = UiKit.Line("", 12);
                label.SetMarkup(GLib.Functions.MarkupEscapeText(name, -1) + (deity is null ? "" : FightKit.Span(" · " + deity, Palette.TextMute, "size=\"85%\"")));
                g.Attach(label, 0, 0, 1, 1);
                var value = Gtk.Label.New(s.Value);
                value.AddCssClass(KitCss.SizeClass(12.5));
                value.AddCssClass("k-w6");
                value.AddCssClass("k-fight-num");
                UiKit.SetFg(value, Fg.GoldBright);
                value.MarginStart = UiKit.Px(8);
                g.Attach(value, 1, 0, 1, 1);
                var n = int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nv) ? nv : 0;
                var bar = UiKit.Bar((double)n / max, 3);
                bar.Widget.MarginTop = UiKit.Px(3);
                g.Attach(bar.Widget, 0, 1, 2, 1);
                if (s.Effects.Count > 0) UiKit.Tip(g, string.Join("\n", s.Effects));
                grid.Attach(g, i % 2, i / 2, 1, 1);
            }
            stack.Append(FightKit.Card(grid, 10, 8, 10, 6));
            any = true;
        }
        return (stack, any);
    }

    private static Gtk.Grid Uniform()
    {
        var g = Gtk.Grid.New();
        g.ColumnHomogeneous = true;
        g.Hexpand = true;
        return g;
    }

    private static Gtk.Widget StatCell(string label, string value, IReadOnlyList<string>? effects, Fg valueFg, double size)
    {
        var cell = UiKit.VBox();
        UiKit.Margin(cell, 8, 4, 8, 4);
        cell.Append(UiKit.Line(label, 10.5, Fg.Mute));
        var v = UiKit.Line(value, size, valueFg, 600);
        v.AddCssClass("k-fight-num");
        cell.Append(v);
        if (effects is { Count: > 0 }) UiKit.Tip(cell, string.Join("\n", effects));
        return cell;
    }

    // ------------------------------------------------------------ Skills

    public Gtk.Widget Skills(CharacterProfile p)
    {
        var stack = UiKit.VBox();
        var groups = ProfileLayout.Skills(p);
        var lang = _shell.Settings.SkillLanguage;
        var first = true;
        foreach (var kind in (string[])["active", "stigma", "passive"])
        {
            var count = 0; var subs = 0;
            foreach (var g in groups) if (g.Key.StartsWith(kind, StringComparison.Ordinal)) { count += g.Skills.Count; subs++; }
            if (count == 0) continue;
            stack.Append(Label($"{(kind == "active" ? T.SkActive : kind == "stigma" ? T.SkStigma : T.SkPassive)} ({count})", !first));
            first = false;
            var wrap = new WrapBox();
            foreach (var g in groups)
            {
                if (!g.Key.StartsWith(kind, StringComparison.Ordinal)) continue;
                var (caption, off) = g.Key switch
                {
                    "active" => (T.SkOnBar, false), "active-off" => (T.SkActiveOff, false), "stigma" => (T.SkEquipped, false),
                    "stigma-off" => (T.SkStigmaOff, false), "stigma-missing" => (T.SkStigmaMissing, true), _ => ("", false),
                };
                if (subs > 1)
                {
                    var cap = UiKit.Text($"{caption} ({g.Skills.Count})", 10.5, Fg.Dim);
                    var box = UiKit.VBox();
                    box.AddCssClass("k-fight-groupcap");
                    box.SetSizeRequest(UiKit.Px(70), UiKit.Px(Tile));
                    box.Append(cap);
                    wrap.Add(box);
                }
                foreach (var s in g.Skills) wrap.Add(SkillCell(s, off, lang));
            }
            stack.Append(FightKit.Card(wrap.Widget, 10, 10, 4, 4));
        }
        return Page(stack, p);
    }

    private Gtk.Widget SkillCell(SkillLine s, bool off, string lang)
    {
        var info = _shell.Engine?.Skills.Get(s.Id);
        var stem = info?.Icon;
        Func<Gdk.Texture?> fetch = stem is { Length: > 0 } ? () => _shell.Icons.Url("skill:" + stem, SkillBook.IconUrl(stem), 48) : () => _shell.Icons.Skill(s.Id);
        var tile = new IconTile(Tile, fetch, Palette.GoldDim, 1, s.Level > 0 ? s.Level.ToString(CultureInfo.InvariantCulture) : null, true, null, off ? 0.4 : 1);
        tile.Widget.Halign = Gtk.Align.Center;
        _tiles.Add(tile);
        var name = info?.Name(lang) is { Length: > 0 } n ? n : s.Name;
        var cell = UiKit.VBox();
        cell.SetSizeRequest(UiKit.Px(66), -1);
        cell.Hexpand = false;
        UiKit.Margin(cell, 0, 0, 4, 8);
        cell.Append(tile.Widget);
        var label = Gtk.Label.New(name);
        label.AddCssClass(KitCss.SizeClass(10));
        UiKit.SetFg(label, off ? Fg.Mute : Fg.Dim);
        label.Justify = Gtk.Justification.Center;
        label.Wrap = true;
        label.WrapMode = Pango.WrapMode.WordChar;
        label.Lines = 2;
        label.Ellipsize = Pango.EllipsizeMode.End;
        label.MaxWidthChars = 1;
        label.Hexpand = true;
        label.MarginTop = UiKit.Px(3);
        cell.Append(label);
        var body = info is not null ? SkillBook.Resolve(info, Math.Max(1, s.Level), lang, plain: true) : null;
        return UiKit.Tip(cell, UiKit.TipBody(name + (s.Level > 0 ? "  ·  " + string.Format(T.ProfLevel, s.Level) : ""), body));
    }

    // ------------------------------------------------------------ Daevanion

    public Gtk.Widget Daevanion(CharacterProfile p)
    {
        var stack = UiKit.VBox();
        var boards = ProfileLayout.Boards(p);
        var any = false;
        if (boards.Count > 0)
        {
            stack.Append(Label(T.CardDaevBoards));
            var list = UiKit.VBox();
            foreach (var b in boards)
            {
                var name = UiKit.Line(b.Name, 12.5, Fg.Text, 600);
                name.Hexpand = false;
                var g = UiKit.Row("92,*,70,42",
                    name,
                    UiKit.Bar(b.Fraction, 6).Widget,
                    Num($"{b.Open} / {b.Total}", 12, Fg.Text),
                    Num(Format.Percent(b.Fraction, 0), 11, Fg.Mute));
                UiKit.Margin(g, 0, 4, 0, 4);
                list.Append(g);
            }
            stack.Append(FightKit.Card(list, 12, 8, 12, 6));
            any = true;
        }
        var (skills, stats) = ProfileLayout.DaevanionTotals(p);
        if (skills.Count > 0)
        {
            stack.Append(Label($"{T.CardDaevSkillLevels} ({skills.Count})", any));
            var wrap = new WrapBox();
            foreach (var s in skills)
            {
                var chip = UiKit.HBox();
                chip.AddCssClass("k-fight-chip");
                var l = Split(s, 11.5);
                l.MaxWidthChars = 40;   // a chip is at most ~one line of the card
                chip.Append(l);
                wrap.Add(chip);
            }
            stack.Append(FightKit.Card(wrap.Widget, 10, 10, 4, 4));
            any = true;
        }
        if (stats.Count > 0)
        {
            stack.Append(Label($"{T.CardDaevStats} ({stats.Count})", any));
            var grid = Uniform();
            for (var i = 0; i < stats.Count; i++)
            {
                var l = Split(stats[i], 12);
                l.Hexpand = true;
                UiKit.Margin(l, 0, 2, 14, 2);
                grid.Attach(l, i % 2, i / 2, 1, 1);
            }
            stack.Append(FightKit.Card(grid, 12, 8, 12, 8));
        }
        return Page(stack, p);
    }

    private static Gtk.Label Num(string text, double size, Fg fg)
    {
        var l = FightKit.Cell(text, size, fg, 1);
        l.Hexpand = false;
        l.AddCssClass("k-fight-num");
        return l;
    }

    /// <summary>"HP +2,800" → name in text colour, "+2,800" in gold semibold.</summary>
    private static Gtk.Label Split(string text, double size)
    {
        var l = Gtk.Label.New("");
        l.Xalign = 0;
        l.AddCssClass(KitCss.SizeClass(size));
        l.Ellipsize = Pango.EllipsizeMode.End;
        l.MaxWidthChars = 1;
        var i = text.LastIndexOf(" +", StringComparison.Ordinal);
        if (i <= 0) l.SetText(text);
        else l.SetMarkup(GLib.Functions.MarkupEscapeText(text[..i], -1) + FightKit.Span(text[i..], Palette.GoldBright, "weight=\"600\""));
        return l;
    }

    // ------------------------------------------------------------ shared pieces

    private static Gtk.Widget Page(Gtk.Widget content, CharacterProfile p)
    {
        var stack = UiKit.VBox();
        UiKit.Margin(stack, 0, 2, 6, 6);
        stack.Append(content);
        var src = UiKit.Text(string.Format(T.ProfSource, p.FetchedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)), 10.5, Fg.Mute);
        UiKit.Margin(src, 2, 10, 0, 0);
        stack.Append(src);
        return stack;
    }

    private static Gtk.Label Label(string text, bool top = false)
    {
        var l = UiKit.SectionLabel(text);
        UiKit.Margin(l, 0, top ? 14 : 0, 0, 6);
        return l;
    }

    internal static string KeyOf(string url)
    {
        var slash = url.LastIndexOf('/');
        var name = slash >= 0 ? url[(slash + 1)..] : url;
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return string.Create(name.Length, name, static (span, n) =>
        {
            for (var i = 0; i < n.Length; i++) span[i] = char.IsLetterOrDigit(n[i]) || n[i] is '_' or '-' ? n[i] : '_';
        });
    }
}
