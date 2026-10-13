using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.Core.Character;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Core.Market;

namespace AionMeter.App.Views;

/// <summary>
/// Items tab (A2M-29): the bound character's bag and warehouse, the account's shared warehouse merged from every character
/// (<see cref="AccountWarehouse"/>), the equipped set, an "Across characters" panel (search → every match with its places, source and the
/// Craft hint) and a currency footer. Layout and colours: docs/mockups/items-tab in the project repo, as approved 2026-10-11. The left
/// column is one virtualised list of rows (card heads, tile rows, list rows), so only visible tiles exist and ask for their icon.
/// </summary>
public partial class ItemsView : UserControl
{
    /// <summary>One character of the server: its inventory.json (the live store for the bound one), "Templar 45" from profile.txt and its
    /// tickets/achievements (live for the bound one, character.json otherwise; the Tickets table).</summary>
    public sealed record CharData(string Key, string Cls, InventoryFile? File, CharacterProgress? Progress = null);

    /// <summary>Everything one render needs; the live tab gathers it from the meter, the render tool builds it from a fixture.</summary>
    public sealed record Data(string CurrentKey, string Server, string ServerName, List<CharData> Chars, AccountWarehouseView? Account, MarketSnapshot Market, DateTimeOffset Now,
        ProgressData? Progress = null);

    /// <summary>A2M-15 cards: the viewed character's achievement rows and content tickets (live for the bound one, its character.json otherwise).</summary>
    public sealed record ProgressData(IReadOnlyDictionary<int, AchievementRow> Achievements, IReadOnlyDictionary<int, ContentTicket> Tickets, AchievementCatalog Catalog,
        IReadOnlyDictionary<int, ExpeditionChest>? Chests = null);

    private static UiText T => UiText.Current;
    private static readonly Brush Text = SkillTile.Frozen(0xFA, 0xFA, 0xFA), Dim = SkillTile.Frozen(0xAF, 0xAF, 0xAF), Mute = SkillTile.Frozen(0x7D, 0x7D, 0x7D),
        Line = SkillTile.Frozen(0x2B, 0x2B, 0x2B), Line2 = SkillTile.Frozen(0x37, 0x37, 0x37), CardBrush = SkillTile.Frozen(0x1F, 0x1F, 0x1F, 0xCC), Band = SkillTile.Frozen(0x1F, 0x1F, 0x1F),
        ChipBack = SkillTile.Frozen(0x2B, 0x2B, 0x2B), Green = SkillTile.Frozen(0x3D, 0xDC, 0x84), Amber = SkillTile.Frozen(0xF5, 0xB5, 0x44), Blue = SkillTile.Frozen(0x65, 0xB0, 0xFC),
        Num = SkillTile.Frozen(0xFC, 0xC7, 0x8B), WarnBack = SkillTile.Frozen(0xF5, 0xB5, 0x44, 0x14), WarnLine = SkillTile.Frozen(0xF5, 0xB5, 0x44, 0x4D), RowLine = SkillTile.Frozen(0x20, 0x20, 0x20),
        LocBack = SkillTile.Frozen(0x1A, 0x1A, 0x1A), MeLine = SkillTile.Frozen(0x65, 0xB0, 0xFC, 0x73), AcctLine = SkillTile.Frozen(0x3D, 0xDC, 0x84, 0x66), HintBack = SkillTile.Frozen(0x14, 0x14, 0x14),
        KvBack = SkillTile.Frozen(0x1F, 0x1F, 0x1F), Faint = SkillTile.Frozen(0x55, 0x55, 0x55), FootLine = SkillTile.Frozen(0x1E, 0x1E, 0x1E), Transparent = SkillTile.Frozen(0, 0, 0, 0);
    private static readonly FontFamily Symbol = new("Segoe UI Symbol");
    private const int Tile = 48, TileGap = 6;

    private readonly MeterService _meter;
    private readonly IconCache _icons;
    private Data? _data;
    private bool _sample, _stale, _syncing;
    private string _group = "all", _q = "", _lang;
    private ItemSortMode _sort = ItemSortMode.Game;
    private readonly HashSet<string> _collapsed = ["eq", "x17", "ach"], _listMode = [];
    private readonly CharacterFiles _charFiles = new();
    private readonly Dictionary<int, (string En, string Es)> _norm = new();
    private readonly Dictionary<string, (DateTime Written, string Cls)> _cls = new();
    private ItemsIndex _index = new();
    private int _columns = 12;
    private bool _narrow;
    private readonly DispatcherTimer _reloadTick = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _searchTick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public event Action? CloseRequested;
    /// <summary>"Open in Craft →": switch to the Craft tab with this text in its search box.</summary>
    public event Action<string>? OpenInCraft;

