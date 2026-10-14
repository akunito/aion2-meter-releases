using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AionMeter.Core.GameDb;
using AionMeter.Core.Market;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// Prices tab (WPF PriceWatchView, A2M-16/23) on Linux: the watch list (state/price-watch.json, the UI's own file: edits are saved atomically
/// and the daemon reloads it by mtime), add with an EN/ES name search, thresholds, holdings, the expandable 24h/7d/30d chart and the
/// browser export scripts. The daemon polls aion2.exchange; Check now sends it `price-poll` and re-reads the files on its `price-polled`.
/// </summary>
public sealed class PricesView : HostedView
{
    private static UiText T => UiText.Current;
    private static readonly IBrush Up = Visuals.Solid(0xFFF67A6C), Down = Visuals.Solid(0xFF8EE08A), Flat = Visuals.Solid(0xFF9AA0B0), HitFrame = Visuals.Solid(0xFF5EB85A),
        Frame = Visuals.Solid(0xFF3A4055), RowFill = Visuals.Solid(0x0DFFFFFF), SmallFg = Visuals.Solid(0xFFF3D58F), SmallBg = Visuals.Solid(0x99111726),
        SmallHover = Visuals.Solid(0xCC1A2235), Mute = Visuals.Solid(0xFF6B7590), ResultsBg = Visuals.Solid(0xFF1A1F2E);
    private static readonly Cursor Hand = new(StandardCursorType.Hand);
    /// <summary>The daemon's manual cooldown as its last `price-polled` said (process-wide: switching tabs keeps it, as WPF's service field).</summary>
    private static DateTimeOffset _manualUntil = DateTimeOffset.MinValue;

    private readonly DataWatch _watch;
    private readonly StackPanel _list, _results;
    private readonly ScrollViewer _resultsScroll, _listScroll;
    private readonly TextBlock _status, _searchHint, _addHint;
    private readonly TextBox _search, _itemBox, _maxBox;
    private readonly CheckBox _sellBox;
    private readonly Border _langEn, _langEs;
    private readonly Button _check;
    private readonly DispatcherTimer _cooldown = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _expanded;
    private bool _shown, _polling;
    private (DateTime Stamp, PriceHistory? History) _history;

