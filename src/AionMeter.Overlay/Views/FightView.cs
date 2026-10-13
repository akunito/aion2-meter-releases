using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay.Views;

/// <summary>The fight tab's decisions that need no widgets (tested in FightTabTests).</summary>
internal static class FightLogic
{
    /// <summary>The skills table's columns on the card (the Avalonia compact layout, &lt; 700 px): name fills, the rest fixed px.</summary>
    public static readonly (SkillColumn C, double W)[] DpsColumns =
        [(SkillColumn.Name, 0), (SkillColumn.Hits, 48), (SkillColumn.Damage, 70), (SkillColumn.Dps, 62), (SkillColumn.Crit, 54), (SkillColumn.Share, 64)];
    public static readonly (SkillColumn C, double W)[] AccuracyColumns =
        [(SkillColumn.Name, 0), (SkillColumn.Hits, 46), (SkillColumn.Crit, 54), (SkillColumn.Back, 54), (SkillColumn.Perfect, 58), (SkillColumn.Heavy, 54)];

    /// <summary>The player to show: <paramref name="actor"/> when in the fight, else you, else the top.</summary>
    public static uint PickActor(IReadOnlyList<CombatantSnapshot> combatants, uint actor)
    {
        if (actor != 0)
            for (var i = 0; i < combatants.Count; i++) if (combatants[i].ActorId == actor) return actor;
        for (var i = 0; i < combatants.Count; i++) if (combatants[i].IsSelf) return combatants[i].ActorId;
        return combatants.Count > 0 ? combatants[0].ActorId : 0;
    }

    /// <summary>1-based place in the ranking; 0 for the unknown-summons row or someone not in it.</summary>
    public static int Place(IReadOnlyList<CombatantSnapshot> combatants, uint actor)
    {
        if (actor == Combatant.UnknownSummonsId) return 0;
        for (var i = 0; i < combatants.Count; i++) if (combatants[i].ActorId == actor) return i + 1;
        return 0;
    }

    /// <summary>A fight sub-tab that the player's profile no longer offers falls back to DPS.</summary>
    public static string FightTab(string tab, List<CardTab> tabs) =>
        tab is "profile" && !tabs.Contains(CardTab.Profile) || tab is "skills" && !tabs.Contains(CardTab.Skills) || tab is "daevanion" && !tabs.Contains(CardTab.Daevanion)
            ? "dps" : tab;

    /// <summary>The player card without a fight: the asked tab when the profile has it, else the first it has ("profile" when none).</summary>
    public static string CardTabFor(string tab, List<CardTab> tabs) =>
        tab is "profile" && tabs.Contains(CardTab.Profile) || tab is "skills" && tabs.Contains(CardTab.Skills) || tab is "daevanion" && tabs.Contains(CardTab.Daevanion)
            ? tab : tabs.Count > 0 ? tabs[0].ToString().ToLowerInvariant() : "profile";

    public static string Cell(SkillRow s, SkillColumn c) => c switch
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

    /// <summary>Header text: the sort column carries the arrow (after the name, before a number).</summary>
    public static string HeadText(string label, SkillColumn col, SkillOrder order)
    {
        if (col != order.Column) return label;
        var arrow = order.Descending ? "▾" : "▴";
        return col == SkillColumn.Name ? label + " " + arrow : arrow + " " + label;
    }

    /// <summary>Party menu line: "▸ 1. Name (Class)  ·  42.0%".</summary>
    public static string PartyItem(CombatantSnapshot c, int place, bool selected, UiText t) =>
        $"{(selected ? "▸ " : "")}{(place > 0 ? place + ". " : "· ")}{t.CombatantLabel(c.ActorId, c.Name, c.Class)}  ·  {Format.Share(c.Share)}";

    /// <summary>The fight line under the player (Avalonia side panel meta, one line on the card).</summary>
    public static string FightMeta(EncounterSnapshot s, UiText t, string? zone, string? note) =>
        $"{s.StartedAt.ToLocalTime().ToString("dd MMM HH:mm", t.Culture)} · {Format.Clock(s.CombatMs)} · {Format.Compact(s.PartyDps)}/s {t.PartyDpsSuffix}"
        + (s.MostDamageUnsent ? " · " + t.DamageUnsentLine(s) : "")
        + (zone is null ? "" : " · " + zone)
        + (note is null ? "" : " · " + note);
}

/// <summary>
/// The fight breakdown / player card (WPF BreakdownView, A2M-43/44) as the layer-shell card's "fight" tab (A2M-50, port of the Avalonia
/// FightView): stat tiles, DPS (timeline + skills), Accuracy, Rotation, Defense, and the selected player's Profile / Skills / Daevanion when
/// the meter knows a profile for them. Saved fights ("h:&lt;file&gt;") are read from fights/ by the viewer engine (off the main loop); live and
/// session fights come from the daemon's `fight-detail` (asked again each second while the fight runs). The card is narrow: party and fight
/// list are header chips with popovers (the Avalonia ≥ 760 px side panel never applies). Widgets are built once and updated in place on
/// each live tick.
/// </summary>
internal sealed class FightView : HostedView, IFightTab, IFightTabProfile
{
    private static UiText T => UiText.Current;
    private static SkillOrder _lastSkillOrder = SkillOrder.Default, _lastAccuracyOrder = SkillOrder.Default;

