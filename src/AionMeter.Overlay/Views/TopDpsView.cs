using System.Net.Http;
using AionMeter.Core.Armory;
using AionMeter.Core.Events;
using AionMeter.Core.Leaderboard;
using AionMeter.Core.Ranking;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Top DPS (WPF TopDpsView, A2M-48 phase 3c) on the layer-shell card (A2M-50, port of the Avalonia TopDpsView, behaviour unchanged): "My fights" =
/// the ten best DPS per class from top-dps.json (the daemon writes it after each fight; re-read by mtime), each row expandable into skill shares,
/// opener, repeated sequences and cadence, Open = the saved fight's breakdown; "Server …" / region = the strongest of the class by combat power
/// from aion2index.com, read by this process when the user asks (the Core client, one page per 400 ms, daily snapshot in armory/leaderboard).
/// Load profile = the A2M-44 player card in the card's fight tab: a fight row opens its fight on Profile, a ranking row the card alone; the
/// official page is asked only when nothing fresh is cached.
/// </summary>
internal sealed class TopDpsView : HostedView
{
    private static UiText T => UiText.Current;
    // survive the card's rebuild on a rescale (the Avalonia view lives as long as the app)
    private static readonly HashSet<string> Expanded = new();
    private static GameClass _class = GameClass.Unknown;
    private static int _scope;     // 0 = own fights, 1 = own server, 2 = region
    private static int _openSeq;   // bumps on every fight / card this tab opens: a late profile answer only lands on the newest

    private readonly Gtk.Label _count, _empty, _abyss;
    private readonly Gtk.Button _scopeHistory, _scopeServer, _scopeRegion;
    private readonly Gtk.Box _loading, _list = UiKit.VBox();
    private readonly BarView _loadingBar;
    private readonly WrapLines _classTabs;
    private readonly UiTimer _clock = new(TimeSpan.FromSeconds(2));
    // the bar's sweep while a ranking loads: 5 steps a second on a 180 px bar, not a frame-rate animation next to the game
    private readonly UiTimer _sweep = new(TimeSpan.FromMilliseconds(200));
    private int _boardLoad;        // bumps on every scope/class change so a late answer is dropped

    public TopDpsView(AppShell shell) : base(shell, "top", UiText.Current.TopTitle)
    {
        TopTimersKit.EnsureCss(shell.UiScale);
        _count = UiKit.Line("", 12, Fg.Mute);
        _count.Hexpand = false;
        _count.MaxWidthChars = 40;   // Avalonia MaxWidth 260 at 12 px; the header must not widen the card
        _count.Xalign = 1;
        HeaderRight.Append(_count);

        var top = UiKit.VBox();
        // Avalonia pulls the toggles 4 px left (Margin -4); GTK warns on negative margins (measured), so the side is 14 and the texts get 4
        UiKit.Margin(top, 14, 0, 18, 8);
        var scopes = UiKit.HBox();
        scopes.MarginBottom = UiKit.Px(2);
        scopes.Append(_scopeHistory = TopTimersKit.TabToggle(T.TopScopeHistory, () => SetScope(0)));
        scopes.Append(_scopeServer = TopTimersKit.TabToggle("…", () => SetScope(1)));
        scopes.Append(_scopeRegion = TopTimersKit.TabToggle(T.TopScopeGlobal, () => SetScope(2)));
        top.Append(scopes);
        // the view frame (2 px margins + border) and the 14/18 px sides: what a 560 px card leaves for the class line
        _classTabs = new WrapLines(TabState.MinTabWidth - 6 - 32);
        _classTabs.Widget.MarginBottom = UiKit.Px(4);
        top.Append(_classTabs.Widget);
        top.Append(_abyss = UiKit.Text("", 12, Fg.GoldBright));
        _abyss.MarginTop = _abyss.MarginBottom = UiKit.Px(2);
        _abyss.MarginStart = UiKit.Px(4);
        _abyss.Visible = false;

        var host = Gtk.Overlay.New();
        var listBox = UiKit.VBox();
        listBox.Append(_list);
        listBox.Append(_empty = UiKit.Text(T.TopEmpty, 12.5, Fg.Mute));
        _empty.Justify = Gtk.Justification.Center;
        _empty.Xalign = 0.5f;
        UiKit.Margin(_empty, 30, 40, 30, 0);
        _empty.Visible = false;
        host.Child = listBox;
        _loading = UiKit.VBox();
        _loading.AddCssClass("k-tt-loading");
        _loading.Halign = Gtk.Align.Center;
        _loading.Valign = Gtk.Align.Start;
        _loadingBar = UiKit.Bar(0.25);
        _loadingBar.Widget.SetSizeRequest(UiKit.Px(180), Math.Max(1, UiKit.Px(4)));
        _loadingBar.Widget.MarginBottom = UiKit.Px(10);
        _loading.Append(_loadingBar.Widget);
        var loadingText = UiKit.Text(T.TopLoading, 13);
        loadingText.MaxWidthChars = -1;
        loadingText.Xalign = 0.5f;
        _loading.Append(loadingText);
        _loading.Visible = false;
        host.AddOverlay(_loading);
        var scroll = UiKit.Scroll(host);
        scroll.MarginBottom = UiKit.Px(12);
        var body = UiKit.VBox();
        body.Append(top);
        body.Append(scroll);
        SetBody(body);

        _clock.Tick += Poll;
        _sweep.Tick += () => _loadingBar.Fraction = _loadingBar.Fraction >= 1 ? 0.1 : _loadingBar.Fraction + 0.1;
        OnDetach(_sweep.Stop);
        Action ready = () => { if (IsShown) Reload(); };
        Action<string> closed = _ => { if (IsShown) Poll(); };
        Listen(() => Shell.EngineReady += ready, () => Shell.EngineReady -= ready);
        Listen(() => Shell.Daemon.FightClosed += closed, () => Shell.Daemon.FightClosed -= closed);
    }

