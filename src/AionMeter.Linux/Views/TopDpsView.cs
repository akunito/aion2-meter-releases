using System.Net.Http;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Events;
using AionMeter.Core.Leaderboard;
using AionMeter.Core.Ranking;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// Top DPS (WPF TopDpsView, A2M-48 phase 3c): "My fights" = the ten best DPS per class from top-dps.json (the daemon writes it after each
/// fight; re-read by mtime), each row expandable into skill shares, opener, repeated sequences and cadence, Open = the saved fight's
/// breakdown; "Server …" / region = the strongest of the class by combat power from aion2index.com, read by this process when the user asks
/// (the Core client, one page per 400 ms, daily snapshot in armory/leaderboard). Load profile = the A2M-44 player card in the card's fight
/// tab: a fight row opens its fight on Profile, a ranking row the card alone; the official page is asked only when nothing fresh is cached.
/// </summary>
public sealed class TopDpsView : HostedView
{
    private static UiText T => UiText.Current;
    private static readonly GameClass[] AllClasses =
        [GameClass.Gladiator, GameClass.Templar, GameClass.Ranger, GameClass.Assassin, GameClass.Sorcerer, GameClass.Elementalist, GameClass.Cleric, GameClass.Chanter];

    private readonly TextBlock _count, _empty, _abyss;
    private readonly Border _scopeHistory, _scopeServer, _scopeRegion, _loading;
    private readonly WrapPanel _classTabs;
    private readonly StackPanel _list;
    private readonly DispatcherTimer _clock;
    private readonly HashSet<string> _expanded = new();
    private GameClass _class = GameClass.Unknown;
    private int _scope;        // 0 = own fights, 1 = own server, 2 = region
    private int _boardLoad;    // bumps on every scope/class change so a late answer is dropped
    private bool _shown;

    public TopDpsView(AppShell shell) : base(shell, "top", T.TopTitle)
    {
        HeaderRight.Children.Add(_count = new TextBlock { FontSize = 12, Foreground = Visuals.TextMute, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });

        var top = new StackPanel { Margin = new Thickness(18, 0, 18, 8) };
        var scopes = new WrapPanel { Margin = new Thickness(-4, 0, 0, 2) };
        scopes.Children.Add(_scopeHistory = BossTimersView.TabToggle(T.TopScopeHistory, () => SetScope(0)));
        scopes.Children.Add(_scopeServer = BossTimersView.TabToggle("…", () => SetScope(1)));
        scopes.Children.Add(_scopeRegion = BossTimersView.TabToggle(T.TopScopeGlobal, () => SetScope(2)));
        top.Children.Add(scopes);
        top.Children.Add(_classTabs = new WrapPanel { Margin = new Thickness(-4, 0, 0, 4) });
        top.Children.Add(_abyss = new TextBlock { FontSize = 12, Foreground = Visuals.GoldBright, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 2, 0, 2) });

