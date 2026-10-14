using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Core.GameDb;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// A2M-44 player card pages on Linux (WPF ProfilePages): Profile (equipment tiles, Arcana, other, attributes, divine stats), Skills (actives,
/// stigmas, passives by group) and Daevanion (boards, skill levels, stats) from a <see cref="CharacterProfile"/> (characters/&lt;key&gt;/profile.json
/// or the armory cache, through the viewer engine's KnownProfile). Sections without data are left out; icons that are still downloading
/// are retried by <see cref="RetryIcons"/> when the icon provider reports new files.
/// </summary>
public sealed class ProfilePages
{
    private static UiText T => UiText.Current;
    private const double Tile = 44;
    private readonly AppShell _shell;
    private readonly List<Func<bool>> _pending = new();

    public ProfilePages(AppShell shell) { _shell = shell; }

    public bool RetryIcons()
    {
        for (var i = _pending.Count - 1; i >= 0; i--) if (_pending[i]()) _pending.RemoveAt(i);
        return _pending.Count == 0;
    }

    public void Clear() => _pending.Clear();

    // ------------------------------------------------------------ Profile

    public Control Profile(PlayerCard card, CharacterProfile p, double width)
    {
        var gear = ProfileLayout.Arrange(p.Equipment);
        var left = new StackPanel();
        if (gear.HasGear || p.Wing is not null || p.Pet is not null)
        {
            left.Children.Add(Label(T.ProfEquipment));
            var g = new StackPanel();
            var armour = new WrapPanel();
            for (var i = 0; i < gear.Armour.Length; i++) armour.Children.Add(GearTile(gear.Armour[i], ProfileLayout.ArmourSlots[i]));
            for (var i = 0; i < gear.Weapons.Length; i++) armour.Children.Add(GearTile(gear.Weapons[i], ProfileLayout.WeaponSlots[i]));
            g.Children.Add(armour);
            var acc = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            for (var i = 0; i < gear.Accessories.Length; i++) acc.Children.Add(GearTile(gear.Accessories[i], ProfileLayout.AccessorySlots[i]));
            g.Children.Add(acc);
            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (p.Wing is { } wing)
            {
                var (name, enchant) = ProfileLayout.SplitWing(wing);
                chips.Children.Add(Companion(name + (enchant > 0 ? " +" + enchant.ToString(CultureInfo.InvariantCulture) : ""), T.ProfWing, ProfileLayout.GradeOf(p.WingGrade), p.WingIcon));
            }
            if (p.Pet is { } pet)
            {
                var (name, level) = ProfileLayout.SplitPet(pet);
                chips.Children.Add(Companion(name + (level > 0 ? " · " + string.Format(T.ProfLevel, level) : ""), T.ProfPet, Grade.Unknown, p.PetIcon));
            }
            if (chips.Children.Count > 0) g.Children.Add(chips);
            left.Children.Add(Card(g));
        }
        if (gear.Arcana.Count > 0) { left.Children.Add(Label(T.ProfArcana, true)); left.Children.Add(Card(TileRow(gear.Arcana))); }
        if (gear.Other.Count > 0) { left.Children.Add(Label(T.ProfOther, true)); left.Children.Add(Card(TileRow(gear.Other))); }
        var right = Attributes(card, p);
        var root = new StackPanel();
        if (width >= 900 && left.Children.Count > 0 && right.Children.Count > 0)
        {
            var two = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
            two.Place(left, 0, 0);
            two.Place(right, 0, 2);
            root.Children.Add(two);
        }
        else
        {
            root.Children.Add(left);
            if (left.Children.Count > 0) right.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(right);
        }
        return Page(root, p);
    }

    private Control TileRow(List<EquipItem> items)
    {
        var w = new WrapPanel();
        foreach (var it in items) w.Children.Add(GearTile(it, it.Slot));
        return w;
    }

