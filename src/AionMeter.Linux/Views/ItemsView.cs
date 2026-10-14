using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AionMeter.Core.Character;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Core.Market;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// Items tab (WPF ItemsView, A2M-29/32/15) on Linux: the viewed character's bag and warehouse, the account's shared warehouse, the equipped
/// set, Today (tickets) and Achievements cards, the "Across characters" panel and the currency footer. Everything comes from the files the
/// daemon writes (characters/*/inventory.json + character.json, account-warehouse.json), re-read when <see cref="DataWatch"/> sees a new
/// mtime; the left column is one virtualised list of rows (card heads, tile rows, list rows), so only visible tiles ask for their icon.
/// </summary>
public sealed class ItemsView : HostedView
{
    public sealed record CharData(string Key, string Cls, InventoryFile? File, CharacterProgress? Progress);
    private sealed record Data(string CurrentKey, string Server, string ServerName, List<CharData> Chars, AccountWarehouseView? Account, MarketSnapshot Market, DateTimeOffset Now,
        CharacterProgress? Progress, AchievementCatalog Catalog);

    private static UiText T => UiText.Current;
    private static readonly IBrush Text = Visuals.Solid(0xFFFAFAFA), Dim = Visuals.Solid(0xFFAFAFAF), Mute = Visuals.Solid(0xFF7D7D7D), Line = Visuals.Solid(0xFF2B2B2B),
        Line2 = Visuals.Solid(0xFF373737), CardBrush = Visuals.Solid(0xCC1F1F1F), Band = Visuals.Solid(0xFF1F1F1F), ChipBack = Visuals.Solid(0xFF2B2B2B),
        Green = Visuals.Solid(0xFF3DDC84), Amber = Visuals.Solid(0xFFF5B544), Blue = Visuals.Solid(0xFF65B0FC), Num = Visuals.Solid(0xFFFCC78B),
        WarnBack = Visuals.Solid(0x14F5B544), WarnLine = Visuals.Solid(0x4DF5B544), RowLine = Visuals.Solid(0xFF202020), LocBack = Visuals.Solid(0xFF1A1A1A),
        MeLine = Visuals.Solid(0x7365B0FC), AcctLine = Visuals.Solid(0x663DDC84), HintBack = Visuals.Solid(0xFF141414), KvBack = Visuals.Solid(0xFF1F1F1F),
        Faint = Visuals.Solid(0xFF555555), FootLine = Visuals.Solid(0xFF1E1E1E), Page = Visuals.Solid(0xFF0D0D0D), FootBack = Visuals.Solid(0xFF111111),
        Transparent = Visuals.Solid(0x00000000);
    private static readonly Cursor Hand = new(StandardCursorType.Hand);
    private const int Tile = 48, TileGap = 6;

    private readonly DataWatch _watch;
    private Data? _data;
    private bool _shown, _narrow, _laidOut;
    private string _group = "all", _q = "", _lang;
    private ItemSortMode _sort = ItemSortMode.Game;
    private readonly HashSet<string> _collapsed = ["eq", "x17", "ach"], _listMode = [];
    private readonly CharacterFiles _charFiles = new();
    private readonly Dictionary<int, (string En, string Es)> _norm = new();
    private ItemQuery _query = ItemQuery.Empty;
    private readonly Dictionary<string, (DateTime Written, string Cls)> _cls = new();
    private ItemsIndex _index = new();
    private int _columns = 12;
    private (DateTime Stamp, AccountWarehouse? Store) _account;
    private readonly DispatcherTimer _searchTick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private readonly ComboBox _picker, _langBox, _sortBox;
    private readonly TextBox _search;
    private readonly TextBlock _status, _sideCount;
    private readonly ItemsControl _mainList;
    private readonly ScrollViewer _mainScroll;
    private readonly StackPanel _sideList, _showPills;
    private readonly Border _side;
    private readonly Grid _cols, _footGrid;
    private readonly WrapPanel _legend;
    private readonly TextBlock _footCaption;

