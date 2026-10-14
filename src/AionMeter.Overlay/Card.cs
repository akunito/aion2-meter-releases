using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay;

/// <summary>
/// The overlay card (Avalonia OverlayWindow): header with the tab strip, fight title, time limit, state label and clock; boss HP bar; the
/// ranking; update banner; status line, Fights menu and the timer/event pills. The DPS part comes from the daemon's `overlay` snapshots
/// (OverlayBuilder); nothing is computed here. A2M-50: the tab host — a registered view (<see cref="ViewRegistry"/>) replaces the boss bar and
/// the rows in a Gtk.Stack, the pill again / Back / Esc return, the gear is the Settings pill (again: back to the tab before it), `?` the tab's help.
/// The tab rules are <see cref="TabState"/>; the host applies keyboard mode and size on <c>tabChanged</c>.
/// </summary>
internal sealed class Card
{
    private readonly OverlayClient _client;
    private readonly OverlaySettings _s;
    private readonly Gtk.Box _frame, _toolbar, _limitBox, _stateBox, _labels, _rows, _banner, _pillLines, _timers, _statusLine, _viewHost;
    private readonly Gtk.Label _title, _state, _clock, _limit, _bossHp, _bossPercent, _empty, _footer, _abyss, _announce, _timersText, _bannerText, _modeLabel, _fightsLabel;
    private readonly Gtk.Stack _stack;
    private readonly Gtk.ScrolledWindow _toolClip;
    private readonly AppShell _shell;
    private readonly TabState _tabs = new(ViewRegistry.Has);
    private readonly Dictionary<string, (Gtk.Box Pill, IconView Icon)> _tabPills = new();
    private readonly Dictionary<string, IHostedView> _views = new();
    private readonly Action<string> _tabChanged;
    private readonly Gtk.Label _colPlayer, _colGs, _colCp, _colDps, _colDamage, _colShare;
    private readonly Gtk.Box _stateDot, _statusDot;
    private readonly IconView _limitIcon;
    private readonly Gtk.Overlay _boss;
    private readonly Gtk.DrawingArea _bossBar;
    private readonly Gtk.Button _fightsButton;
    private readonly Gtk.Popover _fightsMenu;
    private readonly Gtk.Box _fightsList;
    private readonly Columns _cols = new();
    private readonly List<RowWidget> _rowWidgets = new();
    private readonly List<(Gtk.Box Pill, Gtk.Label Text, PillTone Tone)> _pills = new();
    private readonly Gtk.Box[] _lines = new Gtk.Box[3];
    private int[] _lineOf = [];
    private int _pillCount, _reqW;
    private int[] _pillNat = new int[16];
    /// <summary>Frame margins + padding + footer and line insets around the pill lines, unscaled px (measured: allocation = request − 72).</summary>
    private const int PillInset = 72;
    private double _bossFraction = -1;
    private string _language = "";
    private string _stateClass = "", _stateText = "";
    private string _statusClass = "";
    private bool _connected, _hover, _clickThrough, _fightsWanted;
    private OverlayModel? _last;

    private static Gtk.CssProvider? _css;

    public Gtk.Widget Root { get; }
    public OverlayModel? Last => _last;
    public Gtk.Widget Grip { get; }

