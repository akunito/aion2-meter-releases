using System.Globalization;
using AionMeter.Core.Character;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Core.Market;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Items tab (WPF ItemsView, A2M-29/32/15/23) on the layer-shell card (A2M-50, port of the Avalonia ItemsView): the viewed character's bag
/// and warehouse, the account's shared warehouse, the equipped set, Today (tickets) and Achievements cards, "Across characters" and the
/// currency footer with Kina. Everything comes from the files the daemon writes (characters/*/inventory.json + character.json,
/// account-warehouse.json), re-read when <see cref="DataWatch"/> sees a new mtime. Widgets live as long as the view and are updated in place:
/// one card object per card key with a pool of cairo <see cref="ItemTile"/>s and list rows, so a search keystroke only re-sets tiles.
/// GTK has no star columns nor wrap panel: the view reads its width from a probe and sizes columns, tile grids and wrap lines itself.
/// </summary>
internal sealed class ItemsView : HostedView
{
    public sealed record CharData(string Key, string Cls, InventoryFile? File, CharacterProgress? Progress);
    private sealed record Data(string CurrentKey, string Server, string ServerName, List<CharData> Chars, AccountWarehouseView? Account, MarketSnapshot Market, DateTimeOffset Now,
        CharacterProgress? Progress, AchievementCatalog Catalog);

    private static UiText T => UiText.Current;
    private static readonly string[] CardKeys = ["today", "ach", "bag", "wh", "acct", "eq", "x17"];

    private readonly DataWatch _watch;
    private Data? _data;
    private string _group = "all", _q = "", _lang;
    private ItemSortMode _sort = ItemSortMode.Game;
    private readonly HashSet<string> _collapsed = ["eq", "x17", "ach"], _listMode = [];
    private readonly CharacterFiles _charFiles = new();
    private readonly Dictionary<int, (string En, string Es)> _norm = new();
    private ItemQuery _query = ItemQuery.Empty;
    private readonly Dictionary<string, (DateTime Written, string Cls)> _cls = new();
    private ItemsIndex _index = new();
    private (DateTime Stamp, AccountWarehouse? Store) _account;
    private readonly UiTimer _searchTick = new(TimeSpan.FromMilliseconds(150));
    private readonly UiTimer _iconTick = new(TimeSpan.FromMilliseconds(600));
    private int _gen;
    private bool _syncing;
    private readonly Func<int, string> _nmF;
    private readonly Func<int, Grade> _gradeF;
    private readonly Func<int, long?> _priceF;
    private readonly Func<int, bool> _matchesF;

    // layout state (from the probe's width, scaled px)
    private int _width, _cardInner, _sideInner, _footWidth, _cols = 8;
    private bool _narrow = true, _toolsOneLine;
    private readonly int[] _listW = new int[ItemsLogic.ListSpec.Length];

    private readonly Gtk.DropDown _langBox, _sortBox;
    private readonly Gtk.Entry _search;
    private readonly Gtk.Label _status, _sideCount, _sideTitle, _sideHint, _caption, _noChar;
    private readonly Gtk.Box _page, _content, _cardsBox, _row, _toolsLine1, _toolsLine2, _sortPanel, _showPills, _sideRoot, _sideList, _foot, _noCharCard;
    private readonly Gtk.Label _sortLabel;
    private readonly Gtk.ScrolledWindow _sideHost;
    private readonly FlowLines _legend = new(0, 2);
    private readonly Dictionary<string, Card> _cards = new();
    private readonly Dictionary<int, MatchCard> _matches = new();
    private readonly List<Gtk.Button> _pillButtons = new();
    private Gtk.Widget? _tip;
    private (int Item, long Count, int Gen) _tipFor;
    private ItemTile? _tipTile;

    public ItemsView(AppShell shell) : base(shell, "items", UiText.Current.ItemsTitle)
    {
        ItemsKit.EnsureCss(Shell.Host.Scale);
        _watch = DataWatch.Of(shell);
        _lang = shell.Settings.PriceSearchLanguage == "es" ? "es" : "en";
        _nmF = Nm;
        _gradeF = id => M.Grades.Of(id);
        _priceF = Price;
        _matchesF = Matches;
        var (picker, unsubscribe) = _watch.Picker();
        HeaderRight.Append(picker);
        OnDetach(unsubscribe);

        _page = UiKit.VBox();
        _page.AddCssClass("k-items-page");
        // the width every column, tile grid and wrap line is laid out for (the scroll body hides its content's width from the card)
        var probe = Gtk.DrawingArea.New();
        probe.SetContentHeight(0);
        probe.Hexpand = true;
        probe.OnResize += (_, e) => { var w = e.Width; if (w != _width) UiLoop.Post(() => OnWidth(w)); };
        _page.Append(probe);

        // search · item language · status
        var top = UiKit.HBox();
        top.AddCssClass("k-items-top");
        var (searchHost, search) = ItemKit.SearchBox(T.ItemsSearch, 220, () => _searchTick.Restart());
        _search = search;
        top.Append(searchHost);
        _langBox = UiKit.Combo(["Items: English", "Items: Español"], _lang == "es" ? 1 : 0, 130);
        _langBox.MarginEnd = UiKit.Px(10);
        UiKit.Tip(_langBox, T.ItemsLangTip);
        UiKit.OnSelected(_langBox, () => { if (!_syncing) LangChanged(); });
        top.Append(_langBox);
        _status = UiKit.Line("", 12, Fg.Mute);
        _status.Hexpand = true;
        top.Append(_status);
        _page.Append(top);

        // Show pills · Sort: one line when both fit, else Sort on a second line (Avalonia overlapped them at 560 px)
        _showPills = UiKit.HBox();
        _showPills.Valign = Gtk.Align.Center;
        BuildPills();
        _sortPanel = UiKit.HBox();
        _sortPanel.Valign = Gtk.Align.Center;
        _sortLabel = ItemsKit.Label(T.ItemsSort, 'd', 12, ellipsize: false);
        _sortLabel.MarginEnd = UiKit.Px(6);
        _sortPanel.Append(_sortLabel);
        _sortBox = UiKit.Combo(SortLabels(), 0, 130);
        UiKit.OnSelected(_sortBox, () => { if (_syncing) return; _sort = (ItemSortMode)Math.Max(0, UiKit.Selected(_sortBox)); RenderItems(); });
        _sortPanel.Append(_sortBox);
        _toolsLine1 = UiKit.HBox();
        _toolsLine1.Append(_showPills);
        _toolsLine1.Append(UiKit.Spacer());
        _toolsLine2 = UiKit.HBox();
        _toolsLine2.MarginTop = UiKit.Px(6);
        _toolsLine2.Append(_sortPanel);
        var tools = UiKit.VBox();
        tools.AddCssClass("k-items-tools");
        tools.Append(_toolsLine1);
        tools.Append(_toolsLine2);

        // cards of the character | Across characters (under the cards below 820 px, as Avalonia)
        _cardsBox = UiKit.VBox();
        _noChar = ItemsKit.Wrapped(T.ItemsNoChar, 'm', 12);
        _noChar.AddCssClass("k-items-hinttext");
        _noCharCard = UiKit.VBox();
        _noCharCard.AddCssClass("k-items-card");
        _noCharCard.Append(_noChar);
        _noCharCard.Visible = false;
        foreach (var k in CardKeys)
        {
            var c = new Card(this, k, k is not ("today" or "ach"));
            _cards[k] = c;
            c.Root.Visible = false;
            _cardsBox.Append(c.Root);
            if (k == "ach") _cardsBox.Append(_noCharCard);
        }

        _sideTitle = ItemsKit.Label(T.ItemsAcross, 't', 14, 500);
        _sideTitle.Hexpand = true;
        _sideCount = ItemsKit.Label("", 'm', 12, ellipsize: false);
        var band = UiKit.HBox();
        band.AddCssClass("k-items-band");
        band.Append(_sideTitle);
        band.Append(_sideCount);
        _sideHint = ItemsKit.Wrapped("", 'm', 12);
        _sideHint.AddCssClass("k-items-hinttext");
        _sideList = UiKit.VBox();
        _sideRoot = UiKit.VBox();
        _sideRoot.AddCssClass("k-items-card");
        _sideRoot.Append(band);
        _sideRoot.Append(_sideList);

        _content = UiKit.VBox();
        // the tools card scrolls with the cards (Avalonia kept it fixed): at the card's 540 px the fixed rows left ~150 px of list
        _content.Append(tools);
        _content.Append(_cardsBox);
        _content.Append(_sideRoot);
        var legend = _legend.Widget;
        UiKit.Margin(legend, 12, 2, 0, 6);
        _content.Append(legend);
        _caption = ItemsKit.Wrapped("", 'm', 11);
        UiKit.Margin(_caption, 0, 0, 0, 10);
        _content.Append(_caption);
        BuildLegend();

        var mainScroll = UiKit.Scroll(_content);
        // External: the content's width (tile grids, column sizes) never reaches the card; the view sizes it from the probe
        mainScroll.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Automatic);
        _sideHost = Gtk.ScrolledWindow.New();
        _sideHost.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _sideHost.SetSizeRequest(UiKit.Px(330), -1);
        _sideHost.Hexpand = false;   // its wrapped labels propagate hexpand: it took half the row
        _sideHost.MarginEnd = UiKit.Px(14);
        _sideHost.Visible = false;
        _row = UiKit.HBox();
        _row.Vexpand = true;
        _row.Append(mainScroll);
        _row.Append(_sideHost);
        _page.Append(_row);
        SetBody(_page);

