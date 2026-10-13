using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Boss respawn timers (WPF BossTimersView, A2M-39) on the layer-shell card (A2M-50, port of the Avalonia BossTimersView, behaviour unchanged).
/// The list is the viewer engine's BossTimers, re-read from boss-timers.json by mtime (the daemon writes it: game lists, kills, alerts) every
/// second while shown and at once on `timers-changed` / a boss alert. Killed, the bell (watch), the respawn interval and remove are `timer`
/// commands to the daemon, which owns the file; without a daemon they are dimmed. The alert lead time is a setting (settings.json, UI-owned).
/// The last alert shows as a line for 60 s. Rows are cached per NPC code and updated in place on every tick.
/// </summary>
internal sealed class BossTimersView : HostedView
{
    private static UiText T => UiText.Current;
    // survive the card's rebuild on a rescale (the Avalonia view lives as long as the app)
    private static int _server;
    private static bool _followCurrent = true, _watchedOnly;

    private readonly Gtk.Box _list = UiKit.VBox();
    private readonly Gtk.Label _empty, _hint, _announce, _alertLabel;
    private readonly Gtk.Button _serverChip, _alertChip, _filterAll, _filterWatched;
    private readonly UiTimer _clock = new(TimeSpan.FromSeconds(1));
    private readonly Dictionary<int, Row> _rows = new();
    private readonly List<int> _order = new();
    private readonly List<BossTimer> _shown = new();
    private readonly HashSet<int> _keep = new();
    private readonly List<int> _drop = new();
    private readonly TimerOrder _sort = new();
    private Dictionary<int, string>? _portraitFiles;
    private bool _compact = true;
    private string _serverText = "", _announceText = "";

    private sealed class Row
    {
        public required int Code;
        public required Gtk.Box Frame, Portrait, Who, StatusBox, Actions;
        public required Gtk.Grid Grid;
        public required Gtk.Label Name, Meta, Status, Detail;
        public required IconView Bell;
        public required Gtk.Button BellBox, KilledChip, RespawnChip;
        public int Respawn = -1;
        public bool Watch, PortraitSet, Hot, Enabled = true;
        public string NameText = "", MetaText = "", StatusText = "", DetailText = "", RespawnText = "", StatusTip = "", RespawnTip = "";
        public Fg StatusFg = Fg.Text;
        public double StatusSize = 19;
    }

    public BossTimersView(AppShell shell) : base(shell, "timers", UiText.Current.TimersTitle)
    {
        TopTimersKit.EnsureCss(shell.UiScale);
        _serverChip = UiKit.Chip("…", ShowServerMenu, T.TipServer);
        _serverChip.MarginEnd = UiKit.Px(4);
        HeaderRight.Append(_serverChip);

        var top = UiKit.VBox();
        // Avalonia pulls the toggles 4 px left (Margin -4); GTK warns on negative margins, so the side is 14 and the texts get 4
        UiKit.Margin(top, 14, 0, 18, 8);
        var filterRow = UiKit.HBox();
        filterRow.MarginBottom = UiKit.Px(6);
        filterRow.Append(_filterAll = TopTimersKit.TabToggle(T.FilterAll, () => SetFilter(false)));
        filterRow.Append(_filterWatched = TopTimersKit.TabToggle(T.FilterWatched, () => SetFilter(true)));
        // the alert label takes what is left and ellipsizes (Avalonia DockPanel: the alert block docked right)
        _alertLabel = UiKit.Line(T.AlertLabel, 12, Fg.Dim);
        _alertLabel.Xalign = 1;
        _alertLabel.MarginStart = UiKit.Px(8);
        _alertLabel.MarginEnd = UiKit.Px(8);
        filterRow.Append(_alertLabel);
        filterRow.Append(_alertChip = UiKit.Chip("…", ShowAlertMenu));
        top.Append(filterRow);
        top.Append(_announce = UiKit.Text("", 12, Fg.Amber, 600));
        _announce.MarginBottom = UiKit.Px(6);
        _announce.MarginStart = UiKit.Px(4);
        _announce.Visible = false;
        top.Append(_hint = UiKit.Hint(T.TimersHint, 4));
        _hint.MarginBottom = 0;

        var listHost = UiKit.VBox();
        listHost.Append(_list);
        listHost.Append(_empty = UiKit.Text(T.TimersEmpty, 12.5, Fg.Mute));
        _empty.Justify = Gtk.Justification.Center;
        _empty.Xalign = 0.5f;
        UiKit.Margin(_empty, 30, 40, 30, 0);
        _empty.Visible = false;
        var scroll = UiKit.Scroll(listHost);
        scroll.MarginBottom = UiKit.Px(12);
        var body = UiKit.VBox();
        body.Append(top);
        body.Append(scroll);
        SetBody(body);
        SyncFilter();

        // a hidden tab neither ticks nor reloads; it reloads once when shown again
        _clock.Tick += Reload;
        Action changed = () => { if (IsShown) Reload(); };
        Action<string> alert = _ => { if (IsShown) Reload(); };
        Action<bool> conn = _ => { if (IsShown) Reload(); };
        Action icons = () => { foreach (var r in _rows.Values) if (!r.PortraitSet) r.PortraitSet = SetPortrait(r); };
        Listen(() => Shell.Daemon.TimersChanged += changed, () => Shell.Daemon.TimersChanged -= changed);
        Listen(() => Shell.Daemon.BossAlert += alert, () => Shell.Daemon.BossAlert -= alert);
        Listen(() => Shell.Daemon.ConnectionChanged += conn, () => Shell.Daemon.ConnectionChanged -= conn);
        Listen(() => Shell.EngineReady += changed, () => Shell.EngineReady -= changed);
        Listen(() => Shell.Icons.Loaded += icons, () => Shell.Icons.Loaded -= icons);
    }