    /// <param name="tabChanged">The tab in front changed (the host sets the keyboard mode and the size).</param>
    public Card(OverlaySettings s, AppShell shell, Action hide, Action clickThrough, Action<string> tabChanged)
    {
        _shell = shell;
        _client = shell.Daemon;
        var client = _client;
        _s = s;
        _tabChanged = tabChanged;
        Icons.Scale = s.Scale;
        LoadCss(s);

        var root = Gtk.Overlay.New();
        root.AddCssClass("card");
        Root = root;
        _frame = Box(Gtk.Orientation.Vertical, "frame");
        root.SetChild(_frame);
        var inner = Box(Gtk.Orientation.Vertical, "inner");
        inner.Vexpand = true;
        _frame.Append(inner);
        var topline = Box(Gtk.Orientation.Horizontal, "topline");
        topline.Valign = Gtk.Align.Start;
        topline.CanTarget = false;
        root.AddOverlay(topline);

        // ------------------------------------------------ header: tabs · title (toolbar on hover) · limit, state, clock
        var header = Box(Gtk.Orientation.Horizontal, "header");
        // A layer surface takes its content's natural width, and the header's natural width is the sum of its children's: the fight or
        // settings pill plus a long state label ("SAVED FIGHT   12.10 19:30", ES "COMBATE GUARDADO …") widened the card 560 → 642 px
        // surface (622 visible, measured 2026-10-13 in a nested sway). The whole header sits in a clipping scroller (external policy:
        // natural width = 0, the child still gets the full width and at least its minimum), so no header widget can ever widen the card;
        // inside it the box still hands space to the state label up to its natural width before the expanding title gets any.
        var headerClip = Gtk.ScrolledWindow.New();
        headerClip.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        headerClip.PropagateNaturalHeight = true;
        headerClip.Child = header;
        inner.Append(headerClip);
        var tabs = Box(Gtk.Orientation.Horizontal, "tabs");
        tabs.Valign = Gtk.Align.Center;
        // the pills' icons expand inside their pill; set explicitly so that does not propagate and stretch the strip when the title hides
        tabs.Hexpand = false;
        // A2M-50: every pill switches this card in place (the tools window is only a fallback for the views not ported yet: Hyper+Shift+M)
        foreach (var spec in ViewRegistry.Strip) tabs.Append(TabPill(spec.Key, spec.Icon));
        // the breakdown gets a pill only while it is in front (WPF TabFight); the gear is the Settings pill (TabState.PillVisible)
        tabs.Append(TabPill(TabState.Fight, Icons.Fight));
        var gear = TabPill(TabState.Settings, Icons.Gear);
        gear.MarginStart = Icons.Px(2);
        tabs.Append(gear);
        var help = Tool(Icons.Help, UiText.Current.TipHelp, () => _shell.ShowHelp(_tabs.Current is TabState.Meter or TabState.Fight ? "meter" : _tabs.Current));
        tabs.Append(help);
        header.Append(tabs);

        _title = Label("title", 0);
        _title.Ellipsize = Pango.EllipsizeMode.End;
        _title.Hexpand = true;
        // a layer surface takes its content's natural width: a long title ("Waiting for combat…") widened the card past its size (measured
        // 2026-10-13, 560 → 612 px); a 1-char natural width lets it ellipsize inside the size the user set
        _title.MaxWidthChars = 1;
        header.Append(_title);

        _toolbar = Box(Gtk.Orientation.Horizontal, null);
        _toolbar.Hexpand = true;
        _toolbar.Visible = false;
        _toolbar.Valign = Gtk.Align.Center;
        _modeLabel = Gtk.Label.New("");
        var mode = Gtk.Button.New();
        mode.AddCssClass("chip");
        mode.Child = _modeLabel;
        mode.SetTooltipText("Boss only / everything (what the meter counts)");
        mode.OnClicked += (_, _) => _client.Send("set-measure", "mode", _last?.Mode == TargetMode.BossOnly ? "all" : "boss");
        Hand(mode);
        _toolbar.Append(mode);
        _toolbar.Append(Tool(Icons.Reset, "Restart the meter (closes and saves the fight so far)", () => _client.Send("reset")));
        _toolbar.Append(Tool(Icons.Pointer, "Click-through: the mouse goes to the game (SIGUSR1 / the Sway hotkey turns it back off)", clickThrough));
        _toolbar.Append(Tool(Icons.Hide, "Hide the card (SIGUSR2 / the Sway hotkey shows it again; the daemon keeps running)", hide));
        // the strip with the gear and `?` (A2M-50) plus this toolbar no longer fit 560 px: in a clipping scroller (external policy = no
        // minimum width) the toolbar's right end is cut instead of the card growing (it grew to 616 px on hover, measured)
        var toolClip = Gtk.ScrolledWindow.New();
        toolClip.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
        toolClip.PropagateNaturalHeight = true;
        toolClip.Hexpand = true;
        toolClip.Valign = Gtk.Align.Center;
        toolClip.Child = _toolbar;
        _toolClip = toolClip;
        toolClip.Visible = false;
        header.Append(toolClip);

        _limitBox = Box(Gtk.Orientation.Horizontal, "limitbox");
        _limitBox.Valign = Gtk.Align.Center;
        _limitBox.Visible = false;
        _limitIcon = Icons.View(Icons.Stopwatch, 0xF0C060, 11, 1.6);
        _limitBox.Append(_limitIcon.Widget);
        _limit = Label("limit", 0);
        _limitBox.Append(_limit);
        header.Append(_limitBox);
        _stateBox = Box(Gtk.Orientation.Horizontal, "statebox");
        _stateBox.Valign = Gtk.Align.Center;
        _stateDot = Box(Gtk.Orientation.Horizontal, "dot");
        _stateDot.Valign = Gtk.Align.Center;
        _stateBox.Append(_stateDot);
        _state = Label("state", 0);
        // its minimum is "…": with no room the label ellipsizes (full text in the tooltip) instead of clipping the clock
        _state.Ellipsize = Pango.EllipsizeMode.End;
        _stateBox.Append(_state);
        header.Append(_stateBox);
        _clock = Label("clock", 1);
        _clock.SetText("0:00");
        header.Append(_clock);

        // ------------------------------------------------ boss HP: a slim bar
        _boss = Gtk.Overlay.New();
        _boss.AddCssClass("boss");
        _boss.Visible = false;
        _bossBar = Gtk.DrawingArea.New();
        _bossBar.SetDrawFunc(DrawBoss);
        _boss.SetChild(_bossBar);
        var bossText = Box(Gtk.Orientation.Horizontal, null);
        _bossHp = Label("bosstext", 0);
        _bossHp.Hexpand = true;
        _bossPercent = Label("bosstext", 1);
        bossText.Append(_bossHp);
        bossText.Append(_bossPercent);
        _boss.AddOverlay(bossText);
        _boss.SetMeasureOverlay(bossText, true);
        var meterPage = Box(Gtk.Orientation.Vertical, null);
        meterPage.Append(_boss);

        // ------------------------------------------------ party (labels and rows share their column widths)
        var party = Gtk.Overlay.New();
        party.AddCssClass("party");
        party.Vexpand = true;
        var partyBox = Box(Gtk.Orientation.Vertical, null);
        party.SetChild(partyBox);
        _labels = Box(Gtk.Orientation.Horizontal, "cols");
        _labels.Visible = false;
        var t = UiText.Current;
        var hash = Label("col", 0.5f);
        hash.SetText("#");
        hash.SetSizeRequest(Icons.Px(24), -1);
        var hashSlot = Box(Gtk.Orientation.Horizontal, null);
        hashSlot.SetSizeRequest(Icons.Px(30 + 32), -1);   // rank + emblem slots
        hashSlot.Append(hash);
        _labels.Append(hashSlot);
        _labels.Append(_colPlayer = Col(t.ColPlayer, _cols.Name, 0));
        _labels.Append(_colGs = Col(t.ColGs, _cols.Gs, 1));
        _labels.Append(_colCp = Col(t.ColCp, _cols.Cp, 1));
        var spacer = Box(Gtk.Orientation.Horizontal, null);
        spacer.Hexpand = true;
        _labels.Append(spacer);
        var ruleSlot = Box(Gtk.Orientation.Horizontal, null);
        ruleSlot.SetSizeRequest(Icons.Px(11), -1);   // the rows' rule: 1 px + 5 px margins
        _labels.Append(ruleSlot);
        _labels.Append(_colDps = Col(t.ColDps, _cols.Dps, 1));
        _labels.Append(_colDamage = Col(t.ColDamage, _cols.Damage, 1));
        _labels.Append(_colShare = Col(t.ColShare, _cols.Share, 1));
        partyBox.Append(_labels);
        _rows = Box(Gtk.Orientation.Vertical, null);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.Child = _rows;
        scroll.Vexpand = true;
        partyBox.Append(scroll);
        _empty = Label("empty", 0.5f);
        _empty.Wrap = true;
        _empty.Justify = Gtk.Justification.Center;
        _empty.Halign = Gtk.Align.Center;
        _empty.Valign = Gtk.Align.Center;
        _empty.CanTarget = false;
        _empty.SetText(t.EmptyParty);
        party.AddOverlay(_empty);
        meterPage.Append(party);

        // ------------------------------------------------ the tab host: over the boss bar and the rows, between the header and the footer (WPF TabHost)
        _viewHost = Box(Gtk.Orientation.Vertical, null);
        _viewHost.Vexpand = true;
        _stack = Gtk.Stack.New();
        _stack.Vexpand = true;
        _stack.Hhomogeneous = false;
        _stack.Vhomogeneous = false;
        _stack.TransitionType = Gtk.StackTransitionType.None;
        _stack.AddNamed(meterPage, TabState.Meter);
        _stack.AddNamed(_viewHost, "view");
        _stack.SetVisibleChildName(TabState.Meter);
        inner.Append(_stack);

        // ------------------------------------------------ footer
        var footer = Box(Gtk.Orientation.Vertical, "footer");
        _banner = Box(Gtk.Orientation.Horizontal, "banner");
        _banner.Visible = false;
        _banner.Append(Icons.View(Icons.Download, 0xD2FAE6, 12).Widget);
        _bannerText = Label(null, 0);
        _bannerText.Ellipsize = Pango.EllipsizeMode.End;
        _banner.Append(_bannerText);
        footer.Append(_banner);

        var statusLine = _statusLine = Box(Gtk.Orientation.Horizontal, "statusline");
        _statusDot = Box(Gtk.Orientation.Horizontal, "sdot");
        _statusDot.Valign = Gtk.Align.Center;
        statusLine.Append(_statusDot);
        _footer = Label("status", 0);
        _footer.Ellipsize = Pango.EllipsizeMode.End;
        _footer.Hexpand = true;
        statusLine.Append(_footer);
        var fightsRow = Box(Gtk.Orientation.Horizontal, null);
        var fightsLabel = _fightsLabel = Gtk.Label.New(t.Fights);
        fightsRow.Append(fightsLabel);
        fightsRow.Append(Icons.View(Icons.Chevron, 0xD9B56A, 9, 1.6).Widget);
        _fightsButton = Gtk.Button.New();
        _fightsButton.AddCssClass("linkbtn");
        _fightsButton.Child = fightsRow;
        _fightsButton.Valign = Gtk.Align.Center;
        Hand(_fightsButton);
        _fightsButton.OnClicked += (_, _) => { _fightsWanted = true; _client.Send("fights", "max", 40); };
        statusLine.Append(_fightsButton);
        footer.Append(statusLine);
        _abyss = Label("abyss", 0);
        _abyss.Ellipsize = Pango.EllipsizeMode.End;
        _abyss.Visible = false;
        footer.Append(_abyss);
        // A2M-48 phase 3c: the boss-respawn alert (WPF balloon) as a line for 60 s; a click opens Timers
        _announce = Label("announce", 0);
        _announce.Ellipsize = Pango.EllipsizeMode.End;
        _announce.Visible = false;
        Hand(_announce);
        Click(_announce, () => SelectTab("timers"));
        footer.Append(_announce);

        _pillLines = Box(Gtk.Orientation.Vertical, "pillrow");
        for (var i = 0; i < _lines.Length; i++) { _lines[i] = Box(Gtk.Orientation.Horizontal, null); _pillLines.Append(_lines[i]); }
        _timers = Box(Gtk.Orientation.Horizontal, "pill");
        _timers.AddCssClass("timers");
        _timers.Append(Icons.View(Icons.Clock, 0x1A140A, 12, 1.6).Widget);
        _timersText = Label(null, 0);
        _timers.Append(_timersText);
        Hand(_timers);
        Click(_timers, () => SelectTab(_tabs.Current == "timers" ? TabState.Meter : "timers"));
        footer.Append(_pillLines);
        inner.Append(footer);

        _fightsList = Box(Gtk.Orientation.Vertical, null);
        var menuScroll = Gtk.ScrolledWindow.New();
        menuScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        menuScroll.PropagateNaturalHeight = true;
        menuScroll.PropagateNaturalWidth = true;
        menuScroll.MaxContentHeight = (int)Math.Round(380 * s.Scale);
        menuScroll.Child = _fightsList;
        _fightsMenu = Gtk.Popover.New();
        _fightsMenu.AddCssClass("fights");
        _fightsMenu.SetHasArrow(false);
        _fightsMenu.SetPosition(Gtk.PositionType.Top);
        _fightsMenu.Child = menuScroll;
        _fightsMenu.SetParent(_fightsButton);

        // ------------------------------------------------ resize grip
        // gold, brighter under the pointer: Sway's floating_modifier (Alt+drag / Alt+right-drag) never reaches a layer surface, so this
        // corner is the visible resize handle (a right-button drag anywhere also resizes) and must be seen on every tab (the dim 6E5A33
        // mark was missed, Diego 2026-10-13)
        var gripIcon = Icons.View(Icons.Grip, 0xC9A45C, 14, 1.5);
        var grip = gripIcon.Widget;
        grip.CanTarget = true;
        var gripHover = Gtk.EventControllerMotion.New();
        gripHover.OnEnter += (_, _) => gripIcon.Rgb = 0xFFE3A6;
        gripHover.OnLeave += (_, _) => gripIcon.Rgb = 0xC9A45C;
        grip.AddController(gripHover);
        grip.AddCssClass("grip");
        grip.Halign = Gtk.Align.End;
        grip.Valign = Gtk.Align.End;
        grip.SetCursorFromName("se-resize");
        root.AddOverlay(grip);
        Grip = grip;

        // The card stays clean: its buttons only appear while the mouse is over it, in place of the title and the state label.
        var motion = Gtk.EventControllerMotion.New();
        motion.OnEnter += (_, _) => { _hover = true; UpdateHover(); };
        motion.OnLeave += (_, _) => { _hover = false; UpdateHover(); };
        root.AddController(motion);

        client.Overlay += Apply;
        client.ConnectionChanged += OnConnection;
        client.Fights += ShowFights;
        OnConnection(false);
        shell.SelectTabHandler = SelectTab;
        shell.FightHandler = ShowFight;
        shell.CurrentTab = () => _tabs.Current;
        shell.LanguageChanged += ApplyLanguage;
        SyncPills();
    }