    public override void Shown()
    {
        Shell.WhenEngine(e => { e.Top.ReloadIfChanged(); if (IsShown) Reload(); });
        if (Shell.Engine is null) Reload();   // "Loading…" until the engine is up
        _clock.Start();
    }

    public override void Hidden() => _clock.Stop();

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.TopTitle;
        TopTimersKit.SetToggleText(_scopeHistory, T.TopScopeHistory);
        Reload();
    }

    /// <summary>top-dps.json rewritten by the daemon (a fight closed): refill "My fights"; the Abyss Logs line follows the overlay.</summary>
    private void Poll()
    {
        if (Shell.Engine is not { } e) return;
        if (e.Top.ReloadIfChanged() && _scope == 0) Reload();
        UpdateAbyss();
    }

    private void UpdateAbyss()
    {
        var line = Shell.LastOverlay?.AbyssLogs;
        _abyss.Visible = line is not null;
        if (line is not null && line != _abyss.GetText()) _abyss.SetText(line);
    }

    private void Reload()
    {
        if (Shell.Engine is not { } e) { _empty.SetText(T.TopLoading); _empty.Visible = true; return; }
        var srv = e.Server;
        TopTimersKit.SetToggleText(_scopeServer, string.Format(T.TopScopeServer, srv.ServerName.Length > 0 ? srv.ServerName : "?"));
        _scopeServer.Sensitive = srv.ServerName.Length > 0;
        TopTimersKit.SetToggleText(_scopeRegion, T.RegionName(srv.Region));
        TopTimersKit.Paint(_scopeHistory, _scope == 0); TopTimersKit.Paint(_scopeServer, _scope == 1); TopTimersKit.Paint(_scopeRegion, _scope == 2);
        IReadOnlyList<GameClass> classes = _scope == 0 ? e.Top.Classes : TopDpsLogic.AllClasses;
        _class = TopDpsLogic.PickClass(_class, e.Tracker.SelfClass, classes);
        _classTabs.Clear();
        foreach (var c in classes)
        {
            var cls = c;
            var tab = TopTimersKit.TabToggle(TopDpsLogic.ClassTab(T.ClassName(c), _scope == 0 ? e.Top.For(c).Count : 0, _scope == 0), () => { if (cls != _class) { _class = cls; Reload(); } });
            TopTimersKit.Paint(tab, c == _class);
            _classTabs.Add(tab);
        }
        _classTabs.Layout();
        _count.SetText(_scope == 0 && e.Top.Count > 0 ? string.Format(T.TopCount, e.Top.Count) : "");
        _count.SetTooltipText(_scope == 0 ? T.TopHint : T.TopBoardHint);
        UpdateAbyss();
        FillRows(e);
    }

    private void ClearList()
    {
        for (var c = _list.GetFirstChild(); c is not null; c = _list.GetFirstChild()) _list.Remove(c);
    }

    private void FillRows(MeterEngine e)
    {
        ClearList();
        _empty.Visible = false;
        ShowLoading(false);
        if (_scope != 0) { _ = FillBoardAsync(e, ++_boardLoad); return; }
        var rank = 0;
        foreach (var entry in e.Top.For(_class)) _list.Append(FightRow(e, entry, ++rank));
        _empty.SetText(T.TopEmpty);
        _empty.Visible = rank == 0;
    }

    private void ShowLoading(bool on)
    {
        _loading.Visible = on;
        if (on) { _loadingBar.Fraction = 0.1; _sweep.Start(); } else _sweep.Stop();
    }

    /// <summary>Own server or whole region: the ten strongest of the class by combat power, from today's ranking snapshot.</summary>
    private async Task FillBoardAsync(MeterEngine e, int load)
    {
        var srv = e.Server;
        var server = _scope == 1 ? srv.ServerName : null;
        var cls = _class;
        _count.SetText(T.TopBoardLoading);
        ShowLoading(true);   // the first Server/region read walks several ranking pages (Diego: "tarda en cargar la primera vez")
        try
        {
            var rows = await e.Leaderboard.TopAsync(srv.LeaderboardRegion, cls, server);
            if (load != _boardLoad) return;
            ShowLoading(false);
            var me = e.Tracker.SelfName;
            var myServer = e.Data.ServerName(e.Tracker.SelfServerId);
            var rank = 0;
            foreach (var b in rows)
                _list.Append(BoardRow(e, b, ++rank, me is not null && string.Equals(b.Name, me, StringComparison.OrdinalIgnoreCase) && b.ServerName == myServer));
            _count.SetText("");
            _empty.SetText(T.TopEmpty);
            _empty.Visible = rank == 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (load != _boardLoad) return;
            ShowLoading(false);
            Log.Error("Leaderboard read failed", ex);
            _count.SetText(string.Format(T.TopBoardFailed, ex.Message));
        }
    }

    private void SetScope(int scope)
    {
        if (scope == _scope) return;
        _scope = scope;
        _boardLoad++;
        Reload();
    }

    // ------------------------------------------------------------ rows

    private Gtk.Widget FightRow(MeterEngine e, TopEntry entry, int rank)
    {
        var detail = UiKit.VBox();
        UiKit.Margin(detail, 40, 8, 0, 0);
        var detailBox = UiKit.VBox();
        detailBox.AddCssClass("k-tt-detail");
        detailBox.MarginTop = UiKit.Px(8);
        detailBox.Append(detail);
        detailBox.Visible = false;
        var open = UiKit.Chip(T.TopOpenFight, () => OpenFight(e, entry, null));
        var head = Head(rank, entry.Name, TopDpsLogic.FightMeta(entry), TopDpsLogic.Dps(entry), TopDpsLogic.CombatTime(entry), entry.IsSelf, open);
        var key = entry.Name + "|" + entry.Fight;
        void Toggle()
        {
            if (detailBox.Visible) { detailBox.Visible = false; Expanded.Remove(key); return; }
            if (detail.GetFirstChild() is null) FillDetail(e, entry, detail);
            detailBox.Visible = true;
            Expanded.Add(key);
        }
        Click(head, Toggle);
        var stack = UiKit.VBox();
        stack.Append(head);
        stack.Append(detailBox);
        if (Expanded.Contains(key)) { FillDetail(e, entry, detail); detailBox.Visible = true; }
        return Frame(stack, entry.IsSelf);
    }

    private void FillDetail(MeterEngine e, TopEntry entry, Gtk.Box detail)
    {
        var d = e.Data;
        Func<int, string> name = d.SkillName;
        Section(detail, T.TopSkills, TopDpsLogic.Skills(entry), 0);
        Section(detail, T.TopOpening, TopDpsLogic.Opening(entry, name), 8);
        Section(detail, T.TopRepeated, TopDpsLogic.Repeated(entry, name), 8);
        Section(detail, T.TopCadence, TopDpsLogic.Cadence(entry, name), 8);
        // A2M-38: the fight's Abyss Logs percentile when the daemon ranked it (abysslogs/notes.json; self rows only)
        if (entry.IsSelf && Find(e, entry.Fight) is { } h && e.History.Load(h.Path) is { } rec && e.AbyssLogsNoteFor(rec.Summary.Id) is { } note)
        {
            var n = UiKit.Text(note, 12, Fg.GoldBright);
            n.MarginTop = UiKit.Px(8);
            detail.Append(n);
        }
        var load = UiKit.Chip(T.TopProfileFetch, () => OpenFight(e, entry, "profile"), T.TopProfileTip);
        load.Halign = Gtk.Align.Start;
        UiKit.Margin(load, 0, 10, 0, 2);
        detail.Append(load);
    }

    private static void Section(Gtk.Box into, string label, string text, double top)
    {
        var l = UiKit.Text(label, 11, Fg.Gold);
        UiKit.Margin(l, 0, top, 0, 3);
        into.Append(l);
        into.Append(UiKit.Text(text, 12));
    }

    private Gtk.Widget BoardRow(MeterEngine e, LeaderboardRow b, int rank, bool self)
    {
        var load = UiKit.Chip(T.TopProfileFetch, () => OpenBoardProfile(e, b), T.TopProfileTip);
        var head = Head(rank, b.Name, TopDpsLogic.BoardMeta(b), AionMeter.Core.Format.Compact(b.CombatPower) + " CP", "item level " + b.ItemLevel, self, load);
        // a ranking row has nothing else to unfold: a click opens the card (A2M-44)
        Click(head, () => OpenBoardProfile(e, b));
        return Frame(head, self);
    }

    /// <summary>Left click on the row's head; a chip in it claims its own click first (GTK hands the sequence to the innermost gesture).</summary>
    private static void Click(Gtk.Widget w, Action action)
    {
        var g = Gtk.GestureClick.New();
        g.Button = 1;
        g.OnReleased += (_, _) => action();
        w.AddController(g);
        w.SetCursorFromName("pointer");
    }

    private static Gtk.Grid Head(int rank, string name, string meta, string value, string sub, bool self, Gtk.Widget button)
    {
        var g = Gtk.Grid.New();
        var r = UiKit.Text(rank.ToString(System.Globalization.CultureInfo.InvariantCulture), 20, Fg.Dim, 700);
        r.AddCssClass("k-tt-num");
        r.Hexpand = false;
        r.SetSizeRequest(UiKit.Px(34), -1);
        g.Attach(r, 0, 0, 1, 1);
        var who = UiKit.VBox();
        who.Valign = Gtk.Align.Center;
        who.Hexpand = true;
        UiKit.Margin(who, 6, 0, 10, 0);
        who.Append(UiKit.Line(name, 14, self ? Fg.Gold : Fg.Text, 600));
        var m = UiKit.Line(meta, 11.5, Fg.Mute);
        m.MarginTop = UiKit.Px(2);
        who.Append(m);
        who.SetTooltipText(meta);
        g.Attach(who, 1, 0, 1, 1);
        var num = UiKit.VBox();
        num.Valign = Gtk.Align.Center;
        num.Hexpand = false;
        num.MarginEnd = UiKit.Px(12);
        var v = UiKit.Text(value, 19, Fg.Gold, 700);
        v.AddCssClass("k-tt-num");
        v.Wrap = false;
        v.Xalign = 1;
        num.Append(v);
        var s = UiKit.Text(sub, 11, Fg.Dim);
        s.Wrap = false;
        s.Xalign = 1;
        num.Append(s);
        g.Attach(num, 2, 0, 1, 1);
        button.Valign = Gtk.Align.Center;
        g.Attach(button, 3, 0, 1, 1);
        return g;
    }

    private static Gtk.Box Frame(Gtk.Widget child, bool self)
    {
        var b = UiKit.VBox();
        b.AddCssClass("k-tt-frame");
        if (self) b.AddCssClass("k-tt-hot");
        b.Append(child);
        return b;
    }

    // ------------------------------------------------------------ open a fight / a player card

    private static AionMeter.Core.Storage.HistoryEntry? Find(MeterEngine e, string file)
    {
        foreach (var h in e.History.List()) if (h.FileName == file) return h;
        return null;
    }

    /// <summary>The fight tab still shows what this tab opened last (a late armory answer must not land on another fight or card).</summary>
    private bool StillOurs(int seq) => seq == _openSeq && Shell.CurrentTab?.Invoke() == TabState.Fight;

    /// <summary>The saved fight's breakdown on this player (Profile tab when asked); the official page is fetched when the cache is stale.</summary>
    private async void OpenFight(MeterEngine e, TopEntry entry, string? tab)
    {
        if (Find(e, entry.Fight) is not { } h || e.History.Load(h.Path) is not { } record) { _count.SetText(T.FightGone); return; }
        uint actor = 0;
        foreach (var d in record.Details) if (d.Name == entry.Name && d.Class == entry.Class) { actor = d.ActorId; break; }
        var seq = ++_openSeq;
        var id = "h:" + entry.Fight;
        var view = Shell.OpenFight(id, actor, tab);
        if (tab is null || view is null) return;
        var server = entry.ServerId != 0 ? entry.ServerId : e.SelfServerOrEffective;
        var known = server != 0 ? e.KnownProfile(entry.Name, server) : null;
        if (known is not null && DateTimeOffset.UtcNow - known.FetchedAt < e.Armory.MaxAge) return;
        try
        {
            if (await e.Armory.LookupAsync(entry.Name, server) is null) return;
            if (view is IFightTabProfile p) p.RefreshProfile();
            else if (StillOurs(seq)) view.SetFight(id, actor, tab);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { Log.Error("Armory lookup failed", ex); }
    }

    /// <summary>A ranking row: the card alone (CP / item level / rank / seen), the profile when cached, else asked from the official page.</summary>
    private async void OpenBoardProfile(MeterEngine e, LeaderboardRow b)
    {
        var server = ArmoryClient.ServerIdFor(e.Data.Servers, b.ServerName, e.Server.ServerId);
        var known = server != 0 ? e.KnownProfile(b.Name, server) : null;
        var card = PlayerCard.Of(b) with { ServerId = server };
        if (known is not null) card = card.With(known);
        var seq = ++_openSeq;
        var view = Shell.OpenCard(card, known is null ? T.TopProfileLoading : null);
        if (view is null || known is not null && DateTimeOffset.UtcNow - known.FetchedAt < e.Armory.MaxAge) return;
        try
        {
            // a ranking row names its server; when that name is ambiguous across regions the search matches it by name
            var p = await e.Armory.LookupAsync(b.Name, server, serverName: b.ServerName);
            string? status = null;
            PlayerCard next;
            if (p is not null) next = card.With(p);
            else if (known is null) { next = card; status = string.Format(T.TopProfileMissing, b.Name, server != 0 ? $"{e.Data.ServerName(server)} ({server})" : b.ServerName); }
            else return;
            UpdateCard(view, seq, card, next, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Error("Armory lookup failed", ex);
            if (known is null) UpdateCard(view, seq, card, card, string.Format(T.TopProfileFailed, ex.Message));
        }
    }

    private void UpdateCard(IFightTab view, int seq, PlayerCard asked, PlayerCard card, string? status)
    {
        if (view is IFightTabProfile p)
        {
            if (p.Card is not { } shown || !shown.SameCharacter(asked)) return;   // another card or a fight was opened meanwhile
            p.UpdateCard(card, status);
        }
        else if (StillOurs(seq)) view.SetCard(card, status);
    }
}