    private string _id = "live";          // what was asked: "live", "s:…", "h:…"
    private string _asked = "live";
    private uint _actor;
    private FightRecord? _record;
    private bool _live, _waiting, _fightsAsked;
    private long _askedAt;
    private int _loadToken;
    private string _tab = "dps";
    private SkillOrder _skillOrder = _lastSkillOrder, _accuracyOrder = _lastAccuracyOrder;
    private readonly UiTimer _timer = new(TimeSpan.FromSeconds(1));
    private readonly ProfilePages _pages;
    private readonly Dictionary<uint, CharacterProfile?> _profiles = new();
    private CharacterProfile? _profile;
    private PlayerCard? _card;
    private bool _cardOnly;
    private string? _cardStatus;

    private readonly Gtk.Button _partyChip, _fightsChip;
    private readonly Gtk.Popover _partyMenu, _fightsMenu;
    private readonly Gtk.Box _partyList, _fightsList;
    private readonly ClassGlyph _glyph = new(30);
    private readonly Gtk.Label _meta, _status;
    private readonly Gtk.Box _tiles, _tabStrip, _pageHost;
    private readonly Gtk.ScrolledWindow _pageScroll;
    private Gtk.Widget? _pageShown;
    private string _tabKeys = "";
    private readonly List<(string Key, Gtk.Box Box)> _tabWidgets = new();

    // fight pages, built once and updated in place
    private readonly TimelineChart _chart = new(160);
    private readonly SkillTable _dpsTable, _accTable;
    private readonly RotationStrip _rotation;
    private readonly Gtk.Box _dpsPage, _accPage;
    private WrapBox _accTiles = null!, _defTiles = null!;
    private Gtk.Box[] _fightTiles = [], _accTileBoxes = [], _defTileBoxes = [];
    private int _placeClass;
    private readonly List<SkillRow> _filtered = new();
    // profile pages: rebuilt when the player, the profile or the tab changes, not on each live tick
    private Gtk.Widget? _profilePage;
    private (string Tab, CharacterProfile? Profile, PlayerCard? Card) _profileKey;

    public FightView(AppShell shell) : base(shell, "fight", "…", "meter")
    {
        FightKit.LoadCss(shell.UiScale);
        _pages = new ProfilePages(shell);
        _timer.Tick += () =>
        {
            if (!_live || !IsShown) return;
            // a reply lost over a reconnect must not stop the refresh for good
            if (!_waiting || Environment.TickCount64 - _askedAt > 5000) Request();
        };
        OnDetach(_timer.Stop);

        _partyChip = UiKit.Chip(PartyText(), ShowPartyMenu);
        _partyChip.MarginEnd = UiKit.Px(6);
        _fightsChip = UiKit.Chip(T.Fights + " ▾", AskFights, T.TipFightPicker);
        HeaderRight.Append(_partyChip);
        HeaderRight.Append(_fightsChip);
        // parented to the chips' box, not a chip: under a chip the menu's labels took the chip's gold (.k-chip label)
        (_partyMenu, _partyList) = Menu(HeaderRight);
        (_fightsMenu, _fightsList) = Menu(HeaderRight);

        var body = UiKit.VBox();
        var top = UiKit.VBox();
        top.AddCssClass("k-fight-top");
        var who = UiKit.HBox();
        who.MarginBottom = UiKit.Px(8);
        _glyph.Widget.MarginEnd = UiKit.Px(10);
        who.Append(_glyph.Widget);
        _meta = UiKit.Text("", 11.5, Fg.Dim);
        who.Append(_meta);
        top.Append(who);
        _tiles = UiKit.HBox();
        top.Append(HScroll(_tiles, 6));
        _tabStrip = UiKit.HBox();
        top.Append(HScroll(_tabStrip, 8));
        body.Append(top);

        _pageHost = UiKit.VBox();
        _pageHost.MarginBottom = UiKit.Px(8);
        _pageScroll = UiKit.Scroll(_pageHost);
        body.Append(_pageScroll);
        _status = UiKit.Text("", 12.5, Fg.Mute);
        _status.Xalign = 0.5f;
        _status.Justify = Gtk.Justification.Center;
        _status.Valign = Gtk.Align.Center;
        _status.Vexpand = true;
        UiKit.Margin(_status, 20, 20, 20, 20);
        body.Append(_status);
        SetBody(body);

        _dpsTable = new SkillTable(FightLogic.DpsColumns, false, Shell.Icons, SortSkills);
        _accTable = new SkillTable(FightLogic.AccuracyColumns, true, Shell.Icons, SortAccuracy);
        _rotation = new RotationStrip(Shell.Icons);
        _chart.Widget.MarginBottom = UiKit.Px(10);
        _dpsPage = UiKit.VBox();
        _dpsPage.Append(_chart.Widget);
        _dpsPage.Append(_dpsTable.Widget);
        _accPage = UiKit.VBox();
        BuildFightTiles();

        Listen(() => Shell.Daemon.FightDetail += OnDetail, () => Shell.Daemon.FightDetail -= OnDetail);
        Listen(() => Shell.Daemon.Fights += OnFights, () => Shell.Daemon.Fights -= OnFights);
        Listen(() => Shell.Daemon.Error += OnError, () => Shell.Daemon.Error -= OnError);
        Listen(() => Shell.Icons.Loaded += OnIcons, () => Shell.Icons.Loaded -= OnIcons);
        ShowStatus(T.FightLoading);
    }