    /// <summary>A rescale builds a new card: the old one stops listening to the socket and the shell, its views are dropped.</summary>
    public void Detach()
    {
        _client.Overlay -= Apply;
        _client.ConnectionChanged -= OnConnection;
        _client.Fights -= ShowFights;
        _shell.LanguageChanged -= ApplyLanguage;
        foreach (var v in _views.Values) v.Detach();
        _views.Clear();
    }

    /// <summary>The card's sheet + the views' kit (KitCss), scaled; one display-wide provider, reloaded on a rescale or an opacity change.</summary>
    public static void LoadCss(OverlaySettings s)
    {
        if (_css is null)
        {
            _css = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(Gdk.Display.GetDefault()!, _css, 800);   // GTK_STYLE_PROVIDER_PRIORITY_USER
        }
        _css.LoadFromString(CardText.ScaleCss(Css.Sheet(s.Opacity) + KitCss.Sheet(), s.Scale));
    }

    // ------------------------------------------------------------ tab host (A2M-50, the Avalonia OverlayWindow.SelectTab)

    /// <summary>The tab in front ("meter" = the DPS rows).</summary>
    public string Tab => _tabs.Current;

    /// <summary>Shows a tab inside the card ("meter" = the DPS rows); unknown or unported keys are ignored.</summary>
    public void SelectTab(string tab)
    {
        var prev = _tabs.Current;
        if (!_tabs.Select(tab)) { SyncPills(); return; }
        if (_views.TryGetValue(prev, out var old)) old.Hidden();
        var now = _tabs.Current;
        var meter = now == TabState.Meter;
        _statusLine.Visible = meter;   // the DPS status and the fight links belong to the DPS tab
        if (meter)
        {
            ClearHost();
            _stack.SetVisibleChildName(TabState.Meter);
        }
        else
        {
            var v = View(now);
            ClearHost();
            _viewHost.Append(v.View);
            _stack.SetVisibleChildName("view");
            v.Shown();
        }
        SyncPills();
        _shell.SaveLastTab(now);
        _tabChanged(now);
    }