    public ItemsView(MeterService meter, IconCache? icons = null)
    {
        _meter = meter;
        _icons = icons ?? meter.ItemIconCache;
        _lang = meter.Settings.PriceSearchLanguage == "es" ? "es" : "en";
        InitializeComponent();
        _syncing = true;
        LangBox.SelectedIndex = _lang == "es" ? 1 : 0;
        foreach (var (mode, label) in new[] { (ItemSortMode.Game, T.ItemsSortGame), (ItemSortMode.Grade, T.ItemsSortGrade), (ItemSortMode.Name, T.ItemsSortName), (ItemSortMode.Value, T.ItemsSortValue) })
            SortBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        SortBox.SelectedIndex = 0;
        _syncing = false;
        BuildLegend();
        _reloadTick.Tick += (_, _) => { _reloadTick.Stop(); Reload(); };
        _searchTick.Tick += (_, _) => { _searchTick.Stop(); _q = ItemNames.Normalize(SearchBox.Text); Render(); };
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); ItemTile.NotifyIcons(); };
        Loaded += (_, _) =>
        {
            if (_sample) return;
            Reload();
            _meter.Inventory.Changed += OnChanged; _meter.ViewInventoryChanged += OnChanged; _meter.CharacterChanged += OnChanged; _meter.MarketChanged += OnChanged; _meter.Account.Changed += OnChanged; _icons.Loaded += OnIcon;
            _meter.ProgressChanged += OnChanged; _meter.ProfileChanged += OnChanged;
        };
        Unloaded += (_, _) =>
        {
            if (_sample) return;
            _meter.Inventory.Changed -= OnChanged; _meter.ViewInventoryChanged -= OnChanged; _meter.CharacterChanged -= OnChanged; _meter.MarketChanged -= OnChanged; _meter.Account.Changed -= OnChanged; _icons.Loaded -= OnIcon;
            _meter.ProgressChanged -= OnChanged; _meter.ProfileChanged -= OnChanged;
            _reloadTick.Stop(); _iconTick.Stop();
        };
        // hidden tab: one reload when it comes back, not one per packet
        IsVisibleChanged += (_, e) => { if (!_sample && e.NewValue is true && _stale) { _stale = false; Reload(); } };
        SizeChanged += (_, _) => ApplyWidth();
    }

    // packets arrive on the capture thread: coalesce to one reload 300 ms after the last change
    private void OnChanged() => Dispatcher.BeginInvoke(() => { if (!IsVisible) { _stale = true; return; } _reloadTick.Stop(); _reloadTick.Start(); });
    private void OnIcon() { if (!_iconTick.IsEnabled) _iconTick.Start(); }

    /// <summary>Offscreen render (SampleRenderer "items"): this data, nothing read from or written to disk.</summary>
    public void ShowSample(Data data) { _sample = true; _data = data; Render(); }

    /// <summary>Render tool (--tab search): the search box filled as a user would type it.</summary>
    public void SampleSearch(string text) { SearchBox.Text = text; _searchTick.Stop(); _q = ItemNames.Normalize(text); Render(); }

    /// <summary>Render tool (--tab list): every card in list mode, Equipped open.</summary>
    public void SampleListMode() { _listMode.UnionWith(["bag", "wh", "acct", "eq", "x17"]); _collapsed.Clear(); Render(); }

    public void RenderNow() { ApplyWidth(); Render(); }

    private void Reload()
    {
        if (!_sample) _data = Gather();
        Render();
    }

    private static string ServerOf(string key) { var i = key.IndexOf('-'); return i > 0 ? key[..i] : ""; }
    private static string NameOf(string key) { var i = key.IndexOf('-'); return i > 0 ? key[(i + 1)..] : key; }

    private Data Gather()
    {
        var cur = _meter.CharacterKey;
        var bound = _meter.BoundKey;
        var keys = _meter.CharacterKeys();
        if (cur.Length > 0 && !keys.Contains(cur)) keys.Add(cur);
        var server = ServerOf(cur.Length > 0 ? cur : keys.Count > 0 ? keys[0] : "");
        var chars = new List<CharData>(keys.Count);
        foreach (var k in keys)
        {
            if (ServerOf(k) != server) continue;   // the account warehouse and the footer are per server
            var path = Path.Combine(AppSettings.AppDataDir, "characters", k, "inventory.json");
            InventoryFile? f;
            // the viewed character from ViewInventory (live or its file), the bound one live even when not viewed (search panel), others from disk
            var store = k == cur ? _meter.ViewInventory : k == bound ? _meter.Inventory : null;
            if (store is not null)
            {
                var written = DateTime.UtcNow;
                try { if (File.Exists(path)) written = File.GetLastWriteTimeUtc(path); } catch (IOException) { }
                f = new InventoryFile(store.All(), store.SeenAll(), written);
            }
            else f = _meter.OtherInventories.Get(path);
            var progress = k == bound ? new CharacterProgress(_meter.Character.Achievements, _meter.Character.Tickets, _meter.Character.UpdatedMs, _meter.Character.Chests)
                : _charFiles.Get(Path.Combine(AppSettings.AppDataDir, "characters", k, "character.json"));
            chars.Add(new CharData(k, ClassLine(k), f, progress));
        }
        var serverName = int.TryParse(server, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sid) ? _meter.Data.ServerName(sid) : server;
        if (_meter.Market.Server.Length > 0 && serverName == server) serverName = _meter.Market.Server;
        var viewed = cur.Length > 0 ? chars.Find(c => c.Key == cur)?.Progress : null;
        return new Data(cur, server, serverName, chars, server.Length > 0 ? _meter.Account.View(server) : null, _meter.Market, DateTimeOffset.Now,
            viewed is null ? null : new ProgressData(viewed.Achievements, viewed.Tickets, _meter.AchievementBook, viewed.Chests));
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

    private string Nm(int id) => _meter.Names.Name(id, _lang) ?? _meter.Items.NameOf(id);
    private string OtherName(int id) { var (en, es) = _meter.Names.Both(id); var other = _lang == "es" ? en : es; return other is not null && other != Nm(id) ? other : ""; }
    private long? Price(int id) => _data?.Market.Price(id);

    private bool Matches(int id)
    {
        if (_q.Length == 0) return true;
        if (!_norm.TryGetValue(id, out var n))
        {
            var (en, es) = _meter.Names.Both(id);
            n = (ItemNames.Normalize(en ?? _meter.Items.NameOf(id)), ItemNames.Normalize(es ?? ""));
            _norm[id] = n;
        }
        return n.En.Contains(_q, StringComparison.Ordinal) || n.Es.Contains(_q, StringComparison.Ordinal);
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

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
    private static string Stacks(int n) => n == 1 ? T.ItemsStack1 : string.Format(T.ItemsStacks, n);

    private string MarketLabel(MarketSnapshot m) => m.Count == 0 ? T.ItemsNoMarket : string.Format(T.ItemsMarketFrom, m.Server, m.CapturedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");

    // ------------------------------------------------------------------ render

    private void Render()
    {
        if (_data is not { } d) return;
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

        var offset = ScrollOf(MainList)?.VerticalOffset ?? 0;
        var rows = new List<FrameworkElement>(64);
        var cur = d.Chars.Find(c => c.Key == d.CurrentKey);
        var stacksSeen = 0;
        if (d.Progress is { } prog && _q.Length == 0) { AddToday(rows, prog, d.Now); AddAchievements(rows, prog); }
        if (cur?.File is not { } file) rows.Add(Card(Hint(T.ItemsNoChar), top: true, bottom: true));
        else
        {
            var bag = file.Items(Containers.Bag);
            stacksSeen = file.Stacks.Count;
            var bagSeen = file.SeenOf(Containers.Bag);
            var incomplete = bagSeen.FullMs == 0 && ItemKinds.BagLooksIncomplete(bag);
            var maxSlot = bag.Count > 0 ? bag.Max(s => s.Slot) : 0;
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
            else warn = string.Format(T.ItemsAcctFull, NameOf(a.FullBy), When(a.FullMs)) + "; " +
                        string.Join(", ", newer.Select(n => string.Format(T.ItemsAcctNewer, n.Slots, NameOf(n.By), When(n.AtMs)))) + " " + T.ItemsAcctDot;
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
        MainList.ItemsSource = rows;
        if (offset > 0) Dispatcher.InvokeAsync(() => ScrollOf(MainList)?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);

        RenderSide(d);
        RenderFoot(d);
        StatusText.Text = cur is null ? "" : NameOf(cur.Key) + (cur.Cls.Length > 0 ? " · " + cur.Cls : "") + " · " + Stacks(stacksSeen);
    }

    // ------------------------------------------------------------------ A2M-15: Today (content tickets) and Achievements cards

    private static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(0, t.Minutes)} min";

    private void AddToday(List<FrameworkElement> rows, ProgressData p, DateTimeOffset now)
    {
        const string key = "today";
        var shut = _collapsed.Contains(key);
        var daily = GameReset.NextDaily(now); var weekly = GameReset.NextWeekly(now);
        p.Tickets.TryGetValue(60_000_001, out var odyle); p.Tickets.TryGetValue(1, out var keys);
        var right = (odyle is not null ? TicketBook.Of(odyle.Key).Name(_lang) + " " + TicketBook.Value(odyle, TicketBook.Of(odyle.Key)) + " · " : "")
                  + (keys is not null ? TicketBook.Of(keys.Key).Name(_lang) + " " + keys.Total + " · " : "") + "reset " + Span(daily - now);
        rows.Add(Head(key, T.ItemsToday, T.ItemsTodaySub, null, Mute, right, 0, shut, toggle: false));
        if (shut) return;
        rows.Add(Card(Hint(string.Format(T.ItemsTodayReset, daily.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), Span(daily - now),
            weekly.ToLocalTime().ToString("ddd dd HH:mm", CultureInfo.InvariantCulture)))));
        if (p.Tickets.Count == 0) { rows.Add(Card(Hint(T.ItemsTodayNone))); rows.Add(Card(new Border { Height = 6 }, bottom: true)); return; }
        foreach (var (t, info) in TicketBook.Today(p.Tickets)) rows.Add(Card(TicketRow(t, info, p.Chests)));
        // Daeva Pass / event pass levels: the 810 rows count pass EXP and their claimed step is the reward level (A2M-15)
        foreach (var (id, label) in new[] { (810_433_000, T.ItemsDaevaPass), (810_435_000, T.ItemsEventPass) })
        {
            if (!p.Achievements.TryGetValue(id, out var r) || r.Count == 0) continue;
            var tiers = p.Catalog.Get(id)?.Tiers.Length ?? 0;
            rows.Add(Card(Hint(string.Format(T.ItemsPassLine, label, r.Step, tiers > 0 ? tiers.ToString(CultureInfo.InvariantCulture) : "?", N(r.Count)))));
        }
        rows.Add(Card(new Border { Height = 6 }, bottom: true));
    }

    private FrameworkElement TicketRow(ContentTicket t, TicketInfo info, IReadOnlyDictionary<int, ExpeditionChest>? chests)
    {
        var g = new Grid { Margin = new Thickness(12, 0, 12, 0), Height = 26 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        var name = Cell(g, 0, info.Stars > 0 ? info.Name(_lang) + " " + new string('★', info.Stars) : info.Name(_lang), info.Inferred ? Dim : Text, 12.5);
        if (info.Inferred) name.ToolTip = T.ItemsTodayInferred + " (" + t.Key.ToString(CultureInfo.InvariantCulture) + ")";
        if (info.Chest > 0)
        {
            // A2M-41: the 6000N201 ticket value (7/14) is not the chest bar and is never shown; the bar comes from 12 57
            var p = TicketBook.ChestProgress(info, chests);
            if (p >= info.Chest) Cell(g, 1, T.ItemsTodayClaimable, Green, 11.5, true);
            var chest = Cell(g, 2, string.Format(T.ItemsTodayChest, p?.ToString(CultureInfo.InvariantCulture) ?? "?", info.Chest.ToString(CultureInfo.InvariantCulture)),
                p is null ? Faint : p >= info.Chest ? Green : p > 0 ? Num : Dim, 12.5, true);
            chest.ToolTip = p is null ? T.ItemsTodayChestUnknown : T.ItemsTodayChestTip;
            return new Border { BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g };
        }
        var value = t.IsTime ? string.Format(T.ItemsTodayLeft, Span(TimeSpan.FromMilliseconds(t.Time))) : TicketBook.Value(t, info);
        Cell(g, 2, value, t.Total == 0 && !t.IsTime || t.IsTime && t.Time == 0 ? Faint : Num, 12.5, true);
        return new Border { BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g };
    }

    private void AddAchievements(List<FrameworkElement> rows, ProgressData p)
    {
        const string key = "ach";
        var shut = _collapsed.Contains(key);
        var sum = p.Catalog.Summarise(p.Achievements.Values);
        var right = p.Achievements.Count == 0 || p.Catalog.Count == 0 ? "" : string.Format(T.ItemsAchRight, N(sum.TiersClaimed), N(sum.TiersTotal), N(sum.Completed), N(sum.Claimable), N(sum.InProgress));
        rows.Add(Head(key, T.ItemsAch, "", sum.Claimable > 0 ? sum.Claimable.ToString(CultureInfo.InvariantCulture) : null, Green, right, 0, shut, toggle: false));
        if (shut) return;
        if (p.Achievements.Count == 0) rows.Add(Card(Hint(T.ItemsAchNone)));
        else if (p.Catalog.Count == 0) rows.Add(Card(Hint(T.ItemsAchNoCatalog)));
        else if (sum.ClaimableList.Count == 0) rows.Add(Card(Hint(T.ItemsAchNothing)));
        else foreach (var a in sum.ClaimableList) rows.Add(Card(AchievementRowView(a)));
        rows.Add(Card(new Border { Height = 6 }, bottom: true));
    }

    private FrameworkElement AchievementRowView(AchievementProgress a)
    {
        var info = a.Info!;
        var g = new Grid { Margin = new Thickness(12, 0, 12, 0), MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        var name = Cell(g, 0, info.Name(_lang), Text, 12.5);
        var desc = info.Desc(_lang);
        if (desc.Length > 0) name.ToolTip = desc;
        var tier = string.Format(T.ItemsAchTier, a.NextTier, a.Tiers, N(a.Row.Count), N(a.Goal));
        if (a.ClaimableTiers > 1) tier += " · " + string.Format(T.ItemsAchMore, a.ClaimableTiers);
        Cell(g, 1, tier, Dim, 11.5);
        var rewards = info.Tiers[a.NextTier - 1].Rewards;
        var parts = new List<string>(rewards.Length);
        foreach (var (item, amount) in rewards) parts.Add(Nm(item) + " ×" + N(amount));
        var rw = Cell(g, 2, string.Join(", ", parts), Num, 11.5);
        rw.ToolTip = string.Join("\n", parts);
        return new Border { BorderBrush = RowLine, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g };
    }

    private static List<ItemStack> AccountStacks(AccountWarehouseView? a)
    {
        var l = new List<ItemStack>(a?.Slots.Count ?? 0);
        if (a is null) return l;
        foreach (var s in a.Slots) l.Add(new ItemStack(0, s.Item, s.Count, Containers.AccountWarehouse, s.Slot, s.Enchant));
        return l;
    }

    private static ScrollViewer? ScrollOf(ItemsControl list) => list.Template?.FindName("Scroll", list) as ScrollViewer;

    private void AddCard(List<FrameworkElement> rows, string key, string title, string sub, string? chip, Brush chipBrush, string right, List<ItemStack> stacks, string? warn,
        string who, Place place, HashSet<byte>? dots)
    {
        var visible = new List<ItemStack>(stacks.Count);
        foreach (var s in stacks) if (!InventoryStore.IsCurrency(s.Item) && InGroup(s.Item)) visible.Add(s);
        ItemKinds.Sort(visible, _sort, Nm, _meter.Grades.Of, Price);
        long value = 0;
        foreach (var s in visible) value += (Price(s.Item) ?? 0) * s.Count;
        var shut = _collapsed.Contains(key);
        rows.Add(Head(key, title, sub, chip, chipBrush, right, value, shut));
        if (shut) return;
        if (warn is not null) rows.Add(Card(new Border { Background = WarnBack, BorderBrush = WarnLine, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(12, 8, 12, 0),
            Child = new TextBlock { Text = warn, Foreground = Amber, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 17.4, LineStackingStrategy = LineStackingStrategy.BlockLineHeight } }));
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
        t.Set(s.Item, s.Count, s.Enchant, _meter.Grades.Of(s.Item), Nm(s.Item), _icons, dot, dim: searchable && _q.Length > 0 && !hit, hit: hit);
        AttachTip(t);
        return t;
    }

    /// <summary>One row of a card: the card's side borders (and its top or bottom corners) drawn per row, so rows can be virtualised.</summary>
    private static Border Card(UIElement child, bool top = false, bool bottom = false) => new()
    {
        Background = CardBrush, BorderBrush = Line, Child = child,
        BorderThickness = new Thickness(0.8, top ? 0.8 : 0, 0.8, bottom ? 0.8 : 0),
        CornerRadius = new CornerRadius(top ? 8 : 0, top ? 8 : 0, bottom ? 8 : 0, bottom ? 8 : 0),
        Margin = new Thickness(0, 0, 0, bottom ? 10 : 0),
    };

    private static TextBlock Hint(string text) => new() { Text = text, Foreground = Mute, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10, 12, 10), LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };

    private static Border Chip(string text, Brush fg) => new()
    {
        Background = ChipBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = fg, FontSize = 11 },
    };

    private FrameworkElement Head(string key, string title, string sub, string? chip, Brush chipBrush, string right, long value, bool shut, bool toggle = true)
    {
        var dock = new DockPanel { LastChildFill = true };
        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(rightPanel, Dock.Right);
        rightPanel.Children.Add(new TextBlock { Text = right, Foreground = Mute, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        if (value > 0)
            rightPanel.Children.Add(new TextBlock { Text = string.Format(T.ItemsValue, ItemTile.Short(value)), Foreground = Mute, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                ToolTip = string.Format(T.ItemsValueTip, MarketLabel(_data!.Market)) });
        if (toggle) rightPanel.Children.Add(ViewToggle(key));
        dock.Children.Add(rightPanel);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = shut ? "▸" : "▾", Foreground = Mute, FontFamily = Symbol, Width = 14, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        if (sub.Length > 0) left.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 1, 0, 0) });
        if (chip is not null) left.Children.Add(Chip(chip, chipBrush));
        dock.Children.Add(left);
        var b = new Border
        {
            Background = Band, BorderBrush = Line, BorderThickness = new Thickness(0.8, 0.8, 0.8, shut ? 0.8 : 0), CornerRadius = new CornerRadius(8, 8, shut ? 8 : 0, shut ? 8 : 0),
            Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, shut ? 10 : 0), Child = dock, Cursor = Cursors.Hand,
        };
        b.MouseLeftButtonUp += (_, e) => { if (e.Handled) return; if (!_collapsed.Remove(key)) _collapsed.Add(key); Render(); };
        return b;
    }

    /// <summary>Mock .vt: tiles ▦ / list ☰ per card.</summary>
    private FrameworkElement ViewToggle(string key)
    {
        var list = _listMode.Contains(key);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, isList, tip) in new[] { ("▦", false, T.ItemsTiles), ("☰", true, T.ItemsList) })
        {
            var on = isList == list;
            var cell = new Border { Background = on ? ChipBack : Transparent, Padding = new Thickness(8, 1, 8, 1), ToolTip = tip, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = glyph, FontFamily = Symbol, FontSize = 12, Foreground = on ? Text : Mute } };
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (isList) _listMode.Add(key); else _listMode.Remove(key); Render(); };
            panel.Children.Add(cell);
        }
        return new Border { BorderBrush = Line2, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(6), Margin = new Thickness(10, 0, 0, 0), Child = panel, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Grid ListGrid()
    {
        var g = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        foreach (var w in new[] { new GridLength(44), new GridLength(1.4, GridUnitType.Star), new GridLength(70), new GridLength(1, GridUnitType.Star), new GridLength(1.2, GridUnitType.Star), new GridLength(56), new GridLength(64) })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    private static TextBlock Cell(Grid g, int col, string text, Brush fg, double size = 12.5, bool right = false)
    {
        var t = new TextBlock { Text = text, Foreground = fg, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Grid.SetColumn(t, col);
        g.Children.Add(t);
        return t;
    }

    private FrameworkElement ListHeader()
    {
        var g = ListGrid();
        g.Margin = new Thickness(12, 6, 12, 2);
        Cell(g, 1, T.ItemsColName, Mute, 11); Cell(g, 2, T.ItemsColCount, Mute, 11, true); Cell(g, 3, T.ItemsColWhere, Mute, 11); Cell(g, 4, T.ItemsColAlso, Mute, 11);
        Cell(g, 5, T.ItemsColUnit, Mute, 11, true); Cell(g, 6, T.ItemsColValue, Mute, 11, true);
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 0.8), Child = g, Margin = new Thickness(0, 4, 0, 0) };
    }

    private FrameworkElement ListRow(ItemStack s, string who, Place place, HashSet<byte>? dots)
    {
        var g = ListGrid();
        g.Height = 42;
        var tile = MakeTile(s, dots is not null && dots.Contains(s.Slot), 36, searchable: false);
        tile.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(tile);
        var grade = _meter.Grades.Of(s.Item);
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new Run(Nm(s.Item)) { Foreground = ItemTile.TextOf(grade) });
        if (s.Enchant > 0) name.Inlines.Add(new Run(" +" + s.Enchant.ToString(CultureInfo.InvariantCulture)) { Foreground = Num });
        if (_meter.Grades.Bound(s.Item)) { name.Inlines.Add(new LineBreak()); name.Inlines.Add(new Run(T.ItemsBound) { Foreground = Mute, FontSize = 11 }); }
        Grid.SetColumn(name, 1); g.Children.Add(name);
        Cell(g, 2, N(s.Count), Text, 12.5, true);
        var where = string.Format(T.ItemsSlot, s.Slot);
        if (place == Place.AccountWarehouse && _data?.Account?.Slots.FirstOrDefault(x => x.Slot == s.Slot) is { } slot) where += " · " + NameOf(slot.By) + " " + When(slot.AtMs);
        Cell(g, 3, where, Mute, 11.5);
        var also = Elsewhere(s.Item, who, place);
        Cell(g, 4, also.Length > 0 ? also : "—", Mute, 11.5).ToolTip = also.Length > 0 ? also : null;
        var p = Price(s.Item);
        Cell(g, 5, p is { } unit ? ItemTile.Short(unit) : "—", p is null ? Faint : Text, 12.5, true);
        Cell(g, 6, p is { } u2 ? ItemTile.Short(u2 * s.Count) : "", Text, 12.5, true);
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

    private void AttachTip(FrameworkElement t)
    {
        t.ToolTip = "";   // placeholder: the real ToolTip is made on the first opening, its content on every opening
        ToolTipService.SetInitialShowDelay(t, 250);
        ToolTipService.SetShowDuration(t, 60000);
        ToolTipService.SetPlacement(t, PlacementMode.Mouse);
        t.ToolTipOpening += Tip_Opening;
    }

    private void Tip_Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not ItemTile t || t.Item == 0) { e.Handled = true; return; }
        var tip = t.ToolTip as ToolTip ?? new ToolTip { Style = (Style)FindResource("ItemTip"), Placement = PlacementMode.Mouse };
        tip.Content = TipContent(t.Item, t.Count);
        t.ToolTip = tip;
    }

    /// <summary>Mock .tip: icon, name (grade colour), other-language name, "grade · bound · id", then this stack / everywhere / where / market / from.</summary>
    public FrameworkElement TipContent(int item, long count)
    {
        var root = new StackPanel();
        root.Children.Add(ItemHead(item, 44));
        var kv = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        kv.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var r = 0;
        void Kv(string k, string v)
        {
            kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var a = new TextBlock { Text = k, Foreground = Mute, Margin = new Thickness(0, 1, 10, 1) };
            var b = new TextBlock { Text = v, Foreground = Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            Grid.SetRow(a, r); Grid.SetRow(b, r); Grid.SetColumn(b, 1);
            kv.Children.Add(a); kv.Children.Add(b); r++;
        }
        Kv(T.ItemsThisStack, N(count));
        Kv(T.ItemsEverywhere, N(_index.Total(item)));
        var where = string.Join(" · ", _index.Of(item).Select(h => PlaceLabel(h) + " " + N(h.Count)));
        if (where.Length > 0) Kv(T.ItemsWhere, where);
        if (Price(item) is { } p) Kv(T.ItemsMarket, string.Format(T.ItemsMarketEach, N(p)));
        if (_meter.Names.Source(item) is { Length: > 0 } src) Kv(T.ItemsFromLbl, src);
        root.Children.Add(new Border { Background = KvBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Child = kv });
        return root;
    }

    private FrameworkElement ItemHead(int item, double size)
    {
        var head = new DockPanel();
        var tile = new ItemTile { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var grade = _meter.Grades.Of(item);
        tile.Set(item, 1, 0, grade, Nm(item), _icons);
        head.Children.Add(tile);
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = Nm(item), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ItemTile.TextOf(grade), TextWrapping = TextWrapping.Wrap });
        if (OtherName(item) is { Length: > 0 } other) names.Children.Add(new TextBlock { Text = other, FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap });
        var g = grade == Grade.Unknown ? T.ItemsGradeUnknown : ItemGrades.Name(grade);
        names.Children.Add(new TextBlock { Text = g + (_meter.Grades.Bound(item) ? " · " + T.ItemsBound : "") + " · id " + item.ToString(CultureInfo.InvariantCulture), FontSize = 11, Foreground = Mute });
        head.Children.Add(names);
        return head;
    }

    // ------------------------------------------------------------------ Across characters

    private void RenderSide(Data d)
    {
        var cards = new List<FrameworkElement>(26);
        List<int> ids;
        if (_q.Length > 0)
        {
            ids = _index.Matching(Matches);
            SideCount.Text = string.Format(T.ItemsMatches, ids.Count);
            if (ids.Count == 0) cards.Add(Hint(string.Format(T.ItemsNoMatch, SearchBox.Text.Trim())));
            for (var i = 0; i < ids.Count && i < 40; i++) cards.Add(MatchCard(ids[i], d, i == 0));
        }
        else
        {
            // with an empty box: the same item split over 3+ storage places, worth consolidating (Diego 2026-10-11)
            ids = _index.Split();
            SideCount.Text = "";
            cards.Add(Hint(string.Format(T.ItemsAcrossHint, ids.Count)));
            for (var i = 0; i < ids.Count && i < 25; i++) cards.Add(MatchCard(ids[i], d, false));
        }
        SideList.ItemsSource = cards;
    }

    private FrameworkElement MatchCard(int item, Data d, bool first)
    {
        var root = new StackPanel();
        var head = new DockPanel();
        var total = _index.Total(item);
        var tot = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        tot.Children.Add(new TextBlock { Text = N(total), FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right });
        var sub = Price(item) is { } p ? string.Format(T.ItemsValue, ItemTile.Short(p * total)) : _meter.Grades.Bound(item) ? T.ItemsBound : "";
        if (sub.Length > 0) tot.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Mute, HorizontalAlignment = HorizontalAlignment.Right });
        DockPanel.SetDock(tot, Dock.Right);
        head.Children.Add(tot);
        var tile = MakeTile(new ItemStack(0, item, 1, 0, 0, 0), false, 36, searchable: false);
        tile.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(tile);
        var grade = _meter.Grades.Of(item);
        var names = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = Nm(item), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ItemTile.TextOf(grade), TextWrapping = TextWrapping.Wrap });
        if (OtherName(item) is { Length: > 0 } other) names.Children.Add(new TextBlock { Text = other, FontSize = 11, Foreground = Mute, TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(names);
        root.Children.Add(head);

        var locs = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var holds = _index.Of(item).ToList();
        holds.Sort((a, b) => { var c = (b.Who == d.CurrentKey).CompareTo(a.Who == d.CurrentKey); if (c != 0) return c; c = (a.Place == Place.AccountWarehouse).CompareTo(b.Place == Place.AccountWarehouse); return c != 0 ? c : b.Count.CompareTo(a.Count); });
        foreach (var h in holds)
        {
            var t = new TextBlock { FontSize = 11.5, Foreground = Dim };
            t.Inlines.Add(new Run(PlaceLabel(h) + " "));
            t.Inlines.Add(new Run(N(h.Count)) { Foreground = Text, FontWeight = FontWeights.SemiBold });
            locs.Children.Add(new Border { Background = LocBack, BorderBrush = h.Who == d.CurrentKey ? MeLine : h.Place == Place.AccountWarehouse ? AcctLine : Line, BorderThickness = new Thickness(0.8),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4), Child = t });
        }
        root.Children.Add(locs);
        if (_meter.Names.Source(item) is { Length: > 0 } src) root.Children.Add(new TextBlock { Text = string.Format(T.ItemsFrom, src), FontSize = 11, Foreground = Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        foreach (var hint in CraftHints.For(item, _meter.Recipes, _index.Total)) root.Children.Add(HintBox(hint));
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, first ? 0 : 0.8, 0, 0), Padding = new Thickness(12, 8, 12, 8), Child = root };
    }

    private FrameworkElement HintBox(CraftHint h)
    {
        var t = new TextBlock { FontSize = 11.5, Foreground = Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 17, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        var outName = Nm(h.Recipe.Output);
        var link = new Hyperlink(new Run(T.ItemsOpenCraft)) { Foreground = Blue, TextDecorations = null, Cursor = Cursors.Hand };
        link.Click += (_, _) => OpenInCraft?.Invoke(outName);
        var linkBlock = new TextBlock { FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        linkBlock.Inlines.Add(link);
        t.Inlines.Add(new Run(T.ItemsUsedIn + " "));
        t.Inlines.Add(new Run(outName) { Foreground = ItemTile.TextOf(_meter.Grades.Of(h.Recipe.Output)) });
        t.Inlines.Add(new Run($" {h.Recipe.Profession} {h.Recipe.Level}") { Foreground = Mute });
        t.Inlines.Add(new LineBreak());
        t.Inlines.Add(new Run(string.Format(T.ItemsNeeds, N(h.Need), N(h.Have))));
        if (h.Have >= h.Need) t.Inlines.Add(new Run(" ✓") { Foreground = Green });
        if (h.Missing.Length > 0)
        {
            t.Inlines.Add(new LineBreak());
            var miss = string.Join(" · ", h.Missing.Take(4).Select(m => $"{Nm(m.Item)} {N(m.Have)}/{m.Need}")) + (h.Missing.Length > 4 ? " " + string.Format(T.ItemsMore, h.Missing.Length - 4) : "");
            t.Inlines.Add(new Run(string.Format(T.ItemsMissing, miss)) { Foreground = Amber });
        }
        else { t.Inlines.Add(new Run(" · ")); t.Inlines.Add(new Run(T.ItemsAllMats) { Foreground = Green }); }
        var dock = new DockPanel();
        DockPanel.SetDock(linkBlock, Dock.Right);
        dock.Children.Add(linkBlock);
        dock.Children.Add(t);
        return new Border { Background = HintBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 0), Child = dock };
    }

    // ------------------------------------------------------------------ footer + legend

    private static readonly int[] FootCurrencies = [InventoryStore.Kinah, InventoryStore.KinahBound, InventoryStore.AbyssPoints, InventoryStore.EnhanceStone];
    private static readonly string[] FootCurrencyFallback = ["Kina", "Kina (Bound)", "Abyss Points", "Enhance Stone"];
    // Character · Kina · Kina (Bound) · AP · Enhance · Odyle · Shugo · Pass · Event · Seal · Value · Seen: readable at the overlay's 900–1100 px
    private static readonly double[] FootWidths = [1.5, 1.25, 1.15, 1, 0.9, 0.95, 0.65, 0.6, 0.6, 0.8, 0.9, 0.8];
    private const int FootCols = 12, ColBound = 2, ColEvent = 8;

    /// <summary>One row per character: currencies (bag stacks) and its content tickets (A2M-15, live for the bound one, character.json otherwise),
    /// then the account warehouse. Below 820 px Kina (Bound) and Event pass drop out; raids and corridors are the name's tooltip.</summary>
    private void RenderFoot(Data d)
    {
        var g = FootGrid;
        g.Children.Clear(); g.RowDefinitions.Clear(); g.ColumnDefinitions.Clear();
        var narrow = _narrow;
        for (var i = 0; i < FootCols; i++)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = narrow && i is ColBound or ColEvent ? new GridLength(0) : new GridLength(FootWidths[i], GridUnitType.Star) });
        var row = 0;
        void Add(int col, string text, Brush fg, bool head = false, bool left = false, string? tip = null)
        {
            if (narrow && col is ColBound or ColEvent) return;
            var t = new TextBlock { Text = text, Foreground = fg, FontSize = head ? 11 : 12.5, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Center };
            if (!string.IsNullOrEmpty(tip)) t.ToolTip = tip;
            Grid.SetRow(t, row); Grid.SetColumn(t, col); g.Children.Add(t);
        }
        void Rule()
        {
            var r = new Border { BorderBrush = FootLine, BorderThickness = new Thickness(0, 0.8, 0, 0) };
            Grid.SetRow(r, row); Grid.SetColumnSpan(r, FootCols); g.Children.Add(r);
        }
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(0, T.ItemsFootChar, Mute, true, true);
        string Cur(int i) => _meter.Names.Name(FootCurrencies[i], _lang) ?? FootCurrencyFallback[i];
        Add(1, Cur(0), Mute, true); Add(2, Cur(1), Mute, true);
        Add(3, T.ItemsFootAp, Mute, true, tip: Cur(2)); Add(4, T.ItemsFootEnhance, Mute, true, tip: Cur(3));
        Add(5, T.ItemsTkOdyle, Mute, true, tip: T.ItemsTkOdyleTip); Add(6, T.ItemsTkKeys, Mute, true, tip: T.ItemsTkKeysTip);
        Add(7, T.ItemsTkPass, Mute, true, tip: T.ItemsTkPassTip); Add(8, T.ItemsTkEvent, Mute, true, tip: T.ItemsTkEventTip);
        Add(9, T.ItemsTkSeal, Mute, true, tip: T.ItemsTkSealTip);
        Add(10, T.ItemsFootValueShort, Mute, true, tip: T.ItemsFootValue); Add(11, T.ItemsFootSeen, Mute, true, tip: T.ItemsTkSeenTip);
        long Value(List<ItemStack> l) { long v = 0; foreach (var s in l) if (!InventoryStore.IsCurrency(s.Item)) v += (Price(s.Item) ?? 0) * s.Count; return v; }
        var book = _meter.AchievementBook;
        foreach (var c in d.Chars)
        {
            row++; g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Rule();
            var me = c.Key == d.CurrentKey;
            var tk = TicketsOverview.Of(c.Progress, book, _lang);
            var nameBlock = new TextBlock { Margin = new Thickness(6, 3, 6, 3), FontSize = 12.5, Foreground = me ? Text : Dim, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            nameBlock.Inlines.Add(new Run(NameOf(c.Key)));
            if (c.Cls.Length > 0) nameBlock.Inlines.Add(new Run(" " + c.Cls) { Foreground = Mute, FontSize = 11 });
            var tip = tk.RaidsTip.Length > 0 ? tk.RaidsTip : "";
            if (tk.CorridorsTip.Length > 0) tip = tip.Length > 0 ? tip + "\n" + tk.CorridorsTip : tk.CorridorsTip;
            if (tip.Length > 0) nameBlock.ToolTip = tip;
            Grid.SetRow(nameBlock, row); g.Children.Add(nameBlock);

            var bag = c.File?.Items(Containers.Bag);
            for (var i = 0; i < FootCurrencies.Length; i++)
            {
                long n = 0;
                if (bag is not null) foreach (var s in bag) if (s.Item == FootCurrencies[i]) n += s.Count;
                Add(1 + i, n > 0 ? N(n) : "—", n > 0 ? Num : Dim);
            }
            Brush V(string s) => s == TicketsOverview.None ? Dim : Num;
            Add(5, tk.Odyle, V(tk.Odyle)); Add(6, tk.Keys, V(tk.Keys));
            Add(7, tk.DaevaPass, V(tk.DaevaPass), tip: tk.PassTip); Add(8, tk.EventPass, V(tk.EventPass), tip: tk.PassTip);
            Add(9, tk.Seal, V(tk.Seal));
            if (c.File is not { } f) { Add(10, "—", Dim); Add(11, tk.SeenMs > 0 ? When(tk.SeenMs) : T.ItemsFootNever, Dim); continue; }
            var v = Value(bag!) + Value(f.Items(Containers.CharacterWarehouse));
            Add(10, v > 0 ? ItemTile.Short(v) : "—", me ? Text : Dim);
            Add(11, SeenOf(f, tk.SeenMs), me ? Text : Dim);
        }
        if (d.Account is { } a)
        {
            row++; g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Rule();
            Add(0, T.ItemsAcctWh, Dim, left: true);
            long kina = 0;
            foreach (var s in a.Slots) if (s.Item == InventoryStore.Kinah) kina += s.Count;
            Add(1, kina > 0 ? N(kina) : "—", kina > 0 ? Num : Dim);
            for (var i = 2; i < 10; i++) Add(i, "—", Dim);
            var v = Value(AccountStacks(a));
            Add(10, v > 0 ? ItemTile.Short(v) : "—", Dim);
            var (_, at) = a.LastUpdate();
            Add(11, at > 0 ? When(at) : T.ItemsFootNever, Dim);
        }
        FootCaption.Text = string.Format(T.ItemsCaption, MarketLabel(d.Market));
    }

    private void BuildLegend()
    {
        Legend.Children.Clear();
        foreach (var g in new[] { Grade.Common, Grade.Rare, Grade.Legendary, Grade.Unique, Grade.Epic, Grade.Special, Grade.Unknown })
            Legend.Children.Add(LegendItem(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1.5), BorderBrush = ItemTile.LineOf(g), Margin = new Thickness(0, 0, 5, 0) },
                g == Grade.Unknown ? T.ItemsLegendUnknown : ItemGrades.Name(g)));
        Legend.Children.Add(LegendItem(new TextBlock { Text = "+10", Foreground = Num, FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 0, 5, 0) }, T.ItemsLegendEnchant));
        Legend.Children.Add(LegendItem(new TextBlock { Text = "●", Foreground = Blue, FontSize = 11, Margin = new Thickness(0, 0, 5, 0) }, T.ItemsLegendDot));
    }

    private static FrameworkElement LegendItem(FrameworkElement mark, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        mark.VerticalAlignment = VerticalAlignment.Center;
        p.Children.Add(mark);
        p.Children.Add(new TextBlock { Text = text, Foreground = Mute, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    // ------------------------------------------------------------------ layout + controls

    /// <summary>Below 820 px the Across-characters panel moves under the cards (the card can be narrow when docked).</summary>
    private void ApplyWidth()
    {
        var narrow = ActualWidth is > 0 and < 820;
        if (narrow == _narrow) return;
        _narrow = narrow;
        SideCol.Width = new GridLength(narrow ? 0 : 330);
        GapCol.Width = new GridLength(narrow ? 0 : 10);
        SideRow.Height = narrow ? new GridLength(0.8, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(Side, narrow ? 0 : 2);
        Grid.SetRow(Side, narrow ? 1 : 0);
        Side.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
        if (_data is { } d) RenderFoot(d);
    }

    private void MainList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 12 px card padding each side, 2 px of frame, ~18 px for the scroll bar
        var cols = Math.Max(1, (int)((e.NewSize.Width - 24 - 2 - 18 + TileGap) / (Tile + TileGap)));
        if (cols == _columns) return;
        _columns = cols;
        Render();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        ClearSearch.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTick.Stop(); _searchTick.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; SearchBox.Focus(); }

    private void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; } }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || LangBox.SelectedItem is not ComboBoxItem { Tag: string lang }) return;
        _lang = lang;
        if (!_sample && _meter.Settings.PriceSearchLanguage != lang) { _meter.Settings.PriceSearchLanguage = lang; _meter.Settings.Save(); }
        BuildLegend();
        Render();
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SortBox.SelectedItem is not ComboBoxItem { Tag: ItemSortMode mode }) return;
        _sort = mode;
        Render();
    }

    private void Group_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string g }) return;
        _group = g;
        if (IsLoaded || _sample) Render();
    }

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp("items");
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