        _foot = UiKit.VBox();
        SetFooter(ItemsKit.Clip(_foot));

        _searchTick.Tick += () => { _searchTick.Stop(); _q = ItemNames.Normalize(_search.GetText()); _query = ItemQuery.Parse(_q); RenderItems(); };
        _iconTick.Tick += () => { _iconTick.Stop(); RetryIcons(); };
        Listen(() => Shell.Icons.Loaded += OnIcons, () => Shell.Icons.Loaded -= OnIcons);
        Listen(() => _watch.Changed += OnData, () => _watch.Changed -= OnData);
        Listen(() => _watch.SelectionChanged += OnChanged, () => _watch.SelectionChanged -= OnChanged);
        Shell.WhenEngine(e =>
        {
            Listen(() => e.Names.Changed += OnNames, () => e.Names.Changed -= OnNames);
            if (IsShown) Reload();   // shown before the engine loaded (~0.3 s after start)
        });
        OnDetach(_searchTick.Stop);
        OnDetach(_iconTick.Stop);
        ApplyWidth(UiKit.Px(TabState.MinTabWidth - 44));
    }

    private void OnIcons() { if (IsShown && !_iconTick.IsEnabled) _iconTick.Start(); }
    private void OnData(DataChange c) { if ((c & (DataChange.Characters | DataChange.Market)) != 0) OnChanged(); }
    private void OnNames() => UiLoop.Post(() => { _norm.Clear(); _matches.Clear(); OnChanged(); });

    public override void Shown()
    {
        _watch.Acquire();
        Reload();
    }

    public override void Hidden()
    {
        _watch.Release();
        _iconTick.Stop();
        _searchTick.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.ItemsTitle;
        _search.PlaceholderText = T.ItemsSearch;
        UiKit.Tip(_langBox, T.ItemsLangTip);
        _sortLabel.SetText(T.ItemsSort);
        _syncing = true;
        try { UiKit.SetItems(_sortBox, SortLabels(), (int)_sort); }
        finally { _syncing = false; }
        _sideTitle.SetText(T.ItemsAcross);
        _noChar.SetText(T.ItemsNoChar);
        BuildPills();
        BuildLegend();
        _matches.Clear();
        LayoutTools();
        if (IsShown) Render();
    }

    private static string[] SortLabels() => [T.ItemsSortGame, T.ItemsSortGrade, T.ItemsSortName, T.ItemsSortValue];

    // hidden tab: nothing now, Shown reloads
    private void OnChanged() { if (IsShown) Reload(); }

    private void Reload()
    {
        if (Shell.Engine is not { } e) { _status.SetText("…"); return; }
        _data = Gather(e);
        _matches.Clear();
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

    /// <summary>profile.txt first line → "Ranger 45"; re-read only when the file changes.</summary>
    private string ClassLine(string key)
    {
        var path = Path.Combine(AppSettings.AppDataDir, "characters", key, "profile.txt");
        try
        {
            if (!File.Exists(path)) return "";
            var w = File.GetLastWriteTimeUtc(path);
            if (_cls.TryGetValue(key, out var c) && c.Written == w) return c.Cls;
            using var r = new StreamReader(path);
            var cls = ItemsLogic.ClassOf(r.ReadLine() ?? "");
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
    private static string Esc(string s) => GLib.Functions.MarkupEscapeText(s, -1);
    private static string Hex(uint rgb) => Palette.Hex(rgb);

    private static string PlaceLabel(Holding h) => h.Place switch
    {
        Place.AccountWarehouse => T.ItemsPlaceAcct,
        Place.Bag => NameOf(h.Who) + " " + T.ItemsPlaceBag,
        Place.CharacterWarehouse => NameOf(h.Who) + " " + T.ItemsPlaceWh,
        _ => NameOf(h.Who) + " " + T.ItemsPlaceWorn,
    };

    // ------------------------------------------------------------------ render

    /// <summary>Everything: cards, Across characters, footer.</summary>
    private void Render()
    {
        if (_data is not { } d || Shell.Engine is null) return;
        RenderItems();
        RenderFoot(d);
    }

    /// <summary>What a search, sort, Show pill or a fold changes: the cards and Across characters (the footer does not move).</summary>
    private void RenderItems()
    {
        if (_data is not { } d || Shell.Engine is null) return;
        _gen++;
        _index = new ItemsIndex();
        foreach (var c in d.Chars)
        {
            if (c.File is not { } f) continue;
            _index.Add(c.Key, Place.Bag, f.Items(Containers.Bag));
            _index.Add(c.Key, Place.CharacterWarehouse, f.Items(Containers.CharacterWarehouse));
            var eqAll = f.Items(Containers.Equipped); eqAll.AddRange(f.Items(Containers.Cosmetic));
            _index.Add(c.Key, Place.Equipped, eqAll);
        }
        var acct = AccountStacks(d.Account);
        _index.Add("", Place.AccountWarehouse, acct);

        CharData? cur = null;
        foreach (var c in d.Chars) if (c.Key == d.CurrentKey) cur = c;
        var stacksSeen = 0;
        var progress = d.Progress is { } prog && _q.Length == 0 ? prog : null;
        _cards["today"].Root.Visible = progress is not null;
        _cards["ach"].Root.Visible = progress is not null;
        if (progress is not null) { FillToday(_cards["today"], progress, d.Now); FillAchievements(_cards["ach"], progress, d.Catalog); }

        var file = cur?.File;
        _noCharCard.Visible = file is null;
        _cards["bag"].Root.Visible = _cards["wh"].Root.Visible = _cards["eq"].Root.Visible = file is not null;
        _cards["x17"].Root.Visible = false;
        if (file is not null)
        {
            var who = cur!.Key;
            var bag = file.Items(Containers.Bag);
            stacksSeen = file.Stacks.Count;
            var bagSeen = file.SeenOf(Containers.Bag);
            var incomplete = bagSeen.FullMs == 0 && ItemKinds.BagLooksIncomplete(bag);
            var maxSlot = 0;
            foreach (var s in bag) if (s.Slot > maxSlot) maxSlot = s.Slot;
            FillCard(_cards["bag"], T.ItemsBag, NameOf(who),
                incomplete ? T.ItemsChipPartial : T.ItemsChipLogin + (bagSeen.FullMs > 0 ? " · " + When(bagSeen.FullMs) : ""), incomplete ? 'a' : 'g',
                Stacks(bag.Count), bag, incomplete ? string.Format(T.ItemsBagIncomplete, bag.Count, maxSlot + 1 - bag.Count, maxSlot) : null, who, Place.Bag, null);

            var wh = file.Items(Containers.CharacterWarehouse);
            var whSeen = file.SeenOf(Containers.CharacterWarehouse);
            var whAt = whSeen.SeenMs > 0 ? When(whSeen.SeenMs) : When(Local(file.WrittenUtc));
            var (whChip, whFg, whWarn) = ItemsLogic.Warehouse(wh.Count, whSeen.SeenMs, whSeen.FullMs) switch
            {
                ItemsLogic.WhState.Never => (T.ItemsChipNever, 'm', T.ItemsWhNever),
                ItemsLogic.WhState.Full => (string.Format(T.ItemsChipLastSeen, whAt), 'g', (string?)null),
                _ => (string.Format(T.ItemsChipPartialAt, whAt), 'a', string.Format(T.ItemsWhPartial, wh.Count)),
            };
            FillCard(_cards["wh"], T.ItemsCharWh, NameOf(who), whChip, whFg, Stacks(wh.Count), wh, whWarn, who, Place.CharacterWarehouse, null);

            var eq = file.Items(Containers.Equipped); eq.AddRange(file.Items(Containers.Cosmetic));
            FillCard(_cards["eq"], T.ItemsEquipped, NameOf(who), null, 'm', Stacks(eq.Count), eq, null, who, Place.Equipped, null);
            var x17 = file.Items(Containers.Unknown17);
            if (x17.Count > 0)
            {
                _cards["x17"].Root.Visible = true;
                FillCard(_cards["x17"], T.ItemsUnknown17, NameOf(who), null, 'm', Stacks(x17.Count), x17, null, who, Place.Equipped, null);
            }
        }

        _cards["acct"].Root.Visible = d.Account is not null;
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
            FillCard(_cards["acct"], T.ItemsAcctWh, string.Format(T.ItemsShared, d.ServerName), at > 0 ? string.Format(T.ItemsChipUpdatedBy, NameOf(by), When(at)) : T.ItemsChipNever, at > 0 ? 'g' : 'm',
                Stacks(a.Slots.Count), acct, warn, "", Place.AccountWarehouse, dots);
        }

        RenderSide(d);
        _status.SetText(cur is null ? "" : NameOf(cur.Key) + (cur.Cls.Length > 0 ? " · " + cur.Cls : "") + " · " + Stacks(stacksSeen));
    }

    private static List<ItemStack> AccountStacks(AccountWarehouseView? a)
    {
        var l = new List<ItemStack>(a?.Slots.Count ?? 0);
        if (a is null) return l;
        foreach (var s in a.Slots) l.Add(new ItemStack(0, s.Item, s.Count, Containers.AccountWarehouse, s.Slot, s.Enchant));
        return l;
    }

    private void FillCard(Card c, string title, string sub, string? chip, char chipFg, string right, List<ItemStack> stacks, string? warn, string who, Place place, HashSet<byte>? dots)
    {
        var visible = c.Visible;
        var kina = ItemsLogic.Visible(stacks, _group, visible);
        right = ItemsLogic.HeadRight(right, kina, T.ItemsKinaHere);
        ItemKinds.Sort(visible, _sort, _nmF, _gradeF, _priceF);
        long value = 0;
        foreach (var s in visible) value += (Price(s.Item) ?? 0) * s.Count;
        var shut = _collapsed.Contains(c.Key);
        var list = _listMode.Contains(c.Key);
        c.SetHead(title, sub, chip, chipFg, right, value > 0 ? string.Format(T.ItemsValue, ItemKit.Short(value)) : null,
            value > 0 ? string.Format(T.ItemsValueTip, MarketLabel(_data!.Market)) : null, shut, list);
        c.Body.Visible = !shut;
        if (shut) return;
        c.Rows.Visible = false;
        c.Warn.Visible = warn is not null;
        if (warn is not null) c.WarnText.SetText(warn);
        c.Hint.Visible = visible.Count == 0;
        if (visible.Count == 0) c.Hint.SetText(T.ItemsNothing);
        if (list)
        {
            c.Tiles.Visible = false;
            c.ShowList(visible, who, place, dots);
        }
        else
        {
            c.List.Visible = false;
            c.ShowTiles(visible, dots);
        }
    }

    // ------------------------------------------------------------------ A2M-15: Today (content tickets) and Achievements cards

    private void FillToday(Card c, CharacterProgress p, DateTimeOffset now)
    {
        var shut = _collapsed.Contains(c.Key);
        var daily = GameReset.NextDaily(now); var weekly = GameReset.NextWeekly(now);
        p.Tickets.TryGetValue(60_000_001, out var odyle); p.Tickets.TryGetValue(1, out var keys);
        var right = (odyle is not null ? TicketBook.Of(odyle.Key).Name(_lang) + " " + TicketBook.Value(odyle, TicketBook.Of(odyle.Key)) + " · " : "")
                  + (keys is not null ? TicketBook.Of(keys.Key).Name(_lang) + " " + keys.Total + " · " : "") + string.Format(T.ItemsResetIn, ItemsLogic.Span(daily - now));
        c.SetHead(T.ItemsToday, T.ItemsTodaySub, null, 'm', right, null, null, shut, false);
        c.Body.Visible = !shut;
        if (shut) return;
        c.ClearRows();
        c.Warn.Visible = c.Tiles.Visible = c.List.Visible = false;
        c.Hint.Visible = true;
        c.Hint.SetText(string.Format(T.ItemsTodayReset, daily.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), ItemsLogic.Span(daily - now),
            weekly.ToLocalTime().ToString("ddd dd HH:mm", T.Culture)));
        if (p.Tickets.Count == 0) { c.AddRow(HintLabel(T.ItemsTodayNone)); return; }
        Span<int> w = stackalloc int[3];
        ItemsLogic.ColumnWidths(ItemsLogic.TicketSpec, _cardInner, Icons.Scale, w);
        foreach (var (t, info) in TicketBook.Today(p.Tickets)) c.AddRow(TicketRow(t, info, p.Chests, w));
        // Daeva Pass / event pass levels: the 810 rows count pass EXP and their claimed step is the reward level (A2M-15)
        foreach (var (id, label) in new[] { (810_433_000, T.ItemsDaevaPass), (810_435_000, T.ItemsEventPass) })
        {
            if (!p.Achievements.TryGetValue(id, out var r) || r.Count == 0) continue;
            var tiers = M.AchievementBook.Get(id)?.Tiers.Length ?? 0;
            c.AddRow(HintLabel(string.Format(T.ItemsPassLine, label, r.Step, tiers > 0 ? tiers.ToString(CultureInfo.InvariantCulture) : "?", N(r.Count))));
        }
    }

    private static Gtk.Label HintLabel(string text)
    {
        var l = ItemsKit.Wrapped(text, 'm', 12);
        l.AddCssClass("k-items-hinttext");
        return l;
    }

    /// <summary>A row of cells sized by the column widths (Cols3 / ListGrid), bottom hairline.</summary>
    private static Gtk.Box CellRow(double minHeight)
    {
        var b = UiKit.HBox();
        b.AddCssClass("k-items-row");
        b.SetSizeRequest(-1, UiKit.Px(minHeight));
        return b;
    }

    private static Gtk.Label Cell(Gtk.Box row, int width, string text, char fg, double size = 12.5, bool right = false, int weight = 400)
    {
        var l = ItemsKit.Label(text, fg, size, weight);
        l.Xalign = right ? 1 : 0;
        l.MarginStart = l.MarginEnd = UiKit.Px(4);
        l.SetSizeRequest(Math.Max(0, width - UiKit.Px(8)), -1);
        l.Visible = width > 0;
        row.Append(l);
        return l;
    }

    private Gtk.Widget TicketRow(ContentTicket t, TicketInfo info, IReadOnlyDictionary<int, ExpeditionChest>? chests, ReadOnlySpan<int> w)
    {
        var g = CellRow(26);
        var name = Cell(g, w[0], info.Stars > 0 ? info.Name(_lang) + " " + new string('★', info.Stars) : info.Name(_lang), info.Inferred ? 'd' : 't');
        if (info.Inferred) name.SetTooltipText(T.ItemsTodayInferred + " (" + t.Key.ToString(CultureInfo.InvariantCulture) + ")");
        if (info.Chest > 0)
        {
            // A2M-41: the 6000N201 ticket value (7/14) is not the chest bar and is never shown; the bar comes from 12 57
            var p = TicketBook.ChestProgress(info, chests);
            Cell(g, w[1], p >= info.Chest ? T.ItemsTodayClaimable : "", 'g', 11.5, true);
            var chest = Cell(g, w[2], string.Format(T.ItemsTodayChest, p?.ToString(CultureInfo.InvariantCulture) ?? "?", info.Chest.ToString(CultureInfo.InvariantCulture)),
                p is null ? 'f' : p >= info.Chest ? 'g' : p > 0 ? 'n' : 'd', 12.5, true);
            chest.SetTooltipText(p is null ? T.ItemsTodayChestUnknown : T.ItemsTodayChestTip);
            return g;
        }
        Cell(g, w[1], "", 't');
        var value = t.IsTime ? string.Format(T.ItemsTodayLeft, ItemsLogic.Span(TimeSpan.FromMilliseconds(t.Time))) : TicketBook.Value(t, info);
        Cell(g, w[2], value, t.Total == 0 && !t.IsTime || t.IsTime && t.Time == 0 ? 'f' : 'n', 12.5, true);
        return g;
    }

    private void FillAchievements(Card c, CharacterProgress p, AchievementCatalog catalog)
    {
        var shut = _collapsed.Contains(c.Key);
        var sum = catalog.Summarise(p.Achievements.Values);
        var right = p.Achievements.Count == 0 || catalog.Count == 0 ? "" : string.Format(T.ItemsAchRight, N(sum.TiersClaimed), N(sum.TiersTotal), N(sum.Completed), N(sum.Claimable), N(sum.InProgress));
        c.SetHead(T.ItemsAch, "", sum.Claimable > 0 ? sum.Claimable.ToString(CultureInfo.InvariantCulture) : null, 'g', right, null, null, shut, false);
        c.Body.Visible = !shut;
        if (shut) return;
        c.ClearRows();
        c.Warn.Visible = c.Tiles.Visible = c.List.Visible = false;
        c.Hint.Visible = false;
        if (p.Achievements.Count == 0) c.AddRow(HintLabel(T.ItemsAchNone));
        else if (catalog.Count == 0) c.AddRow(HintLabel(T.ItemsAchNoCatalog));
        else if (sum.ClaimableList.Count == 0) c.AddRow(HintLabel(T.ItemsAchNothing));
        else
        {
            Span<int> w = stackalloc int[3];
            ItemsLogic.ColumnWidths(ItemsLogic.AchSpec, _cardInner, Icons.Scale, w);
            foreach (var a in sum.ClaimableList) c.AddRow(AchievementRow(a, w));
        }
    }

    private Gtk.Widget AchievementRow(AchievementProgress a, ReadOnlySpan<int> w)
    {
        var info = a.Info!;
        var g = CellRow(34);
        var name = Cell(g, w[0], info.Name(_lang), 't');
        var desc = info.Desc(_lang);
        if (desc.Length > 0) name.SetTooltipText(desc);
        var tier = string.Format(T.ItemsAchTier, a.NextTier, a.Tiers, N(a.Row.Count), N(a.Goal));
        if (a.ClaimableTiers > 1) tier += " · " + string.Format(T.ItemsAchMore, a.ClaimableTiers);
        Cell(g, w[1], tier, 'd', 11.5);
        var rewards = info.Tiers[a.NextTier - 1].Rewards;
        var parts = new string[rewards.Length];
        for (var i = 0; i < rewards.Length; i++) parts[i] = Lb(rewards[i].Item) + " ×" + N(rewards[i].Amount);
        var rw = Cell(g, w[2], string.Join(", ", parts), 'n', 11.5);
        rw.SetTooltipText(string.Join("\n", parts));
        return g;
    }

    /// <summary>Where else the item is ("Irin bag 4 · acct 191"), equipped pieces left out (mock "Also").</summary>
    private string Elsewhere(int item, string who, Place place) => ItemsLogic.Elsewhere(_index.Of(item), who, place, PlaceLabel);

    // ------------------------------------------------------------------ tooltip (mock .tip)

    private void AttachTip(ItemTile t)
    {
        t.Widget.HasTooltip = true;
        t.Widget.OnQueryTooltip += (_, e) =>
        {
            if (t.Item <= 0 || Shell.Engine is null) return false;
            e.Tooltip.SetCustom(TipFor(t.Item, t.Count));
            return true;
        };
    }

    /// <summary>The tooltip body, built once per item/stack/render (GTK asks again on every pointer motion).</summary>
    private Gtk.Widget TipFor(int item, long count)
    {
        if (_tip is not null && _tipFor == (item, count, _gen)) return _tip;
        _tipFor = (item, count, _gen);
        var root = UiKit.VBox();
        root.AddCssClass("k-root");
        root.SetSizeRequest(UiKit.Px(280), -1);
        root.Append(ItemHead(item));
        var kv = Gtk.Grid.New();
        kv.ColumnSpacing = UiKit.Px(10);
        kv.RowSpacing = UiKit.Px(2);
        kv.AddCssClass("k-items-kv");
        var r = 0;
        void Kv(string k, string v)
        {
            var kl = ItemsKit.Label(k, 'm', 12, ellipsize: false);
            kl.Valign = Gtk.Align.Start;
            kv.Attach(kl, 0, r, 1, 1);
            kv.Attach(TipWrap(ItemsKit.Wrapped(v, 't', 12), 24), 1, r, 1, 1);
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
        root.Append(kv);
        _tip = root;
        return root;
    }

    /// <summary>The tooltip popup takes the height its wrapped labels need at their minimum width: a 1-char minimum (the kit's, right in the
    /// card) made it ~3× too tall (measured), so tooltip labels get a real one.</summary>
    private static Gtk.Label TipWrap(Gtk.Label l, int chars)
    {
        l.WidthChars = chars;
        l.MaxWidthChars = chars;
        return l;
    }

    private Gtk.Widget ItemHead(int item)
    {
        var head = UiKit.HBox();
        _tipTile = new ItemTile(44);
        var grade = M.Grades.Of(item);
        _tipTile.Set(item, 1, 0, grade, Nm(item), Shell.Icons);
        head.Append(_tipTile.Widget);
        var names = UiKit.VBox();
        names.MarginStart = UiKit.Px(10);
        names.Valign = Gtk.Align.Center;
        names.Hexpand = true;
        var nameLine = NameLine(item, grade, 13.5);
        if (nameLine.GetFirstChild() is Gtk.Label nl) TipWrap(nl, 18);
        names.Append(nameLine);
        if (OtherName(item) is { Length: > 0 } other) names.Append(TipWrap(ItemsKit.Wrapped(other, 'm', 11), 34));
        // the grade is on the chip; the line keeps "grade unknown" when there is none
        var meta = (grade == Grade.Unknown ? T.ItemsGradeUnknown + " · " : "") + (M.Grades.Bound(item) ? T.ItemsBound + " · " : "") + "id " + item.ToString(CultureInfo.InvariantCulture);
        names.Append(ItemsKit.Label(meta, 'm', 11));
        head.Append(names);
        return head;
    }

    /// <summary>Name in the grade colour (wrapping) plus the grade chip (same-named items differ only by grade).</summary>
    private Gtk.Widget NameLine(int item, Grade grade, double size)
    {
        var row = UiKit.HBox();
        var l = Gtk.Label.New(Nm(item));
        l.Wrap = true;
        l.WrapMode = Pango.WrapMode.WordChar;
        l.MaxWidthChars = 1;
        l.Hexpand = true;
        l.Xalign = 0;
        l.AddCssClass(KitCss.SizeClass(size));
        l.AddCssClass("k-w6");
        ItemKit.GradeText(l, grade);
        row.Append(l);
        if (ItemKit.GradeChip(grade, _lang) is { } chip) { chip.Valign = Gtk.Align.Start; chip.MarginTop = UiKit.Px(2); row.Append(chip); }
        return row;
    }

    // ------------------------------------------------------------------ Across characters

    private void RenderSide(Data d)
    {
        while (_sideList.GetFirstChild() is { } ch) _sideList.Remove(ch);
        List<int> ids;
        var max = 25;
        if (_q.Length > 0)
        {
            ids = _index.Matching(_matchesF);
            _sideCount.SetText(string.Format(T.ItemsMatches, ids.Count));
            max = 40;
            if (ids.Count == 0) { _sideHint.SetText(string.Format(T.ItemsNoMatch, _search.GetText().Trim())); _sideList.Append(_sideHint); }
        }
        else
        {
            // with an empty box: the same item split over 3+ storage places, worth consolidating (Diego 2026-10-11)
            ids = _index.Split();
            _sideCount.SetText("");
            _sideHint.SetText(string.Format(T.ItemsAcrossHint, ids.Count));
            _sideList.Append(_sideHint);
        }
        for (var i = 0; i < ids.Count && i < max; i++)
        {
            if (!_matches.TryGetValue(ids[i], out var mc)) _matches[ids[i]] = mc = BuildMatch(ids[i], d);
            if (i == 0 && _q.Length > 0) mc.Root.RemoveCssClass("k-items-sep"); else mc.Root.AddCssClass("k-items-sep");
            _sideList.Append(mc.Root);
            mc.Locs.Pack(_sideInner);
        }
    }

    private sealed record MatchCard(Gtk.Box Root, FlowLines Locs, ItemTile Tile);

    private MatchCard BuildMatch(int item, Data d)
    {
        var root = UiKit.VBox();
        root.AddCssClass("k-items-match");
        var head = UiKit.HBox();
        var tile = new ItemTile(36);
        tile.Set(item, 1, 0, M.Grades.Of(item), Nm(item), Shell.Icons);
        AttachTip(tile);
        head.Append(tile.Widget);
        var grade = M.Grades.Of(item);
        var names = UiKit.VBox();
        names.MarginStart = UiKit.Px(9);
        names.Valign = Gtk.Align.Center;
        names.Hexpand = true;
        names.Append(NameLine(item, grade, 13));
        if (OtherName(item) is { Length: > 0 } other) names.Append(ItemsKit.Label(other, 'm', 11));
        head.Append(names);
        var total = _index.Total(item);
        var tot = UiKit.VBox();
        tot.Valign = Gtk.Align.Center;
        tot.MarginStart = UiKit.Px(8);
        var tl = ItemsKit.Label(N(total), 't', 15, 600, false);
        tl.Xalign = 1;
        tot.Append(tl);
        var sub = Price(item) is { } p ? string.Format(T.ItemsValue, ItemKit.Short(p * total)) : M.Grades.Bound(item) ? T.ItemsBound : "";
        if (sub.Length > 0) { var sl = ItemsKit.Label(sub, 'm', 11, ellipsize: false); sl.Xalign = 1; tot.Append(sl); }
        head.Append(tot);
        root.Append(head);

        var locs = new FlowLines(4, 4);
        locs.Widget.MarginTop = UiKit.Px(6);
        var holds = new List<Holding>(_index.Of(item));
        ItemsLogic.SortHoldings(holds, d.CurrentKey);
        foreach (var h in holds)
        {
            var pill = UiKit.HBox();
            pill.AddCssClass("k-items-loc");
            if (h.Who == d.CurrentKey) pill.AddCssClass("k-items-me");
            else if (h.Place == Place.AccountWarehouse) pill.AddCssClass("k-items-acct");
            var t = ItemsKit.Label("", 'd', 11.5, ellipsize: false);
            t.SetMarkup(Esc(PlaceLabel(h)) + " <span foreground=\"" + Hex(ItemsKit.Text) + "\" weight=\"600\">" + Esc(N(h.Count)) + "</span>");
            pill.Append(t);
            locs.Add(pill);
        }
        root.Append(locs.Widget);
        if (M.Names.Source(item) is { Length: > 0 } src)
        {
            var from = ItemsKit.Wrapped(string.Format(T.ItemsFrom, src), 'm', 11);
            from.MarginTop = UiKit.Px(2);
            root.Append(from);
        }
        foreach (var hint in CraftHints.For(item, M.Recipes, _index.Total)) root.Append(HintBox(hint));
        return new MatchCard(root, locs, tile);
    }

    private Gtk.Widget HintBox(CraftHint h)
    {
        var box = UiKit.HBox();
        box.AddCssClass("k-items-hint");
        var outName = Nm(h.Recipe.Output);
        var og = M.Grades.Of(h.Recipe.Output);
        // an ambiguous name carries its grade word so Craft's search keeps only this grade
        var craftQuery = M.Names.Ambiguous(h.Recipe.Output, _lang) && og != Grade.Unknown ? outName + " " + ItemGrades.Word(og, _lang) : outName;
        var sb = new System.Text.StringBuilder(256);
        sb.Append(Esc(T.ItemsUsedIn)).Append(' ')
          .Append("<span foreground=\"").Append(Hex(Palette.GradeText(og))).Append("\">").Append(Esc(outName)).Append("</span>");
        if (og != Grade.Unknown)
            sb.Append(" <span foreground=\"").Append(Hex(Palette.GradeText(og))).Append("\" background=\"").Append(Hex(Palette.GradeLine(og)))
              .Append("\" bgalpha=\"20%\" size=\"small\" weight=\"600\"> ").Append(Esc(ItemGrades.Word(og, _lang))).Append(" </span>");
        sb.Append("<span foreground=\"").Append(Hex(ItemsKit.Mute)).Append("\"> ").Append(Esc(h.Recipe.Profession + " " + h.Recipe.Level)).Append("</span>\n");
        sb.Append(Esc(string.Format(T.ItemsNeeds, N(h.Need), N(h.Have))));
        if (h.Have >= h.Need) sb.Append("<span foreground=\"").Append(Hex(ItemsKit.Green)).Append("\"> ✓</span>");
        if (h.Missing.Length > 0)
        {
            var n = Math.Min(4, h.Missing.Length);
            var parts = new string[n];
            for (var i = 0; i < n; i++) parts[i] = $"{Lb(h.Missing[i].Item)} {N(h.Missing[i].Have)}/{h.Missing[i].Need}";
            var miss = string.Join(" · ", parts) + (h.Missing.Length > 4 ? " " + string.Format(T.ItemsMore, h.Missing.Length - 4) : "");
            sb.Append("\n<span foreground=\"").Append(Hex(ItemsKit.Amber)).Append("\">").Append(Esc(string.Format(T.ItemsMissing, miss))).Append("</span>");
        }
        else sb.Append(" · <span foreground=\"").Append(Hex(ItemsKit.Green)).Append("\">").Append(Esc(T.ItemsAllMats)).Append("</span>");
        var t = ItemsKit.Wrapped("", 'd', 11.5);
        t.SetMarkup(sb.ToString());
        t.Valign = Gtk.Align.Start;
        box.Append(t);
        // Craft is another tab's port: the link appears once a "craft" view is registered (CraftSearchRequest carries the text)
        if (ViewRegistry.Has("craft"))
        {
            var link = Gtk.Button.New();
            link.AddCssClass("k-items-link");
            link.Child = Gtk.Label.New(T.ItemsOpenCraft);
            link.Valign = Gtk.Align.Start;
            link.FocusOnClick = false;
            link.SetCursorFromName("pointer");
            link.OnClicked += (_, _) => { CraftSearchRequest.Request(craftQuery); Shell.SelectTab("craft"); };
            box.Append(link);
        }
        return box;
    }

    // ------------------------------------------------------------------ footer (A2M-23: Kina per character) + legend

    private static readonly int[] FootCurrencies = [InventoryStore.Kinah, InventoryStore.KinahBound, InventoryStore.AbyssPoints, InventoryStore.EnhanceStone];

    /// <summary>One row per character: currencies (bag stacks) and its content tickets (A2M-15, character.json), then the account warehouse.
    /// Below 820 px Kina (Bound) and Event pass drop out; raids and corridors are the name's tooltip.</summary>
    private void RenderFoot(Data d)
    {
        while (_foot.GetFirstChild() is { } ch) _foot.Remove(ch);
        Span<double> spec = stackalloc double[ItemsLogic.FootCols];
        ItemsLogic.FootSpec(_narrow, spec);
        Span<int> w = stackalloc int[ItemsLogic.FootCols];
        ItemsLogic.ColumnWidths(spec, _footWidth, Icons.Scale, w);
        var widths = w.ToArray();
        var pad = UiKit.Px(_narrow ? 3 : 6);
        Gtk.Box row = null!;
        void NewRow(bool rule)
        {
            row = UiKit.HBox();
            if (rule) row.AddCssClass("k-items-frule");
            _foot.Append(row);
        }
        Gtk.Label Add(int col, string text, char fg, bool head = false, bool left = false, string? tip = null)
        {
            var l = ItemsKit.Label(text, fg, head ? 11 : 12.5);
            l.Xalign = left ? 0 : 1;
            // 3 px sides when narrow (WPF 6): at 560 px the Kina column is ~58 px and "2,087,175" needs ~55
            l.MarginStart = l.MarginEnd = pad;
            l.MarginTop = l.MarginBottom = UiKit.Px(3);
            l.SetSizeRequest(Math.Max(0, widths[col] - 2 * pad), -1);
            if (!string.IsNullOrEmpty(tip)) l.SetTooltipText(tip);
            if (widths[col] > 0) row.Append(l);
            return l;
        }
        NewRow(false);
        Add(0, T.ItemsFootChar, 'm', true, true);
        string Cur(int i) => T.Code == "es" ? T.FootCurrencies[i] : M.Names.Name(FootCurrencies[i], _lang) ?? T.FootCurrencies[i];
        Add(1, Cur(0), 'm', true); Add(2, Cur(1), 'm', true);
        Add(3, T.ItemsFootAp, 'm', true, tip: Cur(2)); Add(4, T.ItemsFootEnhance, 'm', true, tip: Cur(3));
        Add(5, T.ItemsTkOdyle, 'm', true, tip: T.ItemsTkOdyleTip); Add(6, T.ItemsTkKeys, 'm', true, tip: T.ItemsTkKeysTip);
        Add(7, T.ItemsTkPass, 'm', true, tip: T.ItemsTkPassTip); Add(8, T.ItemsTkEvent, 'm', true, tip: T.ItemsTkEventTip);
        Add(9, T.ItemsTkSeal, 'm', true, tip: T.ItemsTkSealTip);
        Add(10, T.ItemsFootValueShort, 'm', true, tip: T.ItemsFootValue); Add(11, T.ItemsFootSeen, 'm', true, tip: T.ItemsTkSeenTip);
        long Value(List<ItemStack> l) { long v = 0; foreach (var s in l) if (!InventoryStore.IsCurrency(s.Item)) v += (Price(s.Item) ?? 0) * s.Count; return v; }
        var book = d.Catalog;
        foreach (var c in d.Chars)
        {
            NewRow(true);
            var me = c.Key == d.CurrentKey;
            var tk = TicketsOverview.Of(c.Progress, book, _lang);
            var name = Add(0, "", me ? 't' : 'd', left: true);
            name.SetMarkup(Esc(NameOf(c.Key)) + (c.Cls.Length > 0 ? " <span foreground=\"" + Hex(ItemsKit.Mute) + "\" size=\"small\">" + Esc(c.Cls) + "</span>" : ""));
            var tip = tk.RaidsTip;
            if (tk.CorridorsTip.Length > 0) tip = tip.Length > 0 ? tip + "\n" + tk.CorridorsTip : tk.CorridorsTip;
            if (tip.Length > 0) name.SetTooltipText(tip);
            var bag = c.File?.Items(Containers.Bag);
            for (var i = 0; i < FootCurrencies.Length; i++)
            {
                long n = 0;
                if (bag is not null) foreach (var s in bag) if (s.Item == FootCurrencies[i]) n += s.Count;
                Add(1 + i, n > 0 ? N(n) : "—", n > 0 ? 'n' : 'd');
            }
            char V(string s) => s == TicketsOverview.None ? 'd' : 'n';
            Add(5, tk.Odyle, V(tk.Odyle)); Add(6, tk.Keys, V(tk.Keys));
            Add(7, tk.DaevaPass, V(tk.DaevaPass), tip: tk.PassTip); Add(8, tk.EventPass, V(tk.EventPass), tip: tk.PassTip);
            Add(9, tk.Seal, V(tk.Seal));
            if (c.File is not { } f) { Add(10, "—", 'd'); Add(11, tk.SeenMs > 0 ? When(tk.SeenMs) : T.ItemsFootNever, 'd'); continue; }
            var v = Value(bag!) + Value(f.Items(Containers.CharacterWarehouse));
            Add(10, v > 0 ? ItemKit.Short(v) : "—", me ? 't' : 'd');
            Add(11, SeenOf(f, tk.SeenMs), me ? 't' : 'd');
        }
        if (d.Account is { } a)
        {
            NewRow(true);
            Add(0, T.ItemsAcctWh, 'd', left: true);
            long kina = 0;
            foreach (var s in a.Slots) if (s.Item == InventoryStore.Kinah) kina += s.Count;
            Add(1, kina > 0 ? N(kina) : "—", kina > 0 ? 'n' : 'd');
            for (var i = 2; i < 10; i++) Add(i, "—", 'd');
            var v = Value(AccountStacks(a));
            Add(10, v > 0 ? ItemKit.Short(v) : "—", 'd');
            var (_, at) = a.LastUpdate();
            Add(11, at > 0 ? When(at) : T.ItemsFootNever, 'd');
        }
        _caption.SetText(string.Format(T.ItemsCaption, MarketLabel(d.Market)));
    }

    private void BuildLegend()
    {
        _legend.Clear();
        foreach (var g in new[] { Grade.Common, Grade.Rare, Grade.Legendary, Grade.Unique, Grade.Epic, Grade.Special, Grade.Unknown })
        {
            var line = Palette.GradeLine(g);
            var mark = Gtk.DrawingArea.New();
            mark.SetContentWidth(UiKit.Px(10));
            mark.SetContentHeight(UiKit.Px(10));
            mark.Valign = Gtk.Align.Center;
            mark.SetDrawFunc((_, cr, w, h) =>
            {
                var s = w / 10.0;
                Paint.RoundRect(cr, 0.75 * s, 0.75 * s, w - 1.5 * s, h - 1.5 * s, 2 * s);
                Paint.Rgb(cr, line);
                cr.LineWidth = 1.5 * s;
                cr.Stroke();
            });
            _legend.Add(LegendItem(mark, g == Grade.Unknown ? T.ItemsLegendUnknown : ItemGrades.Word(g, T.Code)));
        }
        _legend.Add(LegendItem(ItemsKit.Label("+10", 'n', 11, 700, false), T.ItemsLegendEnchant));
        _legend.Add(LegendItem(ItemsKit.Label("●", 'b', 11, 400, false), T.ItemsLegendDot));
        _legend.Pack(_cardInner + UiKit.Px(14));
    }

    private static Gtk.Widget LegendItem(Gtk.Widget mark, string text)
    {
        var p = UiKit.HBox();
        p.MarginEnd = UiKit.Px(12);
        mark.MarginEnd = UiKit.Px(5);
        p.Append(mark);
        p.Append(ItemsKit.Label(text, 'm', 11, ellipsize: false));
        return p;
    }

    /// <summary>The mock's .pill radio row (All · Equipment · Materials · Consumables).</summary>
    private void BuildPills()
    {
        while (_showPills.GetFirstChild() is { } ch) _showPills.Remove(ch);
        _pillButtons.Clear();
        var show = ItemsKit.Label(T.ItemsShow, 'd', 12, ellipsize: false);
        show.MarginEnd = UiKit.Px(8);
        _showPills.Append(show);
        foreach (var (tag, label) in new[] { ("all", T.ItemsAll), ("eq", T.ItemsEquipment), ("mat", T.ItemsMaterials), ("use", T.ItemsConsumables) })
        {
            var b = Gtk.Button.New();
            b.AddCssClass("k-items-pill");
            if (tag == _group) b.AddCssClass("k-items-on");
            b.Child = Gtk.Label.New(label);
            b.FocusOnClick = false;
            b.Valign = Gtk.Align.Center;
            b.SetCursorFromName("pointer");
            b.OnClicked += (_, _) =>
            {
                if (_group == tag) return;
                _group = tag;
                for (var i = 0; i < _pillButtons.Count; i++)
                    if (i == Array.IndexOf(PillTags, tag)) _pillButtons[i].AddCssClass("k-items-on"); else _pillButtons[i].RemoveCssClass("k-items-on");
                RenderItems();
            };
            _pillButtons.Add(b);
            _showPills.Append(b);
        }
    }

    private static readonly string[] PillTags = ["all", "eq", "mat", "use"];

    // ------------------------------------------------------------------ layout + controls

    private void OnWidth(int width)
    {
        if (width <= 0 || width == _width) return;
        ApplyWidth(width);
        if (IsShown) Render();
    }

    /// <summary>
    /// Sizes for a body <paramref name="width"/>: below 820 px the Across-characters panel moves under the cards (the card is 560 by default),
    /// tile columns, list/ticket/footer columns and wrap lines from the widths left by the gutters (k-scroll 16, card frame 1 + padding 12).
    /// </summary>
    private void ApplyWidth(int width)
    {
        _width = width;
        var narrow = width < UiKit.Px(820);
        if (narrow != _narrow)
        {
            _narrow = narrow;
            if (narrow)
            {
                _sideHost.Child = null;
                _sideHost.Visible = false;
                _content.InsertChildAfter(_sideRoot, _cardsBox);
            }
            else
            {
                _content.Remove(_sideRoot);
                _sideHost.Child = _sideRoot;
                _sideHost.Visible = true;
            }
        }
        var main = (narrow ? width : width - UiKit.Px(330 + 14)) - UiKit.Px(32);
        _cardInner = Math.Max(UiKit.Px(100), main - UiKit.Px(2 + 24));
        _sideInner = Math.Max(UiKit.Px(100), (narrow ? main : UiKit.Px(330)) - UiKit.Px(2 + 24));
        _footWidth = Math.Max(UiKit.Px(100), width - UiKit.Px(32));
        // tiles carry ItemTile.Pad on each side: the 6 px gap of the WPF grid is 2 * Pad + 2
        _cols = ItemsLogic.TileColumns(_cardInner + UiKit.Px(4), UiKit.Px(ItemsLogic.Tile + 2 * ItemTile.Pad), UiKit.Px(ItemsLogic.TileGap - 2 * ItemTile.Pad));
        ItemsLogic.ColumnWidths(ItemsLogic.ListSpec, _cardInner, Icons.Scale, _listW);
        _legend.Pack(_cardInner + UiKit.Px(14));
        LayoutTools();
    }

    private void LayoutTools()
    {
        var avail = _cardInner;   // the tools card sits in the main column like a card (frame 1 + padding 12)
        _showPills.Measure(Gtk.Orientation.Horizontal, -1, out _, out var pills, out _, out _);
        _sortPanel.Measure(Gtk.Orientation.Horizontal, -1, out _, out var sort, out _, out _);
        var one = pills + UiKit.Px(10) + sort <= avail;
        if (one == _toolsOneLine) return;
        _toolsOneLine = one;
        (one ? _toolsLine2 : _toolsLine1).Remove(_sortPanel);
        (one ? _toolsLine1 : _toolsLine2).Append(_sortPanel);
        _toolsLine2.Visible = !one;
    }

    private void LangChanged()
    {
        var lang = UiKit.Selected(_langBox) == 1 ? "es" : "en";
        if (lang == _lang) return;
        _lang = lang;
        Shell.RefreshSettings();
        if (Shell.Settings.PriceSearchLanguage != lang) { Shell.Settings.PriceSearchLanguage = lang; Shell.SaveSettings(); }
        _matches.Clear();
        BuildLegend();
        Render();
    }

    private void RetryIcons()
    {
        foreach (var c in _cards.Values) c.RetryIcons();
        foreach (var m in _matches.Values) m.Tile.RetryIcon();
        _tipTile?.RetryIcon();
    }

    // ------------------------------------------------------------------ one card (head + body), widgets reused across renders

    private sealed class Card
    {
        private readonly ItemsView _v;
        public readonly string Key;
        public readonly Gtk.Box Root, Head, Body, Warn, Tiles2, List, Rows;
        public readonly Gtk.Grid Tiles;
        public readonly Gtk.Label WarnText, Hint;
        public readonly List<ItemStack> Visible = new(64);
        private readonly Gtk.Label _arrow, _title, _sub, _chipText, _right, _value;
        private readonly Gtk.Box _chip;
        private readonly Gtk.ScrolledWindow _leftClip;
        private readonly Gtk.DrawingArea? _toggle;
        private readonly List<ItemTile> _tiles = new();
        private readonly List<ListRow> _rows = new();
        private ListRow? _header;
        private int _placedCols;
        private bool _list;

        public Card(ItemsView v, string key, bool toggle)
        {
            _v = v;
            Key = key;
            Root = UiKit.VBox();
            Head = UiKit.HBox();
            Head.AddCssClass("k-items-head");
            Head.SetCursorFromName("pointer");
            var left = UiKit.HBox();
            _arrow = ItemsKit.Label("▾", 'm', 12, ellipsize: false);
            _arrow.SetSizeRequest(UiKit.Px(14), -1);
            left.Append(_arrow);
            _title = ItemsKit.Label("", 't', 14, 500);
            left.Append(_title);
            _sub = ItemsKit.Label("", 'm', 11.5);
            _sub.MarginStart = UiKit.Px(6);
            _sub.MarginTop = UiKit.Px(1);
            left.Append(_sub);
            _chip = UiKit.HBox();
            _chip.AddCssClass("k-items-chip");
            _chip.Valign = Gtk.Align.Center;
            _chipText = ItemsKit.Label("", 'g', 11, ellipsize: false);
            _chip.Append(_chipText);
            left.Append(_chip);
            // the left side clips under the right one on a narrow card (WPF DockPanel)
            _leftClip = ItemsKit.Clip(left);
            Head.Append(_leftClip);
            var right = UiKit.HBox();
            right.Valign = Gtk.Align.Center;
            _right = ItemsKit.Label("", 'm', 12);
            _right.MarginStart = UiKit.Px(10);
            right.Append(_right);
            _value = ItemsKit.Label("", 'm', 12, ellipsize: false);
            _value.MarginStart = UiKit.Px(10);
            right.Append(_value);
            if (toggle)
            {
                _toggle = ViewToggle();
                right.Append(_toggle);
            }
            Head.Append(right);
            var click = Gtk.GestureClick.New();
            click.Button = 1;
            click.OnReleased += (_, _) => { if (!_v._collapsed.Remove(Key)) _v._collapsed.Add(Key); _v.RenderItems(); };
            Head.AddController(click);
            Root.Append(Head);

            Body = UiKit.VBox();
            Body.AddCssClass("k-items-body");
            Warn = UiKit.HBox();
            Warn.AddCssClass("k-items-warn");
            WarnText = ItemsKit.Wrapped("", 'a', 12);
            Warn.Append(WarnText);
            Body.Append(Warn);
            Hint = ItemsKit.Wrapped("", 'm', 12);
            Hint.AddCssClass("k-items-hinttext");
            Body.Append(Hint);
            Tiles = Gtk.Grid.New();
            var gap = UiKit.Px(ItemsLogic.TileGap - 2 * ItemTile.Pad);
            Tiles.ColumnSpacing = gap;
            Tiles.RowSpacing = gap;
            Tiles2 = UiKit.HBox();
            UiKit.Margin(Tiles2, 12 - ItemTile.Pad, 10 - ItemTile.Pad, 12 - ItemTile.Pad, 0);
            Tiles2.Append(Tiles);
            Body.Append(Tiles2);
            List = UiKit.VBox();
            Body.Append(List);
            Rows = UiKit.VBox();
            Body.Append(Rows);
            Root.Append(Body);
        }

        public void SetHead(string title, string sub, string? chip, char chipFg, string right, string? value, string? valueTip, bool shut, bool list)
        {
            _arrow.SetText(shut ? "▸" : "▾");
            _title.SetText(title);
            // the title stays whole, sub and chip clip under the right side (the chip does not ellipsize: without its own minimum the
            // title got what the chip left)
            _arrow.Measure(Gtk.Orientation.Horizontal, -1, out _, out var aw, out _, out _);
            _title.SetSizeRequest(-1, -1);
            _title.Measure(Gtk.Orientation.Horizontal, -1, out _, out var tw, out _, out _);
            _title.SetSizeRequest(tw, -1);
            _leftClip.SetSizeRequest(aw + tw, -1);
            _sub.SetText(sub);
            _sub.Visible = sub.Length > 0;
            _chip.Visible = chip is not null;
            if (chip is not null) { _chipText.SetText(chip); ItemsKit.SetFg(_chipText, chipFg); }
            _right.SetText(right);
            _right.Visible = right.Length > 0;
            _value.Visible = value is not null;
            if (value is not null) { _value.SetText(value); _value.SetTooltipText(valueTip); }
            if (shut) Head.AddCssClass("k-items-shut"); else Head.RemoveCssClass("k-items-shut");
            if (_list != list) { _list = list; _toggle?.QueueDraw(); }
        }

        /// <summary>Mock .vt: tiles / list per card, drawn (the ▦ glyph is missing from Inter and Noto Sans).</summary>
        private Gtk.DrawingArea ViewToggle()
        {
            var a = Gtk.DrawingArea.New();
            a.SetContentWidth(UiKit.Px(56));
            a.SetContentHeight(UiKit.Px(18));
            a.MarginStart = UiKit.Px(10);
            a.Valign = Gtk.Align.Center;
            a.SetDrawFunc((_, cr, w, h) =>
            {
                var s = w / 56.0;
                cr.Scale(s, s);
                const double H = 18;
                Paint.RoundRect(cr, 0.5, 0.5, 55, H - 1, 6);
                cr.Save();
                cr.Clip();
                Paint.Rgb(cr, ItemsKit.ChipBack);
                cr.Rectangle(_list ? 28 : 0, 0, 28, H);
                cr.Fill();
                cr.Restore();
                Paint.RoundRect(cr, 0.5, 0.5, 55, H - 1, 6);
                Paint.Rgb(cr, ItemsKit.Line2);
                cr.LineWidth = 1;
                cr.Stroke();
                // tiles: 2×2 squares; list: three bars
                Paint.Rgb(cr, _list ? ItemsKit.Mute : ItemsKit.Text);
                for (var i = 0; i < 4; i++) cr.Rectangle(9 + (i % 2) * 5.5, 4 + (i / 2) * 5.5, 4.5, 4.5);
                cr.Fill();
                Paint.Rgb(cr, _list ? ItemsKit.Text : ItemsKit.Mute);
                for (var i = 0; i < 3; i++) cr.Rectangle(36, 4.5 + i * 3.6, 12, 1.6);
                cr.Fill();
            });
            var click = Gtk.GestureClick.New();
            click.Button = 1;
            // claimed on press: the head's own click (fold) does not fire
            click.OnPressed += (g, _) => g.SetState(Gtk.EventSequenceState.Claimed);
            click.OnReleased += (_, e) =>
            {
                var list = e.X >= a.GetWidth() / 2.0;
                if (list) _v._listMode.Add(Key); else _v._listMode.Remove(Key);
                _v.RenderItems();
            };
            a.AddController(click);
            a.HasTooltip = true;
            a.OnQueryTooltip += (_, e) => { e.Tooltip.SetText(e.X >= a.GetWidth() / 2 ? T.ItemsList : T.ItemsTiles); return true; };
            a.SetCursorFromName("pointer");
            return a;
        }

        public void ShowTiles(List<ItemStack> visible, HashSet<byte>? dots)
        {
            Tiles.Visible = Tiles2.Visible = visible.Count > 0;
            var cols = _v._cols;
            var replace = cols != _placedCols;
            _placedCols = cols;
            var hasQ = _v._q.Length > 0;
            for (var i = 0; i < visible.Count; i++)
            {
                ItemTile t;
                if (i < _tiles.Count) t = _tiles[i];
                else
                {
                    t = new ItemTile();
                    _v.AttachTip(t);
                    _tiles.Add(t);
                    Tiles.Attach(t.Widget, i % cols, i / cols, 1, 1);
                }
                var s = visible[i];
                var hit = hasQ && _v.Matches(s.Item);
                t.Set(s.Item, s.Count, s.Enchant, _v.M.Grades.Of(s.Item), _v.Nm(s.Item), _v.Shell.Icons, dots is not null && dots.Contains(s.Slot), hasQ && !hit, hit);
                t.Widget.Visible = true;
            }
            for (var i = visible.Count; i < _tiles.Count; i++) _tiles[i].Widget.Visible = false;
            if (!replace) return;
            for (var i = 0; i < _tiles.Count; i++)
            {
                Tiles.Remove(_tiles[i].Widget);
                Tiles.Attach(_tiles[i].Widget, i % cols, i / cols, 1, 1);
            }
        }

        public void ShowList(List<ItemStack> visible, string who, Place place, HashSet<byte>? dots)
        {
            List.Visible = visible.Count > 0;
            if (_header is null)
            {
                _header = new ListRow(_v, true);
                List.Append(_header.Root);
            }
            _header.Widths(_v._listW);
            for (var i = 0; i < visible.Count; i++)
            {
                ListRow r;
                if (i < _rows.Count) r = _rows[i];
                else { r = new ListRow(_v, false); _rows.Add(r); List.Append(r.Root); }
                r.Widths(_v._listW);
                r.Set(visible[i], who, place, dots);
                r.Root.Visible = true;
            }
            for (var i = visible.Count; i < _rows.Count; i++) _rows[i].Root.Visible = false;
        }

        public void ClearRows()
        {
            Rows.Visible = true;
            while (Rows.GetFirstChild() is { } c) Rows.Remove(c);
        }

        public void AddRow(Gtk.Widget w) => Rows.Append(w);

        public void RetryIcons()
        {
            foreach (var t in _tiles) t.RetryIcon();
            foreach (var r in _rows) r.Tile?.RetryIcon();
        }
    }

    /// <summary>One list-mode row (WPF ListGrid 44,1.4*,70,*,1.2*,56,64), or the header row; updated in place.</summary>
    private sealed class ListRow
    {
        private readonly ItemsView _v;
        public readonly Gtk.Box Root;
        public readonly ItemTile? Tile;
        private readonly Gtk.Widget[] _cells = new Gtk.Widget[7];
        private readonly Gtk.Box? _nameLine;
        private readonly Gtk.Label _name, _count, _where, _also, _unit, _value;
        private readonly Gtk.Label? _ench, _bound;
        private Gtk.Box? _chip;
        private Grade _chipGrade = (Grade)255;
        private string _chipLang = "";
        private int _gradeClass = -1;
        private readonly int[] _w = new int[7];

        public ListRow(ItemsView v, bool header)
        {
            _v = v;
            Root = UiKit.HBox();
            if (header)
            {
                Root.AddCssClass("k-items-lhead");
                _cells[0] = Spacer();
                _name = Cell(1, T.ItemsColName, 'm', 11);
                _count = Cell(2, T.ItemsColCount, 'm', 11, true);
                _where = Cell(3, T.ItemsColWhere, 'm', 11);
                _also = Cell(4, T.ItemsColAlso, 'm', 11);
                _unit = Cell(5, T.ItemsColUnit, 'm', 11, true);
                _value = Cell(6, T.ItemsColValue, 'm', 11, true);
                return;
            }
            Root.AddCssClass("k-items-row");
            Root.SetSizeRequest(-1, UiKit.Px(42));
            Tile = new ItemTile(36);
            v.AttachTip(Tile);
            var tc = UiKit.HBox();
            tc.Valign = Gtk.Align.Center;
            tc.Append(Tile.Widget);
            _cells[0] = tc;
            Root.Append(tc);
            var nameCell = UiKit.VBox();
            nameCell.Valign = Gtk.Align.Center;
            nameCell.MarginStart = nameCell.MarginEnd = UiKit.Px(4);
            nameCell.Overflow = Gtk.Overflow.Hidden;
            _nameLine = UiKit.HBox();
            _name = Gtk.Label.New("");
            _name.Xalign = 0;
            _name.Ellipsize = Pango.EllipsizeMode.End;
            _nameLine.Append(_name);
            _ench = ItemsKit.Label("", 'n', 12.5, ellipsize: false);
            _nameLine.Append(_ench);
            nameCell.Append(_nameLine);
            _bound = ItemsKit.Label(T.ItemsBound, 'm', 11);
            nameCell.Append(_bound);
            _cells[1] = nameCell;
            Root.Append(nameCell);
            _count = Cell(2, "", 't', 12.5, true);
            _where = Cell(3, "", 'm', 11.5);
            _also = Cell(4, "", 'm', 11.5);
            _unit = Cell(5, "", 't', 12.5, true);
            _value = Cell(6, "", 't', 12.5, true);
        }

        private Gtk.Box Spacer()
        {
            var b = UiKit.HBox();
            Root.Append(b);
            return b;
        }

        private Gtk.Label Cell(int col, string text, char fg, double size, bool right = false)
        {
            var l = ItemsKit.Label(text, fg, size);
            l.Xalign = right ? 1 : 0;
            l.MarginStart = l.MarginEnd = UiKit.Px(4);
            _cells[col] = l;
            Root.Append(l);
            return l;
        }

        public void Widths(int[] w)
        {
            for (var i = 0; i < 7; i++)
            {
                if (_w[i] == w[i]) continue;
                _w[i] = w[i];
                var inner = i == 0 ? w[i] : Math.Max(0, w[i] - UiKit.Px(8));
                _cells[i].SetSizeRequest(inner, -1);
            }
        }

        /// <summary>The chip gives way to the name in a narrow name column (Avalonia trimmed the inline chip before the name).</summary>
        private void FitChip()
        {
            if (_chip is null) return;
            _name.Measure(Gtk.Orientation.Horizontal, -1, out _, out var name, out _, out _);
            _chip.Measure(Gtk.Orientation.Horizontal, -1, out _, out var chip, out _, out _);
            var ench = 0;
            if (_ench!.Visible) _ench.Measure(Gtk.Orientation.Horizontal, -1, out _, out ench, out _, out _);
            _chip.Visible = name + ench + chip <= _w[1] - UiKit.Px(8);
        }

        public void Set(ItemStack s, string who, Place place, HashSet<byte>? dots)
        {
            var v = _v;
            var m = v.M;
            var grade = m.Grades.Of(s.Item);
            Tile!.Set(s.Item, s.Count, s.Enchant, grade, v.Nm(s.Item), v.Shell.Icons, dots is not null && dots.Contains(s.Slot));
            _name.SetText(v.Nm(s.Item));
            var gc = (int)grade < Palette.GradeCount ? (int)grade : 0;
            if (gc != _gradeClass)
            {
                if (_gradeClass >= 0) _name.RemoveCssClass("k-gt" + _gradeClass.ToString(CultureInfo.InvariantCulture));
                _gradeClass = gc;
                ItemKit.GradeText(_name, grade);
            }
            _ench!.SetText(s.Enchant > 0 ? " +" + s.Enchant.ToString(CultureInfo.InvariantCulture) : "");
            _ench.Visible = s.Enchant > 0;
            if (grade != _chipGrade || v._lang != _chipLang)
            {
                if (_chip is not null) _nameLine!.Remove(_chip);
                _chip = ItemKit.GradeChip(grade, v._lang);
                if (_chip is not null) _nameLine!.Append(_chip);
                _chipGrade = grade;
                _chipLang = v._lang;
            }
            FitChip();
            _bound!.Visible = m.Grades.Bound(s.Item);
            _count.SetText(N(s.Count));
            var where = string.Format(T.ItemsSlot, s.Slot);
            if (place == Place.AccountWarehouse && v._data?.Account is { } acc)
                foreach (var x in acc.Slots) if (x.Slot == s.Slot) { where += " · " + NameOf(x.By) + " " + v.When(x.AtMs); break; }
            _where.SetText(where);
            var also = v.Elsewhere(s.Item, who, place);
            _also.SetText(also.Length > 0 ? also : "—");
            _also.SetTooltipText(also.Length > 0 ? also : null);
            var p = v.Price(s.Item);
            _unit.SetText(p is { } unit ? ItemKit.Short(unit) : "—");
            ItemsKit.SetFg(_unit, p is null ? 'f' : 't');
            _value.SetText(p is { } u2 ? ItemKit.Short(u2 * s.Count) : "");
            Root.Opacity = v._q.Length > 0 && !v.Matches(s.Item) ? 0.25 : 1;
        }
    }
}