    /// <summary>Esc (the window's key controller): a tab in front goes back; false on the DPS rows (nothing to do).</summary>
    public bool Escape()
    {
        if (_tabs.Current == TabState.Meter) return false;
        SelectTab(_tabs.Back());
        return true;
    }

    private void ClearHost()
    {
        while (_viewHost.GetFirstChild() is { } c) _viewHost.Remove(c);
    }

    private IHostedView View(string key)
    {
        if (_views.TryGetValue(key, out var v)) return v;
        v = ViewRegistry.Create(key, _shell)!;
        v.Hosted = true;
        v.CloseRequested += () => SelectTab(key == TabState.Settings ? _tabs.BeforeSettings : TabState.Meter);
        _views[key] = v;
        return v;
    }

    /// <summary>A2M-43: the fight's breakdown / player card as this card's "fight" tab (a row click, Top DPS); null until a FightView is registered.</summary>
    private IFightTab? ShowFight(Action<IFightTab> setup)
    {
        if (!ViewRegistry.Has(TabState.Fight) || View(TabState.Fight) is not IFightTab f) return null;
        _tabs.FightOpen = true;
        setup(f);
        if (_tabs.Current == TabState.Fight) ((IHostedView)f).Shown();
        else SelectTab(TabState.Fight);
        return f;
    }