    public override void Shown()
    {
        UpdateAlertChip();
        Reload();
        _clock.Start();
    }

    public override void Hidden() => _clock.Stop();

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.TimersTitle;
        _hint.SetText(T.TimersHint);
        _empty.SetText(T.TimersEmpty);
        _alertLabel.SetText(T.AlertLabel);
        _serverChip.SetTooltipText(T.TipServer);
        TopTimersKit.SetToggleText(_filterAll, T.FilterAll);
        TopTimersKit.SetToggleText(_filterWatched, T.FilterWatched);
        foreach (var r in _rows.Values)
        {
            UiKit.SetChipText(r.KilledChip, T.BtnKilled);
            r.KilledChip.SetTooltipText(T.TipKilled);
            r.BellBox.SetTooltipText(T.TipWatch);
            r.NameText = r.RespawnText = r.StatusTip = r.RespawnTip = r.MetaText = r.DetailText = r.StatusText = "";
        }
        UpdateAlertChip();
        Reload();
    }

    // ------------------------------------------------------------ list

    private void Reload()
    {
        if (Shell.Engine is not { } e) { _empty.SetText(T.FightLoading); _empty.Visible = true; return; }
        // Avalonia SizeChanged: the card can be widened past 700 px, the layout follows on the next tick
        if (View.GetWidth() is var w && w > 0) ApplyCompact(w / Shell.UiScale < BossTimersLogic.CompactBelow);
        var timers = e.Timers;
        timers.ReloadIfChanged();
        var now = DateTimeOffset.Now;
        if (_followCurrent) _server = timers.CurrentServer;
        var serverText = ServerLabel(e, _server) + "  ▾";
        if (serverText != _serverText) { _serverText = serverText; UiKit.SetChipText(_serverChip, serverText); }
        _shown.Clear();
        var all = timers.ForServer(_server);
        for (var i = 0; i < all.Count; i++) if (BossTimersLogic.Listed(all[i], _watchedOnly)) _shown.Add(all[i]);
        _sort.Now = now;
        _shown.Sort(_sort);

        var same = _shown.Count == _order.Count;
        for (var i = 0; same && i < _shown.Count; i++) same = _shown[i].NpcCode == _order[i];
        if (!same)
        {
            for (var c = _list.GetFirstChild(); c is not null; c = _list.GetFirstChild()) _list.Remove(c);
            _order.Clear();
            _keep.Clear();
            foreach (var t in _shown)
            {
                if (!_rows.TryGetValue(t.NpcCode, out var row)) _rows[t.NpcCode] = row = NewRow(t.NpcCode);
                _keep.Add(t.NpcCode);
                _order.Add(t.NpcCode);
                _list.Append(row.Frame);
            }
            _drop.Clear();
            foreach (var code in _rows.Keys) if (!_keep.Contains(code)) _drop.Add(code);
            foreach (var code in _drop) _rows.Remove(code);
        }
        var connected = Shell.Daemon.Connected;
        var alertMinutes = Shell.Settings.BossAlertMinutes;
        foreach (var t in _shown) Fill(e, _rows[t.NpcCode], t, now, connected, alertMinutes);
        _empty.SetText(T.TimersEmpty);
        _empty.Visible = _shown.Count == 0;
        var an = Shell.LastOverlay?.Announcement;
        _announce.Visible = an is not null;
        if (an is not null && an != _announceText) { _announceText = an; _announce.SetText(an); }
    }

    private Row NewRow(int code)
    {
        var name = UiKit.Line("", 14, Fg.Text, 600);
        var meta = UiKit.Line("", 11.5, Fg.Mute);
        meta.MarginTop = UiKit.Px(2);
        var who = UiKit.VBox();
        who.Valign = Gtk.Align.Center;
        who.Hexpand = true;
        UiKit.Margin(who, 12, 0, 10, 0);
        who.Append(name);
        who.Append(meta);
        var status = UiKit.Text("", 19, Fg.GoldBright, 700);
        status.AddCssClass("k-tt-num");
        status.Wrap = false;
        status.Xalign = 1;
        var detail = UiKit.Line("", 11, Fg.Dim);
        detail.Xalign = 1;
        var statusBox = UiKit.VBox();
        statusBox.SetSizeRequest(UiKit.Px(150), -1);
        statusBox.Hexpand = false;
        statusBox.MarginEnd = UiKit.Px(12);
        statusBox.Valign = Gtk.Align.Center;
        statusBox.Append(status);
        statusBox.Append(detail);

        Row? self = null;
        var respawn = UiKit.Chip("", () => ShowRespawnMenu(self!), T.TipRespawn, Icons.Reset);
        respawn.SetSizeRequest(UiKit.Px(96), -1);
        respawn.GetChild()!.Halign = Gtk.Align.Center;
        respawn.MarginEnd = UiKit.Px(8);
        var bell = Icons.View(TopTimersKit.Bell, Palette.TextDim, 13, 1.5);
        var bellBox = Gtk.Button.New();
        bellBox.AddCssClass("k-tt-bell");
        bellBox.Child = bell.Widget;
        bellBox.SetCursorFromName("pointer");
        bellBox.FocusOnClick = false;
        bellBox.Valign = Gtk.Align.Center;
        bellBox.SetTooltipText(T.TipWatch);
        bellBox.OnClicked += (_, _) => Command(self!.Watch ? "unwatch" : "watch", code);
        var killed = UiKit.Chip(T.BtnKilled, () => Command("killed", code), T.TipKilled);
        killed.MarginEnd = UiKit.Px(4);
        var remove = UiKit.IconButton(Icons.Close, T.TipRemoveTimer, () => Command("remove", code));
        var actions = UiKit.HBox();
        actions.Valign = Gtk.Align.Center;
        actions.Hexpand = false;
        actions.Append(respawn);
        actions.Append(bellBox);
        actions.Append(killed);
        actions.Append(remove);

        var portrait = UiKit.VBox();
        portrait.AddCssClass("k-tt-portrait");
        portrait.Overflow = Gtk.Overflow.Hidden;
        portrait.Valign = Gtk.Align.Center;
        portrait.Halign = Gtk.Align.Start;
        portrait.Hexpand = false;
        var g = Gtk.Grid.New();
        g.Attach(portrait, 0, 0, 1, 2);
        g.Attach(who, 1, 0, 1, 1);
        g.Attach(statusBox, 2, 0, 1, 1);
        var frame = UiKit.VBox();
        frame.AddCssClass("k-tt-frame");
        frame.Append(g);
        self = new Row
        {
            Code = code, Frame = frame, Grid = g, Portrait = portrait, Who = who, Name = name, Meta = meta, Status = status, Detail = detail, StatusBox = statusBox,
            Actions = actions, Bell = bell, BellBox = bellBox, KilledChip = killed, RespawnChip = respawn,
        };
        Place(self, _compact);
        self.PortraitSet = SetPortrait(self);
        return self;
    }

    /// <summary>Narrow (the 560 px card tab): the actions drop to a second line under name and status (WPF Compact under 700 px).</summary>
    private static void Place(Row r, bool compact)
    {
        if (r.Actions.GetParent() is not null) r.Grid.Remove(r.Actions);
        if (compact) r.Grid.Attach(r.Actions, 1, 1, 2, 1);
        else r.Grid.Attach(r.Actions, 3, 0, 1, 1);
        r.Actions.MarginStart = UiKit.Px(compact ? 12 : 0);
        r.Actions.MarginTop = UiKit.Px(compact ? 6 : 0);
        r.Actions.Halign = compact ? Gtk.Align.Start : Gtk.Align.End;
    }

    private void ApplyCompact(bool compact)
    {
        if (compact == _compact) return;
        _compact = compact;
        foreach (var r in _rows.Values) Place(r, compact);
    }

    private void Fill(MeterEngine e, Row row, BossTimer t, DateTimeOffset now, bool connected, int alertMinutes)
    {
        Set(row.Name, ref row.NameText, e.Data.NpcName(t.NpcCode));
        if (row.Watch != t.Watch || row.Respawn < 0)
        {
            row.Watch = t.Watch;
            row.Bell.Rgb = t.Watch ? Palette.GoldBright : Palette.TextDim;
            if (t.Watch) row.BellBox.AddCssClass("k-tt-on"); else row.BellBox.RemoveCssClass("k-tt-on");
        }
        var tip = BossTimersLogic.SourceTip(t, now, T);
        if (tip != row.StatusTip) { row.StatusTip = tip; row.StatusBox.SetTooltipText(tip); }
        var zone = e.Timers.MapName(t.MapId, T.Code)
                   ?? (t.SlotId != 0 ? string.Format(T.MapNumber, t.MapId) : t.MapId != 0 ? e.Data.MapName(t.MapId) : t.Zone);
        Set(row.Meta, ref row.MetaText, BossTimersLogic.Meta(zone, t, now, T));
        row.Respawn = t.RespawnMinutes;
        var respawnText = BossTimersLogic.RespawnLabel(t, T) + " ▾";
        if (respawnText != row.RespawnText) { row.RespawnText = respawnText; UiKit.SetChipText(row.RespawnChip, respawnText); }
        var respawnTip = t.RespawnLearned ? T.TimerLearned : T.TipRespawn;
        if (respawnTip != row.RespawnTip) { row.RespawnTip = respawnTip; row.RespawnChip.SetTooltipText(respawnTip); }
        if (row.Enabled != connected)
        {
            row.Enabled = connected;
            row.Actions.Opacity = connected ? 1 : 0.4;
            row.Actions.CanTarget = connected;
            // CanTarget false also stops the tooltip: the reason sits on the row's status instead
            row.Grid.SetTooltipText(connected ? null : "aionmeter-daemon is not running");
        }

        var s = BossTimersLogic.Status(t, now, alertMinutes, T);
        Set(row.Status, ref row.StatusText, s.Text);
        Set(row.Detail, ref row.DetailText, s.Detail);
        if (s.Fg != row.StatusFg) { row.StatusFg = s.Fg; UiKit.SetFg(row.Status, s.Fg); }
        if (Math.Abs(s.Size - row.StatusSize) > 0.01)
        {
            row.Status.RemoveCssClass(KitCss.SizeClass(row.StatusSize));
            row.Status.AddCssClass(KitCss.SizeClass(s.Size));
            row.StatusSize = s.Size;
        }
        if (s.Hot != row.Hot)
        {
            row.Hot = s.Hot;
            if (s.Hot) row.Frame.AddCssClass("k-tt-hot"); else row.Frame.RemoveCssClass("k-tt-hot");
        }
    }

    private static void Set(Gtk.Label l, ref string last, string text)
    {
        if (text == last) return;
        last = text;
        l.SetText(text);
    }

    /// <summary>MetaBot.GG portrait (game art © NCSOFT) through the icon cache: field bosses by NPC code, dungeon bosses by data/boss_portraits.json.</summary>
    private bool SetPortrait(Row r)
    {
        if (r.Code <= 0) return true;
        _portraitFiles ??= BossTimersLogic.LoadPortraitFiles(MeterEngine.DataDirectory);
        var (key, url) = BossTimersLogic.Portrait(r.Code, _portraitFiles);
        var child = r.Portrait.GetFirstChild();
        if (Shell.Icons.Url(key, url, 64) is not { } tex)
        {
            if (child is null)
            {
                var clock = Icons.View(Icons.Clock, Palette.GoldDim, 18, 1.4).Widget;
                clock.Vexpand = true;
                r.Portrait.Append(clock);
            }
            return false;
        }
        if (child is not null) r.Portrait.Remove(child);
        var img = ItemKit.Image(tex, 44);
        img.Vexpand = true;
        r.Portrait.Append(img);
        return true;
    }

    // ------------------------------------------------------------ actions

    private void Command(string action, int code, int value = 0)
    {
        if (!Shell.Daemon.Connected) return;
        Shell.Daemon.Timer(action, _server, code, value);   // the daemon writes boss-timers.json and answers `timers-changed`
    }

    private void ShowRespawnMenu(Row row)
    {
        var minutes = BossTimersLogic.RespawnChoices(row.Respawn);
        var items = new List<(string, Action)>(minutes.Length);
        var code = row.Code;
        foreach (var m in minutes)
        {
            var chosen = m;
            items.Add((BossTimersLogic.MenuLine(m == 0 ? T.RespawnUnknown : T.Duration(m), m == row.Respawn), () => Command("respawn", code, chosen)));
        }
        TopTimersKit.Menu(row.RespawnChip, items);
    }

    private void ShowAlertMenu()
    {
        var current = Shell.Settings.BossAlertMinutes;
        var items = new List<(string, Action)>(BossTimersLogic.AlertChoices.Length);
        foreach (var m in BossTimersLogic.AlertChoices)
        {
            var chosen = m;
            items.Add((BossTimersLogic.MenuLine(BossTimersLogic.AlertText(m, T), m == current), () =>
            {
                Shell.RefreshSettings();
                Shell.Settings.BossAlertMinutes = chosen;
                Shell.SaveSettings();   // the daemon announces with it (reload-settings)
                UpdateAlertChip();
                Reload();
            }));
        }
        TopTimersKit.Menu(_alertChip, items);
    }

    private void UpdateAlertChip() => UiKit.SetChipText(_alertChip, BossTimersLogic.AlertText(Shell.Settings.BossAlertMinutes, T) + "  ▾");

    /// <summary>Other servers' timers (another character's): pick whose to show; the current one is marked.</summary>
    private void ShowServerMenu()
    {
        if (Shell.Engine is not { } e) return;
        var current = e.Timers.CurrentServer;
        var servers = e.Timers.Servers();
        if (servers.Count == 0) servers = [current];
        var items = new List<(string, Action)>(servers.Count);
        foreach (var s in servers)
        {
            var chosen = s;
            items.Add(((s == _server ? "▸ " : "") + ServerLabel(e, s) + (s == current ? "  · " + T.ServerYours : ""), () => { _server = chosen; _followCurrent = chosen == current; Reload(); }));
        }
        TopTimersKit.Menu(_serverChip, items);
    }

    private static string ServerLabel(MeterEngine e, int server) =>
        server == 0 ? T.ServerUnknown : $"{e.Data.ServerName(server)} {Core.Game.GameData.RegionOf(server)}".Trim();

    private void SetFilter(bool watched)
    {
        if (watched == _watchedOnly) return;
        _watchedOnly = watched;
        SyncFilter();
        Reload();
    }

    private void SyncFilter()
    {
        TopTimersKit.Paint(_filterAll, !_watchedOnly);
        TopTimersKit.Paint(_filterWatched, _watchedOnly);
    }
}
