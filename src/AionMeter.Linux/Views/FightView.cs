using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Linux.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux.Views;

/// <summary>
/// The fight breakdown / player card (WPF BreakdownView, A2M-43/44) as the card's "fight" tab: party list, stat tiles, DPS (timeline +
/// skills), Accuracy, Rotation, Defense, and the selected player's Profile / Skills / Daevanion when the meter knows a profile for them.
/// Saved fights ("h:&lt;file&gt;") are read from fights/ by the viewer engine; live and session fights come from the daemon's `fight-detail`
/// (asked again each second while the fight runs). Wide (pop-out ≥ 760 px): party list on the left; narrow: a "Party ▾" chip.
/// </summary>
public sealed class FightView : HostedView
{
    private static UiText T => UiText.Current;
    private const double NarrowBelow = 760, CompactBelow = 700, SideWidth = 190;
    private static SkillOrder _lastSkillOrder = SkillOrder.Default, _lastAccuracyOrder = SkillOrder.Default;

    private string _id = "live";          // what was asked: "live", "s:…", "h:…"
    private string _asked = "live";
    private uint _actor;
    private FightRecord? _record;
    private bool _live, _shown, _waiting;
    private string _tab = "dps";
    private SkillOrder _skillOrder = _lastSkillOrder, _accuracyOrder = _lastAccuracyOrder;
    private readonly DispatcherTimer _timer;
    private readonly ProfilePages _pages;
    private readonly Dictionary<uint, CharacterProfile?> _profiles = new();
    private CharacterProfile? _profile;
    private PlayerCard? _card;

    private readonly Grid _layoutRoot;
    private readonly Border _side;
    private readonly StackPanel _party, _tiles, _tabStrip;
    private readonly TextBlock _fightTitle, _fightMeta, _meta, _status;
    private readonly ClassGlyph _glyph;
    private readonly ContentControl _page;
    private readonly Border _partyChip, _fightsChip, _sideFights;
    private readonly TimelineChart _chart = new() { Height = 160, Margin = new Thickness(0, 0, 0, 10) };
    private readonly RotationStrip _rotation = new();
    private bool _narrow, _compact;   // false/false so the first ApplyWidth lays out either way