    private void SyncPills()
    {
        var cur = _tabs.Current;
        foreach (var (key, (pill, icon)) in _tabPills)
        {
            var on = key == cur;
            var usable = key == TabState.Meter || ViewRegistry.Has(key);
            if (on) pill.AddCssClass("on"); else pill.RemoveCssClass("on");
            if (usable) pill.AddCssClass("live"); else pill.RemoveCssClass("live");
            icon.Rgb = on ? 0x1A1407u : 0xF3D58Fu;
        }
        _tabPills[TabState.Fight].Pill.Visible = TabState.PillVisible(TabState.Fight, cur);
    }

    private void ApplyLanguage()
    {
        var t = UiText.Current;
        RowWidget.Set(_fightsLabel, t.Fights);
        RowWidget.Set(_empty, t.EmptyParty);
        foreach (var (key, (pill, _)) in _tabPills) pill.SetTooltipText(PillTip(key));
        foreach (var v in _views.Values) v.LanguageChanged();
    }

    private static string PillTip(string key)
    {
        var t = UiText.Current;
        var tip = key switch { TabState.Fight => t.FightTab, TabState.Settings => t.SettingsTitle, _ => Array.Find(ViewRegistry.Strip, s => s.Key == key)?.Tip(t) ?? key };
        return key == TabState.Meter || ViewRegistry.Has(key) ? tip : string.Format(t.LinuxNotYet, tip) + " — Hyper+Shift+M";
    }

    /// <summary>WPF TabPill: icon toggle, the selected one filled gold; clicking the selected one goes back (TabState.PillTarget).</summary>
    private Gtk.Box TabPill(string key, Icon icon)
    {
        var b = Box(Gtk.Orientation.Horizontal, "tab");
        var v = Icons.View(icon, 0xF3D58F, 13, 1.5);
        v.Widget.Hexpand = true;
        b.Append(v.Widget);
        b.SetTooltipText(PillTip(key));
        Hand(b);
        // a click, not a drag: the card's GestureDrag still moves it from a pill pressed and dragged
        Click(b, () => SelectTab(_tabs.PillTarget(key)));
        _tabPills[key] = (b, v);
        return b;
    }