    private static string PartyText() => char.ToUpper(T.Party[0], T.Culture) + T.Party[1..] + " ▾";

    /// <summary>A horizontal scroller that never widens the card (natural width = its minimum, Automatic bar when the row overflows).</summary>
    private static Gtk.ScrolledWindow HScroll(Gtk.Widget child, double bottom)
    {
        var s = Gtk.ScrolledWindow.New();
        s.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
        s.Child = child;
        s.Hexpand = true;
        s.MarginBottom = UiKit.Px(bottom);
        return s;
    }

    private static (Gtk.Popover, Gtk.Box) Menu(Gtk.Widget anchor)
    {
        var list = UiKit.VBox();
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.PropagateNaturalHeight = true;
        scroll.PropagateNaturalWidth = true;
        scroll.MaxContentHeight = UiKit.Px(380);
        scroll.Child = list;
        var p = Gtk.Popover.New();
        p.AddCssClass("fights");   // the card's Fights menu look (Css.cs)
        p.SetHasArrow(false);
        p.SetPosition(Gtk.PositionType.Bottom);
        // right-aligned under the chips: centred, a 40-fight list ran off the output's right edge (measured)
        p.Halign = Gtk.Align.End;
        p.Child = scroll;
        p.SetParent(anchor);
        return (p, list);
    }

    private static void MenuItem(Gtk.Box list, Gtk.Popover menu, string text, Action? click)
    {
        var l = Gtk.Label.New(text);
        l.Xalign = 0;
        if (click is null) { l.AddCssClass("off"); list.Append(l); return; }
        var b = Gtk.Button.New();
        b.Child = l;
        b.SetCursorFromName("pointer");
        b.FocusOnClick = false;
        b.OnClicked += (_, _) => { menu.Popdown(); click(); };
        list.Append(b);
    }

    private static void Clear(Gtk.Box box)
    {
        while (box.GetFirstChild() is { } c) box.Remove(c);
    }

    // ------------------------------------------------------------ IFightTab

    /// <summary>Shows a fight ("live", "s:&lt;guid&gt;", "h:&lt;file&gt;") on <paramref name="actor"/> (0: you, else the top of the party), on
    /// <paramref name="tab"/> when given ("profile" for a Top DPS row: falls back to DPS when the player has no known profile). The card calls
    /// Shown() right after, which loads it.</summary>
    public void SetFight(string fightId, uint actor, string? tab)
    {
        _cardOnly = false;
        _cardStatus = null;
        if (tab is not null) _tab = tab;
        _id = _asked = fightId.Length == 0 ? "live" : fightId;
        _actor = actor;
        _record = null;
        _live = false;
        _waiting = false;
        _loadToken++;
        _profiles.Clear();
        _profile = null;
        _card = null;
        _pages.Clear();
        _profilePage = null;
        _partyChip.Visible = true;
        _fightsChip.Visible = true;
        ShowStatus(T.FightLoading);
    }

    /// <summary>The player card alone (A2M-44, Top DPS ranking rows): header, CP / item level / rank / seen tiles, Profile / Skills / Daevanion.</summary>
    public void SetCard(PlayerCard card, string? status)
    {
        _cardOnly = true;
        _record = null;
        _live = false;
        _waiting = false;
        _loadToken++;
        _id = _asked = "";
        _profiles.Clear();
        _pages.Clear();
        _profilePage = null;
        _tab = "profile";
        UpdateCard(card, status);
    }

    /// <summary>The armory answered (or failed) for the card shown by <see cref="SetCard"/>: the card with its profile, or the status line.</summary>
    public void UpdateCard(PlayerCard card, string? status)
    {
        if (!_cardOnly) return;
        if (!ReferenceEquals(card.Profile, _profile)) { _pages.Clear(); _profilePage = null; }
        _card = card;
        _profile = card.Profile;
        _cardStatus = status;
        _tab = FightLogic.CardTabFor(_tab, CardLayout.Tabs(false, _profile));
        if (IsShown) RenderCard();
    }

    /// <summary>The card shown without a fight (null in a fight).</summary>
    public PlayerCard? Card => _cardOnly ? _card : null;