    public ItemsView(AppShell shell) : base(shell, "items", UiText.Current.ItemsTitle)
    {
        _watch = DataWatch.Of(shell);
        _lang = shell.Settings.ResolvedNamesLanguage;
        _picker = _watch.Picker();
        HeaderRight.Children.Add(_picker);

        // search · item language · status | Show pills · Sort
        var top = new WrapPanel { Margin = new Thickness(14, 0, 14, 6) };
        var (searchHost, search) = ItemKit.SearchBox(T.ItemsSearch, 220, () => { _searchTick.Stop(); _searchTick.Start(); });
        _search = search;
        searchHost.Margin = new Thickness(0, 0, 6, 4);
        top.Children.Add(searchHost);
        _langBox = UiKit.Combo(["Items: English", "Items: Español"], _lang == "es" ? 1 : 0, 130);
        _langBox.Height = 28; _langBox.Margin = new Thickness(0, 0, 10, 4);
        UiKit.Tip(_langBox, T.ItemsLangTip);
        _langBox.SelectionChanged += (_, _) => LangChanged();
        top.Children.Add(_langBox);
        _status = new TextBlock { FontSize = 12, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(_status);

        var tools = new DockPanel();
        var sortPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sortPanel.Children.Add(new TextBlock { Text = T.ItemsSort, FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        _sortBox = UiKit.Combo([T.ItemsSortGame, T.ItemsSortGrade, T.ItemsSortName, T.ItemsSortValue], 0, 130);
        _sortBox.Height = 28;
        _sortBox.SelectionChanged += (_, _) => { _sort = (ItemSortMode)Math.Max(0, _sortBox.SelectedIndex); Render(); };
        sortPanel.Children.Add(_sortBox);
        DockPanel.SetDock(sortPanel, Dock.Right);
        tools.Children.Add(sortPanel);
        _showPills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        BuildPills();
        tools.Children.Add(new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _showPills } });
        var toolsCard = new Border { Background = CardBrush, BorderBrush = Line, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(8), Margin = new Thickness(14, 0, 14, 10), Padding = new Thickness(12, 7), Child = tools };

        // cards of the character (virtualised rows) | Across characters
        _mainList = new ItemsControl { ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
        _mainScroll = new ScrollViewer { Content = _mainList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _mainScroll.SizeChanged += (_, e) => MainResized(e.NewSize.Width);
        _sideCount = new TextBlock { FontSize = 12, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center };
        var sideHead = new DockPanel();
        DockPanel.SetDock(_sideCount, Dock.Right);
        sideHead.Children.Add(_sideCount);
        sideHead.Children.Add(new TextBlock { Text = T.ItemsAcross, FontSize = 14, FontWeight = FontWeight.Medium, Foreground = Text, VerticalAlignment = VerticalAlignment.Center });
        _sideList = new StackPanel();
        var sideDock = new DockPanel();
        var band = new Border { Background = Band, CornerRadius = new CornerRadius(8, 8, 0, 0), Padding = new Thickness(12, 8), Child = sideHead };
        DockPanel.SetDock(band, Dock.Top);
        sideDock.Children.Add(band);
        sideDock.Children.Add(new ScrollViewer { Content = _sideList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _side = new Border { Background = CardBrush, BorderBrush = Line, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(8), Child = sideDock };
        _cols = new Grid { Margin = new Thickness(14, 0, 14, 0), ColumnDefinitions = new ColumnDefinitions("*,10,330"), RowDefinitions = new RowDefinitions("*,0") };
        _cols.Place(_mainScroll, 0, 0);
        _cols.Place(_side, 0, 2);

        _legend = new WrapPanel { Margin = new Thickness(26, 6, 14, 8) };
        BuildLegend();

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Background = Page };
        body.Place(top, 0, 0);
        body.Place(toolsCard, 1, 0);
        body.Place(_cols, 2, 0);
        body.Place(_legend, 3, 0);
        SetBody(body);

        _footGrid = new Grid();
        _footCaption = new TextBlock { FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        SetFooter(new StackPanel { Children = { _footGrid, _footCaption } });

        _searchTick.Tick += (_, _) => { _searchTick.Stop(); _q = ItemNames.Normalize(_search.Text ?? ""); _query = ItemQuery.Parse(_q); Render(); };
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); ItemTile.NotifyIcons(); };
        shell.Icons.Loaded += () => { if (_shown && !_iconTick.IsEnabled) _iconTick.Start(); };
        _watch.Changed += c => { if ((c & (DataChange.Characters | DataChange.Market)) != 0) OnChanged(); };
        _watch.SelectionChanged += OnChanged;
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) ApplyWidth(e.NewSize.Width); };
        ApplyWidth(560);
        shell.WhenEngine(e => { e.Names.Changed += () => Dispatcher.UIThread.Post(() => { _norm.Clear(); OnChanged(); }); if (_shown) Reload(); });
    }

    /// <summary>"Open in Craft →" from this tab: the Craft tab with this text in its search box.</summary>
    private void OpenInCraft(string text)
    {
        CraftView.RequestSearch(text);
        Shell.SelectTab("craft");
    }

    public override void Shown()
    {
        _shown = true;
        _watch.Acquire();
        if (!_argsApplied) Shell.WhenEngine(_ => ApplyViewArgs());
        Reload();
    }

    private bool _argsApplied;   // per instance: --popout makes a second view after the card's

    /// <summary>--view search=…, open=bag,wh (cards to expand), shut=today,ach, list=bag,acct, char=&lt;key&gt; (screenshots, as WPF ShowSample/SampleSearch).</summary>
    private void ApplyViewArgs()
    {
        if (_argsApplied) return;
        _argsApplied = true;
        var a = App.ViewArgs;
        if (a.TryGetValue("char", out var key)) _watch.Select(key);
        if (a.TryGetValue("open", out var open)) foreach (var k in open.Split(',')) _collapsed.Remove(k);
        if (a.TryGetValue("shut", out var shut)) foreach (var k in shut.Split(',')) _collapsed.Add(k);
        if (a.TryGetValue("list", out var list)) foreach (var k in list.Split(',')) _listMode.Add(k);
        if (a.TryGetValue("search", out var q)) { _search.Text = q; _searchTick.Stop(); _q = ItemNames.Normalize(q); _query = ItemQuery.Parse(_q); }
        Reload();
    }

    public override void Hidden()
    {
        _shown = false;
        _watch.Release();
        _iconTick.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.ItemsTitle;
        BuildPills();
        BuildLegend();
        if (_shown) Render();
    }

    // hidden tab: nothing now, Shown reloads
    private void OnChanged() { if (_shown) Reload(); }

    private void Reload()
    {
        if (Shell.Engine is not { } e) { _status.Text = "…"; return; }
        _data = Gather(e);
        Render();
    }

    private static string ServerOf(string key) { var i = key.IndexOf('-'); return i > 0 ? key[..i] : ""; }
    private static string NameOf(string key) { var i = key.IndexOf('-'); return i > 0 ? key[(i + 1)..] : key; }

    private Data Gather(MeterEngine m)
    {
        var cur = _watch.Viewed;
        var keys = m.CharacterKeys();
        if (cur.Length > 0 && !keys.Contains(cur)) keys.Add(cur);
        // nobody viewed yet: the effective server's characters (A2M-40, Settings → Game) when it has any, else the first folder's
        var server = cur.Length > 0 ? ServerOf(cur) : ServerWithCharacters(keys, m.Server.WarehouseKey);
        var chars = new List<CharData>(keys.Count);
        var root = Path.Combine(AppSettings.AppDataDir, "characters");
        foreach (var k in keys)
        {
            if (ServerOf(k) != server) continue;   // the account warehouse and the footer are per server
            var f = m.OtherInventories.Get(Path.Combine(root, k, "inventory.json"));
            chars.Add(new CharData(k, ClassLine(k), f, _charFiles.Get(Path.Combine(root, k, "character.json"))));
        }
        var serverName = int.TryParse(server, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid) ? m.Data.ServerName(sid) : server;
        if (m.Market.Server.Length > 0 && serverName == server) serverName = m.Market.Server;
        CharacterProgress? viewed = null;
        if (cur.Length > 0) foreach (var c in chars) if (c.Key == cur) viewed = c.Progress;
        return new Data(cur, server, serverName, chars, server.Length > 0 ? Account()?.View(server) : null, m.Market, DateTimeOffset.Now, viewed, m.AchievementBook);
    }

    private static string ServerWithCharacters(List<string> keys, string preferred)
    {
        for (var i = 0; i < keys.Count; i++) if (ServerOf(keys[i]) == preferred) return preferred;
        return keys.Count > 0 ? ServerOf(keys[0]) : "";
    }

    /// <summary>account-warehouse.json, read again only when the daemon rewrote it (the engine's copy is a start-up snapshot).</summary>
    private AccountWarehouse? Account()
    {
        var path = Path.Combine(AppSettings.AppDataDir, "account-warehouse.json");
        DateTime w;
        try { w = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return _account.Store; }
        if (_account.Store is null || _account.Stamp != w) _account = (w, new AccountWarehouse(path));
        return _account.Store;
    }

    /// <summary>"Ranger lv45  ·  combat power …" (profile.txt, first line) → "Ranger 45"; re-read only when the file changes.</summary>
    private string ClassLine(string key)
    {
        var path = Path.Combine(AppSettings.AppDataDir, "characters", key, "profile.txt");
        try
        {
            if (!File.Exists(path)) return "";
            var w = File.GetLastWriteTimeUtc(path);
            if (_cls.TryGetValue(key, out var c) && c.Written == w) return c.Cls;
            using var r = new StreamReader(path);
            var first = r.ReadLine() ?? "";
            var dot = first.IndexOf("  ·  ", StringComparison.Ordinal);
            var cls = (dot > 0 ? first[..dot] : first).Replace(" lv", " ", StringComparison.Ordinal).Trim();
            _cls[key] = (w, cls);
            return cls;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // ------------------------------------------------------------------ helpers

    private MeterEngine M => Shell.Engine!;
    private string Nm(int id) => M.Names.Name(id, _lang) ?? M.UnnamedItem(id);
    private string Lb(int id) => M.Names.Label(id, _lang) ?? M.UnnamedItem(id);   // inside a line: "(legendary)" only on an ambiguous name
    private string OtherName(int id) { var (en, es) = M.Names.Both(id); var other = _lang == "es" ? en : es; return other is not null && other != Nm(id) ? other : ""; }
    private long? Price(int id) => _data?.Market.Price(id);

    private bool Matches(int id)
    {
        if (_q.Length == 0) return true;
        if (!_norm.TryGetValue(id, out var n))
        {
            var (en, es) = M.Names.Both(id);
            n = (ItemNames.Normalize(en ?? M.Items.NameOf(id)), ItemNames.Normalize(es ?? ""));
            _norm[id] = n;
        }
        return _query.Matches(M.Grades.Of(id), n.En, n.Es);
    }

    private bool InGroup(int id) => _group switch { "eq" => ItemKinds.Group(id) == ItemGroup.Equipment, "mat" => ItemKinds.Group(id) == ItemGroup.Materials, "use" => ItemKinds.Group(id) == ItemGroup.Consumables, _ => true };

    private string When(long ms) => ms <= 0 ? "" : When(DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime());
    private string When(DateTimeOffset t) => _data is { } d && t.Date == d.Now.Date ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static DateTimeOffset Local(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime();

    /// <summary>When the character was last seen: its newest container time, else (files of older builds) the file's write time.</summary>
    private string SeenOf(InventoryFile f, long alsoMs = 0)
    {
        var ms = alsoMs;
        foreach (var v in f.Seen.Values) ms = Math.Max(ms, v.SeenMs);
        return ms > 0 ? When(ms) : When(Local(f.WrittenUtc));
    }

    private static string N(long v) => ItemKit.N(v);
    private static string Stacks(int n) => n == 1 ? T.ItemsStack1 : string.Format(T.ItemsStacks, n);
    private static string MarketLabel(MarketSnapshot m) => m.Count == 0 ? T.ItemsNoMarket : string.Format(T.ItemsMarketFrom, m.Server, m.CapturedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");

    private static TextBlock Line1(IBrush? fg = null, double size = 12.5) => new() { Foreground = fg ?? Text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Inlines = new InlineCollection() };

    // ------------------------------------------------------------------ render

    private void Render()
    {
        if (_data is not { } d || Shell.Engine is null) return;
        _index = new ItemsIndex();
        foreach (var c in d.Chars)
        {
            if (c.File is not { } f) continue;
            _index.Add(c.Key, Place.Bag, f.Items(Containers.Bag));
            _index.Add(c.Key, Place.CharacterWarehouse, f.Items(Containers.CharacterWarehouse));
            var eq = f.Items(Containers.Equipped); eq.AddRange(f.Items(Containers.Cosmetic));
            _index.Add(c.Key, Place.Equipped, eq);
        }
        var acct = AccountStacks(d.Account);
        _index.Add("", Place.AccountWarehouse, acct);

        var offset = _mainScroll.Offset;
        var rows = new List<Control>(64);
        CharData? cur = null;
        foreach (var c in d.Chars) if (c.Key == d.CurrentKey) cur = c;
        var stacksSeen = 0;
        if (d.Progress is { } prog && _q.Length == 0) { AddToday(rows, prog, d.Now); AddAchievements(rows, prog, d.Catalog); }
        if (cur?.File is not { } file) rows.Add(Card(Hint(T.ItemsNoChar), top: true, bottom: true));
        else
        {
            var bag = file.Items(Containers.Bag);
            stacksSeen = file.Stacks.Count;
            var bagSeen = file.SeenOf(Containers.Bag);
            var incomplete = bagSeen.FullMs == 0 && ItemKinds.BagLooksIncomplete(bag);
            var maxSlot = 0;
            foreach (var s in bag) if (s.Slot > maxSlot) maxSlot = s.Slot;
            AddCard(rows, "bag", T.ItemsBag, NameOf(cur.Key),
                incomplete ? T.ItemsChipPartial : T.ItemsChipLogin + (bagSeen.FullMs > 0 ? " · " + When(bagSeen.FullMs) : ""), incomplete ? Amber : Green,
                Stacks(bag.Count), bag, incomplete ? string.Format(T.ItemsBagIncomplete, bag.Count, maxSlot + 1 - bag.Count, maxSlot) : null, cur.Key, Place.Bag, null);

            var wh = file.Items(Containers.CharacterWarehouse);
            var whSeen = file.SeenOf(Containers.CharacterWarehouse);
            // container times exist since A2M-32; older files: 10+ stacks can only come from the window's full list
            var whFull = whSeen.FullMs > 0 || (whSeen.SeenMs == 0 && wh.Count >= 10);
            var whAt = whSeen.SeenMs > 0 ? When(whSeen.SeenMs) : When(Local(file.WrittenUtc));
            var (whChip, whBrush, whWarn) = wh.Count == 0 && whSeen.SeenMs == 0 ? (T.ItemsChipNever, Mute, T.ItemsWhNever)
                : whFull ? (string.Format(T.ItemsChipLastSeen, whAt), Green, (string?)null)
                : (string.Format(T.ItemsChipPartialAt, whAt), Amber, string.Format(T.ItemsWhPartial, wh.Count));
            AddCard(rows, "wh", T.ItemsCharWh, NameOf(cur.Key), whChip, whBrush, Stacks(wh.Count), wh, whWarn, cur.Key, Place.CharacterWarehouse, null);
        }

        if (d.Account is { } a)
        {
            var (by, at) = a.LastUpdate();
            var newer = a.NewerThanFull();
            string? warn;
            if (a.FullMs == 0) warn = T.ItemsAcctNoFull;
            else if (newer.Count == 0) warn = null;
            else
            {
                var parts = new string[newer.Count];
                for (var i = 0; i < newer.Count; i++) parts[i] = string.Format(T.ItemsAcctNewer, newer[i].Slots, NameOf(newer[i].By), When(newer[i].AtMs));
                warn = string.Format(T.ItemsAcctFull, NameOf(a.FullBy), When(a.FullMs)) + "; " + string.Join(", ", parts) + " " + T.ItemsAcctDot;
            }
            var dots = new HashSet<byte>();
            foreach (var s in a.Slots) if (s.AtMs > a.FullMs) dots.Add(s.Slot);
            AddCard(rows, "acct", T.ItemsAcctWh, string.Format(T.ItemsShared, d.ServerName), at > 0 ? string.Format(T.ItemsChipUpdatedBy, NameOf(by), When(at)) : T.ItemsChipNever, at > 0 ? Green : Mute,
                Stacks(a.Slots.Count), acct, warn, "", Place.AccountWarehouse, dots);
        }

        if (cur?.File is { } f2)
        {
            var eq = f2.Items(Containers.Equipped); eq.AddRange(f2.Items(Containers.Cosmetic));
            AddCard(rows, "eq", T.ItemsEquipped, NameOf(cur.Key), null, Mute, Stacks(eq.Count), eq, null, cur.Key, Place.Equipped, null);
            var x17 = f2.Items(Containers.Unknown17);
            if (x17.Count > 0) AddCard(rows, "x17", T.ItemsUnknown17, NameOf(cur.Key), null, Mute, Stacks(x17.Count), x17, null, cur.Key, Place.Equipped, null);
        }
        _mainList.ItemsSource = rows;
        if (offset.Y > 0) Dispatcher.UIThread.Post(() => _mainScroll.Offset = offset, DispatcherPriority.Loaded);

        RenderSide(d);
        RenderFoot(d);
        _status.Text = cur is null ? "" : NameOf(cur.Key) + (cur.Cls.Length > 0 ? " · " + cur.Cls : "") + " · " + Stacks(stacksSeen);
    }

    // ------------------------------------------------------------------ A2M-15: Today (content tickets) and Achievements cards

    private static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(0, t.Minutes)} min";

    private void AddToday(List<Control> rows, CharacterProgress p, DateTimeOffset now)
    {
        const string key = "today";
        var shut = _collapsed.Contains(key);
        var daily = GameReset.NextDaily(now); var weekly = GameReset.NextWeekly(now);
        p.Tickets.TryGetValue(60_000_001, out var odyle); p.Tickets.TryGetValue(1, out var keys);
        var right = (odyle is not null ? TicketBook.Of(odyle.Key).Name(_lang) + " " + TicketBook.Value(odyle, TicketBook.Of(odyle.Key)) + " · " : "")
                  + (keys is not null ? TicketBook.Of(keys.Key).Name(_lang) + " " + keys.Total + " · " : "") + string.Format(T.ItemsResetIn, Span(daily - now));
        rows.Add(Head(key, T.ItemsToday, T.ItemsTodaySub, null, Mute, right, 0, shut, toggle: false));
        if (shut) return;
        rows.Add(Card(Hint(string.Format(T.ItemsTodayReset, daily.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), Span(daily - now),
            weekly.ToLocalTime().ToString("ddd dd HH:mm", T.Culture)))));
        if (p.Tickets.Count == 0) { rows.Add(Card(Hint(T.ItemsTodayNone))); rows.Add(Card(new Border { Height = 6 }, bottom: true)); return; }
        foreach (var (t, info) in TicketBook.Today(p.Tickets)) rows.Add(Card(TicketRow(t, info, p.Chests)));
        // Daeva Pass / event pass levels: the 810 rows count pass EXP and their claimed step is the reward level (A2M-15)
        foreach (var (id, label) in new[] { (810_433_000, T.ItemsDaevaPass), (810_435_000, T.ItemsEventPass) })
        {
            if (!p.Achievements.TryGetValue(id, out var r) || r.Count == 0) continue;
            var tiers = M.AchievementBook.Get(id)?.Tiers.Length ?? 0;
            rows.Add(Card(Hint(string.Format(T.ItemsPassLine, label, r.Step, tiers > 0 ? tiers.ToString(CultureInfo.InvariantCulture) : "?", N(r.Count)))));
        }
        rows.Add(Card(new Border { Height = 6 }, bottom: true));
    }

    private static Grid Cols3(string spec, double height) => new() { Margin = new Thickness(12, 0, 12, 0), MinHeight = height, ColumnDefinitions = new ColumnDefinitions(spec) };

    private Control TicketRow(ContentTicket t, TicketInfo info, IReadOnlyDictionary<int, ExpeditionChest>? chests)
    {
        var g = Cols3("*,150,130", 26);
        var name = Cell(g, 0, info.Stars > 0 ? info.Name(_lang) + " " + new string('★', info.Stars) : info.Name(_lang), info.Inferred ? Dim : Text, 12.5);
        if (info.Inferred) UiKit.Tip(name, T.ItemsTodayInferred + " (" + t.Key.ToString(CultureInfo.InvariantCulture) + ")");
        if (info.Chest > 0)
        {
            // A2M-41: the 6000N201 ticket value (7/14) is not the chest bar and is never shown; the bar comes from 12 57
            var p = TicketBook.ChestProgress(info, chests);
            if (p >= info.Chest) Cell(g, 1, T.ItemsTodayClaimable, Green, 11.5, true);
            var chest = Cell(g, 2, string.Format(T.ItemsTodayChest, p?.ToString(CultureInfo.InvariantCulture) ?? "?", info.Chest.ToString(CultureInfo.InvariantCulture)),
                p is null ? Faint : p >= info.Chest ? Green : p > 0 ? Num : Dim, 12.5, true);
            UiKit.Tip(chest, p is null ? T.ItemsTodayChestUnknown : T.ItemsTodayChestTip);
            return new Border { BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g };
        }
        var value = t.IsTime ? string.Format(T.ItemsTodayLeft, Span(TimeSpan.FromMilliseconds(t.Time))) : TicketBook.Value(t, info);
        Cell(g, 2, value, t.Total == 0 && !t.IsTime || t.IsTime && t.Time == 0 ? Faint : Num, 12.5, true);
        return new Border { BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g };
    }

    private void AddAchievements(List<Control> rows, CharacterProgress p, AchievementCatalog catalog)
    {
        const string key = "ach";
        var shut = _collapsed.Contains(key);
        var sum = catalog.Summarise(p.Achievements.Values);
        var right = p.Achievements.Count == 0 || catalog.Count == 0 ? "" : string.Format(T.ItemsAchRight, N(sum.TiersClaimed), N(sum.TiersTotal), N(sum.Completed), N(sum.Claimable), N(sum.InProgress));
        rows.Add(Head(key, T.ItemsAch, "", sum.Claimable > 0 ? sum.Claimable.ToString(CultureInfo.InvariantCulture) : null, Green, right, 0, shut, toggle: false));
        if (shut) return;
        if (p.Achievements.Count == 0) rows.Add(Card(Hint(T.ItemsAchNone)));
        else if (catalog.Count == 0) rows.Add(Card(Hint(T.ItemsAchNoCatalog)));
        else if (sum.ClaimableList.Count == 0) rows.Add(Card(Hint(T.ItemsAchNothing)));
        else foreach (var a in sum.ClaimableList) rows.Add(Card(AchievementRowView(a)));
        rows.Add(Card(new Border { Height = 6 }, bottom: true));
    }

    private Control AchievementRowView(AchievementProgress a)
    {
        var info = a.Info!;
        var g = Cols3("1.2*,150,1.4*", 34);
        var name = Cell(g, 0, info.Name(_lang), Text, 12.5);
        var desc = info.Desc(_lang);
        if (desc.Length > 0) UiKit.Tip(name, desc);
        var tier = string.Format(T.ItemsAchTier, a.NextTier, a.Tiers, N(a.Row.Count), N(a.Goal));
        if (a.ClaimableTiers > 1) tier += " · " + string.Format(T.ItemsAchMore, a.ClaimableTiers);
        Cell(g, 1, tier, Dim, 11.5);
        var rewards = info.Tiers[a.NextTier - 1].Rewards;
        var parts = new string[rewards.Length];
        for (var i = 0; i < rewards.Length; i++) parts[i] = Lb(rewards[i].Item) + " ×" + N(rewards[i].Amount);
        var rw = Cell(g, 2, string.Join(", ", parts), Num, 11.5);
        UiKit.Tip(rw, string.Join("\n", parts));
        return new Border { BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g };
    }

    private static List<ItemStack> AccountStacks(AccountWarehouseView? a)
    {
        var l = new List<ItemStack>(a?.Slots.Count ?? 0);
        if (a is null) return l;
        foreach (var s in a.Slots) l.Add(new ItemStack(0, s.Item, s.Count, Containers.AccountWarehouse, s.Slot, s.Enchant));
        return l;
    }

    private void AddCard(List<Control> rows, string key, string title, string sub, string? chip, IBrush chipBrush, string right, List<ItemStack> stacks, string? warn,
        string who, Place place, HashSet<byte>? dots)
    {
        var visible = new List<ItemStack>(stacks.Count);
        long kina = 0;
        foreach (var s in stacks) { if (s.Item == InventoryStore.Kinah) kina += s.Count; if (!InventoryStore.IsCurrency(s.Item) && InGroup(s.Item)) visible.Add(s); }
        // Currencies stay out of the grid (the footer sums them); the purse itself is named in the header so a warehouse deposit is visible at a glance (Diego 2026-10-13).
        if (kina > 0) right = right + " · " + string.Format(T.ItemsKinaHere, N(kina));
        ItemKinds.Sort(visible, _sort, Nm, M.Grades.Of, Price);
        long value = 0;
        foreach (var s in visible) value += (Price(s.Item) ?? 0) * s.Count;
        var shut = _collapsed.Contains(key);
        rows.Add(Head(key, title, sub, chip, chipBrush, right, value, shut));
        if (shut) return;
        if (warn is not null) rows.Add(Card(new Border { Background = WarnBack, BorderBrush = WarnLine, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), Margin = new Thickness(12, 8, 12, 0),
            Child = new TextBlock { Text = warn, Foreground = Amber, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 17.4 } }));
        if (visible.Count == 0) rows.Add(Card(Hint(T.ItemsNothing)));
        else if (_listMode.Contains(key))
        {
            rows.Add(Card(ListHeader()));
            foreach (var s in visible) rows.Add(Card(ListRow(s, who, place, dots)));
        }
        else
        {
            for (var i = 0; i < visible.Count; i += _columns)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, i == 0 ? 10 : 0, 12, TileGap) };
                for (var j = i; j < visible.Count && j < i + _columns; j++)
                {
                    var t = MakeTile(visible[j], dots is not null && dots.Contains(visible[j].Slot));
                    t.Margin = new Thickness(0, 0, TileGap, 0);
                    row.Children.Add(t);
                }
                rows.Add(Card(row));
            }
        }
        rows.Add(Card(new Border { Height = 6 }, bottom: true));
    }

    private ItemTile MakeTile(ItemStack s, bool dot, double size = Tile, bool searchable = true)
    {
        var t = new ItemTile { Width = size, Height = size };
        var hit = searchable && _q.Length > 0 && Matches(s.Item);
        t.Set(s.Item, s.Count, s.Enchant, M.Grades.Of(s.Item), Nm(s.Item), Shell.Icons, dot, dim: searchable && _q.Length > 0 && !hit, hit: hit);
        AttachTip(t);
        return t;
    }

    /// <summary>One row of a card: the card's side borders (and its top or bottom corners) drawn per row, so rows can be virtualised.</summary>
    private static Border Card(Control child, bool top = false, bool bottom = false) => new()
    {
        Background = CardBrush, BorderBrush = Line, Child = child,
        BorderThickness = new Thickness(0.8, top ? 0.8 : 0, 0.8, bottom ? 0.8 : 0),
        CornerRadius = new CornerRadius(top ? 8 : 0, top ? 8 : 0, bottom ? 8 : 0, bottom ? 8 : 0),
        Margin = new Thickness(0, 0, 0, bottom ? 10 : 0),
    };

    private static TextBlock Hint(string text) => new() { Text = text, Foreground = Mute, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10), LineHeight = 18 };

    private static Border Chip(string text, IBrush fg) => new()
    {
        Background = ChipBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = fg, FontSize = 11 },
    };

    private Control Head(string key, string title, string sub, string? chip, IBrush chipBrush, string right, long value, bool shut, bool toggle = true)
    {
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightPanel.Children.Add(new TextBlock { Text = right, Foreground = Mute, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        if (value > 0)
            rightPanel.Children.Add(UiKit.Tip(new TextBlock { Text = string.Format(T.ItemsValue, ItemKit.Short(value)), Foreground = Mute, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) },
                string.Format(T.ItemsValueTip, MarketLabel(_data!.Market))));
        if (toggle) rightPanel.Children.Add(ViewToggle(key));
        // the left side clips under the right one on a narrow card (WPF DockPanel)
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        left.Children.Add(new TextBlock { Text = shut ? "▸" : "▾", Foreground = Mute, Width = 14, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.Medium, Foreground = Text, VerticalAlignment = VerticalAlignment.Center });
        if (sub.Length > 0) left.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) });
        if (chip is not null) left.Children.Add(Chip(chip, chipBrush));
        var dock = UiKit.Row("*,Auto", left, rightPanel);
        var b = new Border
        {
            Background = Band, BorderBrush = Line, BorderThickness = new Thickness(0.8, 0.8, 0.8, shut ? 0.8 : 0), CornerRadius = new CornerRadius(8, 8, shut ? 8 : 0, shut ? 8 : 0),
            Padding = new Thickness(12, 7), Margin = new Thickness(0, 0, 0, shut ? 10 : 0), Child = dock, Cursor = Hand,
        };
        b.PointerReleased += (_, e) => { if (e.Handled || e.InitialPressMouseButton != MouseButton.Left) return; if (!_collapsed.Remove(key)) _collapsed.Add(key); Render(); };
        return b;
    }

    /// <summary>Mock .vt: tiles ▦ / list ☰ per card.</summary>
    private Control ViewToggle(string key)
    {
        var list = _listMode.Contains(key);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, isList, tip) in new[] { ("▦", false, T.ItemsTiles), ("☰", true, T.ItemsList) })
        {
            var on = isList == list;
            var cell = new Border { Background = on ? ChipBack : Transparent, Padding = new Thickness(8, 1), Cursor = Hand,
                Child = new TextBlock { Text = glyph, FontSize = 12, Foreground = on ? Text : Mute } };
            UiKit.Tip(cell, tip);
            cell.PointerReleased += (_, e) => { e.Handled = true; if (isList) _listMode.Add(key); else _listMode.Remove(key); Render(); };
            panel.Children.Add(cell);
        }
        return new Border { BorderBrush = Line2, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(6), Margin = new Thickness(10, 0, 0, 0), Child = panel, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Grid ListGrid() => new() { Margin = new Thickness(12, 0, 12, 0), ColumnDefinitions = new ColumnDefinitions("44,1.4*,70,*,1.2*,56,64") };

    private static TextBlock Cell(Grid g, int col, string text, IBrush fg, double size = 12.5, bool right = false)
    {
        var t = new TextBlock { Text = text, Foreground = fg, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Grid.SetColumn(t, col);
        g.Children.Add(t);
        return t;
    }

    private static Control ListHeader()
    {
        var g = ListGrid();
        g.Margin = new Thickness(12, 6, 12, 2);
        Cell(g, 1, T.ItemsColName, Mute, 11); Cell(g, 2, T.ItemsColCount, Mute, 11, true); Cell(g, 3, T.ItemsColWhere, Mute, 11); Cell(g, 4, T.ItemsColAlso, Mute, 11);
        Cell(g, 5, T.ItemsColUnit, Mute, 11, true); Cell(g, 6, T.ItemsColValue, Mute, 11, true);
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g, Margin = new Thickness(0, 4, 0, 0) };
    }

    private Control ListRow(ItemStack s, string who, Place place, HashSet<byte>? dots)
    {
        var g = ListGrid();
        g.Height = 42;
        var tile = MakeTile(s, dots is not null && dots.Contains(s.Slot), 36, searchable: false);
        tile.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(tile);
        var grade = M.Grades.Of(s.Item);
        var name = Line1();
        name.Margin = new Thickness(4, 0);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Inlines!.Add(new Run(Nm(s.Item)) { Foreground = Visuals.GradeText(grade) });
        if (s.Enchant > 0) name.Inlines.Add(new Run(" +" + s.Enchant.ToString(CultureInfo.InvariantCulture)) { Foreground = Num });
        ItemKit.AddChip(name.Inlines, grade, _lang);
        if (M.Grades.Bound(s.Item)) { name.Inlines.Add(new LineBreak()); name.Inlines.Add(new Run(T.ItemsBound) { Foreground = Mute, FontSize = 11 }); }
        Grid.SetColumn(name, 1); g.Children.Add(name);
        Cell(g, 2, N(s.Count), Text, 12.5, true);
        var where = string.Format(T.ItemsSlot, s.Slot);
        if (place == Place.AccountWarehouse && _data?.Account is { } acc)
            foreach (var x in acc.Slots) if (x.Slot == s.Slot) { where += " · " + NameOf(x.By) + " " + When(x.AtMs); break; }
        Cell(g, 3, where, Mute, 11.5);
        var also = Elsewhere(s.Item, who, place);
        var alsoCell = Cell(g, 4, also.Length > 0 ? also : "—", Mute, 11.5);
        if (also.Length > 0) UiKit.Tip(alsoCell, also);
        var p = Price(s.Item);
        Cell(g, 5, p is { } unit ? ItemKit.Short(unit) : "—", p is null ? Faint : Text, 12.5, true);
        Cell(g, 6, p is { } u2 ? ItemKit.Short(u2 * s.Count) : "", Text, 12.5, true);
        var b = new Border { BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g };
        if (_q.Length > 0 && !Matches(s.Item)) b.Opacity = 0.25;
        return b;
    }

    /// <summary>Where else the item is ("Irin bag 4 · acct 191"), equipped pieces left out (mock "Also").</summary>
    private string Elsewhere(int item, string who, Place place)
    {
        var parts = new List<string>(4);
        foreach (var h in _index.Of(item))
        {
            if (h.Place == Place.Equipped || (h.Place == place && h.Who == who)) continue;
            parts.Add(PlaceLabel(h) + " " + N(h.Count));
        }
        return string.Join(" · ", parts);
    }

    private static string PlaceLabel(Holding h) => h.Place switch
    {
        Place.AccountWarehouse => T.ItemsPlaceAcct,
        Place.Bag => NameOf(h.Who) + " " + T.ItemsPlaceBag,
        Place.CharacterWarehouse => NameOf(h.Who) + " " + T.ItemsPlaceWh,
        _ => NameOf(h.Who) + " " + T.ItemsPlaceWorn,
    };

    // ------------------------------------------------------------------ tooltip

    private static readonly EventHandler<CancelRoutedEventArgs> TipOpening = (s, _) =>
    {
        if (s is not ItemTile { Item: > 0 } t || t.FindAncestorOfType<ItemsView>() is not { } view) return;
        ToolTip.SetTip(t, view.TipContent(t.Item, t.Count));
    };

    private static void AttachTip(ItemTile t)
    {
        ToolTip.SetTip(t, "…");   // the content is built on every opening (the index and prices move)
        ToolTip.SetShowDelay(t, 250);
        ToolTip.SetPlacement(t, PlacementMode.Pointer);
        t.AddHandler(ToolTip.ToolTipOpeningEvent, TipOpening);
    }

    /// <summary>Mock .tip: icon, name (grade colour), other-language name, "grade · bound · id", then this stack / everywhere / where / market / from.</summary>
    private Control TipContent(int item, long count)
    {
        var root = new StackPanel { Width = 280 };
        root.Children.Add(ItemHead(item, 44));
        var kv = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var r = 0;
        void Kv(string k, string v)
        {
            kv.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            kv.Place(new TextBlock { Text = k, Foreground = Mute, FontSize = 12, Margin = new Thickness(0, 1, 10, 1) }, r, 0);
            kv.Place(new TextBlock { Text = v, Foreground = Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) }, r, 1);
            r++;
        }
        Kv(T.ItemsThisStack, N(count));
        Kv(T.ItemsEverywhere, N(_index.Total(item)));
        var holds = _index.Of(item);
        if (holds.Count > 0)
        {
            var parts = new string[holds.Count];
            for (var i = 0; i < holds.Count; i++) parts[i] = PlaceLabel(holds[i]) + " " + N(holds[i].Count);
            Kv(T.ItemsWhere, string.Join(" · ", parts));
        }
        if (Price(item) is { } p) Kv(T.ItemsMarket, string.Format(T.ItemsMarketEach, N(p)));
        if (M.Names.Source(item) is { Length: > 0 } src) Kv(T.ItemsFromLbl, src);
        root.Children.Add(new Border { Background = KvBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6), Child = kv });
        return root;
    }

    private Control ItemHead(int item, double size)
    {
        var head = new DockPanel();
        var tile = new ItemTile { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var grade = M.Grades.Of(item);
        tile.Set(item, 1, 0, grade, Nm(item), Shell.Icons);
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(NameLine(item, grade, 13.5, wrap: true));
        if (OtherName(item) is { Length: > 0 } other) names.Children.Add(new TextBlock { Text = other, FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap });
        // the grade is on the chip; the line keeps "grade unknown" when there is none
        var meta = (grade == Grade.Unknown ? T.ItemsGradeUnknown + " · " : "") + (M.Grades.Bound(item) ? T.ItemsBound + " · " : "") + "id " + item.ToString(CultureInfo.InvariantCulture);
        names.Children.Add(new TextBlock { Text = meta, FontSize = 11, Foreground = Mute });
        head.Children.Add(names);
        return head;
    }

    /// <summary>Name in the grade colour plus the grade chip (same-named items differ only by grade).</summary>
    private TextBlock NameLine(int item, Grade grade, double size, bool wrap)
    {
        var t = Line1(null, size);
        t.FontWeight = FontWeight.SemiBold;
        t.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        t.Inlines!.Add(new Run(Nm(item)) { Foreground = Visuals.GradeText(grade) });
        ItemKit.AddChip(t.Inlines, grade, _lang);
        return t;
    }

    // ------------------------------------------------------------------ Across characters

    private void RenderSide(Data d)
    {
        _sideList.Children.Clear();
        List<int> ids;
        if (_q.Length > 0)
        {
            ids = _index.Matching(Matches);
            _sideCount.Text = string.Format(T.ItemsMatches, ids.Count);
            if (ids.Count == 0) _sideList.Children.Add(Hint(string.Format(T.ItemsNoMatch, (_search.Text ?? "").Trim())));
            for (var i = 0; i < ids.Count && i < 40; i++) _sideList.Children.Add(MatchCard(ids[i], d, i == 0));
        }
        else
        {
            // with an empty box: the same item split over 3+ storage places, worth consolidating (Diego 2026-10-11)
            ids = _index.Split();
            _sideCount.Text = "";
            _sideList.Children.Add(Hint(string.Format(T.ItemsAcrossHint, ids.Count)));
            for (var i = 0; i < ids.Count && i < 25; i++) _sideList.Children.Add(MatchCard(ids[i], d, false));
        }
    }

    private Control MatchCard(int item, Data d, bool first)
    {
        var root = new StackPanel();
        var head = new DockPanel();
        var total = _index.Total(item);
        var tot = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        tot.Children.Add(new TextBlock { Text = N(total), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Right });
        var sub = Price(item) is { } p ? string.Format(T.ItemsValue, ItemKit.Short(p * total)) : M.Grades.Bound(item) ? T.ItemsBound : "";
        if (sub.Length > 0) tot.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Mute, HorizontalAlignment = HorizontalAlignment.Right });
        DockPanel.SetDock(tot, Dock.Right);
        head.Children.Add(tot);
        var tile = MakeTile(new ItemStack(0, item, 1, 0, 0, 0), false, 36, searchable: false);
        tile.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var grade = M.Grades.Of(item);
        var names = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(NameLine(item, grade, 13, wrap: true));
        if (OtherName(item) is { Length: > 0 } other) names.Children.Add(new TextBlock { Text = other, FontSize = 11, Foreground = Mute, TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(names);
        root.Children.Add(head);

        var locs = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var holds = new List<Holding>(_index.Of(item));
        holds.Sort((a, b) => { var c = (b.Who == d.CurrentKey).CompareTo(a.Who == d.CurrentKey); if (c != 0) return c; c = (a.Place == Place.AccountWarehouse).CompareTo(b.Place == Place.AccountWarehouse); return c != 0 ? c : b.Count.CompareTo(a.Count); });
        foreach (var h in holds)
        {
            var t = Line1(Dim, 11.5);
            t.Inlines!.Add(new Run(PlaceLabel(h) + " "));
            t.Inlines.Add(new Run(N(h.Count)) { Foreground = Text, FontWeight = FontWeight.SemiBold });
            locs.Children.Add(new Border { Background = LocBack, BorderBrush = h.Who == d.CurrentKey ? MeLine : h.Place == Place.AccountWarehouse ? AcctLine : Line, BorderThickness = new Thickness(0.8),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), Margin = new Thickness(0, 0, 4, 4), Child = t });
        }
        root.Children.Add(locs);
        if (M.Names.Source(item) is { Length: > 0 } src) root.Children.Add(new TextBlock { Text = string.Format(T.ItemsFrom, src), FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        foreach (var hint in CraftHints.For(item, M.Recipes, _index.Total)) root.Children.Add(HintBox(hint));
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, first ? 0 : 0.8, 0, 0), Padding = new Thickness(12, 8), Child = root };
    }

    private Control HintBox(CraftHint h)
    {
        var t = Line1(Dim, 11.5);
        t.TextWrapping = TextWrapping.Wrap;
        t.VerticalAlignment = VerticalAlignment.Top;
        var outName = Nm(h.Recipe.Output);
        // an ambiguous name carries its grade word so Craft's search keeps only this grade
        var craftQuery = M.Names.Ambiguous(h.Recipe.Output, _lang) && M.Grades.Of(h.Recipe.Output) is var og && og != Grade.Unknown ? outName + " " + ItemGrades.Word(og, _lang) : outName;
        var link = new TextBlock { Text = T.ItemsOpenCraft, Foreground = Blue, FontSize = 11.5, Cursor = Hand, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        link.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; OpenInCraft(craftQuery); } };
        t.Inlines!.Add(new Run(T.ItemsUsedIn + " "));
        t.Inlines.Add(new Run(outName) { Foreground = Visuals.GradeText(M.Grades.Of(h.Recipe.Output)) });
        ItemKit.AddChip(t.Inlines, M.Grades.Of(h.Recipe.Output), _lang, 10);
        t.Inlines.Add(new Run($" {h.Recipe.Profession} {h.Recipe.Level}") { Foreground = Mute });
        t.Inlines.Add(new LineBreak());
        t.Inlines.Add(new Run(string.Format(T.ItemsNeeds, N(h.Need), N(h.Have))));
        if (h.Have >= h.Need) t.Inlines.Add(new Run(" ✓") { Foreground = Green });
        if (h.Missing.Length > 0)
        {
            t.Inlines.Add(new LineBreak());
            var n = Math.Min(4, h.Missing.Length);
            var parts = new string[n];
            for (var i = 0; i < n; i++) parts[i] = $"{Lb(h.Missing[i].Item)} {N(h.Missing[i].Have)}/{h.Missing[i].Need}";
            var miss = string.Join(" · ", parts) + (h.Missing.Length > 4 ? " " + string.Format(T.ItemsMore, h.Missing.Length - 4) : "");
            t.Inlines.Add(new Run(string.Format(T.ItemsMissing, miss)) { Foreground = Amber });
        }
        else { t.Inlines.Add(new Run(" · ")); t.Inlines.Add(new Run(T.ItemsAllMats) { Foreground = Green }); }
        var dock = new DockPanel();
        DockPanel.SetDock(link, Dock.Right);
        dock.Children.Add(link);
        dock.Children.Add(t);
        return new Border { Background = HintBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5), Margin = new Thickness(0, 6, 0, 0), Child = dock };
    }

    // ------------------------------------------------------------------ footer + legend

    private static readonly int[] FootCurrencies = [InventoryStore.Kinah, InventoryStore.KinahBound, InventoryStore.AbyssPoints, InventoryStore.EnhanceStone];
    // Character · Kina · Kina (Bound) · AP · Enhance · Odyle · Shugo · Pass · Event · Seal · Value · Seen: readable at the overlay's 900–1100 px
    private static readonly double[] FootWidths = [1.5, 1.25, 1.15, 1, 0.9, 0.95, 0.65, 0.6, 0.6, 0.8, 0.9, 0.8];
    private const int FootCols = 12, ColBound = 2, ColEvent = 8;

    /// <summary>One row per character: currencies (bag stacks) and its content tickets (A2M-15, character.json), then the account warehouse.
    /// Below 820 px Kina (Bound) and Event pass drop out; raids and corridors are the name's tooltip.</summary>
    private void RenderFoot(Data d)
    {
        var g = _footGrid;
        g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
        var narrow = _narrow;
        for (var i = 0; i < FootCols; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition(narrow && i is ColBound or ColEvent ? new GridLength(0) : new GridLength(FootWidths[i], GridUnitType.Star)));
        var row = 0;
        void Add(int col, string text, IBrush fg, bool head = false, bool left = false, string? tip = null)
        {
            if (narrow && col is ColBound or ColEvent) return;
            var t = new TextBlock { Text = text, Foreground = fg, FontSize = head ? 11 : 12.5, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 3), VerticalAlignment = VerticalAlignment.Center };
            if (!string.IsNullOrEmpty(tip)) UiKit.Tip(t, tip);
            g.Place(t, row, col);
        }
        void Rule() => g.Place(new Border { BorderBrush = FootLine, BorderThickness = new Thickness(0, 0.8, 0, 0) }, row, 0, FootCols);
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Add(0, T.ItemsFootChar, Mute, true, true);
        string Cur(int i) => T.Code == "es" ? T.FootCurrencies[i] : M.Names.Name(FootCurrencies[i], _lang) ?? T.FootCurrencies[i];
        Add(1, Cur(0), Mute, true); Add(2, Cur(1), Mute, true);
        Add(3, T.ItemsFootAp, Mute, true, tip: Cur(2)); Add(4, T.ItemsFootEnhance, Mute, true, tip: Cur(3));
        Add(5, T.ItemsTkOdyle, Mute, true, tip: T.ItemsTkOdyleTip); Add(6, T.ItemsTkKeys, Mute, true, tip: T.ItemsTkKeysTip);
        Add(7, T.ItemsTkPass, Mute, true, tip: T.ItemsTkPassTip); Add(8, T.ItemsTkEvent, Mute, true, tip: T.ItemsTkEventTip);
        Add(9, T.ItemsTkSeal, Mute, true, tip: T.ItemsTkSealTip);
        Add(10, T.ItemsFootValueShort, Mute, true, tip: T.ItemsFootValue); Add(11, T.ItemsFootSeen, Mute, true, tip: T.ItemsTkSeenTip);
        long Value(List<ItemStack> l) { long v = 0; foreach (var s in l) if (!InventoryStore.IsCurrency(s.Item)) v += (Price(s.Item) ?? 0) * s.Count; return v; }
        var book = d.Catalog;
        foreach (var c in d.Chars)
        {
            row++; g.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); Rule();
            var me = c.Key == d.CurrentKey;
            var tk = TicketsOverview.Of(c.Progress, book, _lang);
            var nameBlock = Line1(me ? Text : Dim);
            nameBlock.Margin = new Thickness(6, 3);
            nameBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            nameBlock.Inlines!.Add(new Run(NameOf(c.Key)));
            if (c.Cls.Length > 0) nameBlock.Inlines.Add(new Run(" " + c.Cls) { Foreground = Mute, FontSize = 11 });
            var tip = tk.RaidsTip.Length > 0 ? tk.RaidsTip : "";
            if (tk.CorridorsTip.Length > 0) tip = tip.Length > 0 ? tip + "\n" + tk.CorridorsTip : tk.CorridorsTip;
            if (tip.Length > 0) UiKit.Tip(nameBlock, tip);
            g.Place(nameBlock, row, 0);

            var bag = c.File?.Items(Containers.Bag);
            for (var i = 0; i < FootCurrencies.Length; i++)
            {
                long n = 0;
                if (bag is not null) foreach (var s in bag) if (s.Item == FootCurrencies[i]) n += s.Count;
                Add(1 + i, n > 0 ? N(n) : "—", n > 0 ? Num : Dim);
            }
            IBrush V(string s) => s == TicketsOverview.None ? Dim : Num;
            Add(5, tk.Odyle, V(tk.Odyle)); Add(6, tk.Keys, V(tk.Keys));
            Add(7, tk.DaevaPass, V(tk.DaevaPass), tip: tk.PassTip); Add(8, tk.EventPass, V(tk.EventPass), tip: tk.PassTip);
            Add(9, tk.Seal, V(tk.Seal));
            if (c.File is not { } f) { Add(10, "—", Dim); Add(11, tk.SeenMs > 0 ? When(tk.SeenMs) : T.ItemsFootNever, Dim); continue; }
            var v = Value(bag!) + Value(f.Items(Containers.CharacterWarehouse));
            Add(10, v > 0 ? ItemKit.Short(v) : "—", me ? Text : Dim);
            Add(11, SeenOf(f, tk.SeenMs), me ? Text : Dim);
        }
        if (d.Account is { } a)
        {
            row++; g.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); Rule();
            Add(0, T.ItemsAcctWh, Dim, left: true);
            long kina = 0;
            foreach (var s in a.Slots) if (s.Item == InventoryStore.Kinah) kina += s.Count;
            Add(1, kina > 0 ? N(kina) : "—", kina > 0 ? Num : Dim);
            for (var i = 2; i < 10; i++) Add(i, "—", Dim);
            var v = Value(AccountStacks(a));
            Add(10, v > 0 ? ItemKit.Short(v) : "—", Dim);
            var (_, at) = a.LastUpdate();
            Add(11, at > 0 ? When(at) : T.ItemsFootNever, Dim);
        }
        _footCaption.Text = string.Format(T.ItemsCaption, MarketLabel(d.Market));
    }

    private void BuildLegend()
    {
        _legend.Children.Clear();
        foreach (var g in new[] { Grade.Common, Grade.Rare, Grade.Legendary, Grade.Unique, Grade.Epic, Grade.Special, Grade.Unknown })
            _legend.Children.Add(LegendItem(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1.5), BorderBrush = Visuals.GradeLine(g), Margin = new Thickness(0, 0, 5, 0) },
                g == Grade.Unknown ? T.ItemsLegendUnknown : ItemGrades.Word(g, T.Code)));
        _legend.Children.Add(LegendItem(new TextBlock { Text = "+10", Foreground = Num, FontWeight = FontWeight.Bold, FontSize = 11, Margin = new Thickness(0, 0, 5, 0) }, T.ItemsLegendEnchant));
        _legend.Children.Add(LegendItem(new TextBlock { Text = "●", Foreground = Blue, FontSize = 11, Margin = new Thickness(0, 0, 5, 0) }, T.ItemsLegendDot));
    }

    private static Control LegendItem(Control mark, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        mark.VerticalAlignment = VerticalAlignment.Center;
        p.Children.Add(mark);
        p.Children.Add(new TextBlock { Text = text, Foreground = Mute, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    /// <summary>The mock's .pill radio row (All · Equipment · Materials · Consumables).</summary>
    private void BuildPills()
    {
        _showPills.Children.Clear();
        _showPills.Children.Add(new TextBlock { Text = T.ItemsShow, FontSize = 12, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var (tag, label) in new[] { ("all", T.ItemsAll), ("eq", T.ItemsEquipment), ("mat", T.ItemsMaterials), ("use", T.ItemsConsumables) })
        {
            var on = tag == _group;
            var text = new TextBlock { Text = label, FontSize = 12, Foreground = on ? Text : Dim };
            var b = new Border { Background = on ? ChipBack : Transparent, BorderBrush = on ? Visuals.Solid(0xFF555555) : Line2, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 2), Margin = new Thickness(0, 0, 6, 0), Cursor = Hand, Child = text };
            b.PointerEntered += (_, _) => text.Foreground = Text;
            b.PointerExited += (_, _) => text.Foreground = tag == _group ? Text : Dim;
            b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton != MouseButton.Left) return; _group = tag; BuildPills(); Render(); };
            _showPills.Children.Add(b);
        }
    }

    // ------------------------------------------------------------------ layout + controls

    /// <summary>Below 820 px the Across-characters panel moves under the cards (the card can be narrow when docked).</summary>
    private void ApplyWidth(double width)
    {
        var narrow = width is > 0 and < 820;
        if (narrow == _narrow && _laidOut) return;
        _narrow = narrow;
        _laidOut = true;
        _cols.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 330);
        _cols.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 10);
        _cols.RowDefinitions[1].Height = narrow ? new GridLength(0.8, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(_side, narrow ? 0 : 2);
        Grid.SetRow(_side, narrow ? 1 : 0);
        _side.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
        if (_data is { } d && Shell.Engine is not null) RenderFoot(d);
    }

    private void MainResized(double width)
    {
        // 12 px card padding each side, 2 px of frame, ~18 px for the scroll bar
        var cols = Math.Max(1, (int)((width - 24 - 2 - 18 + TileGap) / (Tile + TileGap)));
        if (cols == _columns) return;
        _columns = cols;
        if (_shown) Render();
    }

    private void LangChanged()
    {
        var lang = _langBox.SelectedIndex == 1 ? "es" : "en";
        if (lang == _lang) return;
        _lang = lang;
        Shell.RefreshSettings();
        if (Shell.Settings.ResolvedNamesLanguage != lang) { Shell.Settings.PickNames(lang); Shell.SaveSettings(); }
        BuildLegend();
        Render();
    }
}