        _list = new StackPanel();
        var host = new Grid();
        host.Children.Add(_list);
        host.Children.Add(_empty = new TextBlock { Text = T.TopEmpty, TextWrapping = TextWrapping.Wrap, Foreground = Visuals.TextMute, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(30, 40, 30, 0), MaxWidth = 420, IsVisible = false });
        var box = new StackPanel();
        box.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 180, Height = 4, Foreground = Visuals.Gold, Background = Visuals.Solid(0x22FFFFFF), Margin = new Thickness(0, 0, 0, 10) });
        box.Children.Add(new TextBlock { Text = T.TopLoading, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 13, Foreground = Visuals.Text });
        host.Children.Add(_loading = new Border
        {
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 70, 0, 0), Background = Visuals.Solid(0xD9141A26),
            BorderBrush = Visuals.Solid(0x33FFFFFF), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 14), Child = box,
        });
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Place(top, 0, 0);
        body.Place(UiKit.Scroll(host, new Thickness(14, 0, 14, 12)), 1, 0);
        SetBody(body);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _clock.Tick += (_, _) => Poll();
        shell.EngineReady += () => { if (_shown) Reload(); };
        shell.Daemon.FightClosed += _ => { if (_shown) Poll(); };
    }

    public override void Shown()
    {
        _shown = true;
        Shell.WhenEngine(e => { e.Top.ReloadIfChanged(); Reload(); });
        _clock.Start();
    }

    public override void Hidden()
    {
        _shown = false;
        _clock.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.TopTitle;
        SetToggleText(_scopeHistory, T.TopScopeHistory);
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
        _abyss.IsVisible = line is not null;
        if (line is not null) _abyss.Text = line;
    }

    private void Reload()
    {
        if (Shell.Engine is not { } e) { _empty.Text = T.TopLoading; _empty.IsVisible = true; return; }
        var srv = e.Server;
        SetToggleText(_scopeServer, string.Format(T.TopScopeServer, srv.ServerName.Length > 0 ? srv.ServerName : "?"));
        _scopeServer.IsEnabled = srv.ServerName.Length > 0;
        SetToggleText(_scopeRegion, T.RegionName(srv.Region));
        Paint(_scopeHistory, _scope == 0); Paint(_scopeServer, _scope == 1); Paint(_scopeRegion, _scope == 2);
        IReadOnlyList<GameClass> classes = _scope == 0 ? e.Top.Classes : AllClasses;
        if (_class == GameClass.Unknown && e.Tracker.SelfClass != GameClass.Unknown) _class = e.Tracker.SelfClass;
        if (_class == GameClass.Unknown || !classes.Contains(_class)) _class = classes.Count > 0 ? classes[0] : GameClass.Unknown;
        _classTabs.Children.Clear();
        foreach (var c in classes)
        {
            var cls = c;
            var tab = BossTimersView.TabToggle(_scope == 0 ? $"{T.ClassName(c)} ({e.Top.For(c).Count})" : T.ClassName(c), () => { if (cls != _class) { _class = cls; Reload(); } });
            Paint(tab, c == _class);
            _classTabs.Children.Add(tab);
        }
        _count.Text = _scope == 0 && e.Top.Count > 0 ? string.Format(T.TopCount, e.Top.Count) : "";
        UiKit.Tip(_count, _scope == 0 ? T.TopHint : T.TopBoardHint);
        UpdateAbyss();
        FillRows(e);
    }

    private void FillRows(MeterEngine e)
    {
        _list.Children.Clear();
        _empty.IsVisible = false;
        _loading.IsVisible = false;
        if (_scope != 0) { _ = FillBoardAsync(e, ++_boardLoad); return; }
        var rank = 0;
        foreach (var entry in e.Top.For(_class)) _list.Children.Add(FightRow(e, entry, ++rank));
        _empty.Text = T.TopEmpty;
        _empty.IsVisible = _list.Children.Count == 0;
    }

    /// <summary>Own server or whole region: the ten strongest of the class by combat power, from today's ranking snapshot.</summary>
    private async Task FillBoardAsync(MeterEngine e, int load)
    {
        var srv = e.Server;
        var server = _scope == 1 ? srv.ServerName : null;
        var cls = _class;
        _count.Text = T.TopBoardLoading;
        _loading.IsVisible = true;   // the first Server/region read walks several ranking pages (Diego: "tarda en cargar la primera vez")
        try
        {
            var rows = await e.Leaderboard.TopAsync(srv.LeaderboardRegion, cls, server);
            if (load != _boardLoad) return;
            _loading.IsVisible = false;
            var me = e.Tracker.SelfName;
            var myServer = e.Data.ServerName(e.Tracker.SelfServerId);
            var rank = 0;
            foreach (var b in rows)
                _list.Children.Add(BoardRow(e, b, ++rank, me is not null && string.Equals(b.Name, me, StringComparison.OrdinalIgnoreCase) && b.ServerName == myServer));
            _count.Text = "";
            _empty.Text = T.TopEmpty;
            _empty.IsVisible = _list.Children.Count == 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (load != _boardLoad) return;
            _loading.IsVisible = false;
            Log.Error("Leaderboard read failed", ex);
            _count.Text = string.Format(T.TopBoardFailed, ex.Message);
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

    private Control FightRow(MeterEngine e, TopEntry entry, int rank)
    {
        var detail = new StackPanel { Margin = new Thickness(40, 8, 0, 0) };
        var detailBox = new Border { BorderBrush = Visuals.Solid(0x22FFFFFF), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = detail, IsVisible = false };
        var open = UiKit.Chip(T.TopOpenFight, () => OpenFight(e, entry, null));
        var head = Head(rank, entry.Name, $"{entry.Title}{(string.IsNullOrEmpty(entry.Zone) ? "" : " · " + entry.Zone)} · {entry.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Format.Compact(entry.Damage)} dmg",
            Format.Compact(entry.Dps) + "/s", TimeSpan.FromMilliseconds(entry.CombatMs).ToString(@"m\:ss"), entry.IsSelf, open);
        var key = entry.Name + "|" + entry.Fight;
        void Toggle()
        {
            if (detailBox.IsVisible) { detailBox.IsVisible = false; _expanded.Remove(key); return; }
            if (detail.Children.Count == 0) FillDetail(e, entry, detail);
            detailBox.IsVisible = true;
            _expanded.Add(key);
        }
        head.PointerPressed += (_, ev) => { if (!ev.GetCurrentPoint(head).Properties.IsLeftButtonPressed) return; ev.Handled = true; Toggle(); };
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(detailBox);
        if (_expanded.Contains(key)) Dispatcher.UIThread.Post(Toggle);
        return Frame(stack, entry.IsSelf);
    }

    private void FillDetail(MeterEngine e, TopEntry entry, StackPanel detail)
    {
        var d = e.Data;
        var sb = new StringBuilder();
        foreach (var s in entry.Skills.OrderByDescending(s => s.Share).Take(12))
            sb.Append(sb.Length > 0 ? "   " : "").Append(s.Name).Append(' ').Append((s.Share * 100).ToString("0")).Append("% ×").Append(s.Hits);
        Section(detail, T.TopSkills, sb.ToString(), 0);
        Section(detail, T.TopOpening, string.Join(" → ", TopDpsStore.Opening(entry, 16).Select(d.SkillName)), 8);
        sb.Clear();
        foreach (var (skills, count) in TopDpsStore.Frequent(entry, 3, 5))
            sb.Append(sb.Length > 0 ? "\n" : "").Append(string.Join(" → ", skills.Select(d.SkillName))).Append("   ×").Append(count);
        Section(detail, T.TopRepeated, sb.Length > 0 ? sb.ToString() : "—", 8);
        sb.Clear();
        foreach (var (skill, _, perMin, _) in TopDpsStore.Cadence(entry).Take(10))
            sb.Append(sb.Length > 0 ? "   " : "").Append(d.SkillName(skill)).Append(' ').Append(perMin.ToString("0.0")).Append("/min");
        Section(detail, T.TopCadence, sb.ToString(), 8);
        // A2M-38: the fight's Abyss Logs percentile when the daemon ranked it (abysslogs/notes.json; self rows only)
        if (entry.IsSelf && Find(e, entry.Fight) is { } h && e.History.Load(h.Path) is { } rec && e.AbyssLogsNoteFor(rec.Summary.Id) is { } note)
            detail.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Visuals.GoldBright, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var load = UiKit.Chip(T.TopProfileFetch, () => OpenFight(e, entry, "profile"), T.TopProfileTip);
        load.HorizontalAlignment = HorizontalAlignment.Left;
        load.Margin = new Thickness(0, 10, 0, 2);
        detail.Children.Add(load);
    }

    private static void Section(StackPanel into, string label, string text, double top)
    {
        into.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Visuals.Gold, Margin = new Thickness(0, top, 0, 3) });
        into.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Visuals.Text, TextWrapping = TextWrapping.Wrap });
    }

    private Control BoardRow(MeterEngine e, LeaderboardRow b, int rank, bool self)
    {
        var load = UiKit.Chip(T.TopProfileFetch, () => OpenBoardProfile(e, b), T.TopProfileTip);
        var head = Head(rank, b.Name, $"{b.ServerName} · lv{b.Level} · #{b.Rank} in {b.Region.ToUpperInvariant()}{(b.LastInfoAt is { } t ? " · seen " + t.ToLocalTime().ToString("MM-dd HH:mm") : "")}",
            Format.Compact(b.CombatPower) + " CP", "item level " + b.ItemLevel, self, load);
        // a ranking row has nothing else to unfold: a click opens the card (A2M-44)
        head.PointerPressed += (_, ev) => { if (!ev.GetCurrentPoint(head).Properties.IsLeftButtonPressed) return; ev.Handled = true; OpenBoardProfile(e, b); };
        return Frame(head, self);
    }

    private static Grid Head(int rank, string name, string meta, string value, string sub, bool self, Control button)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,Auto,Auto"), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        g.Place(new TextBlock { Text = rank.ToString(), FontFamily = Visuals.Number, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Visuals.TextDim, VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        var who = new StackPanel { Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = self ? Visuals.Gold : Visuals.Text, TextTrimming = TextTrimming.CharacterEllipsis });
        who.Children.Add(new TextBlock { Text = meta, FontSize = 11.5, Foreground = Visuals.TextMute, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        UiKit.Tip(who, meta);
        g.Place(who, 0, 1);
        var num = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        num.Children.Add(new TextBlock { Text = value, Foreground = Visuals.Gold, HorizontalAlignment = HorizontalAlignment.Right, FontFamily = Visuals.Number, FontSize = 19, FontWeight = FontWeight.Bold });
        num.Children.Add(new TextBlock { Text = sub, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11, Foreground = Visuals.TextDim, Margin = new Thickness(0, -1, 0, 0) });
        g.Place(num, 0, 2);
        button.VerticalAlignment = VerticalAlignment.Center;
        g.Place(button, 0, 3);
        return g;
    }

    private static Border Frame(Control child, bool self) => new()
    {
        Background = Visuals.Solid(0x0DFFFFFF), BorderBrush = self ? Visuals.GoldDim : Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
        Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 7), Child = child,
    };

    // ------------------------------------------------------------ open a fight / a player card

    private static Core.Storage.HistoryEntry? Find(MeterEngine e, string file)
    {
        foreach (var h in e.History.List()) if (h.FileName == file) return h;
        return null;
    }

    /// <summary>The saved fight's breakdown on this player (Profile tab when asked); the official page is fetched when the cache is stale.</summary>
    private async void OpenFight(MeterEngine e, TopEntry entry, string? tab)
    {
        if (Find(e, entry.Fight) is not { } h || e.History.Load(h.Path) is not { } record) { _count.Text = T.FightGone; return; }
        uint actor = 0;
        foreach (var d in record.Details) if (d.Name == entry.Name && d.Class == entry.Class) { actor = d.ActorId; break; }
        var view = Shell.OpenFight("h:" + entry.Fight, actor, tab);
        if (tab is null || view is null) return;
        var server = entry.ServerId != 0 ? entry.ServerId : e.SelfServerOrEffective;
        var known = server != 0 ? e.KnownProfile(entry.Name, server) : null;
        if (known is not null && DateTimeOffset.UtcNow - known.FetchedAt < e.Armory.MaxAge) return;
        try
        {
            if (await e.Armory.LookupAsync(entry.Name, server) is not null) view.RefreshProfile();
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
        var view = Shell.OpenCard(card, known is null ? T.TopProfileLoading : null);
        if (view is null || known is not null && DateTimeOffset.UtcNow - known.FetchedAt < e.Armory.MaxAge) return;
        try
        {
            // a ranking row names its server; when that name is ambiguous across regions the search matches it by name
            var p = await e.Armory.LookupAsync(b.Name, server, serverName: b.ServerName);
            if (view.Card is not { } shown || !shown.SameCharacter(card)) return;   // another card or a fight was opened meanwhile
            if (p is not null) view.UpdateCard(card.With(p), null);
            else if (known is null) view.UpdateCard(card, string.Format(T.TopProfileMissing, b.Name, server != 0 ? $"{e.Data.ServerName(server)} ({server})" : b.ServerName));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Error("Armory lookup failed", ex);
            if (known is null) view.UpdateCard(card, string.Format(T.TopProfileFailed, ex.Message));
        }
    }

    // ------------------------------------------------------------ tab toggles

    private static void SetToggleText(Border b, string text) { if (b.Child is TextBlock t) t.Text = text; }

    private static void Paint(Border b, bool on)
    {
        b.BorderBrush = on ? Visuals.Gold : Brushes.Transparent;
        if (b.Child is TextBlock t) { t.Foreground = on ? Visuals.GoldBright : b.IsEnabled ? Visuals.TextDim : Visuals.TextMute; t.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal; }
    }
}
