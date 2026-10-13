using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Core.GameDb;

namespace AionMeter.App.Views;

/// <summary>What the player card needs beyond a fight to draw a profile: icons (item, skill, pet, portrait), skill catalogue, the profile source.</summary>
/// <param name="Lookup">The profile known without the network for (name, server id), e.g. <see cref="MeterService.KnownProfile"/>.</param>
public sealed record ProfileDeps(IconCache Icons, SkillBook Skills, Func<string> SkillLanguage, Func<string, int, CharacterProfile?>? Lookup);

/// <summary>
/// A2M-44: the card's Profile / Skills / Daevanion pages, built in code from a <see cref="CharacterProfile"/> (questlog's armory layout in
/// the meter's gold theme). Every section without data is left out. Tiles whose icon is still downloading register in <see cref="Pending"/>;
/// the view retries them when the icon cache reports new files (CraftView's 600 ms coalescing).
/// </summary>
public sealed class ProfilePages
{
    private static UiText T => UiText.Current;
    private const double Tile = 44, Gap = 6;
    public const double GearWidth = 520;           // the gear screen's natural width; the stats sit beside it when there is room
    public const double StatsMinWidth = 360;

    private readonly ProfileDeps _deps;
    private readonly List<Func<bool>> _pending = new();
    private readonly Dictionary<string, Brush> _brushes = new();

    public ProfilePages(ProfileDeps deps) { _deps = deps; }

    /// <summary>Retries the icons still missing; drops each one that arrived. False while some are still pending.</summary>
    public bool RetryIcons()
    {
        for (var i = _pending.Count - 1; i >= 0; i--) if (_pending[i]()) _pending.RemoveAt(i);
        return _pending.Count == 0;
    }

    public void Clear() => _pending.Clear();

    private Brush Res(string key) => _brushes.TryGetValue(key, out var b) ? b : _brushes[key] = (Brush)Application.Current.FindResource(key);

    // ------------------------------------------------------------ Profile: gear screen + Arcana | attributes