    /// <summary>The armory cached a newer profile for someone in this fight (Top DPS "Load profile"): look the profiles up again.</summary>
    public void RefreshProfile()
    {
        if (_cardOnly || _record is null) return;
        _profiles.Clear();
        Render();
    }

    // ------------------------------------------------------------ lifecycle

    public override void Shown()
    {
        if (_cardOnly) { RenderCard(); return; }
        if (_record is null || _live) Load();
        else Render();
        _timer.Start();
    }

    public override void Hidden() => _timer.Stop();

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        UiKit.SetChipText(_partyChip, PartyText());
        UiKit.SetChipText(_fightsChip, T.Fights + " ▾");
        _fightsChip.SetTooltipText(T.TipFightPicker);
        _tabKeys = "";
        _profilePage = null;
        _pages.Clear();
        BuildFightTiles();
        if (_cardOnly) RenderCard();
        else if (_record is not null) Render();
    }

    // ------------------------------------------------------------ loading

    private void Load()
    {
        if (_id.StartsWith("h:", StringComparison.Ordinal))
        {
            var token = ++_loadToken;
            var file = _id[2..];
            Shell.WhenEngine(e => AppShell.Run(() =>
            {
                foreach (var h in e.History.List())
                    if (h.FileName == file) return e.History.Load(h.Path);
                return null;
            }, rec =>
            {
                if (token != _loadToken || _cardOnly) return;
                if (rec is not null) { Show(rec, false); return; }
                if (Shell.Daemon.Connected) Request();   // not on this disk (yet): the daemon may know it
                else NotFound();
            }, "saved fight read"));
            return;
        }
        if (!Shell.Daemon.Connected) { NotFound(); return; }
        if (_waiting && _asked == _id) return;   // SetFight then Shown asked already
        Request();
    }

    private void Request()
    {
        _waiting = true;
        _askedAt = Environment.TickCount64;
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

    private void NotFound() => ShowStatus(T.FightGone);

    private void ShowStatus(string text)
    {
        FightKit.Set(_status, text);
        _status.Visible = true;
        _pageScroll.Visible = false;
    }

    private void ShowPage(Gtk.Widget? page)
    {
        if (!ReferenceEquals(page, _pageShown))
        {
            Clear(_pageHost);
            if (page is not null)
            {
                if (page.GetParent() is Gtk.Box old) old.Remove(page);
                _pageHost.Append(page);
            }
            _pageShown = page;
        }
        _status.Visible = page is null;
        _pageScroll.Visible = page is not null;
    }

    private void Show(FightRecord record, bool live)
    {
        _record = record;
        _live = live;
        _actor = FightLogic.PickActor(record.Summary.Combatants, _actor);
        Render();
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
        if (!IsShown) return;
        if (_cardOnly || _tab is "profile" or "skills" or "daevanion") { _pages.RetryIcons(); return; }
        if (_record is null) return;
        if (_tab == "rotation") _rotation.Redraw();
        else Render();
    }

    // ------------------------------------------------------------ render

    private void Render()
    {
        if (_record is not { } rec || _cardOnly) return;
        var summary = rec.Summary;
        var d = Detail(_actor);
        if (d is null) { ShowStatus(T.FightGone); return; }

        Title = T.CombatantLabel(d.ActorId, d.Name, d.Class);
        _glyph.Class = d.Class;
        var engine = Shell.Engine;
        var server = d.ServerId != 0 && engine is not null ? $" · {engine.Data.ServerName(d.ServerId)}" : "";
        var gear = (d.GearScore > 0 ? $" · {T.Chat.GearScore} {d.GearScore}" : "") + (d.CombatPower > 0 ? $" · {T.Chat.CombatPower} {Format.Grouped(d.CombatPower)}" : "");
        var zone = summary.Zone is { } z ? engine?.DisplayZone(z) ?? z : null;
        var note = summary.Id != Guid.Empty ? engine?.AbyssLogsNoteFor(summary.Id) : null;
        FightKit.Set(_meta, $"{T.ClassName(d.Class)}{server}{gear} · {summary.Title}{(d.IsSelf ? $" · {T.You}" : "")}\n" + FightLogic.FightMeta(summary, T, zone, note));

        var place = FightLogic.Place(summary.Combatants, d.ActorId);
        SetTile(_fightTiles[0], place > 0 ? $"{place} / {summary.PlayerCount}" : "—");
        SetPlaceClass(place is >= 1 and <= 3 ? place : 0);
        SetTile(_fightTiles[1], Format.Compact(d.Damage));
        SetTile(_fightTiles[2], Format.Compact(d.Dps));
        SetTile(_fightTiles[3], Format.Share(d.Share));
        SetTile(_fightTiles[4], Format.Clock(d.CombatMs));
        if (_tiles.GetFirstChild() != _fightTiles[0]) { Clear(_tiles); foreach (var t in _fightTiles) _tiles.Append(t); }

        SetProfile(summary, d, place);
        var tabs = CardLayout.Tabs(true, _profile);
        BuildTabs(tabs, true);
        ShowPage(_tab switch
        {
            "accuracy" => AccuracyPage(d),
            "rotation" => RotationPage(d),
            "defense" => DefensePage(d),
            "profile" when _profile is not null && _card is not null => ProfilePage(),
            "skills" when _profile is not null => ProfilePage(),
            "daevanion" when _profile is not null => ProfilePage(),
            _ => DpsPage(d),
        });
    }

    private void SetProfile(EncounterSnapshot summary, CombatantDetail d, int place)
    {
        if (!_profiles.TryGetValue(d.ActorId, out var p))
        {
            p = d.ActorId == Combatant.UnknownSummonsId || Shell.Engine is not { } e ? null : e.KnownProfile(d.Name, d.ServerId);
            if (Shell.Engine is not null) _profiles[d.ActorId] = p;
        }
        var server = d.ServerId != 0 ? Shell.Engine?.Data.ServerName(d.ServerId) : null;
        // the card (and its FightStats) only changes with the player or the profile: a live tick keeps the profile page as it is
        if (_card is null || !ReferenceEquals(p, _profile) || _card.Name != d.Name || _card.ServerId != d.ServerId)
        {
            var card = new PlayerCard(d.Name, d.Class, server, d.ServerId) { Fight = new FightStats(place, summary.PlayerCount, d.Damage, d.Dps, d.Share, d.CombatMs) };
            _card = p is null ? card : card.With(p);
            _profilePage = null;
        }
        if (!ReferenceEquals(p, _profile)) _pages.Clear();
        _profile = p;
        _tab = FightLogic.FightTab(_tab, CardLayout.Tabs(true, p));
    }

    private Gtk.Widget? ProfilePage()
    {
        if (_profile is not { } p) return null;
        var key = (_tab, (CharacterProfile?)p, _card);
        if (_profilePage is not null && _profileKey == key) return _profilePage;
        _pages.Clear();
        _profilePage = _tab switch
        {
            "skills" => _pages.Skills(p),
            "daevanion" => _pages.Daevanion(p),
            _ => _card is { } c ? _pages.Profile(c, p) : null,
        };
        _profileKey = key;
        return _profilePage;
    }

    private void BuildTabs(List<CardTab> tabs, bool fight)
    {
        var keys = (fight ? "f" : "c") + (tabs.Contains(CardTab.Profile) ? "p" : "") + (tabs.Contains(CardTab.Skills) ? "s" : "") + (tabs.Contains(CardTab.Daevanion) ? "d" : "");
        if (keys != _tabKeys)
        {
            _tabKeys = keys;
            Clear(_tabStrip);
            _tabWidgets.Clear();
            if (fight)
            {
                AddTab("dps", T.TabDps);
                AddTab("accuracy", T.TabAccuracy);
                AddTab("rotation", T.TabRotation);
                AddTab("defense", T.TabDefense);
            }
            if (tabs.Contains(CardTab.Profile)) AddTab("profile", T.TabProfile);
            if (tabs.Contains(CardTab.Skills)) AddTab("skills", T.TabSkills);
            if (tabs.Contains(CardTab.Daevanion)) AddTab("daevanion", T.TabDaevanion);
        }
        foreach (var (key, box) in _tabWidgets)
            if (key == _tab) box.AddCssClass("on"); else box.RemoveCssClass("on");
    }

    private void AddTab(string key, string text)
    {
        var b = UiKit.HBox();
        b.AddCssClass("k-fight-tab");
        b.Append(Gtk.Label.New(text));
        b.SetCursorFromName("pointer");
        var click = Gtk.GestureClick.New();
        click.OnReleased += (_, e) =>
        {
            if (e.NPress != 1 || _tab == key) return;
            _tab = key;
            if (_cardOnly) RenderCard(); else Render();
        };
        b.AddController(click);
        _tabWidgets.Add((key, b));
        _tabStrip.Append(b);
    }

    // ------------------------------------------------------------ tiles

    private void BuildFightTiles()
    {
        _fightTiles =
        [
            UiKit.StatTile(T.TilePlace, "—"),
            UiKit.StatTile(T.TileDamage, "—"),
            UiKit.StatTile(T.TileDps, "—", null, Fg.GoldBright),
            UiKit.StatTile(T.TileContrib, "—"),
            UiKit.StatTile(T.TileTime, "—"),
        ];
        _placeClass = 0;
        _accTileBoxes =
        [
            UiKit.StatTile(T.ThCrit, "—", " ", Fg.Text, 20, 96),
            UiKit.StatTile(T.ThBack, "—", T.QPositional, Fg.Text, 20, 96),
            UiKit.StatTile(T.ThFront, "—", T.QPositional, Fg.Text, 20, 96),
            UiKit.StatTile(T.ThPerfect, "—", null, Fg.Text, 20, 96),
            UiKit.StatTile(T.ThSmite, "—", null, Fg.Text, 20, 96),
            UiKit.StatTile(T.ThMulti, "—", T.QExtraHits, Fg.Text, 20, 96),
            UiKit.StatTile(T.QBiggestHit, "—", " ", Fg.Text, 20, 96),
        ];
        _defTileBoxes =
        [
            UiKit.StatTile(T.DefTaken, "—", " ", Fg.Text, 20, 96),
            UiKit.StatTile(T.DefTakenPerSec, "—", null, Fg.Text, 20, 96),
            UiKit.StatTile(T.DefDotTicks, "—", T.DefDot, Fg.Text, 20, 96),
        ];
        Clear(_accPage);
        _accTiles = new WrapBox();
        _accTiles.Widget.MarginBottom = UiKit.Px(8);
        foreach (var t in _accTileBoxes) _accTiles.Add(t);
        _accPage.Append(_accTiles.Widget);
        _accPage.Append(_accTable.Widget);
        _defTiles = new WrapBox();
        foreach (var t in _defTileBoxes) _defTiles.Add(t);
        if (_pageShown is not null && !ReferenceEquals(_pageShown, _dpsPage) && !ReferenceEquals(_pageShown, _accPage)) _pageShown = null;
        Clear(_tiles);
    }

    /// <summary>StatTile children: [label, value, caption?].</summary>
    private static void SetTile(Gtk.Box tile, string value, string? caption = null)
    {
        if (tile.GetFirstChild()?.GetNextSibling() is Gtk.Label v) FightKit.Set(v, value);
        if (caption is not null && tile.GetLastChild() is Gtk.Label c && tile.GetFirstChild()?.GetNextSibling() != c) FightKit.Set(c, caption);
    }

    private void SetPlaceClass(int medal)
    {
        if (medal == _placeClass) return;
        var t = _fightTiles[0];
        if (_placeClass > 0) t.RemoveCssClass("k-fight-r" + _placeClass);
        if (medal > 0) t.AddCssClass("k-fight-r" + medal);
        _placeClass = medal;
    }

    // ------------------------------------------------------------ party + fight list

    private void ShowPartyMenu()
    {
        if (_record is null) return;
        Clear(_partyList);
        var combatants = _record.Summary.Combatants;
        for (var i = 0; i < combatants.Count; i++)
        {
            var c = combatants[i];
            var actor = c.ActorId;
            MenuItem(_partyList, _partyMenu, FightLogic.PartyItem(c, c.IsUnknownSummons ? 0 : i + 1, actor == _actor, T), () => Select(actor));
        }
        _partyMenu.Popup();
    }

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
        if (!IsShown || !_fightsChip.Visible) return;
        Clear(_fightsList);
        foreach (var it in items)
        {
            var id = it.Id;
            MenuItem(_fightsList, _fightsMenu, CardText.FightItem(it, _id, T), () => { SetFight(id, 0, null); Shown(); });
        }
        if (items.Length == 0) MenuItem(_fightsList, _fightsMenu, T.NoFights, null);
        _fightsMenu.Popup();
    }

    // ------------------------------------------------------------ pages

    private Gtk.Widget DpsPage(CombatantDetail d)
    {
        _chart.Begin();
        foreach (var o in _record!.Details) if (o.ActorId != d.ActorId) _chart.Add(o.PerSecond, OverlayPalette.ClassColor(o.Class), false);
        _chart.Add(d.PerSecond, OverlayPalette.ClassColor(d.Class), true);
        _chart.End();
        double maxShare = 0;
        foreach (var s in d.Skills) if (s.Share > maxShare) maxShare = s.Share;
        _dpsTable.Update(_skillOrder.Apply(d.Skills, T.Culture), _skillOrder, maxShare);
        return _dpsPage;
    }

    private Gtk.Widget AccuracyPage(CombatantDetail d)
    {
        var q = d.Quality;
        SetTile(_accTileBoxes[0], Format.Percent(q.CritRate), string.Format(T.QHitsMeasured, q.Hits));
        SetTile(_accTileBoxes[1], Format.Percent(q.BackRate));
        SetTile(_accTileBoxes[2], Format.Percent(q.FrontRate));
        SetTile(_accTileBoxes[3], Format.Percent(q.PerfectRate));
        SetTile(_accTileBoxes[4], Format.Percent(q.HeavyRate));
        SetTile(_accTileBoxes[5], Format.Percent(q.MultiRate));
        SetTile(_accTileBoxes[6], Format.Compact(q.MaxHit), q.MaxHitSkill ?? "");
        _filtered.Clear();
        foreach (var s in d.Skills) if (s.Hits > 0) _filtered.Add(s);
        _accTable.Update(_accuracyOrder.Apply(_filtered, T.Culture), _accuracyOrder, 0);
        return _accPage;
    }

    private Gtk.Widget RotationPage(CombatantDetail d)
    {
        var names = new Dictionary<int, string>(d.Skills.Count);
        foreach (var s in d.Skills) names[s.SkillCode] = s.Name;
        var data = Shell.Engine?.Data;
        _rotation.SetData(d.Casts, code => names.TryGetValue(code, out var n) ? n : data?.SkillName(code) ?? code.ToString());
        return _rotation.Widget;
    }

    private Gtk.Widget DefensePage(CombatantDetail d)
    {
        SetTile(_defTileBoxes[0], Format.Compact(d.DamageTaken), string.Format(T.DefHits, d.HitsTaken));
        SetTile(_defTileBoxes[1], Format.Compact(d.DamageTaken / Math.Max(1.0, d.CombatMs / 1000.0)));
        SetTile(_defTileBoxes[2], d.Quality.DotTicks.ToString());
        return _defTiles.Widget;
    }

    private void SortSkills(SkillColumn col)
    {
        _lastSkillOrder = _skillOrder = _skillOrder.Toggle(col);
        Render();
    }

    private void SortAccuracy(SkillColumn col)
    {
        _lastAccuracyOrder = _accuracyOrder = _accuracyOrder.Toggle(col);
        Render();
    }

    // ------------------------------------------------------------ player card without a fight

    private void RenderCard()
    {
        if (_card is not { } c) return;
        _partyChip.Visible = false;
        _fightsChip.Visible = false;
        Title = c.Name;
        _glyph.Class = c.Class;
        FightKit.Set(_meta, CardLayout.Meta(T.ClassName(c.Class), c) + (c.Title is { Length: > 0 } title ? $" · “{title}”" : ""));
        Clear(_tiles);
        foreach (var (tile, value) in CardLayout.Tiles(c, DateTimeOffset.UtcNow))
            _tiles.Append(UiKit.StatTile(tile switch { CardTile.CombatPower => T.TileCp, CardTile.ItemLevel => T.TileItemLevel, CardTile.Rank => T.TileRank, _ => T.TileSeen },
                tile == CardTile.Seen && value != "now" ? string.Format(T.TileSeenAgo, value) : value, null, tile == CardTile.CombatPower ? Fg.GoldBright : Fg.Text));
        var tabs = CardLayout.Tabs(false, _profile);
        BuildTabs(tabs, false);
        var page = _profile is null ? null
            : _tab == "skills" && tabs.Contains(CardTab.Skills) || _tab == "daevanion" && tabs.Contains(CardTab.Daevanion) || tabs.Contains(CardTab.Profile) ? ProfilePage() : null;
        if (page is null) FightKit.Set(_status, _cardStatus ?? string.Format(T.TopProfileMissing, c.Name, c.Server ?? "?"));
        ShowPage(page);
    }
}