    /// <summary>A single click (released without a drag) on <paramref name="w"/>.</summary>
    private static void Click(Gtk.Widget w, Action click)
    {
        var g = Gtk.GestureClick.New();
        g.OnReleased += (_, e) => { if (e.NPress == 1) click(); };
        w.AddController(g);
    }

    /// <summary>The new card shows what the old one showed until the next snapshot.</summary>
    public void Seed(OverlayModel? last)
    {
        if (!_client.Connected) return;
        OnConnection(true);
        if (last is not null) Apply(last);
    }

    private static Gtk.Box Box(Gtk.Orientation o, string? css)
    {
        var b = Gtk.Box.New(o, 0);
        if (css is not null) b.AddCssClass(css);
        return b;
    }

    private static Gtk.Label Label(string? css, float xalign)
    {
        var l = Gtk.Label.New("");
        if (css is not null) l.AddCssClass(css);
        l.Xalign = xalign;
        l.Valign = Gtk.Align.Center;
        return l;
    }

    private static Gtk.Label Col(string text, Gtk.SizeGroup group, float xalign)
    {
        var l = Label("col", xalign);
        l.SetText(text);
        if (xalign > 0) l.AddCssClass("num");
        group.AddWidget(l);
        return l;
    }

    private static void Hand(Gtk.Widget w) => w.SetCursorFromName("pointer");

    private static Gtk.Button Tool(Icon icon, string tip, Action click)
    {
        var v = Icons.View(icon, 0xA3ACBF, 12, 1.5);
        var b = Gtk.Button.New();
        b.AddCssClass("tool");
        b.Child = v.Widget;
        b.SetTooltipText(tip);
        Hand(b);
        b.OnClicked += (_, _) => click();
        var motion = Gtk.EventControllerMotion.New();
        motion.OnEnter += (_, _) => v.Rgb = 0xECCB82;
        motion.OnLeave += (_, _) => v.Rgb = 0xA3ACBF;
        b.AddController(motion);
        return b;
    }

    public void SetSize(int w, int h)
    {
        Root.SetSizeRequest(w, h);
        if (w == _reqW) return;
        _reqW = w;
        LayoutPills(_pillCount);
    }

    /// <summary>The narrowest card (px) the footer pills fit into at their current texts.</summary>
    public int PillMinWidth() => PillWrap.MinWidth(_pillNat.AsSpan(0, _pillCount + 1), _lines.Length) + (int)Math.Round(PillInset * _s.Scale);