    public FrameworkElement Profile(PlayerCard card, CharacterProfile p, double width)
    {
        var gear = ProfileLayout.Arrange(p.Equipment);
        var left = new StackPanel();
        if (gear.HasGear || p.Wing is not null || p.Pet is not null)
        {
            left.Children.Add(Label(T.ProfEquipment));
            left.Children.Add(Card(GearScreen(card, p, gear)));
        }
        if (gear.Arcana.Count > 0)
        {
            left.Children.Add(Label(T.ProfArcana, top: true));
            left.Children.Add(Card(TileRow(gear.Arcana)));
        }
        if (gear.Other.Count > 0)
        {
            left.Children.Add(Label(T.ProfOther, top: true));
            left.Children.Add(Card(TileRow(gear.Other)));
        }
        var right = Attributes(card, p);

        var twoCols = width >= GearWidth + 16 + StatsMinWidth && left.Children.Count > 0 && right.Children.Count > 0;
        var root = new Grid();
        if (twoCols)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GearWidth) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(left);
            Grid.SetColumn(right, 2);
            root.Children.Add(right);
        }
        else
        {
            var stack = new StackPanel();
            // the gear screen fills the width up to GearWidth, left-aligned: a Left-aligned stack would shrink its star centre to the content,
            // a fixed width would clip under the scroll bar (the width known at build time is an estimate)
            var holder = new Grid();
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = GearWidth });
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) });
            holder.Children.Add(left);
            stack.Children.Add(holder);
            if (left.Children.Count > 0) right.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(right);
            root.Children.Add(stack);
        }
        return Page(root, p);
    }

    /// <summary>
    /// The in-game character screen: armour down the left, accessories two per row down the right, the centre with the portrait (or the
    /// class emblem), wings and pet, the weapons at the bottom.
    /// </summary>
    private FrameworkElement GearScreen(PlayerCard card, CharacterProfile p, GearLayout g)
    {
        var grid = new Grid();
        foreach (var w in (double[])[Tile, 12, 0, 12, Tile, Gap, Tile])
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        var rows = ProfileLayout.ArmourSlots.Length;
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Tile + Gap) });

        for (var i = 0; i < g.Armour.Length; i++) Place(grid, SlotTile(g.Armour[i], ProfileLayout.ArmourSlots[i]), i, 0);
        for (var i = 0; i < g.Accessories.Length; i++) Place(grid, SlotTile(g.Accessories[i], ProfileLayout.AccessorySlots[i]), i / 2, i % 2 == 0 ? 4 : 6);

        var centre = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, Gap) };
        Grid.SetColumn(centre, 2);
        Grid.SetRowSpan(centre, rows);
        grid.Children.Add(centre);

        // weapons, a little larger, side by side at the bottom
        var weapons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        for (var i = 0; i < g.Weapons.Length; i++)
        {
            var t = SlotTile(g.Weapons[i], ProfileLayout.WeaponSlots[i], 52);
            t.Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0);
            weapons.Children.Add(t);
        }
        DockPanel.SetDock(weapons, Dock.Bottom);
        centre.Children.Add(weapons);

        var chips = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (p.Wing is { } wing)
        {
            var (name, enchant) = ProfileLayout.SplitWing(wing);
            var grade = ProfileLayout.GradeOf(p.WingGrade);
            chips.Children.Add(Companion(name, enchant > 0 ? "+" + enchant.ToString(CultureInfo.InvariantCulture) : null, T.ProfWing, grade, p.WingIcon));
        }
        if (p.Pet is { } pet)
        {
            var (name, level) = ProfileLayout.SplitPet(pet);
            chips.Children.Add(Companion(name, level > 0 ? string.Format(T.ProfLevel, level) : null, T.ProfPet, Grade.Unknown, p.PetIcon));
        }
        if (chips.Children.Count > 0) { DockPanel.SetDock(chips, Dock.Bottom); centre.Children.Add(chips); }

        centre.Children.Add(Portrait(card, p, 84));
        return grid;
    }

    private static void Place(Grid grid, FrameworkElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        e.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(e);
    }

    /// <summary>The character's portrait from the official page when known, else the class emblem with its glow.</summary>
    public FrameworkElement Portrait(PlayerCard card, CharacterProfile? p, double size)
    {
        var host = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new ClassGlyph { Class = card.Class, Glow = true, Width = size * 0.8, Height = size * 0.8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(glyph);
        if (p?.Portrait is not { Length: > 0 } url) return host;
        var key = "portrait:" + KeyOf(p.Ref.CharacterId);
        var img = new Image { Stretch = Stretch.UniformToFill, Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2) };
        var frame = new Border { BorderBrush = Res("GoldDim"), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(size / 2), Visibility = Visibility.Collapsed };
        host.Children.Add(img);
        host.Children.Add(frame);
        bool Try()
        {
            if (_deps.Icons.GetUrl(key, url, 128) is not { } src) return false;
            img.Source = src; glyph.Visibility = Visibility.Collapsed; frame.Visibility = Visibility.Visible;
            return true;
        }
        if (!Try()) _pending.Add(Try);
        return host;
    }

    /// <summary>Wings / pet: small icon, name, "+3" / "Lv. 2", the kind as a caption.</summary>
    private FrameworkElement Companion(string name, string? extra, string kind, Grade grade, string? iconUrl)
    {
        var tile = new GearTile(30);
        tile.Set(name, grade, null, null, IconFetch(iconUrl, 0));
        Watch(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = grade == Grade.Unknown ? Res("Text") : ItemTile.TextOf(grade), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 };
        title.Inlines.Add(name);
        if (extra is not null) title.Inlines.Add(new System.Windows.Documents.Run("  " + extra) { Foreground = Res("GoldBright") });
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = kind, FontSize = 10.5, Foreground = Res("TextMute") });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(tile);
        row.Children.Add(text);
        return row;
    }

    private FrameworkElement TileRow(List<EquipItem> items)
    {
        var wrap = new WrapPanel();
        foreach (var it in items)
        {
            var t = SlotTile(it, it.Slot);
            t.Margin = new Thickness(0, 0, Gap + 2, Gap);
            wrap.Children.Add(t);
        }
        return wrap;
    }

    private GearTile SlotTile(EquipItem? it, string slot, double size = Tile)
    {
        var t = new GearTile(size);
        if (it is null)
        {
            t.ToolTip = ProfileLayout.SlotLabel(slot);
            return t;
        }
        var grade = ProfileLayout.GradeOf(it.Grade);
        t.Set(it.Name, grade, ProfileLayout.EnchantText(it.Enchant), ProfileLayout.ExceedText(it.Exceed), IconFetch(it.Icon, it.Id));
        t.ToolTip = ItemTip(it, grade);
        ToolTipService.SetInitialShowDelay(t, 150);
        Watch(t);
        return t;
    }

    private void Watch(GearTile t) => _pending.Add(t.Retry);

    /// <summary>The official page's own icon URL when it has one (exact for skins and new items), else aion2hub's icon by item id.</summary>
    private Func<ImageSource?>? IconFetch(string? url, long id)
    {
        var icons = _deps.Icons;
        if (url is { Length: > 0 })
        {
            var key = "gear:" + KeyOf(System.IO.Path.GetFileNameWithoutExtension(url.AsSpan()).ToString());
            return () => icons.GetUrl(key, url, 48);
        }
        if (id is > 0 and <= int.MaxValue) { var item = (int)id; return () => icons.Get(item); }
        return null;
    }

    private FrameworkElement ItemTip(EquipItem it, Grade grade)
    {
        var name = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ItemTile.TextOf(grade) };
        name.Inlines.Add((it.Enchant > 0 ? "+" + it.Enchant.ToString(CultureInfo.InvariantCulture) + " " : "") + it.Name);
        if (GradeTag.Of(grade, _deps.SkillLanguage()) is { } tag) name.Inlines.Add(tag.Inline());
        var stack = new StackPanel();
        stack.Children.Add(name);
        var line = ProfileLayout.SlotLabel(it.Slot) + (it.Exceed > 0 ? " · " + string.Format(T.ProfExceed, it.Exceed) : "");
        stack.Children.Add(new TextBlock { Text = line, FontSize = 11, Foreground = Res("TextDim"), Margin = new Thickness(0, 2, 0, 0) });
        return stack;
    }

    private StackPanel Attributes(PlayerCard card, CharacterProfile p)
    {
        var stack = new StackPanel();
        if (card.Fight is not null && (p.CombatPower > 0 || p.ItemLevel > 0))
        {
            // the header shows the fight's tiles: CP and item level go here
            var top = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 0, 12) };
            if (p.CombatPower > 0) top.Children.Add(StatCell(T.ProfCombatPower, Format.Grouped(p.CombatPower), null, Res("GoldBright"), 20));
            if (p.ItemLevel > 0) top.Children.Add(StatCell(T.ProfItemLevel, Format.Grouped(p.ItemLevel), null, null, 20));
            stack.Children.Add(top);
        }
        var primary = ProfileLayout.Primary(p);
        if (primary.Count > 0)
        {
            stack.Children.Add(Label(T.ProfAttributes));
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 0) };
            foreach (var s in primary) grid.Children.Add(StatCell(s.Name, s.Value, s.Effects, null, 17));
            stack.Children.Add(Card(grid, new Thickness(6, 6, 6, 6)));
        }
        var divine = ProfileLayout.Divine(p);
        if (divine.Count > 0)
        {
            stack.Children.Add(Label(T.ProfDivine, top: primary.Count > 0));
            var max = 1;
            foreach (var s in divine) if (int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > max) max = v;
            var grid = new UniformGrid { Columns = 2 };
            foreach (var s in divine) grid.Children.Add(DivineRow(s, max));
            stack.Children.Add(Card(grid, new Thickness(10, 8, 10, 6)));
        }
        return stack;
    }

    private FrameworkElement StatCell(string label, string value, IReadOnlyList<string>? effects, Brush? valueBrush, double size)
    {
        var cell = new StackPanel { Margin = new Thickness(8, 4, 8, 4), Background = Brushes.Transparent };
        cell.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = Res("TextMute"), TextTrimming = TextTrimming.CharacterEllipsis });
        cell.Children.Add(new TextBlock
        {
            Text = value, FontSize = size, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.FindResource("NumberFont"),
            Foreground = valueBrush ?? Res("Text"),
        });
        if (effects is { Count: > 0 }) { cell.ToolTip = string.Join("\n", effects); ToolTipService.SetInitialShowDelay(cell, 150); }
        return cell;
    }

    private FrameworkElement DivineRow(StatLine s, int max)
    {
        var (name, deity) = ProfileLayout.SplitDeity(s.Name);
        var g = new Grid { Margin = new Thickness(4, 3, 10, 5), Background = Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("Text") };
        label.Inlines.Add(name);
        if (deity is not null) label.Inlines.Add(new System.Windows.Documents.Run(" · " + deity) { Foreground = Res("TextMute"), FontSize = 10.5 });
        g.Children.Add(label);
        var value = new TextBlock { Text = s.Value, FontSize = 12.5, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.FindResource("NumberFont"), Foreground = Res("GoldBright"), Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(value, 1);
        g.Children.Add(value);
        var v = int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var bar = Bar(max > 0 ? (double)v / max : 0, 3);
        bar.Margin = new Thickness(0, 3, 0, 0);
        Grid.SetRow(bar, 1); Grid.SetColumnSpan(bar, 2);
        g.Children.Add(bar);
        if (s.Effects.Count > 0) { g.ToolTip = string.Join("\n", s.Effects); ToolTipService.SetInitialShowDelay(g, 150); }
        return g;
    }

    // ------------------------------------------------------------ Skills

    /// <summary>One card per kind (actives, stigmas, passives); inside it the sub-groups follow each other, each after a caption cell
    /// ("On the bar", "Not on the bar"; "Equipped", "Learned", "Not acquired" in grey) when the kind has more than one.</summary>
    public FrameworkElement Skills(CharacterProfile p)
    {
        var stack = new StackPanel();
        var groups = ProfileLayout.Skills(p);
        foreach (var kind in (string[])["active", "stigma", "passive"])
        {
            var count = 0; var subs = 0;
            foreach (var g in groups) if (g.Key.StartsWith(kind, StringComparison.Ordinal)) { count += g.Skills.Count; subs++; }
            if (count == 0) continue;
            stack.Children.Add(Label($"{(kind == "active" ? T.SkActive : kind == "stigma" ? T.SkStigma : T.SkPassive)} ({count})", top: stack.Children.Count > 0));
            var wrap = new WrapPanel();
            foreach (var g in groups)
            {
                if (!g.Key.StartsWith(kind, StringComparison.Ordinal)) continue;
                var (caption, look) = g.Key switch
                {
                    "active" => (T.SkOnBar, SkillTile.Look.Normal),
                    "active-off" => (T.SkActiveOff, SkillTile.Look.Normal),
                    "stigma" => (T.SkEquipped, SkillTile.Look.Normal),
                    "stigma-off" => (T.SkStigmaOff, SkillTile.Look.Normal),
                    "stigma-missing" => (T.SkStigmaMissing, SkillTile.Look.Off),
                    _ => ("", SkillTile.Look.Normal),
                };
                if (subs > 1) wrap.Children.Add(Caption($"{caption} ({g.Skills.Count})"));
                foreach (var s in g.Skills) wrap.Children.Add(SkillCell(s, kind, look));
            }
            stack.Children.Add(Card(wrap, new Thickness(10, 10, 4, 4)));
        }
        return Page(stack, p);
    }

    private FrameworkElement Caption(string text) => new Border
    {
        Width = 70, Height = Tile, Margin = new Thickness(0, 0, 6, 8), BorderBrush = Res("GoldDim"), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(7, 0, 0, 0),
        Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = Res("TextDim"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
    };

    private FrameworkElement SkillCell(SkillLine s, string sub, SkillTile.Look look)
    {
        var info = _deps.Skills.Get(s.Id);
        var tile = new SkillTile { Width = Tile, Height = Tile, HorizontalAlignment = HorizontalAlignment.Center };
        var stem = info?.Icon;
        ImageSource? Fetch() => stem is { Length: > 0 } ? _deps.Icons.GetUrl("skill:" + stem, SkillBook.IconUrl(stem), 48) : null;
        var icon = Fetch();
        tile.Set(s.Id, icon, sub, look, 0, false, s.Level > 0 ? s.Level.ToString(CultureInfo.InvariantCulture) : null, null, null);
        if (icon is null && stem is { Length: > 0 })
            _pending.Add(() => { if (Fetch() is not { } img) return false; tile.SetIcon(img); return true; });
        var lang = _deps.SkillLanguage();
        var name = info?.Name(lang) is { Length: > 0 } n ? n : s.Name;
        var cell = new StackPanel { Width = 66, Margin = new Thickness(0, 0, 4, 8), Background = Brushes.Transparent };
        cell.Children.Add(tile);
        cell.Children.Add(new TextBlock
        {
            Text = name, FontSize = 10, Foreground = look == SkillTile.Look.Off ? Res("TextMute") : Res("TextDim"), TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 27, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
        });
        var tip = new StackPanel { MaxWidth = 320 };
        tip.Children.Add(new TextBlock { Text = name + (s.Level > 0 ? "  ·  " + string.Format(T.ProfLevel, s.Level) : ""), FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        if (info is not null)
            tip.Children.Add(new TextBlock { Text = SkillBook.Resolve(info, Math.Max(1, s.Level), lang, plain: true), TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) });
        cell.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(cell, 150);
        return cell;
    }

    // ------------------------------------------------------------ Daevanion

    public FrameworkElement Daevanion(CharacterProfile p, double width)
    {
        var stack = new StackPanel();
        var boards = ProfileLayout.Boards(p);
        if (boards.Count > 0)
        {
            stack.Children.Add(Label(T.CardDaevBoards));
            var list = new StackPanel();
            foreach (var b in boards) list.Children.Add(BoardRow(b));
            stack.Children.Add(Card(list, new Thickness(12, 8, 12, 6)));
        }
        var (skills, stats) = ProfileLayout.DaevanionTotals(p);
        if (skills.Count > 0)
        {
            stack.Children.Add(Label($"{T.CardDaevSkillLevels} ({skills.Count})", top: stack.Children.Count > 0));
            var wrap = new WrapPanel();
            foreach (var s in skills) wrap.Children.Add(EffectChip(s));
            stack.Children.Add(Card(wrap, new Thickness(10, 10, 4, 4)));
        }
        if (stats.Count > 0)
        {
            stack.Children.Add(Label($"{T.CardDaevStats} ({stats.Count})", top: stack.Children.Count > 0));
            var grid = new UniformGrid { Columns = width >= 700 ? 3 : 2 };
            foreach (var s in stats) grid.Children.Add(EffectLine(s));
            stack.Children.Add(Card(grid, new Thickness(12, 8, 12, 8)));
        }
        return Page(stack, p);
    }

    private FrameworkElement BoardRow(BoardBar b)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        g.Children.Add(new TextBlock { Text = b.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var bar = Bar(b.Fraction, 6);
        bar.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(bar, 1);
        g.Children.Add(bar);
        var nf = (FontFamily)Application.Current.FindResource("NumberFont");
        var count = new TextBlock { Text = $"{b.Open} / {b.Total}", FontFamily = nf, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(count, 2);
        g.Children.Add(count);
        var pct = new TextBlock { Text = Format.Percent(b.Fraction, 0), FontFamily = nf, FontSize = 11, Foreground = Res("TextMute"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pct, 3);
        g.Children.Add(pct);
        return g;
    }

    private FrameworkElement EffectChip(string text) => new Border
    {
        Background = Res("HoverFill"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
        Padding = new Thickness(7, 2, 7, 3), Margin = new Thickness(0, 0, 6, 6),
        Child = Split(text, 11.5),
    };

    private FrameworkElement EffectLine(string text) => new Border { Padding = new Thickness(0, 2, 14, 2), Child = Split(text, 12) };

    /// <summary>"HP +2,800" → name in text colour, "+2,800" in gold.</summary>
    private TextBlock Split(string text, double size)
    {
        var tb = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        var i = text.LastIndexOf(" +", StringComparison.Ordinal);
        if (i <= 0) { tb.Text = text; return tb; }
        tb.Inlines.Add(text[..i]);
        tb.Inlines.Add(new System.Windows.Documents.Run(text[i..]) { Foreground = Res("GoldBright"), FontWeight = FontWeights.SemiBold });
        return tb;
    }

    // ------------------------------------------------------------ shared pieces

    /// <summary>The page's content plus the source line ("official character page, fetched 10-13 10:38").</summary>
    private FrameworkElement Page(FrameworkElement content, CharacterProfile p)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 2, 6, 6) };
        stack.Children.Add(content);
        stack.Children.Add(new TextBlock
        {
            Text = string.Format(T.ProfSource, p.FetchedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)),
            FontSize = 10.5, Foreground = Res("TextMute"), Margin = new Thickness(2, 12, 0, 0),
        });
        return stack;
    }

    private TextBlock Label(string text, bool top = false) => new()
    {
        Text = text, Style = (Style)Application.Current.FindResource("SectionLabel"), Margin = new Thickness(2, top ? 14 : 0, 0, 6),
    };

    private Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = SkillTile.Frozen(0xFF, 0xFF, 0xFF, 0x0A), BorderBrush = SkillTile.Frozen(0xFF, 0xFF, 0xFF, 0x14), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(12, 10, 12, 4), Child = child,
    };

    /// <summary>Track + gold fill as two star columns (no SizeChanged handler: nothing re-lays out inside the layout pass).</summary>
    private FrameworkElement Bar(double fraction, double height)
    {
        var f = Math.Clamp(fraction, 0, 1);
        var g = new Grid { Height = height };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(height / 2), Background = Res("RowTrack") };
        Grid.SetColumnSpan(track, 2);
        g.Children.Add(track);
        if (f > 0) g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Res("Gold") });
        return g;
    }

    /// <summary>Icon cache keys allow letters, digits, '_' and '-' only.</summary>
    private static string KeyOf(string s)
    {
        Span<char> buf = s.Length <= 128 ? stackalloc char[s.Length] : new char[s.Length];
        var n = 0;
        foreach (var c in s) if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-') buf[n++] = c;
        return new string(buf[..n]);
    }
}