/// <summary>
/// The sortable skills table (Avalonia FightView.Table) with pooled rows: a header of column labels (gold + arrow on the sort column, click
/// toggles), then one row per skill whose labels are only re-set when their text changed; the tooltip is built when GTK asks for it.
/// </summary>
internal sealed class SkillTable
{
    private static UiText T => UiText.Current;
    private readonly (SkillColumn C, double W)[] _cols;
    private readonly bool _accuracy;
    private readonly IconProvider _icons;
    private readonly Gtk.Label[] _head;
    private readonly List<Row> _rows = new();
    public Gtk.Box Widget { get; }

    private sealed class Row
    {
        public Gtk.Box Box = null!;
        public Gtk.Image Icon = null!;
        public Gtk.DrawingArea Swatch = null!;
        public Gtk.Label Name = null!, Dot = null!;
        public Gtk.Label[] Cells = null!;
        public SkillRow? Skill;
        public int Code = -1;
    }

    public SkillTable((SkillColumn C, double W)[] cols, bool accuracy, IconProvider icons, Action<SkillColumn> sort)
    {
        _cols = cols;
        _accuracy = accuracy;
        _icons = icons;
        Widget = UiKit.VBox();
        var head = UiKit.HBox();
        head.MarginBottom = UiKit.Px(4);
        _head = new Gtk.Label[cols.Length];
        for (var i = 0; i < cols.Length; i++)
        {
            var col = cols[i].C;
            var l = Gtk.Label.New("");
            l.AddCssClass("k-fight-th");
            l.Ellipsize = Pango.EllipsizeMode.End;
            l.MaxWidthChars = 1;
            l.Xalign = col == SkillColumn.Name ? 0 : 1;
            l.SetCursorFromName("pointer");
            l.SetTooltipText(T.SortTip);
            if (cols[i].W > 0) l.SetSizeRequest(UiKit.Px(cols[i].W), -1); else l.Hexpand = true;
            var click = Gtk.GestureClick.New();
            click.OnReleased += (_, e) => { if (e.NPress == 1) sort(col); };
            l.AddController(click);
            _head[i] = l;
            head.Append(l);
        }
        Widget.Append(head);
    }