    /// <summary>The smallest surface the content allows (px, frame margins included): Root's own size request does not count here.</summary>
    public (int W, int H) ContentMin()
    {
        _frame.Measure(Gtk.Orientation.Horizontal, -1, out var w, out _, out _, out _);
        _frame.Measure(Gtk.Orientation.Vertical, -1, out var h, out _, out _, out _);
        return (w, h);
    }

    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        if (on) _frame.AddCssClass("through"); else _frame.RemoveCssClass("through");
        UpdateHover();
    }

    private void UpdateHover()
    {
        var tools = _hover && _connected && !_clickThrough;
        _toolbar.Visible = tools;
        _toolClip.Visible = tools;
        _title.Visible = !tools;
        _stateBox.Visible = !tools;
    }

    private void OnConnection(bool on)
    {
        _connected = on;
        UpdateHover();
        if (on) return;
        // No daemon: the card says so instead of showing a stale fight.
        _last = null;
        RowWidget.Set(_title, "No daemon");
        SetState("OFFLINE", "", false);
        RowWidget.Set(_clock, "—");
        _limitBox.Visible = false;
        _boss.Visible = false;
        _labels.Visible = false;
        _empty.Visible = true;
        RowWidget.Set(_empty, CardText.NoDaemon(_client.Path));
        foreach (var r in _rowWidgets) r.Root.Visible = false;
        SetStatusTone(StatusTone.Warn);
        RowWidget.Set(_footer, "Connecting to the daemon…");
        _footer.SetTooltipText(null);
        _abyss.Visible = false;
        _announce.Visible = false;
        _banner.Visible = false;
        RowWidget.Set(_timersText, UiText.Current.TimersButton);
        for (var i = 0; i < _pills.Count; i++) _pills[i].Pill.Visible = false;
        LayoutPills(0);
    }

    private void SetState(string text, string cls, bool live)
    {
        if (!string.Equals(_stateText, text, StringComparison.Ordinal)) { _stateText = text; _state.SetText(text); _state.SetTooltipText(text); }
        if (_stateClass != cls)
        {
            if (_stateClass.Length > 0) { _state.RemoveCssClass(_stateClass); _stateDot.RemoveCssClass(_stateClass); }
            if (cls.Length > 0) { _state.AddCssClass(cls); _stateDot.AddCssClass(cls); }
            _stateClass = cls;
        }
        // A pulse driven by the 5 Hz snapshots (1.6 s cycle): a CSS animation would redraw at the monitor's refresh rate during the
        // whole fight, next to the game.
        _stateDot.Opacity = live ? 0.25 + 0.75 * Math.Abs(Math.Cos(Environment.TickCount64 / 1600.0 * Math.PI)) : 1;
    }

    private void SetStatusTone(StatusTone tone)
    {
        var cls = tone switch { StatusTone.Good => "good", StatusTone.Error => "error", StatusTone.Mute => "mute", _ => "" };
        if (_statusClass == cls) return;
        if (_statusClass.Length > 0) _statusDot.RemoveCssClass(_statusClass);
        if (cls.Length > 0) _statusDot.AddCssClass(cls);
        _statusClass = cls;
    }

    private void Apply(OverlayModel m)
    {
        _last = m;
        if (m.Language != _language)
        {
            _language = m.Language;
            UiText.Use(m.Language);
            var t = UiText.Current;
            RowWidget.Set(_colPlayer, t.ColPlayer); RowWidget.Set(_colGs, t.ColGs); RowWidget.Set(_colCp, t.ColCp);
            RowWidget.Set(_colDps, t.ColDps); RowWidget.Set(_colDamage, t.ColDamage); RowWidget.Set(_colShare, t.ColShare);
        }
        var f = m.Fight;
        RowWidget.Set(_title, CardText.Title(f));
        _title.SetTooltipText(f.TitleTip.Length > 0 ? f.TitleTip : null);
        SetState(CardText.HeaderLabel(m.HeaderLabel, m.HeaderWhen),
            m.View switch { OverlayView.Live => "live", OverlayView.Saved or OverlayView.Session => "saved", _ => "" }, m.View == OverlayView.Live);
        RowWidget.Set(_clock, f.Clock);
        RowWidget.Set(_modeLabel, m.ModeLabel);
        if (m.Limit is { } limit)
        {
            _limitBox.Visible = true;
            RowWidget.Set(_limit, limit.Text);
            if (limit.Urgent) _limit.AddCssClass("urgent"); else _limit.RemoveCssClass("urgent");
            _limitIcon.Rgb = limit.Urgent ? 0xEF4D56u : 0xF0C060u;
            _limitBox.SetTooltipText(limit.Tip);
        }
        else _limitBox.Visible = false;

        if (f.Boss is { } boss)
        {
            _boss.Visible = true;
            if (_bossFraction != boss.Fraction) { _bossFraction = boss.Fraction; _bossBar.QueueDraw(); }
            RowWidget.Set(_bossHp, boss.HpText);
            RowWidget.Set(_bossPercent, boss.Percent);
        }
        else _boss.Visible = false;

        _labels.Visible = f.HasData;
        _colGs.Visible = f.HasGear;
        _colCp.Visible = f.HasGear;
        _empty.Visible = !f.HasData;
        RowWidget.Set(_empty, UiText.Current.EmptyParty);
        var rows = f.Rows;
        while (_rowWidgets.Count < rows.Length)
        {
            var rw = new RowWidget(_cols);
            rw.Clicked += actor => _shell.OpenFight(_last?.FightId ?? "live", actor);
            _rowWidgets.Add(rw);
            _rows.Append(rw.Root);
        }
        for (var i = 0; i < _rowWidgets.Count; i++)
        {
            var rw = _rowWidgets[i];
            if (i >= rows.Length) { rw.Root.Visible = false; continue; }
            rw.Root.Visible = true;
            rw.Update(rows[i], f.HasGear);
        }

        SetStatusTone(m.StatusTone);
        RowWidget.Set(_footer, m.FooterText);
        _footer.SetTooltipText(m.Status.Length > 0 ? m.Status : null);
        _abyss.Visible = m.AbyssLogs is not null;
        if (m.AbyssLogs is { } al) RowWidget.Set(_abyss, al);
        _announce.Visible = m.Announcement is not null;
        if (m.Announcement is { } an) RowWidget.Set(_announce, an);
        _banner.Visible = m.UpdateBanner is not null;
        if (m.UpdateBanner is { } ub) RowWidget.Set(_bannerText, ub);

        RowWidget.Set(_timersText, m.TimersLabel);
        var pills = m.Pills;
        for (var i = 0; i < pills.Length; i++)
        {
            if (i >= _pills.Count)
            {
                var pill = Box(Gtk.Orientation.Horizontal, "pill");
                var text = Label(null, 0);
                pill.Append(text);
                _pills.Add((pill, text, (PillTone)255));
            }
            var (b, tb, tone) = _pills[i];
            b.Visible = true;
            RowWidget.Set(tb, pills[i].Text);
            if (tone != pills[i].Tone)
            {
                if (tone != (PillTone)255) b.RemoveCssClass(CardText.PillClass(tone));
                b.AddCssClass(CardText.PillClass(pills[i].Tone));
                _pills[i] = (b, tb, pills[i].Tone);
            }
        }
        for (var i = pills.Length; i < _pills.Count; i++) _pills[i].Pill.Visible = false;
        LayoutPills(pills.Length);
    }

    /// <summary>
    /// GTK4 has no wrap panel (FlowBox lines its children up in columns): the timers pill and the event pills go into up to three
    /// lines by their natural widths. Children are only moved when a pill changes line (countdowns change text, rarely width).
    /// </summary>
    private void LayoutPills(int count)
    {
        _pillCount = count;
        // Measured in the nested sway (2026-10-14): lines laid out from the allocation kept a card that had been wider from shrinking (the
        // longest line is the card's minimum width: 420 → 530 → resized back to 470 stayed 487), with the grip and the right-drag alike. The
        // allocation lags a resize by a frame; the lines get the requested width minus PillInset (allocation 348 at a 420 request, 398 at 470).
        var avail = (int)Math.Round(((_reqW > 0 ? _reqW / _s.Scale : _s.Width) - PillInset) * _s.Scale);
        var n = count + 1;
        if (_pillNat.Length < n) _pillNat = new int[Math.Max(n, 2 * _pillNat.Length)];
        for (var i = 0; i < n; i++)
        {
            var w = i == 0 ? _timers : _pills[i - 1].Pill;
            w.Measure(Gtk.Orientation.Horizontal, -1, out _, out _pillNat[i], out _, out _);
        }
        Span<int> line = stackalloc int[n];
        PillWrap.Lines(_pillNat.AsSpan(0, n), avail, line, _lines.Length);
        var same = _lineOf.Length == n;
        for (var i = 0; same && i < n; i++) same = _lineOf[i] == line[i];
        if (same) return;
        for (var i = 0; i < _lineOf.Length; i++)
        {
            var w = i == 0 ? _timers : _pills[i - 1].Pill;
            if (w.GetParent() is Gtk.Box p) p.Remove(w);
        }
        _lineOf = line.ToArray();
        for (var i = 0; i < n; i++) _lines[line[i]].Append(i == 0 ? _timers : _pills[i - 1].Pill);
        for (var i = 0; i < _lines.Length; i++) _lines[i].Visible = Array.IndexOf(_lineOf, i) >= 0;
    }

    private void ShowFights(OverlayFightItem[] items)
    {
        if (!_fightsWanted) return;
        _fightsWanted = false;
        while (_fightsList.GetFirstChild() is { } c) _fightsList.Remove(c);
        var t = UiText.Current;
        _fightsList.Append(MenuItem($"● {t.Live}", "live"));
        var shown = _last?.FightId;
        foreach (var it in items) _fightsList.Append(MenuItem(CardText.FightItem(it, shown, t), it.Id));
        if (items.Length == 0)
        {
            var none = Gtk.Label.New(t.NoFights);
            none.AddCssClass("off");
            none.Xalign = 0;
            _fightsList.Append(none);
        }
        _fightsMenu.Popup();
    }

    private Gtk.Button MenuItem(string text, string id)
    {
        var l = Gtk.Label.New(text);
        l.Xalign = 0;
        var b = Gtk.Button.New();
        b.Child = l;
        Hand(b);
        b.OnClicked += (_, _) => { _client.Send("show-fight", "id", id); _fightsMenu.Popdown(); };
        return b;
    }

    private void DrawBoss(Gtk.DrawingArea area, Cairo.Context cr, int w, int h)
    {
        const double r = 6;
        Paint.RoundRect(cr, 0.5, 0.5, w - 1, h - 1, r);
        Paint.Rgb(cr, 0x230C0F);
        cr.FillPreserve();
        Paint.Rgb(cr, 0x5E1F24);
        cr.LineWidth = 1;
        cr.Stroke();
        var fw = Math.Clamp(_bossFraction, 0, 1) * (w - 2);
        if (fw >= 1)
        {
            Paint.RoundRect(cr, 1, 1, fw, h - 2, Math.Min(r, fw / 2));
            Paint.FillWith(cr, Paint.HpFill, 1, 1, fw, h - 2);
        }
        Paint.RoundRect(cr, 1, 1, w - 2, Math.Min(9, h - 2), r, r, 0, 0);
        Paint.Argb(cr, 0x14FFFFFF);
        cr.Fill();
    }
}