    public PricesView(AppShell shell) : base(shell, "prices", UiText.Current.PricesTitle)
    {
        _watch = DataWatch.Of(shell);
        _status = new TextBlock { FontSize = 12, Foreground = Visuals.TextMute, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(18, 0, 18, 4) };
        var hint = new TextBlock { Margin = new Thickness(18, 0, 18, 10), TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Visuals.TextMute, Text = T.PricesHint };

        // margin on the content, not ScrollViewer.Padding: with the padding the rows measured wider than the viewport (right column clipped, as FightView)
        _list = new StackPanel { [Grid.IsSharedSizeScopeProperty] = true, Margin = new Thickness(14, 0, 14, 6) };
        _listScroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        // search (EN/ES) → results → item id + max + sell + Add
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal };
        _search = UiKit.Input("", 300);
        _search.Watermark = T.PricesSearch;
        UiKit.Tip(_search, T.PricesSearch);
        _search.TextChanged += (_, _) => RunSearch();
        _search.KeyDown += SearchKey;
        searchRow.Children.Add(_search);
        _langEn = LangToggle("EN", "en");
        _langEn.Margin = new Thickness(8, 0, 0, 0);
        _langEs = LangToggle("ES", "es");
        searchRow.Children.Add(_langEn);
        searchRow.Children.Add(_langEs);
        _searchHint = new TextBlock { Foreground = Visuals.TextMute, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        searchRow.Children.Add(_searchHint);
        _results = new StackPanel();
        _resultsScroll = new ScrollViewer { Content = _results, MaxHeight = 170, IsVisible = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var resultsBox = new Border { Background = ResultsBg, BorderBrush = Visuals.GoldDim, BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0), Child = _resultsScroll };
        resultsBox.Bind(IsVisibleProperty, _resultsScroll.GetObservable(IsVisibleProperty));
        var searchBlock = new StackPanel { Margin = new Thickness(18, 4, 18, 0), Children = { searchRow, resultsBox } };

        var addRow = new WrapPanel { Margin = new Thickness(18, 6, 18, 4) };
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 4) };
        _itemBox = UiKit.Input("", 130);
        _itemBox.Watermark = T.PricesItemId;
        UiKit.Tip(_itemBox, T.PricesItemId);
        _maxBox = UiKit.Input("", 130);
        _maxBox.Watermark = T.PricesMax;
        _maxBox.Margin = new Thickness(8, 0, 0, 0);
        UiKit.Tip(_maxBox, T.PricesMax);
        _itemBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Add(); };
        _maxBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Add(); };
        _sellBox = UiKit.Check(T.PricesSell, false);
        _sellBox.VerticalAlignment = VerticalAlignment.Center;
        _sellBox.Margin = new Thickness(10, 0, 0, 0);
        add.Children.Add(_itemBox);
        add.Children.Add(_maxBox);
        add.Children.Add(_sellBox);
        var addBtn = Small(T.PricesAdd, Add);
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        add.Children.Add(addBtn);
        _addHint = new TextBlock { Foreground = Visuals.TextMute, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        add.Children.Add(_addHint);
        addRow.Children.Add(add);

        // the three actions in their own row, bottom-right; they wrap inside the row when the card is narrow (Diego, 2026-10-10)
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 0, 18, 12) };
        var copyMarket = Small(T.PricesCopyMarket, () => _ = CopyScript("aion2exchange-export.js", null));
        copyMarket.Margin = new Thickness(0, 0, 6, 4);
        UiKit.Tip(copyMarket, T.PricesCopyTip);
        var copyItems = Small(T.PricesCopyItems, CopyItems);
        copyItems.Margin = new Thickness(0, 0, 6, 4);
        UiKit.Tip(copyItems, T.PricesCopyTip);
        _check = Small(T.PricesCheckNow, CheckNow);
        _check.MinWidth = 110;
        _check.Margin = new Thickness(0, 0, 0, 4);
        actions.Children.Add(copyMarket);
        actions.Children.Add(copyItems);
        actions.Children.Add(_check);

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto") };
        body.Place(_status, 0, 0);
        body.Place(hint, 1, 0);
        body.Place(_listScroll, 2, 0);
        body.Place(searchBlock, 3, 0);
        body.Place(addRow, 4, 0);
        body.Place(actions, 5, 0);
        SetBody(body);

        SyncLang();
        _cooldown.Tick += (_, _) => CooldownTick();
        shell.Daemon.PricePolled += OnPolled;
        shell.Daemon.ConnectionChanged += _ => { if (_shown) CooldownTick(); };
        shell.Icons.Loaded += () => { if (_shown) Reload(); };
        _watch.Changed += c => { if (_shown && (c & DataChange.Prices) != 0) { Shell.Engine?.Prices.ReloadIfChanged(); Reload(); } };
        shell.WhenEngine(e =>
        {
            e.Prices.Changed += () => Dispatcher.UIThread.Post(() => { if (_shown) Reload(); });
            e.Names.Changed += () => Dispatcher.UIThread.Post(() => { if (_shown) Reload(); });
            if (_shown) Reload();
        });
    }

    public override void Shown()
    {
        _shown = true;
        _watch.Acquire();
        Shell.Engine?.Prices.ReloadIfChanged();
        Reload();
        _cooldown.Start();
        CooldownTick();
        if (!_argsApplied) Shell.WhenEngine(_ => ApplyViewArgs());
    }

    private bool _argsApplied;
    private static bool _checkArgUsed;   // per instance: --popout makes a second view after the card's

    /// <summary>--view expand=&lt;item id&gt; (0 = the first watch), search=…, add=&lt;id&gt;:&lt;max&gt; (the Add button's path), check=1 (Check now once the daemon is connected) for screenshots.</summary>
    private void ApplyViewArgs()
    {
        if (_argsApplied) return;
        _argsApplied = true;
        var a = App.ViewArgs;
        if (a.TryGetValue("expand", out var ex) && int.TryParse(ex, out var id))
        {
            var all = Shell.Engine!.Prices.All();
            _expanded = id == 0 && all.Count > 0 ? all[0].Item : id;
        }
        if (a.TryGetValue("search", out var q)) _search.Text = q;
        if (a.TryGetValue("add", out var add) && add.Split(':') is [var id2, var max2] && Shell.Engine!.Prices.All().TrueForAll(x => x.Item.ToString(CultureInfo.InvariantCulture) != id2))
        {
            _itemBox.Text = id2; _maxBox.Text = max2;
            Add();
        }
        if (a.TryGetValue("check", out var c) && c == "1") DispatcherTimer.RunOnce(() => { if (_shown && !_checkArgUsed) { _checkArgUsed = true; CheckNow(); } }, TimeSpan.FromSeconds(3));
        Reload();
    }

    public override void Hidden()
    {
        _shown = false;
        _watch.Release();
        _cooldown.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.PricesTitle;
        if (_shown) Reload();
    }

    private string Lang => Shell.Settings.ResolvedNamesLanguage;

    /// <summary>price-history.json, read again when the daemon rewrote it (the engine's copy is a start-up snapshot).</summary>
    private PriceHistory History(MeterEngine m)
    {
        var path = Path.Combine(AppSettings.AppDataDir, "state", "price-history.json");
        DateTime w = default;
        try { if (File.Exists(path)) w = File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (_history.History is null || _history.Stamp != w) _history = (w, w == default ? m.Prices.History : new PriceHistory(path));
        return _history.History;
    }

    // ── rows ─────────────────────────────────────────────────────────────────────────────────────

    private void Reload()
    {
        if (Shell.Engine is not { } m) { _status.Text = "…"; return; }
        var watches = m.Prices.All();
        _list.Children.Clear();
        Control? open = null;
        DateTimeOffset? last = null;
        foreach (var w in watches)
        {
            if (w.LastCheck is { } c && (last is null || c > last)) last = c;
            var row = RowView(m, w);
            if (w.Item == _expanded) open = row;
            _list.Children.Add(row);
        }
        // the daemon's LastPollAt lives in its memory: the newest read of any watch is the same clock
        _status.Text = last is { } l ? string.Format(T.PricesLastPoll, l.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)) + " · " + T.PricesHourlyNote : T.PricesNotChecked;
        if (open is not null) Dispatcher.UIThread.Post(() => open.BringIntoView(), DispatcherPriority.Loaded);
    }

    private Control RowView(MeterEngine m, PriceWatch w)
    {
        var (en, es) = m.Names.Both(w.Item);
        // English name on the row (old watches stored "en · es" in Name: keep the part before the dot), Spanish only on hover (Diego, 2026-10-10)
        var catalog = en ?? m.Items.Cached(w.Item)?.Name ?? "";
        var name = catalog.Length > 0 ? catalog : (w.Name.IndexOf(" · ", StringComparison.Ordinal) is var cut && cut > 0 ? w.Name[..cut] : w.Name);
        var grade = m.Names.GradeOf(w.Item);
        if (grade == Grade.Unknown) grade = m.Grades.Of(w.Item);
        var expanded = w.Item == _expanded;

        // Two clocks: the site's sample (what the price really is as of) and our read of it. They differ by up to an hour.
        var when = w.LastCheck is { } c ? string.Format(T.PricesChecked, c.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)) : T.PricesNotChecked;
        var sample = w.SampledAt is { } sa ? string.Format(T.PricesSampled, sa.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)) + " · " : "";
        var sub = w.LastError is null ? $"#{w.Item} · {sample}{when}" : $"#{w.Item} · {sample}{when} · {w.LastError}";

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions() };
        g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 100 });
        foreach (var group in new[] { "Hold", "", "Max", "Price", "Trend", "Count", "", "" })
            g.ColumnDefinitions.Add(group == "" ? new ColumnDefinition(GridLength.Auto) : new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = group });
        g.ColumnDefinitions[2].Width = new GridLength(44);

        // name column: chevron, icon, name + grade chip (the name trims, the chip stays), sub line
        var left = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        var chevron = Visuals.Icon(Visuals.IconChevron, Visuals.TextMute, 9, 1.6);
        chevron.Margin = new Thickness(0, 0, 8, 0);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        if (!expanded) chevron.RenderTransform = new RotateTransform(-90);
        DockPanel.SetDock(chevron, Dock.Left);
        left.Children.Add(UiKit.Tip(chevron, T.PricesExpandTip));
        var icon = new Image { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Source = Shell.Icons.Item(w.Item) };
        DockPanel.SetDock(icon, Dock.Left);
        left.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
        if (ItemKit.GradeChip(grade, "en") is { } chip) { DockPanel.SetDock(chip, Dock.Right); nameLine.Children.Add(chip); }
        var nameText = new TextBlock { MinWidth = 60, Text = name, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = Visuals.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        // full name (it trims on a narrow card) with its grade, then the Spanish name with the Spanish grade word
        var altGrade = ItemGrades.Word(grade, "es");
        UiKit.Tip(nameText, (grade != Grade.Unknown ? name + " · " + ItemGrades.Word(grade, "en") : name) + (es is { Length: > 0 } ? "\n" + (altGrade.Length > 0 ? es + " · " + altGrade : es) : ""));
        nameLine.Children.Add(nameText);
        names.Children.Add(nameLine);
        names.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Visuals.TextMute, TextTrimming = TextTrimming.CharacterEllipsis });
        left.Children.Add(names);
        g.Place(left, 0, 0);

        // holdings (units, paid each, value) in their own column so every control of the row sits on one line (Diego, 2026-10-11)
        var hold = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var qty = UiKit.Input(w.Quantity > 0 ? w.Quantity.ToString(CultureInfo.InvariantCulture) : "", 54);
        qty.FontSize = 11;
        UiKit.Tip(qty, T.PricesQty);
        var paid = UiKit.Input(w.PaidEach > 0 ? ItemKit.N(w.PaidEach) : "", 84);
        paid.FontSize = 11;
        paid.Margin = new Thickness(4, 0, 0, 0);
        UiKit.Tip(paid, T.PricesPaid);
        void ApplyHolding()
        {
            var q = int.TryParse((qty.Text ?? "").Trim(), out var qq) ? qq : 0;
            var p = ItemKit.TryParsePrice(paid.Text, out var pp) ? pp : 0;
            m.Prices.SetHolding(w.Item, q, p);
        }
        foreach (var box in new[] { qty, paid })
        {
            box.LostFocus += (_, _) => ApplyHolding();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyHolding(); Focus(); } };
        }
        hold.Children.Add(qty);
        hold.Children.Add(paid);
        string valueText = ""; IBrush valueBrush = Flat;
        if (w.Quantity > 0 && w.LastPrice is { } lastp)
        {
            valueText = ItemKit.N(w.Quantity * lastp);
            if (w.PaidEach > 0) { var gain = (lastp - w.PaidEach) * 100.0 / w.PaidEach; valueText += "  " + gain.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%"; valueBrush = gain > 0 ? Down : gain < 0 ? Up : Flat; }
        }
        hold.Children.Add(new TextBlock { Text = valueText, Foreground = valueBrush, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        g.Place(hold, 0, 1);

        var mode = Small(w.Sell ? "≥" : "≤", () => m.Prices.SetSell(w.Item, !w.Sell));
        mode.Width = 30; mode.Padding = new Thickness(0, 4); mode.Margin = new Thickness(6, 0, 0, 0); mode.HorizontalContentAlignment = HorizontalAlignment.Center;
        UiKit.Tip(mode, T.PricesSell);
        g.Place(mode, 0, 2);
        var max = UiKit.Input(ItemKit.N(w.MaxPrice), 100);
        max.Margin = new Thickness(4, 0);
        UiKit.Tip(max, T.PricesMax);
        void ApplyMax() { if (ItemKit.TryParsePrice(max.Text, out var v)) m.Prices.SetMax(w.Item, v); }
        max.LostFocus += (_, _) => ApplyMax();
        max.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { ApplyMax(); Focus(); } };
        g.Place(max, 0, 3);
        g.Place(new TextBlock
        {
            Text = w.LastPrice is { } lp ? ItemKit.N(lp) : "—", Foreground = w.Hit ? Down : w.LastPrice is null ? Flat : Up, FontWeight = FontWeight.SemiBold, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 0, 6, 0), MinWidth = 84, TextAlignment = TextAlignment.Right,
        }, 0, 4);
        string trend = ""; IBrush trendBrush = Flat;
        if (w.LastPrice is { } lp2 && w.DayAgoPrice is { } ago && ago > 0)
        {
            var pct = (lp2 - ago) * 100.0 / ago;
            trend = (pct > 3 ? "▲ " : pct < -3 ? "▼ " : "→ ") + pct.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
            trendBrush = pct > 3 ? Up : pct < -3 ? Down : Flat;
        }
        g.Place(new TextBlock { Text = trend, Foreground = trendBrush, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Center, Margin = new Thickness(4, 0) }, 0, 5);
        g.Place(new TextBlock { Text = w.LastListings is { } n ? n.ToString(CultureInfo.InvariantCulture) : "", Foreground = Visuals.TextMute, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 40, TextAlignment = TextAlignment.Right, Margin = new Thickness(4, 0, 6, 0) }, 0, 6);
        var openBtn = UiKit.IconButton(Visuals.IconPopOut, "aion2.exchange", () => OpenItem(m, w));
        openBtn.Margin = new Thickness(6, 0, 0, 0);
        g.Place(openBtn, 0, 7);
        var removeBtn = UiKit.IconButton(Visuals.IconClose, T.Cancel, () => m.Prices.Remove(w.Item));
        removeBtn.Margin = new Thickness(2, 0, 0, 0);
        g.Place(removeBtn, 0, 8);

        var stack = new StackPanel { Children = { g } };
        if (expanded) stack.Children.Add(Detail(m, w));
        var row = new Border { Background = RowFill, BorderBrush = w.Hit ? HitFrame : Frame, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 7), Cursor = Hand, Child = stack };
        // click anywhere on a row but its inputs, buttons and the open detail toggles it; one row open at a time
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
            for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, row); v = v.GetVisualParent())
                if (v is TextBox or Button or PriceChart || (v is Control { Tag: "detail" })) return;
            _expanded = _expanded == w.Item ? 0 : w.Item;
            Reload();
        };
        return row;
    }

    private Control Detail(MeterEngine m, PriceWatch w)
    {
        // aion2.exchange serves no per-listing data (the depth/ladder call is empty for Vaizel and most servers, and the in-game
        // broker is HTTPS NcMarketPlaceAPI the capture never sees), so the expand is the price chart alone — no "cheapest listings"
        // section that would only ever say "no snapshot" (A2M-23, 2026-10-13).
        var root = new DockPanel { Tag = "detail", Margin = new Thickness(0, 10, 0, 2), Cursor = Cursor.Default, Background = Brushes.Transparent };
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        var collecting = new TextBlock { FontSize = 11, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
        var chart = new PriceChart { Height = 176 };
        (PriceRange R, string Label, CornerRadius C)[] opts = [(PriceRange.Day, T.PricesRange24h, new CornerRadius(6, 0, 0, 6)), (PriceRange.Week, T.PricesRange7d, new CornerRadius(0)), (PriceRange.Month, T.PricesRange30d, new CornerRadius(0, 6, 6, 0))];
        var buttons = new Border[opts.Length];
        void Paint()
        {
            var now = PriceHistory.ParseRange(Shell.Settings.PricesRange);
            for (var i = 0; i < opts.Length; i++)
            {
                var on = opts[i].R == now;
                buttons[i].Background = on ? Visuals.Solid(0x55C9A45C) : Visuals.Solid(0x66111726);
                buttons[i].BorderBrush = on ? Visuals.Gold : Visuals.GoldDim;
                ((TextBlock)buttons[i].Child!).Foreground = on ? Visuals.Text : Visuals.TextDim;
            }
        }
        for (var i = 0; i < opts.Length; i++)
        {
            var (r, label, c) = opts[i];
            var b = new Border { CornerRadius = c, BorderThickness = new Thickness(1), Padding = new Thickness(10, 3), Margin = new Thickness(i == 0 ? 0 : -1, 0, 0, 0), Cursor = Hand,
                Child = new TextBlock { Text = label, FontSize = 11.5 } };
            b.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                if (PriceHistory.RangeKey(r) == Shell.Settings.PricesRange) return;
                Shell.RefreshSettings();
                Shell.Settings.PricesRange = PriceHistory.RangeKey(r);
                Shell.SaveSettings();
                Paint();
                FillChart(m, chart, collecting, w);
            };
            buttons[i] = b;
            seg.Children.Add(b);
        }
        Paint();
        DockPanel.SetDock(seg, Dock.Left);
        bar.Children.Add(seg);
        bar.Children.Add(collecting);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(chart);
        FillChart(m, chart, collecting, w);
        return root;
    }

    private void FillChart(MeterEngine m, PriceChart chart, TextBlock collecting, PriceWatch w)
    {
        var history = History(m);
        var range = PriceHistory.ParseRange(Shell.Settings.PricesRange);
        var now = DateTimeOffset.Now;
        var win = history.Window(w.Item, range, now);
        var from = now.ToUnixTimeSeconds() - PriceHistory.SpanSeconds(range);
        chart.SetData(PriceHistory.Downsample(win), PriceHistory.Stats(win), from, now.ToUnixTimeSeconds(), range, w.MaxPrice, w.Sell, T.PricesChartEmpty, T.PricesBuyBelow, T.PricesSellAbove);
        // the series does not reach back over the whole range yet: say since when it exists
        collecting.Text = history.First(w.Item) is { } first && first.ToUnixTimeSeconds() > from + 7200
            ? string.Format(T.PricesCollecting, first.ToLocalTime().ToString("d MMM HH:mm", T.Culture)) : "";
    }

    // ── add ──────────────────────────────────────────────────────────────────────────────────────

    private void Add()
    {
        if (Shell.Engine is not { } m) return;
        if (!int.TryParse((_itemBox.Text ?? "").Trim(), out var item) || item <= 0) { _addHint.Text = T.PricesItemId; return; }
        if (!ItemKit.TryParsePrice(_maxBox.Text, out var max)) { _addHint.Text = T.PricesMax; return; }
        _addHint.Text = "";
        // the viewer never asks aion2hub (its items cache is shared with the daemon): an id the catalogue lacks stays "Item n" until a poll names it
        var name = m.Names.Name(item, Lang) ?? m.Items.Cached(item)?.Name ?? $"Item {item}";
        m.Prices.Add(item, name, max);
        if (_sellBox.IsChecked == true) m.Prices.SetSell(item, true);
        _itemBox.Text = ""; _maxBox.Text = ""; _sellBox.IsChecked = false;
        // a new watch is read at once whatever the manual cooldown (WPF PollPricesAfterAdd)
        Shell.Daemon.Send("price-poll", "mode", "added");
    }

    private Border LangToggle(string text, string lang)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, FontFamily = Visuals.Number };
        var b = new Border { Child = label, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Cursor = Hand, VerticalAlignment = VerticalAlignment.Center };
        UiKit.Tip(b, T.PricesSearchLang);
        b.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || Lang == lang) return;
            Shell.RefreshSettings();
            Shell.Settings.PickNames(lang);
            Shell.SaveSettings();
            SyncLang();
            RunSearch();
            Reload();
        };
        return b;
    }

    private void SyncLang()
    {
        foreach (var (b, lang) in new[] { (_langEn, "en"), (_langEs, "es") })
        {
            var on = Lang == lang;
            b.BorderBrush = on ? Visuals.Gold : Visuals.GoldDim;
            b.Background = on ? Visuals.Solid(0x55C9A45C) : Visuals.Solid(0x66111726);
            ((TextBlock)b.Child!).Foreground = on ? Visuals.Text : Visuals.TextDim;
        }
    }

    private readonly List<(int Id, string Main, string Alt, Grade Grade)> _hits = new();

    /// <summary>Search result: name in the search language, its grade chip right after it, then "· other-language name".</summary>
    private void RunSearch()
    {
        _results.Children.Clear();
        _hits.Clear();
        var q = _search.Text ?? "";
        if (q.Trim().Length < 2 || Shell.Engine is not { } m) { _resultsScroll.IsVisible = false; _searchHint.Text = ""; return; }
        var lang = Lang;
        foreach (var (id, en, es) in m.Names.Search(q, lang, 40))
        {
            var main = lang == "es" && es.Length > 0 ? es : en;
            var alt = lang == "es" ? en : es;
            var hit = (id, main, alt.Length > 0 && alt != main ? "  ·  " + alt : "", m.Names.GradeOf(id));
            _hits.Add(hit);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = hit.main, FontSize = 12.5, Foreground = Visuals.Text });
            if (ItemKit.GradeChip(hit.Item4, lang) is { } chip) line.Children.Add(chip);
            line.Children.Add(new TextBlock { Text = hit.Item3, FontSize = 12.5, Foreground = Visuals.TextMute });
            var g = UiKit.Row("*,Auto", line, new TextBlock { Text = id.ToString(CultureInfo.InvariantCulture), FontSize = 12.5, Foreground = Visuals.TextMute, Margin = new Thickness(12, 0, 4, 0) });
            var row = new Border { Padding = new Thickness(6, 2), Background = Brushes.Transparent, Cursor = Hand, Child = g };
            row.PointerEntered += (_, _) => row.Background = Visuals.HoverFill;
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            var pick = hit;
            row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Pick(pick.id, pick.main, pick.Item3, pick.Item4); };
            _results.Children.Add(row);
        }
        _resultsScroll.IsVisible = _hits.Count > 0;
        _searchHint.Text = _hits.Count == 0 ? "0" : _hits.Count >= 40 ? "40+" : _hits.Count.ToString(CultureInfo.InvariantCulture);
    }

    private void SearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && _hits.Count > 0) { var h = _hits[0]; Pick(h.Id, h.Main, h.Alt, h.Grade); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Escape) _search.Text = "";
    }

    private void Pick(int id, string main, string alt, Grade grade)
    {
        _itemBox.Text = id.ToString(CultureInfo.InvariantCulture);
        _addHint.Text = grade != Grade.Unknown ? main + " [" + ItemGrades.Word(grade, Lang) + "]" + alt : main + alt;
        _resultsScroll.IsVisible = false;
        _maxBox.Focus();
    }

    // ── actions ──────────────────────────────────────────────────────────────────────────────────

    private void OpenItem(MeterEngine m, PriceWatch w)
    {
        var id = w.Item.ToString(CultureInfo.InvariantCulture);
        var url = Shell.Settings.PriceItemUrl.Replace("{id}", id).Replace("{server}", m.Prices.ExchangeServerId.ToString(CultureInfo.InvariantCulture));
        if (!url.Contains(id)) _ = SetClipboard(m.Names.Name(w.Item) ?? w.Name);   // no item route known: the name goes to the clipboard for the site's search box
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { Log.Info($"xdg-open {url}: {e.Message}"); }
    }

    private void CopyItems()
    {
        if (Shell.Engine is not { } m) return;
        var watches = m.Prices.All();
        var ids = new string[watches.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = watches[i].Item.ToString(CultureInfo.InvariantCulture);
        var list = string.Join(", ", ids);
        _ = CopyScript("aion2exchange-item-servers.js", src => System.Text.RegularExpressions.Regex.Replace(src, @"const ITEMS = \[[^\]]*\];", $"const ITEMS = [{list}];"));
    }

    /// <summary>The browser-console export scripts ride along in data/gamedb; the watched-items one gets the current ids spliced in.</summary>
    private async Task CopyScript(string file, Func<string, string>? edit)
    {
        try
        {
            var src = await File.ReadAllTextAsync(Path.Combine(MeterEngine.DataDirectory, "gamedb", file));
            if (await SetClipboard(edit is null ? src : edit(src))) _status.Text = T.PricesCopyHint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; Log.Error("Copy script", ex); }
    }

    private async Task<bool> SetClipboard(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return false;
        await cb.SetTextAsync(text);
        return true;
    }

    // The button shows the remaining cooldown (m:ss) and stays disabled until it ends; the deadline is the daemon's, from its last answer.
    private void CheckNow()
    {
        if (!Shell.Daemon.Connected) { _status.Text = T.PricesNoDaemon; return; }
        _polling = true;
        Shell.Daemon.Send("price-poll");
        CooldownTick();
    }

    private void OnPolled(PricePollReply r)
    {
        _polling = false;
        _manualUntil = r.Next > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.Next) : DateTimeOffset.MinValue;
        if (Shell.Engine is { } m) { m.Prices.ReloadIfChanged(); _history = default; }
        if (_shown)
        {
            Reload();
            if (!r.Ran && r.Reason != "cooldown") _status.Text = string.Format(T.PricesPollSkipped, r.Reason);
            CooldownTick();
        }
    }

    private void CooldownTick()
    {
        if (_polling) { _check.IsEnabled = false; _check.Content = T.PricesChecking; return; }
        var left = _manualUntil - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) { _check.IsEnabled = Shell.Daemon.Connected; _check.Content = T.PricesCheckNow; ToolTip.SetTip(_check, Shell.Daemon.Connected ? null : T.PricesNoDaemon); return; }
        _check.IsEnabled = false;
        _check.Content = $"{T.PricesCheckNow} ({(int)left.TotalMinutes}:{left.Seconds:00})";
    }

    /// <summary>WPF "Small": gold text, gold hairline on a dark fill, brighter under the mouse.</summary>
    private static Button Small(string text, Action click)
    {
        var b = new Button
        {
            Content = text, Foreground = SmallFg, Background = SmallBg, BorderBrush = Visuals.Gold, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4), FontSize = 12.5, Cursor = Hand, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
        };
        b.PointerEntered += (_, _) => { b.BorderBrush = Visuals.Solid(0xFFFFE3A6); };
        b.PointerExited += (_, _) => { b.BorderBrush = Visuals.Gold; };
        b.Click += (_, _) => click();
        return b;
    }
}