    public void Update(IReadOnlyList<SkillRow> rows, SkillOrder order, double maxShare)
    {
        for (var i = 0; i < _cols.Length; i++)
        {
            var col = _cols[i].C;
            FightKit.Set(_head[i], FightLogic.HeadText(HeadLabel(col), col, order));
            if (col == order.Column) _head[i].AddCssClass("on"); else _head[i].RemoveCssClass("on");
        }
        while (_rows.Count < rows.Count) _rows.Add(NewRow());
        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (r >= rows.Count) { row.Box.Visible = false; row.Skill = null; continue; }
            var s = rows[r];
            row.Box.Visible = true;
            row.Skill = s;
            var icon = _icons.Skill(s.SkillCode);
            if (row.Code != s.SkillCode) { row.Code = s.SkillCode; row.Swatch.QueueDraw(); }
            if (icon is not null) { if (!ReferenceEquals(row.Icon.GetPaintable(), icon)) row.Icon.SetFromPaintable(icon); }
            row.Icon.Visible = icon is not null;
            row.Swatch.Visible = icon is null;
            FightKit.Set(row.Name, s.Name);
            row.Dot.Visible = s.Hits == 0 && s.DotTicks > 0;
            for (var i = 0; i < _cols.Length; i++)
                if (row.Cells[i] is { } c) FightKit.Set(c, FightLogic.Cell(s, _cols[i].C));
        }
    }

    private Row NewRow()
    {
        var row = new Row();
        var box = UiKit.HBox();
        box.AddCssClass("k-fight-row");
        row.Box = box;
        row.Cells = new Gtk.Label[_cols.Length];
        for (var i = 0; i < _cols.Length; i++)
        {
            var col = _cols[i].C;
            if (col == SkillColumn.Name)
            {
                var name = UiKit.HBox();
                name.Hexpand = true;
                name.Valign = Gtk.Align.Center;
                row.Icon = ItemKit.Image(null, 20);
                row.Icon.MarginEnd = UiKit.Px(8);
                name.Append(row.Icon);
                row.Swatch = Gtk.DrawingArea.New();
                row.Swatch.SetSizeRequest(UiKit.Px(20), UiKit.Px(20));
                row.Swatch.MarginEnd = UiKit.Px(8);
                row.Swatch.Valign = Gtk.Align.Center;
                row.Swatch.SetDrawFunc((_, cr, w, h) =>
                {
                    Paint.RoundRect(cr, 0, 0, w, h, 4 * Icons.Scale);
                    Paint.Rgb(cr, ChartMath.SkillColor(row.Code));
                    cr.Fill();
                });
                name.Append(row.Swatch);
                row.Name = FightKit.Cell("", 12.5, Fg.Text, 0);
                name.Append(row.Name);
                row.Dot = Gtk.Label.New("DoT");
                row.Dot.AddCssClass(KitCss.SizeClass(10));
                UiKit.SetFg(row.Dot, Fg.Mute);
                row.Dot.MarginStart = UiKit.Px(6);
                row.Dot.MarginEnd = UiKit.Px(4);
                name.Append(row.Dot);
                box.Append(name);
                continue;
            }
            var share = col == SkillColumn.Share && !_accuracy;
            var l = FightKit.Cell("", share ? 12 : 12.5, col == SkillColumn.Dps ? Fg.GoldBright : share ? Fg.Dim : Fg.Text, 1);
            l.AddCssClass("k-fight-num");
            l.Hexpand = false;
            l.SetSizeRequest(UiKit.Px(_cols[i].W), -1);
            row.Cells[i] = l;
            box.Append(l);
        }
        box.HasTooltip = true;
        box.OnQueryTooltip += (_, e) =>
        {
            if (row.Skill is not { } s) return false;
            e.Tooltip.SetText($"{s.Name} ({s.SkillCode})\n{T.TipSkillDamage} {Format.Grouped(s.Damage)} · {Format.Percent(s.Share)}\n{T.TipSkillHits} {s.Hits} · {T.TipSkillDotTicks} {s.DotTicks}\n" +
                              $"{T.TipSkillMin} {Format.Compact(s.Min)} · {T.TipSkillAvg} {Format.Compact(s.Average)} · {T.TipSkillMax} {Format.Compact(s.Max)}");
            return true;
        };
        Widget.Append(box);
        return row;
    }

    private static string HeadLabel(SkillColumn column) => column switch
    {
        SkillColumn.Name => T.ThSkill, SkillColumn.Hits => T.ThHits, SkillColumn.Damage => T.ThDamage, SkillColumn.Dps => T.ThDps,
        SkillColumn.Average => T.ThAvg, SkillColumn.Max => T.ThMax, SkillColumn.Crit => T.ThCrit, SkillColumn.Share => T.ThShare,
        SkillColumn.Back => T.ThBack, SkillColumn.Front => T.ThFront, SkillColumn.Perfect => T.ThPerfect, SkillColumn.Heavy => T.ThSmite,
        SkillColumn.Multi => T.ThMulti, _ => column.ToString(),
    };
}