    public FightView(AppShell shell) : base(shell, "fight", "…", "meter")
    {
        _pages = new ProfilePages(shell);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { if (_live && _shown && !_waiting) Request(); };

        _fightsChip = UiKit.Chip(T.Fights + " ▾", AskFights, T.TipFightPicker);
        _fightsChip.Margin = new Thickness(0, 0, 6, 0);
        _partyChip = UiKit.Chip(char.ToUpper(T.Party[0], T.Culture) + T.Party[1..] + " ▾", ShowPartyMenu);
        HeaderRight.Children.Add(_partyChip);
        HeaderRight.Children.Add(_fightsChip);

        // side panel (wide): fights chip, fight title + meta, party list
        var side = new StackPanel { Margin = new Thickness(12, 4, 8, 8) };
        _sideFights = UiKit.Chip(T.Fights + " ▾", AskFights, T.TipFightPicker);
        _sideFights.HorizontalAlignment = HorizontalAlignment.Left;
        _sideFights.Margin = new Thickness(0, 0, 0, 10);
        side.Children.Add(_sideFights);
        side.Children.Add(_fightTitle = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Text, TextTrimming = TextTrimming.CharacterEllipsis });
        side.Children.Add(_fightMeta = new TextBlock { FontSize = 10.5, FontFamily = Visuals.Number, Foreground = Visuals.TextMute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        _party = new StackPanel();
        side.Children.Add(_party);
        _side = new Border { Width = SideWidth, BorderBrush = Visuals.Line, BorderThickness = new Thickness(0, 0, 1, 0), Background = Visuals.Bg1, Child = new ScrollViewer { Content = side, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };

        // main column: player line, tiles, tab strip, page
        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(14, 0, 8, 0) };
        var who = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        who.Children.Add(_glyph = new ClassGlyph { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        who.Children.Add(_meta = new TextBlock { FontSize = 11.5, Foreground = Visuals.TextDim, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 900 });
        main.Place(who, 0, 0);
        _tiles = new StackPanel { Orientation = Orientation.Horizontal };
        main.Place(new ScrollViewer { Content = _tiles, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 6) }, 1, 0);
        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        main.Place(_tabStrip, 2, 0);
        // margin on the content, not ScrollViewer.Padding: with the padding the page measured 8 px wider than the viewport (right column clipped)
        _page = new ContentControl { Margin = new Thickness(0, 0, 14, 8) };
        main.Place(new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 3, 0);
        _status = new TextBlock { FontSize = 12.5, Foreground = Visuals.TextMute, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) };
        main.Place(_status, 3, 0);

        _layoutRoot = new Grid { ColumnDefinitions = new ColumnDefinitions($"{SideWidth},*") };
        _layoutRoot.Place(_side, 0, 0);
        _layoutRoot.Place(main, 0, 1);
        SetBody(_layoutRoot);
        ApplyWidth(560);

        shell.Daemon.FightDetail += OnDetail;
        shell.Daemon.Fights += OnFights;
        shell.Daemon.Error += OnError;
        shell.Icons.Loaded += OnIcons;
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) ApplyWidth(e.NewSize.Width); };
    }

    /// <summary>The fight id this view shows ("s:…"/"h:…" once resolved) and the selected player, for a pop-out copy.</summary>
    public string FightId => _id;
    public uint Actor => _actor;

    /// <summary>Shows a fight ("live", "s:&lt;guid&gt;", "h:&lt;file&gt;") on <paramref name="actor"/> (0: you, else the top of the party),
    /// on <paramref name="tab"/> when given ("profile" for a Top DPS row: falls back to DPS when the player has no known profile).</summary>
    public void SetFight(string id, uint actor, string? tab = null)
    {
        if (_cardOnly)
        {
            _side.IsVisible = !_narrow;
            _layoutRoot.ColumnDefinitions[0].Width = new GridLength(_narrow ? 0 : SideWidth);
            _partyChip.IsVisible = _narrow;
            _fightsChip.IsVisible = _narrow;
        }
        _cardOnly = false;
        _cardStatus = null;
        if (tab is not null) _tab = tab;
        _id = _asked = id.Length == 0 ? "live" : id;
        _actor = actor;
        _record = null;
        _profiles.Clear();
        _profile = null;
        _status.Text = T.FightLoading;
        _status.IsVisible = true;
        _page.IsVisible = false;
        Load();
    }

    // ------------------------------------------------------------ player card without a fight (A2M-44, Top DPS ranking rows)

    private bool _cardOnly;
    private string? _cardStatus;

    /// <summary>The player card alone: header, CP / item level / rank / seen tiles, Profile / Skills / Daevanion when the profile has them.</summary>
    public void SetCard(PlayerCard card, string? status)
    {
        _cardOnly = true;
        _record = null;
        _live = false;
        _id = _asked = "";
        _profiles.Clear();
        _pages.Clear();
        _tab = "profile";
        UpdateCard(card, status);
    }

    /// <summary>The armory answered (or failed): the card with its profile, or the status line (null = none).</summary>
    public void UpdateCard(PlayerCard card, string? status)
    {
        if (!_cardOnly) return;
        if (!ReferenceEquals(card.Profile, _profile)) _pages.Clear();
        _card = card;
        _profile = card.Profile;
        _cardStatus = status;
        var tabs = CardLayout.Tabs(false, _profile);
        if (!(_tab is "profile" && tabs.Contains(CardTab.Profile) || _tab is "skills" && tabs.Contains(CardTab.Skills) || _tab is "daevanion" && tabs.Contains(CardTab.Daevanion)))
            _tab = tabs.Count > 0 ? tabs[0].ToString().ToLowerInvariant() : "profile";
        RenderCard();
    }

    public PlayerCard? Card => _cardOnly ? _card : null;

    /// <summary>The armory cached a newer profile for someone in this fight (Top DPS "Load profile"): look the profiles up again.</summary>
    public void RefreshProfile()
    {
        if (_cardOnly || _record is null) return;
        _profiles.Clear();
        Render();
    }

    private void RenderCard()
    {
        if (_card is not { } c) return;
        _side.IsVisible = false;
        _layoutRoot.ColumnDefinitions[0].Width = new GridLength(0);
        _partyChip.IsVisible = false;
        _fightsChip.IsVisible = false;
        Title = c.Name;
        _glyph.Class = c.Class;
        _meta.Text = CardLayout.Meta(T.ClassName(c.Class), c) + (c.Title is { Length: > 0 } title ? $" · “{title}”" : "");
        _tiles.Children.Clear();
        foreach (var (tile, value) in CardLayout.Tiles(c, DateTimeOffset.UtcNow))
            _tiles.Children.Add(UiKit.StatTile(tile switch { CardTile.CombatPower => T.TileCp, CardTile.ItemLevel => T.TileItemLevel, CardTile.Rank => T.TileRank, _ => T.TileSeen },
                tile == CardTile.Seen && value != "now" ? string.Format(T.TileSeenAgo, value) : value, null, tile == CardTile.CombatPower ? Visuals.GoldBright : null));
        _tabStrip.Children.Clear();
        var tabs = CardLayout.Tabs(false, _profile);
        if (tabs.Contains(CardTab.Profile)) AddTab("profile", T.TabProfile, RenderCard);
        if (tabs.Contains(CardTab.Skills)) AddTab("skills", T.TabSkills, RenderCard);
        if (tabs.Contains(CardTab.Daevanion)) AddTab("daevanion", T.TabDaevanion, RenderCard);
        Control? page = _profile is not { } p ? null : _tab switch
        {
            "skills" when tabs.Contains(CardTab.Skills) => _pages.Skills(p),
            "daevanion" when tabs.Contains(CardTab.Daevanion) => _pages.Daevanion(p, Bounds.Width),
            _ when tabs.Contains(CardTab.Profile) => _pages.Profile(c, p, Bounds.Width),
            _ => null,
        };
        _page.Content = page;
        _page.IsVisible = page is not null;
        _status.Text = _cardStatus ?? (page is null ? string.Format(T.TopProfileMissing, c.Name, c.Server ?? "?") : "");
        _status.IsVisible = page is null;
    }

    public override IHostedView PopOut()
    {
        var copy = new FightView(Shell);
        if (_cardOnly && _card is { } card) { copy.SetCard(card, _cardStatus); copy._tab = _tab; return copy; }
        copy.SetFight(_id, _actor);
        copy._tab = _tab;
        return copy;
    }

    public override void Shown()
    {
        _shown = true;
        if (_cardOnly) { RenderCard(); return; }
        if (_record is null || _live) Load();
        _timer.Start();
    }

    public override void Hidden()
    {
        _shown = false;
        _timer.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        if (_cardOnly) RenderCard();
        else if (_record is not null) Render();
    }

    private void Load()
    {
        if (_id.StartsWith("h:", StringComparison.Ordinal))
        {
            Shell.WhenEngine(e =>
            {
                var file = _id[2..];
                foreach (var h in e.History.List())
                {
                    if (h.FileName != file) continue;
                    if (e.History.Load(h.Path) is { } rec) { Show(rec, false); return; }
                    break;
                }
                if (Shell.Daemon.Connected) Request();   // not on this disk (yet): the daemon may know it
                else NotFound();
            });
            return;
        }
        if (!Shell.Daemon.Connected) { NotFound(); return; }
        Request();
    }

    private void Request()
    {
        _waiting = true;
        _asked = _id;
        Shell.Daemon.Send("fight-detail", "id", _id);
    }

    private void OnDetail(FightDetailReply r)
    {
        if (_cardOnly || r.Asked != _asked) return;
        _waiting = false;
        if (_id == "live") _id = r.Id;   // stay on that fight when the next pull starts (WPF keeps the segment it opened)
        Show(r.Record, r.Live);
    }

    private void OnError(string message)
    {
        if (!_waiting || !message.StartsWith("no fight", StringComparison.Ordinal)) return;
        _waiting = false;
        if (_record is null) NotFound();
        else _live = false;
    }

    private void NotFound()
    {
        _status.Text = T.FightGone;
        _status.IsVisible = true;
        _page.IsVisible = false;
    }

    private void Show(FightRecord record, bool live)
    {
        _record = record;
        _live = live;
        var combatants = record.Summary.Combatants;
        if (_actor == 0 || !Has(_actor))
        {
            _actor = combatants.Count > 0 ? combatants[0].ActorId : 0;
            foreach (var c in combatants) if (c.IsSelf) { _actor = c.ActorId; break; }
        }
        _status.IsVisible = false;
        _page.IsVisible = true;
        Render();
    }

    private bool Has(uint actor)
    {
        foreach (var c in _record!.Summary.Combatants) if (c.ActorId == actor) return true;
        return false;
    }

    private CombatantDetail? Detail(uint actor)
    {
        foreach (var d in _record!.Details) if (d.ActorId == actor) return d;
        return null;
    }

    private void Select(uint actor)
    {
        if (actor == _actor || _record is null) return;
        _actor = actor;
        Render();
    }

    private void OnIcons()
    {
        if (_cardOnly && _shown) { _pages.RetryIcons(); return; }
        if (!_shown || _record is null) return;
        if (_tab is "profile" or "skills" or "daevanion") _pages.RetryIcons();
        else Render();
    }

    // ------------------------------------------------------------ layout

    private void ApplyWidth(double width)
    {
        var narrow = width < NarrowBelow;
        var compact = width - (narrow ? 0 : SideWidth) < CompactBelow;
        if (narrow == _narrow && compact == _compact) return;
        _narrow = narrow;
        _compact = compact;
        _side.IsVisible = !narrow;
        _layoutRoot.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : SideWidth);
        _partyChip.IsVisible = narrow;
        _fightsChip.IsVisible = narrow;
        if (_cardOnly) RenderCard();
        else if (_record is not null) Render();
    }

    // ------------------------------------------------------------ render

    private void Render()
    {
        if (_record is not { } rec) return;
        var summary = rec.Summary;
        var d = Detail(_actor);
        _fightTitle.Text = summary.Title;
        var meta = $"{summary.StartedAt.ToLocalTime().ToString("dd MMM HH:mm", T.Culture)} · {Format.Clock(summary.CombatMs)} · {Format.Compact(summary.PartyDps)}/s {T.PartyDpsSuffix}"
                   + (summary.MostDamageUnsent ? $"\n{T.DamageUnsentLine(summary)}" : "")
                   + (summary.Zone is { } z ? $"\n{Shell.Engine?.DisplayZone(z) ?? z}" : "")
                   + (summary.Id != Guid.Empty && Shell.Engine?.AbyssLogsNoteFor(summary.Id) is { } note ? "\n" + note : "");
        _fightMeta.Text = meta;
        BuildParty(summary);
        if (d is null) { _status.Text = T.FightGone; _status.IsVisible = true; _page.IsVisible = false; return; }

        Title = T.CombatantLabel(d.ActorId, d.Name, d.Class);
        _glyph.Class = d.Class;
        var server = d.ServerId != 0 && Shell.Engine is { } e ? $" · {e.Data.ServerName(d.ServerId)}" : "";
        var gear = (d.GearScore > 0 ? $" · {T.Chat.GearScore} {d.GearScore}" : "") + (d.CombatPower > 0 ? $" · {T.Chat.CombatPower} {Format.Grouped(d.CombatPower)}" : "");
        _meta.Text = $"{T.ClassName(d.Class)}{server}{gear} · {summary.Title}{(d.IsSelf ? $" · {T.You}" : "")}" + (_narrow ? "\n" + meta.Replace("\n", " · ") : "");

        var place = 0;
        if (d.ActorId != Combatant.UnknownSummonsId)
            for (var i = 0; i < summary.Combatants.Count; i++) if (summary.Combatants[i].ActorId == d.ActorId) { place = i + 1; break; }
        _tiles.Children.Clear();
        var (rankBg, rankFg, rankBorder) = Visuals.Rank(place);
        var placeTile = UiKit.StatTile(T.TilePlace, place > 0 ? $"{place} / {summary.PlayerCount}" : "—", null, place is >= 1 and <= 3 ? rankFg : Visuals.Text);
        if (place is >= 1 and <= 3) { placeTile.Background = rankBg; placeTile.BorderBrush = rankBorder; if (placeTile.Child is StackPanel { Children: [TextBlock l, ..] }) l.Foreground = rankFg; }
        _tiles.Children.Add(placeTile);
        _tiles.Children.Add(UiKit.StatTile(T.TileDamage, Format.Compact(d.Damage)));
        _tiles.Children.Add(UiKit.StatTile(T.TileDps, Format.Compact(d.Dps), null, Visuals.GoldBright));
        _tiles.Children.Add(UiKit.StatTile(T.TileContrib, Format.Share(d.Share)));
        _tiles.Children.Add(UiKit.StatTile(T.TileTime, Format.Clock(d.CombatMs)));

        SetProfile(summary, d);
        BuildTabs();
        _page.Content = _tab switch
        {
            "accuracy" => AccuracyPage(d),
            "rotation" => RotationPage(d),
            "defense" => DefensePage(d),
            "profile" when _profile is { } p && _card is { } c => _pages.Profile(c, p, Bounds.Width),
            "skills" when _profile is { } p => _pages.Skills(p),
            "daevanion" when _profile is { } p => _pages.Daevanion(p, Bounds.Width),
            _ => DpsPage(d),
        };
    }

    private void SetProfile(EncounterSnapshot summary, CombatantDetail d)
    {
        if (!_profiles.TryGetValue(d.ActorId, out var p))
        {
            p = d.ActorId == Combatant.UnknownSummonsId || Shell.Engine is not { } e ? null : e.KnownProfile(d.Name, d.ServerId);
            if (Shell.Engine is not null) _profiles[d.ActorId] = p;
        }
        var place = 0;
        for (var i = 0; i < summary.Combatants.Count; i++) if (summary.Combatants[i].ActorId == d.ActorId) { place = i + 1; break; }
        var server = d.ServerId != 0 ? Shell.Engine?.Data.ServerName(d.ServerId) : null;
        var card = new PlayerCard(d.Name, d.Class, server, d.ServerId) { Fight = new FightStats(place, summary.PlayerCount, d.Damage, d.Dps, d.Share, d.CombatMs) };
        _card = p is null ? card : card.With(p);
        if (!ReferenceEquals(p, _profile)) _pages.Clear();
        _profile = p;
        var tabs = CardLayout.Tabs(true, p);
        if (_tab is "profile" && !tabs.Contains(CardTab.Profile) || _tab is "skills" && !tabs.Contains(CardTab.Skills) || _tab is "daevanion" && !tabs.Contains(CardTab.Daevanion)) _tab = "dps";
    }

    private void BuildTabs()
    {
        _tabStrip.Children.Clear();
        var tabs = CardLayout.Tabs(true, _profile);
        Add("dps", T.TabDps);
        Add("accuracy", T.TabAccuracy);
        Add("rotation", T.TabRotation);
        Add("defense", T.TabDefense);
        if (tabs.Contains(CardTab.Profile)) Add("profile", T.TabProfile);
        if (tabs.Contains(CardTab.Skills)) Add("skills", T.TabSkills);
        if (tabs.Contains(CardTab.Daevanion)) Add("daevanion", T.TabDaevanion);

        void Add(string key, string text) => AddTab(key, text, Render);
    }

    private void AddTab(string key, string text, Action render)
    {
        {
            var on = _tab == key;
            var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal, Foreground = on ? Visuals.GoldBright : Visuals.TextDim };
            var b = new Border
            {
                Padding = new Thickness(10, 4, 10, 6), Margin = new Thickness(0, 0, 2, 0), BorderThickness = new Thickness(0, 0, 0, 2),
                BorderBrush = on ? Visuals.Gold : Brushes.Transparent, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = label,
            };
            b.PointerEntered += (_, _) => { if (_tab != key) label.Foreground = Visuals.Text; };
            b.PointerExited += (_, _) => { if (_tab != key) label.Foreground = Visuals.TextDim; };
            b.PointerPressed += (_, e) => { e.Handled = true; if (_tab == key) return; _tab = key; render(); };
            _tabStrip.Children.Add(b);
        }
    }

    private void BuildParty(EncounterSnapshot summary)
    {
        _party.Children.Clear();
        if (_narrow) return;
        var combatants = summary.Combatants;
        for (var i = 0; i < combatants.Count; i++)
        {
            var c = combatants[i];
            var place = c.IsUnknownSummons ? 0 : i + 1;
            _party.Children.Add(PartyRow(c, place));
        }
    }

    private Control PartyRow(CombatantSnapshot c, int place)
    {
        var (bg, fg, border) = Visuals.Rank(place);
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,Auto") };
        var medal = new Grid { Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };
        medal.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Fill = bg, Stroke = border, StrokeThickness = 1 });
        medal.Children.Add(new TextBlock { Text = place > 0 ? place.ToString() : "·", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = fg, FontFamily = Visuals.Number, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(medal);
        g.Place(new TextBlock { Text = T.CombatantLabel(c.ActorId, c.Name, c.Class), FontSize = 12, Foreground = c.IsSelf ? Visuals.GoldBright : Visuals.Text, FontWeight = c.IsSelf ? FontWeight.Bold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) }, 0, 1);
        g.Place(new TextBlock { Text = Format.Share(c.Share), FontSize = 11, FontFamily = Visuals.Number, Foreground = Visuals.TextMute, VerticalAlignment = VerticalAlignment.Center }, 0, 2);
        var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 5), Margin = new Thickness(0, 1), Background = c.ActorId == _actor ? Visuals.Solid(0x26C9A45C) : Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = g };
        var actor = c.ActorId;
        row.PointerEntered += (_, _) => { if (actor != _actor) row.Background = Visuals.HoverFill; };
        row.PointerExited += (_, _) => { if (actor != _actor) row.Background = Brushes.Transparent; };
        row.PointerPressed += (_, e) => { e.Handled = true; Select(actor); };
        return row;
    }

    private void ShowPartyMenu()
    {
        if (_record is null) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var combatants = _record.Summary.Combatants;
        for (var i = 0; i < combatants.Count; i++)
        {
            var c = combatants[i];
            var place = c.IsUnknownSummons ? 0 : i + 1;
            var item = new MenuItem { Header = $"{(c.ActorId == _actor ? "▸ " : "")}{(place > 0 ? place + ". " : "· ")}{T.CombatantLabel(c.ActorId, c.Name, c.Class)}  ·  {Format.Share(c.Share)}" };
            var actor = c.ActorId;
            item.Click += (_, _) => Select(actor);
            menu.Items.Add(item);
        }
        menu.ShowAt(_partyChip);
    }

    private bool _fightsAsked;

    private void AskFights()
    {
        _fightsAsked = true;
        if (Shell.Daemon.Connected) { Shell.Daemon.Send("fights", "max", 40); return; }
        // no daemon: the saved fights the viewer engine sees
        Shell.WhenEngine(e => OnFights(OverlayBuilder.FightList(e, 40).ToArray()));
    }

    /// <summary>The daemon's fight list (session + saved): the fight picked opens here, on your character.</summary>
    private void OnFights(OverlayFightItem[] items)
    {
        if (!_fightsAsked) return;
        _fightsAsked = false;
        var anchor = _narrow ? _fightsChip : _sideFights;
        if (!anchor.IsEffectivelyVisible) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var it in items)
        {
            var mark = it.Active ? "●" : it.Reason switch { EncounterEndReason.Kill => "✔", EncounterEndReason.Wipe => "✖", _ => "•" };
            var dps = it.SelfDps > 0 ? $"  ·  {Format.Compact(it.SelfDps)}/s{(it.Place > 0 ? $" #{it.Place}" : "")}" : it.PartyDps > 0 ? $"  ·  {T.Party} {Format.Compact(it.PartyDps)}/s" : "";
            var item = new MenuItem { Header = $"{(it.Id == _id ? "▸ " : "")}{mark} {it.StartedAt.ToLocalTime():HH:mm}  {it.Title}  ·  {Format.Clock(it.CombatMs)}{dps}" };
            var id = it.Id;
            item.Click += (_, _) => SetFight(id, 0);
            menu.Items.Add(item);
        }
        if (items.Length == 0) menu.Items.Add(new MenuItem { Header = T.NoFights, IsEnabled = false });
        menu.ShowAt(anchor);
    }

    // ------------------------------------------------------------ pages

    private Control DpsPage(CombatantDetail d)
    {
        var stack = new StackPanel();
        var series = new List<ChartSeries> { new(d.Name, d.PerSecond, Visuals.Rgb(OverlayPalette.ClassColor(d.Class)), true) };
        foreach (var o in _record!.Details) if (o.ActorId != d.ActorId) series.Add(new ChartSeries(o.Name, o.PerSecond, Visuals.Rgb(OverlayPalette.ClassColor(o.Class)), false));
        _chart.SetData(series);
        if (_chart.Parent is Panel old) old.Children.Remove(_chart);
        stack.Children.Add(_chart);
        var cols = _compact
            ? new (SkillColumn C, string W)[] { (SkillColumn.Name, "*"), (SkillColumn.Hits, "48"), (SkillColumn.Damage, "70"), (SkillColumn.Dps, "62"), (SkillColumn.Crit, "54"), (SkillColumn.Share, "64") }
            : [(SkillColumn.Name, "*"), (SkillColumn.Hits, "52"), (SkillColumn.Damage, "78"), (SkillColumn.Dps, "66"), (SkillColumn.Average, "64"), (SkillColumn.Max, "64"), (SkillColumn.Crit, "56"), (SkillColumn.Share, "110")];
        stack.Children.Add(Table(d, cols, false));
        return stack;
    }

    private Control AccuracyPage(CombatantDetail d)
    {
        var stack = new StackPanel();
        var q = d.Quality;
        var tiles = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        tiles.Children.Add(UiKit.StatTile(T.ThCrit, Format.Percent(q.CritRate), string.Format(T.QHitsMeasured, q.Hits), null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.ThBack, Format.Percent(q.BackRate), T.QPositional, null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.ThFront, Format.Percent(q.FrontRate), T.QPositional, null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.ThPerfect, Format.Percent(q.PerfectRate), null, null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.ThSmite, Format.Percent(q.HeavyRate), null, null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.ThMulti, Format.Percent(q.MultiRate), T.QExtraHits, null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.QBiggestHit, Format.Compact(q.MaxHit), q.MaxHitSkill ?? "", null, 20, 96));
        stack.Children.Add(tiles);
        var cols = _compact
            ? new (SkillColumn C, string W)[] { (SkillColumn.Name, "*"), (SkillColumn.Hits, "46"), (SkillColumn.Crit, "54"), (SkillColumn.Back, "54"), (SkillColumn.Perfect, "58"), (SkillColumn.Heavy, "54") }
            : [(SkillColumn.Name, "*"), (SkillColumn.Hits, "50"), (SkillColumn.Crit, "56"), (SkillColumn.Back, "56"), (SkillColumn.Front, "60"), (SkillColumn.Perfect, "60"), (SkillColumn.Heavy, "56"), (SkillColumn.Multi, "60"), (SkillColumn.Damage, "78")];
        stack.Children.Add(Table(d, cols, true));
        return stack;
    }

    private Control RotationPage(CombatantDetail d)
    {
        var names = new Dictionary<int, string>(d.Skills.Count);
        foreach (var s in d.Skills) names[s.SkillCode] = s.Name;
        var data = Shell.Engine?.Data;
        _rotation.SetData(d.Casts, code => names.TryGetValue(code, out var n) ? n : data?.SkillName(code) ?? code.ToString(), Shell.Icons.Skill);
        if (_rotation.Parent is ContentControl cc) cc.Content = null;
        return _rotation;
    }

    private Control DefensePage(CombatantDetail d)
    {
        var tiles = new WrapPanel();
        tiles.Children.Add(UiKit.StatTile(T.DefTaken, Format.Compact(d.DamageTaken), string.Format(T.DefHits, d.HitsTaken), null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.DefTakenPerSec, Format.Compact(d.DamageTaken / Math.Max(1.0, d.CombatMs / 1000.0)), null, null, 20, 96));
        tiles.Children.Add(UiKit.StatTile(T.DefDotTicks, d.Quality.DotTicks.ToString(), T.DefDot, null, 20, 96));
        return tiles;
    }

    /// <summary>A sortable skills table: a header row of column buttons (gold + arrow on the sort column), then one row per skill.</summary>
    private Control Table(CombatantDetail d, (SkillColumn C, string W)[] cols, bool accuracy)
    {
        var spec = string.Join(",", cols.Select(c => c.W));
        var order = accuracy ? _accuracyOrder : _skillOrder;
        var stack = new StackPanel();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(spec), Margin = new Thickness(0, 0, 0, 4) };
        for (var i = 0; i < cols.Length; i++)
        {
            var col = cols[i].C;
            var label = HeadLabel(col);
            var arrow = order.Descending ? "▾" : "▴";
            var text = col != order.Column ? label : col == SkillColumn.Name ? $"{label} {arrow}" : $"{arrow} {label}";
            var tb = new TextBlock { Text = text, FontFamily = Visuals.Number, FontSize = 10.5, FontWeight = FontWeight.SemiBold, Foreground = col == order.Column ? Visuals.Gold : Visuals.TextMute, HorizontalAlignment = col == SkillColumn.Name ? HorizontalAlignment.Left : HorizontalAlignment.Right, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Padding = new Thickness(0, 2) };
            tb.PointerEntered += (_, _) => tb.Foreground = Visuals.GoldBright;
            tb.PointerExited += (_, _) => tb.Foreground = col == order.Column ? Visuals.Gold : Visuals.TextMute;
            tb.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (accuracy) _lastAccuracyOrder = _accuracyOrder = _accuracyOrder.Toggle(col);
                else _lastSkillOrder = _skillOrder = _skillOrder.Toggle(col);
                Render();
            };
            UiKit.Tip(tb, T.SortTip);
            head.Place(tb, 0, i);
        }
        stack.Children.Add(head);
        double maxShare = 0;
        foreach (var s in d.Skills) if (s.Share > maxShare) maxShare = s.Share;
        IEnumerable<SkillRow> rows = d.Skills;
        if (accuracy) rows = rows.Where(s => s.Hits > 0);
        foreach (var s in order.Apply(rows, T.Culture))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(spec), Height = 26, Background = Brushes.Transparent };
            for (var i = 0; i < cols.Length; i++)
            {
                var col = cols[i].C;
                if (col == SkillColumn.Name) { g.Place(NameCell(s), 0, i); continue; }
                if (col == SkillColumn.Share && !accuracy) { g.Place(ShareCell(s, maxShare), 0, i); continue; }
                g.Place(new TextBlock { Text = Cell(s, col), FontFamily = Visuals.Number, FontSize = 12.5, Foreground = col == SkillColumn.Dps ? Visuals.GoldBright : Visuals.Text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }, 0, i);
            }
            var tip = $"{s.Name} ({s.SkillCode})\n{T.TipSkillDamage} {Format.Grouped(s.Damage)} · {Format.Percent(s.Share)}\n{T.TipSkillHits} {s.Hits} · {T.TipSkillDotTicks} {s.DotTicks}\n" +
                      $"{T.TipSkillMin} {Format.Compact(s.Min)} · {T.TipSkillAvg} {Format.Compact(s.Average)} · {T.TipSkillMax} {Format.Compact(s.Max)}";
            UiKit.Tip(g, tip);
            g.PointerEntered += (_, _) => g.Background = Visuals.HoverFill;
            g.PointerExited += (_, _) => g.Background = Brushes.Transparent;
            stack.Children.Add(g);
        }
        return stack;
    }

    private Control NameCell(SkillRow s)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = Shell.Icons.Skill(s.SkillCode);
        if (icon is not null) row.Children.Add(new Image { Source = icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) });
        else row.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(RotationStrip.SkillColor(s.SkillCode)), Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = s.Name, FontSize = 12.5, Foreground = Visuals.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (s.Hits == 0 && s.DotTicks > 0) row.Children.Add(new TextBlock { Text = "DoT", FontSize = 10, Foreground = Visuals.TextMute, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private Control ShareCell(SkillRow s, double maxShare)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(_compact ? 8 : 14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (!_compact)
        {
            var bar = UiKit.Bar(maxShare > 0 ? s.Share / maxShare : 0, 5);
            bar.VerticalAlignment = VerticalAlignment.Center;
            bar.Margin = new Thickness(0, 0, 8, 0);
            g.Children.Add(bar);
        }
        g.Place(new TextBlock { Text = Format.Percent(s.Share), FontFamily = Visuals.Number, FontSize = 12, Foreground = Visuals.TextDim, VerticalAlignment = VerticalAlignment.Center }, 0, 1);
        return g;
    }

    private static string Cell(SkillRow s, SkillColumn c) => c switch
    {
        SkillColumn.Hits => s.DotTicks > 0 && s.Hits > 0 ? $"{s.Hits}+{s.DotTicks}" : (s.Hits + s.DotTicks).ToString(),
        SkillColumn.Damage => Format.Compact(s.Damage),
        SkillColumn.Dps => Format.Compact(s.Dps),
        SkillColumn.Average => Format.Compact(s.Average),
        SkillColumn.Max => Format.Compact(s.Max),
        SkillColumn.Crit => Format.Percent(s.CritRate),
        SkillColumn.Share => Format.Percent(s.Share),
        SkillColumn.Back => Format.Percent(s.BackRate),
        SkillColumn.Front => Format.Percent(s.FrontRate),
        SkillColumn.Perfect => Format.Percent(s.PerfectRate),
        SkillColumn.Heavy => Format.Percent(s.HeavyRate),
        SkillColumn.Multi => Format.Percent(s.MultiRate),
        _ => "",
    };

    private static string HeadLabel(SkillColumn column) => column switch
    {
        SkillColumn.Name => T.ThSkill, SkillColumn.Hits => T.ThHits, SkillColumn.Damage => T.ThDamage, SkillColumn.Dps => T.ThDps,
        SkillColumn.Average => T.ThAvg, SkillColumn.Max => T.ThMax, SkillColumn.Crit => T.ThCrit, SkillColumn.Share => T.ThShare,
        SkillColumn.Back => T.ThBack, SkillColumn.Front => T.ThFront, SkillColumn.Perfect => T.ThPerfect, SkillColumn.Heavy => T.ThSmite,
        SkillColumn.Multi => T.ThMulti, _ => column.ToString(),
    };
}