    private Control GearTile(EquipItem? it, string slot)
    {
        var grade = it is null ? Grade.Unknown : ProfileLayout.GradeOf(it.Grade);
        var box = new Border
        {
            Width = Tile, Height = Tile, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.5),
            BorderBrush = it is null ? Visuals.Line : Visuals.GradeLine(grade), Background = Visuals.Solid(0xFF161C2C), ClipToBounds = true,
        };
        var g = new Grid();
        box.Child = g;
        if (it is null)
        {
            g.Children.Add(new TextBlock { Text = ProfileLayout.SlotLabel(slot), FontSize = 8.5, Foreground = Visuals.TextMute, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2) });
            return UiKit.Tip(box, ProfileLayout.SlotLabel(slot));
        }
        var image = new Image { Stretch = Stretch.UniformToFill };
        g.Children.Add(image);
        if (Fetch() is { } bmp) image.Source = bmp;
        else if (it.Icon is { Length: > 0 } || it.Id > 0) _pending.Add(() => { if (Fetch() is not { } b) return false; image.Source = b; return true; });
        if (ProfileLayout.EnchantText(it.Enchant) is { } en)
            g.Children.Add(new Border
            {
                Background = Visuals.Solid(0xCC000000), CornerRadius = new CornerRadius(0, 0, 4, 0), Padding = new Thickness(3, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = en, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Visuals.GoldBright, FontFamily = Visuals.Number },
            });
        var line = ProfileLayout.SlotLabel(it.Slot) + (it.Exceed > 0 ? " · " + string.Format(T.ProfExceed, it.Exceed) : "");
        var tip = new StackPanel { MaxWidth = 320 };
        tip.Children.Add(new TextBlock { Text = it.Name + (it.Enchant > 0 ? " +" + it.Enchant.ToString(CultureInfo.InvariantCulture) : ""), FontWeight = FontWeight.SemiBold, Foreground = Visuals.GradeText(grade), TextWrapping = TextWrapping.Wrap });
        tip.Children.Add(new TextBlock { Text = line, FontSize = 11.5, Foreground = Visuals.TextDim });
        return UiKit.Tip(box, tip);

        Bitmap? Fetch() => it.Icon is { Length: > 0 } url ? _shell.Icons.Url("gear:" + KeyOf(url), url) : it.Id > 0 && it.Id < int.MaxValue ? _shell.Icons.Item((int)it.Id) : null;
    }

    private Control Companion(string name, string kind, Grade grade, string? iconUrl)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var image = new Image { Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        if (iconUrl is { Length: > 0 })
        {
            if (_shell.Icons.Url("gear:" + KeyOf(iconUrl), iconUrl) is { } b) image.Source = b;
            else _pending.Add(() => { if (_shell.Icons.Url("gear:" + KeyOf(iconUrl), iconUrl) is not { } bb) return false; image.Source = bb; return true; });
            row.Children.Add(image);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = kind, FontSize = 10, Foreground = Visuals.TextMute });
        text.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = grade == Grade.Unknown ? Visuals.Text : Visuals.GradeText(grade), FontWeight = FontWeight.SemiBold });
        row.Children.Add(text);
        return new Border { Background = Visuals.HoverFill, BorderBrush = Visuals.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4), Margin = new Thickness(0, 0, 8, 4), Child = row };
    }

    private StackPanel Attributes(PlayerCard card, CharacterProfile p)
    {
        var stack = new StackPanel();
        if (card.Fight is not null && (p.CombatPower > 0 || p.ItemLevel > 0))
        {
            var top = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 12) };
            if (p.CombatPower > 0) top.Children.Add(StatCell(T.ProfCombatPower, Format.Grouped(p.CombatPower), null, Visuals.GoldBright, 20));
            if (p.ItemLevel > 0) top.Children.Add(StatCell(T.ProfItemLevel, Format.Grouped(p.ItemLevel), null, null, 20));
            stack.Children.Add(top);
        }
        var primary = ProfileLayout.Primary(p);
        if (primary.Count > 0)
        {
            stack.Children.Add(Label(T.ProfAttributes));
            var grid = new UniformGrid { Columns = 3 };
            foreach (var s in primary) grid.Children.Add(StatCell(s.Name, s.Value, s.Effects, null, 17));
            stack.Children.Add(Card(grid, new Thickness(6)));
        }
        var divine = ProfileLayout.Divine(p);
        if (divine.Count > 0)
        {
            stack.Children.Add(Label(T.ProfDivine, primary.Count > 0));
            var max = 1;
            foreach (var s in divine) if (int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > max) max = v;
            var grid = new UniformGrid { Columns = 2 };
            foreach (var s in divine)
            {
                var (name, deity) = ProfileLayout.SplitDeity(s.Name);
                var g = new Grid { Margin = new Thickness(4, 3, 10, 5), ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Background = Brushes.Transparent };
                var label = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Visuals.Text };
                label.Inlines = [new Run(name)];
                if (deity is not null) label.Inlines.Add(new Run(" · " + deity) { Foreground = Visuals.TextMute, FontSize = 10.5 });
                g.Children.Add(label);
                g.Place(new TextBlock { Text = s.Value, FontSize = 12.5, FontWeight = FontWeight.SemiBold, FontFamily = Visuals.Number, Foreground = Visuals.GoldBright, Margin = new Thickness(8, 0, 0, 0) }, 0, 1);
                var n = int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nv) ? nv : 0;
                var bar = UiKit.Bar((double)n / max, 3);
                bar.Margin = new Thickness(0, 3, 0, 0);
                g.Place(bar, 1, 0, 2);
                if (s.Effects.Count > 0) UiKit.Tip(g, string.Join("\n", s.Effects));
                grid.Children.Add(g);
            }
            stack.Children.Add(Card(grid, new Thickness(10, 8, 10, 6)));
        }
        return stack;
    }

    private static Control StatCell(string label, string value, IReadOnlyList<string>? effects, IBrush? valueBrush, double size)
    {
        var cell = new StackPanel { Margin = new Thickness(8, 4), Background = Brushes.Transparent };
        cell.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = Visuals.TextMute, TextTrimming = TextTrimming.CharacterEllipsis });
        cell.Children.Add(new TextBlock { Text = value, FontSize = size, FontWeight = FontWeight.SemiBold, FontFamily = Visuals.Number, Foreground = valueBrush ?? Visuals.Text });
        if (effects is { Count: > 0 }) UiKit.Tip(cell, string.Join("\n", effects));
        return cell;
    }

    // ------------------------------------------------------------ Skills

    public Control Skills(CharacterProfile p)
    {
        var stack = new StackPanel();
        var groups = ProfileLayout.Skills(p);
        var lang = _shell.Settings.ResolvedNamesLanguage;
        foreach (var kind in (string[])["active", "stigma", "passive"])
        {
            var count = 0; var subs = 0;
            foreach (var g in groups) if (g.Key.StartsWith(kind, StringComparison.Ordinal)) { count += g.Skills.Count; subs++; }
            if (count == 0) continue;
            stack.Children.Add(Label($"{(kind == "active" ? T.SkActive : kind == "stigma" ? T.SkStigma : T.SkPassive)} ({count})", stack.Children.Count > 0));
            var wrap = new WrapPanel();
            foreach (var g in groups)
            {
                if (!g.Key.StartsWith(kind, StringComparison.Ordinal)) continue;
                var (caption, off) = g.Key switch
                {
                    "active" => (T.SkOnBar, false), "active-off" => (T.SkActiveOff, false), "stigma" => (T.SkEquipped, false),
                    "stigma-off" => (T.SkStigmaOff, false), "stigma-missing" => (T.SkStigmaMissing, true), _ => ("", false),
                };
                if (subs > 1)
                    wrap.Children.Add(new Border
                    {
                        Width = 70, Height = Tile, Margin = new Thickness(0, 0, 6, 8), BorderBrush = Visuals.GoldDim, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(7, 0, 0, 0),
                        Child = new TextBlock { Text = $"{caption} ({g.Skills.Count})", FontSize = 10.5, Foreground = Visuals.TextDim, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                    });
                foreach (var s in g.Skills) wrap.Children.Add(SkillCell(s, off, lang));
            }
            stack.Children.Add(Card(wrap, new Thickness(10, 10, 4, 4)));
        }
        return Page(stack, p);
    }

    private Control SkillCell(SkillLine s, bool off, string lang)
    {
        var info = _shell.Engine?.Skills.Get(s.Id);
        var stem = info?.Icon;
        var tile = new Grid { Width = Tile, Height = Tile, HorizontalAlignment = HorizontalAlignment.Center };
        var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Visuals.GoldDim, Background = Visuals.Solid(0xFF161C2C), ClipToBounds = true, Opacity = off ? 0.4 : 1 };
        var image = new Image { Stretch = Stretch.UniformToFill };
        frame.Child = image;
        tile.Children.Add(frame);
        Bitmap? Fetch() => stem is { Length: > 0 } ? _shell.Icons.Url("skill:" + stem, SkillBook.IconUrl(stem), 48) : _shell.Icons.Skill(s.Id);
        if (Fetch() is { } bmp) image.Source = bmp;
        else _pending.Add(() => { if (Fetch() is not { } b) return false; image.Source = b; return true; });
        if (s.Level > 0)
            tile.Children.Add(new Border
            {
                Background = Visuals.Solid(0xCC000000), CornerRadius = new CornerRadius(4, 0, 6, 0), Padding = new Thickness(3, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = s.Level.ToString(CultureInfo.InvariantCulture), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Visuals.GoldBright, FontFamily = Visuals.Number },
            });
        var name = info?.Name(lang) is { Length: > 0 } n ? n : s.Name;
        var cell = new StackPanel { Width = 66, Margin = new Thickness(0, 0, 4, 8), Background = Brushes.Transparent };
        cell.Children.Add(tile);
        cell.Children.Add(new TextBlock { Text = name, FontSize = 10, Foreground = off ? Visuals.TextMute : Visuals.TextDim, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 27, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
        var body = info is not null ? SkillBook.Resolve(info, Math.Max(1, s.Level), lang, plain: true) : null;
        return UiKit.Tip(cell, UiKit.TipBody(name + (s.Level > 0 ? "  ·  " + string.Format(T.ProfLevel, s.Level) : ""), body));
    }

    // ------------------------------------------------------------ Daevanion

    public Control Daevanion(CharacterProfile p, double width)
    {
        var stack = new StackPanel();
        var boards = ProfileLayout.Boards(p);
        if (boards.Count > 0)
        {
            stack.Children.Add(Label(T.CardDaevBoards));
            var list = new StackPanel();
            foreach (var b in boards)
            {
                var g = new Grid { Margin = new Thickness(0, 4), ColumnDefinitions = new ColumnDefinitions("92,*,70,42") };
                g.Children.Add(new TextBlock { Text = b.Name, FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Visuals.Text });
                var bar = UiKit.Bar(b.Fraction, 6);
                bar.VerticalAlignment = VerticalAlignment.Center;
                g.Place(bar, 0, 1);
                g.Place(new TextBlock { Text = $"{b.Open} / {b.Total}", FontFamily = Visuals.Number, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Visuals.Text }, 0, 2);
                g.Place(new TextBlock { Text = Format.Percent(b.Fraction, 0), FontFamily = Visuals.Number, FontSize = 11, Foreground = Visuals.TextMute, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 0, 3);
                list.Children.Add(g);
            }
            stack.Children.Add(Card(list, new Thickness(12, 8, 12, 6)));
        }
        var (skills, stats) = ProfileLayout.DaevanionTotals(p);
        if (skills.Count > 0)
        {
            stack.Children.Add(Label($"{T.CardDaevSkillLevels} ({skills.Count})", stack.Children.Count > 0));
            var wrap = new WrapPanel();
            foreach (var s in skills)
                wrap.Children.Add(new Border { Background = Visuals.HoverFill, BorderBrush = Visuals.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 0, 6, 6), Child = Split(s, 11.5) });
            stack.Children.Add(Card(wrap, new Thickness(10, 10, 4, 4)));
        }
        if (stats.Count > 0)
        {
            stack.Children.Add(Label($"{T.CardDaevStats} ({stats.Count})", stack.Children.Count > 0));
            var grid = new UniformGrid { Columns = width >= 700 ? 3 : 2 };
            foreach (var s in stats) grid.Children.Add(new Border { Padding = new Thickness(0, 2, 14, 2), Child = Split(s, 12) });
            stack.Children.Add(Card(grid, new Thickness(12, 8)));
        }
        return Page(stack, p);
    }

    /// <summary>"HP +2,800" → name in text colour, "+2,800" in gold.</summary>
    private static TextBlock Split(string text, double size)
    {
        var tb = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Visuals.Text };
        var i = text.LastIndexOf(" +", StringComparison.Ordinal);
        if (i <= 0) { tb.Text = text; return tb; }
        tb.Inlines = [new Run(text[..i]), new Run(text[i..]) { Foreground = Visuals.GoldBright, FontWeight = FontWeight.SemiBold }];
        return tb;
    }

    // ------------------------------------------------------------ shared pieces

    private static Control Page(Control content, CharacterProfile p)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 2, 6, 6) };
        stack.Children.Add(content);
        stack.Children.Add(new TextBlock { Text = string.Format(T.ProfSource, p.FetchedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)), FontSize = 10.5, Foreground = Visuals.TextMute, Margin = new Thickness(2, 10, 0, 0) });
        return stack;
    }

    private static TextBlock Label(string text, bool top = false) => UiKit.SectionLabel(text, new Thickness(0, top ? 14 : 0, 0, 6));

    private static Border Card(Control child, Thickness? padding = null) => new()
    {
        Background = Visuals.SectionFill, BorderBrush = Visuals.SectionLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(10), Child = child,
    };

    private static string KeyOf(string url)
    {
        var slash = url.LastIndexOf('/');
        var name = slash >= 0 ? url[(slash + 1)..] : url;
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }
}
