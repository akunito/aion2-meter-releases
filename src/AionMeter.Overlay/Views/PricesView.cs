using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AionMeter.Core.GameDb;
using AionMeter.Core.Market;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Prices tab (WPF PriceWatchView, A2M-16/23) on the layer-shell card (A2M-50, port of the Avalonia PricesView, behaviour unchanged): the watch
/// list (state/price-watch.json, the UI's own file: edits are saved atomically and the daemon reloads it by mtime), add with an EN/ES name
/// search, thresholds, holdings, the expandable 24h/7d/30d chart and the browser export scripts. The daemon polls aion2.exchange; Check now
/// sends it `price-poll` and re-reads the files on its `price-polled`. Layout for the 560 px card: each watch is two lines (Avalonia: one
/// grid row), Check now sits in the header (the three actions did not fit one row in Spanish), the add hint goes under the add row.
/// </summary>
internal sealed class PricesView : HostedView
{
    private static UiText T => UiText.Current;
    private static readonly Icon ChevronRight = new("M6 3 L11 8 L6 13");
    /// <summary>The daemon's manual cooldown as its last `price-polled` said (process-wide: switching tabs and card rebuilds keep it, as WPF's service field).</summary>
    private static DateTimeOffset _manualUntil = DateTimeOffset.MinValue;

    private readonly DataWatch _watch;
    private readonly UiTimer _cooldown = new(TimeSpan.FromSeconds(1));
    private readonly Gtk.Button _check;
    private Gtk.Box _list = null!, _results = null!, _resultsBox = null!;
    private Gtk.ScrolledWindow _listScroll = null!;
    private Gtk.Label _status = null!, _searchHint = null!, _addHint = null!;
    private Gtk.Entry _search = null!, _itemBox = null!, _maxBox = null!;
    private Gtk.CheckButton _sellBox = null!;
    private Gtk.Button _langEn = null!, _langEs = null!;
    private int _expanded;
    private bool _polling, _reloadQueued, _detached;
    private uint _scrollSource;
    private (DateTime Stamp, PriceHistory? History) _history;
    private readonly List<(int Id, string Main, string Alt, Grade Grade)> _hits = new();
    /// <summary>Row icons not on disk yet: filled in place on IconProvider.Loaded instead of rebuilding the list.</summary>
    private readonly List<(Gtk.Image Image, int Item)> _pendingIcons = new();

    public PricesView(AppShell shell) : base(shell, "prices", UiText.Current.PricesTitle)
    {
        _watch = DataWatch.Of(shell);
        PricesKit.EnsureCss();
        _check = PricesKit.Small(T.PricesCheckNow, CheckNow);
        _check.SetSizeRequest(UiKit.Px(110), -1);
        HeaderRight.Append(_check);
        Build();
        _cooldown.Tick += CooldownTick;
        OnDetach(() => { _detached = true; UiLoop.Cancel(ref _scrollSource); });
        Listen(() => Shell.Daemon.PricePolled += OnPolled, () => Shell.Daemon.PricePolled -= OnPolled);
        Listen(() => Shell.Daemon.ConnectionChanged += OnConnection, () => Shell.Daemon.ConnectionChanged -= OnConnection);
        Listen(() => Shell.Icons.Loaded += OnIcons, () => Shell.Icons.Loaded -= OnIcons);
        Listen(() => _watch.Changed += OnData, () => _watch.Changed -= OnData);
        Shell.WhenEngine(e =>
        {
            if (_detached) return;
            var names = e.Names;
            Listen(() => { e.Prices.Changed += OnStore; names.Changed += OnStore; }, () => { e.Prices.Changed -= OnStore; names.Changed -= OnStore; });
            if (IsShown) Reload();
        });
    }

    public override void Shown()
    {
        _watch.Acquire();
        Shell.Engine?.Prices.ReloadIfChanged();
        Reload();
        _cooldown.Start();
        CooldownTick();
    }

    public override void Hidden()
    {
        _watch.Release();
        _cooldown.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        // names language only (Settings → Names, the EN/ES switch here): keep the widgets and the search text
        if (_builtFor == T.Code) { SyncLang(); RunSearch(); if (IsShown) Reload(); return; }
        Title = T.PricesTitle;
        Build();
        if (IsShown) Reload();
        CooldownTick();
    }

    private string Lang => Shell.Settings.ResolvedNamesLanguage;
    private string _builtFor = "";

    // engine events may come from any thread; one rebuild per loop turn however many arrive
    private void OnStore() => UiLoop.Post(QueueReload);

    private void QueueReload()
    {
        if (_reloadQueued || _detached || !IsShown) return;
        _reloadQueued = true;
        UiLoop.Post(() => { _reloadQueued = false; if (!_detached && IsShown) Reload(); });
    }

    private void OnConnection(bool _) { if (IsShown) CooldownTick(); }

    private void OnData(DataChange c)
    {
        if (!IsShown || (c & DataChange.Prices) == 0) return;
        Shell.Engine?.Prices.ReloadIfChanged();
        QueueReload();
    }

    private void OnIcons()
    {
        for (var i = _pendingIcons.Count - 1; i >= 0; i--)
        {
            var (img, item) = _pendingIcons[i];
            if (Shell.Icons.Item(item) is not { } tex) continue;
            img.SetFromPaintable(tex);
            _pendingIcons.RemoveAt(i);
        }
    }

    private void Build()
    {
        _builtFor = T.Code;
        var body = UiKit.VBox();
        _status = UiKit.Line("", 12, Fg.Mute);
        UiKit.Margin(_status, 18, 0, 18, 4);
        body.Append(_status);
        var hint = UiKit.Text(T.PricesHint, 11.5, Fg.Mute);
        UiKit.Margin(hint, 18, 0, 18, 10);
        body.Append(hint);

        _list = UiKit.VBox();
        UiKit.Margin(_list, 14, 0, 14, 6);
        _listScroll = UiKit.Scroll(_list, false);
        body.Append(_listScroll);

        // search (EN/ES) → results → item id + max + sell + Add
        var searchRow = UiKit.HBox();
        var (host, search) = ItemKit.SearchBox(T.PricesSearch, 300, RunSearch);
        _search = search;
        _search.Hexpand = host.Hexpand = false;
        UiKit.Tip(_search, T.PricesSearch);
        _search.OnActivate += (_, _) => { if (_hits.Count > 0) { var h = _hits[0]; Pick(h.Id, h.Main, h.Alt, h.Grade); } };
        searchRow.Append(host);
        _langEn = PricesKit.Toggle("EN", "k-prices-lang", () => SetLang("en"));
        _langEn.MarginStart = UiKit.Px(2);
        _langEs = PricesKit.Toggle("ES", "k-prices-lang", () => SetLang("es"));
        UiKit.Tip(_langEn, T.PricesSearchLang);
        UiKit.Tip(_langEs, T.PricesSearchLang);
        searchRow.Append(_langEn);
        searchRow.Append(_langEs);
        _searchHint = UiKit.Line("", 11.5, Fg.Mute);
        _searchHint.MarginStart = UiKit.Px(12);
        searchRow.Append(_searchHint);
        _results = UiKit.VBox();
        var resultsScroll = Gtk.ScrolledWindow.New();
        resultsScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        resultsScroll.MaxContentHeight = UiKit.Px(170);
        resultsScroll.PropagateNaturalHeight = true;
        resultsScroll.Child = _results;
        _resultsBox = UiKit.VBox();
        _resultsBox.AddCssClass("k-prices-results");
        _resultsBox.Append(resultsScroll);
        _resultsBox.Visible = false;
        var searchBlock = UiKit.VBox();
        UiKit.Margin(searchBlock, 18, 4, 18, 0);
        searchBlock.Append(searchRow);
        searchBlock.Append(_resultsBox);
        body.Append(searchBlock);

        var add = UiKit.HBox();
        UiKit.Margin(add, 18, 6, 18, 0);
        // 110 each, not Avalonia's 130: with the Spanish "vender por encima" / "Añadir" the row grew the card past 560 px (measured)
        _itemBox = UiKit.Input("", 110);
        _itemBox.PlaceholderText = T.PricesItemId;
        UiKit.Tip(_itemBox, T.PricesItemId);
        _maxBox = UiKit.Input("", 110);
        _maxBox.PlaceholderText = T.PricesMax;
        _maxBox.MarginStart = UiKit.Px(8);
        UiKit.Tip(_maxBox, T.PricesMax);
        Pin(_itemBox); Pin(_maxBox);
        _itemBox.OnActivate += (_, _) => Add();
        _maxBox.OnActivate += (_, _) => Add();
        // a plain label: UiKit.Check wraps at a 1-char natural width, which in a row squeezes it into a column
        _sellBox = Gtk.CheckButton.NewWithLabel(T.PricesSell);
        _sellBox.Valign = Gtk.Align.Center;
        _sellBox.MarginStart = UiKit.Px(10);
        add.Append(_itemBox);
        add.Append(_maxBox);
        add.Append(_sellBox);
        var addBtn = PricesKit.Small(T.PricesAdd, Add);
        addBtn.MarginStart = UiKit.Px(8);
        add.Append(addBtn);
        body.Append(add);
        // Avalonia: beside Add inside a WrapPanel; under the row here (the 560 px card has no room beside it)
        _addHint = UiKit.Text("", 11.5, Fg.Mute);
        UiKit.Margin(_addHint, 18, 4, 18, 0);
        _addHint.Visible = false;
        body.Append(_addHint);

        var actions = UiKit.HBox();
        actions.Halign = Gtk.Align.End;
        UiKit.Margin(actions, 18, 8, 18, 12);
        var copyMarket = PricesKit.Small(T.PricesCopyMarket, () => CopyScript("aion2exchange-export.js", null));
        copyMarket.MarginEnd = UiKit.Px(6);
        UiKit.Tip(copyMarket, T.PricesCopyTip);
        var copyItems = PricesKit.Small(T.PricesCopyItems, CopyItems);
        UiKit.Tip(copyItems, T.PricesCopyTip);
        actions.Append(copyMarket);
        actions.Append(copyItems);
        body.Append(actions);

        UiKit.SetText(_check, T.PricesCheckNow);
        SetBody(body);
        SyncLang();
    }

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
        if (Shell.Engine is not { } m) { _status.SetText("…"); return; }
        var watches = m.Prices.All();
        _pendingIcons.Clear();
        while (_list.GetFirstChild() is { } child) _list.Remove(child);
        // the Avalonia SharedSizeGroups: price, trend and count line up down the list
        var price = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal);
        var trend = Gtk.SizeGroup.New(Gtk.SizeGroupMode.Horizontal);
        Gtk.Widget? open = null;
        for (var i = 0; i < watches.Count; i++)
        {
            var row = RowView(m, watches[i], price, trend);
            if (watches[i].Item == _expanded) open = row;
            _list.Append(row);
        }
        // the daemon's LastPollAt lives in its memory: the newest read of any watch is the same clock
        _status.SetText(PriceRowText.Status(PriceRowText.LastCheck(watches), T));
        if (open is not null) BringIntoView(open);
    }

    /// <summary>Avalonia BringIntoView: the open row scrolled into the list's viewport once it has its allocation (the minimum scroll).</summary>
    private void BringIntoView(Gtk.Widget row)
    {
        UiLoop.Cancel(ref _scrollSource);
        _scrollSource = UiLoop.Delay(40, () =>
        {
            _scrollSource = 0;
            if (_detached || row.GetParent() is null) return;
            var adj = _listScroll.GetVadjustment();
            if (adj is null) return;
            // the bounds are the border box in _list's coordinates (TranslateCoordinates gives the content box: the row's padding stayed hidden)
            if (!row.ComputeBounds(_list, out var r)) return;
            double top = r.GetY(), bottom = top + r.GetHeight(), view = adj.GetPageSize(), now = adj.GetValue();
            if (top < now) adj.SetValue(top);
            else if (bottom > now + view) adj.SetValue(Math.Min(top, bottom - view));
        });
    }

    private Gtk.Widget RowView(MeterEngine m, PriceWatch w, Gtk.SizeGroup priceGroup, Gtk.SizeGroup trendGroup)
    {
        var (en, es) = m.Names.Both(w.Item);
        // English name on the row (old watches stored "en · es" in Name: keep the part before the dot), Spanish only on hover (Diego, 2026-10-10)
        var name = PriceRowText.DisplayName(en ?? m.Items.Cached(w.Item)?.Name, w.Name);
        var grade = m.Names.GradeOf(w.Item);
        if (grade == Grade.Unknown) grade = m.Grades.Of(w.Item);
        var expanded = w.Item == _expanded;

        var g = Gtk.Grid.New();
        // line 1: chevron, icon, name + grade chip (the name trims, the chip stays) over the sub line, price, trend, listings, open, remove
        var chevBox = UiKit.HBox();
        chevBox.Append(Icons.View(expanded ? Icons.Chevron : ChevronRight, Palette.TextMute, 9, 1.6).Widget);
        chevBox.MarginEnd = UiKit.Px(8);
        chevBox.Valign = Gtk.Align.Center;
        UiKit.Tip(chevBox, T.PricesExpandTip);
        g.Attach(chevBox, 0, 0, 1, 1);
        var tex = Shell.Icons.Item(w.Item);
        var icon = ItemKit.Image(tex, 30);
        icon.SetSizeRequest(UiKit.Px(30), UiKit.Px(30));
        icon.MarginEnd = UiKit.Px(10);
        icon.Valign = Gtk.Align.Center;
        if (tex is null) _pendingIcons.Add((icon, w.Item));
        g.Attach(icon, 1, 0, 1, 1);
        var names = UiKit.VBox();
        names.Valign = Gtk.Align.Center;
        names.Hexpand = true;
        var nameLine = UiKit.HBox();
        var nameText = UiKit.Line(name, 12.5, Fg.Text, 600);
        nameText.Hexpand = false;
        // an ellipsized label's natural width is MaxWidthChars (the kit's 1): the name showed as "…". Inside the list's Never-policy scroll a
        // child's natural width does not reach the card (the scroll asks only for its minimum), so the full natural width is safe here.
        nameText.MaxWidthChars = -1;
        // full name (it trims on a narrow card) with its grade, then the Spanish name with the Spanish grade word
        UiKit.Tip(nameText, PriceRowText.NameTip(name, es, grade));
        nameLine.Append(nameText);
        if (ItemKit.GradeChip(grade, "en") is { } chip) nameLine.Append(chip);
        names.Append(nameLine);
        names.Append(UiKit.Line(PriceRowText.Sub(w, T), 11, Fg.Mute));
        g.Attach(names, 2, 0, 1, 1);

        var priceLabel = Gtk.Label.New(w.LastPrice is { } lp ? ItemKit.N(lp) : "—");
        priceLabel.AddCssClass(KitCss.SizeClass(14));
        priceLabel.AddCssClass("k-w6");
        PricesKit.SetTone(priceLabel, PriceRowText.PriceToneOf(w));
        priceLabel.Xalign = 1;
        priceLabel.Valign = Gtk.Align.Center;
        priceLabel.SetSizeRequest(UiKit.Px(84), -1);
        UiKit.Margin(priceLabel, 10, 0, 6, 0);
        priceGroup.AddWidget(priceLabel);
        g.Attach(priceLabel, 3, 0, 1, 1);
        var (trendText, trendTone) = PriceRowText.Trend(w.LastPrice, w.DayAgoPrice);
        var trendLabel = Gtk.Label.New(trendText);
        PricesKit.SetTone(trendLabel, trendTone);
        trendLabel.Valign = Gtk.Align.Center;
        trendLabel.SetSizeRequest(UiKit.Px(64), -1);
        UiKit.Margin(trendLabel, 4, 0, 4, 0);
        trendGroup.AddWidget(trendLabel);
        g.Attach(trendLabel, 4, 0, 1, 1);
        var count = UiKit.SetFg(Gtk.Label.New(w.LastListings is { } n ? n.ToString(CultureInfo.InvariantCulture) : ""), Fg.Mute);
        count.Xalign = 1;
        count.Valign = Gtk.Align.Center;
        count.SetSizeRequest(UiKit.Px(40), -1);
        UiKit.Margin(count, 4, 0, 6, 0);
        g.Attach(count, 5, 0, 1, 1);
        var openBtn = UiKit.IconButton(Icons.PopOut, "aion2.exchange", () => OpenItem(m, w));
        openBtn.MarginStart = UiKit.Px(6);
        g.Attach(openBtn, 6, 0, 1, 1);
        var removeBtn = UiKit.IconButton(Icons.Close, T.Cancel, () => m.Prices.Remove(w.Item));
        removeBtn.MarginStart = UiKit.Px(2);
        g.Attach(removeBtn, 7, 0, 1, 1);

        // line 2, under the name: holdings (units, paid each, value), ≤/≥, max
        var line2 = UiKit.HBox();
        line2.MarginTop = UiKit.Px(6);
        var qty = UiKit.Input(w.Quantity > 0 ? w.Quantity.ToString(CultureInfo.InvariantCulture) : "", 54);
        qty.AddCssClass("k-prices-e11");
        qty.PlaceholderText = T.PricesQty;
        UiKit.Tip(qty, T.PricesQty);
        var paid = UiKit.Input(w.PaidEach > 0 ? ItemKit.N(w.PaidEach) : "", 84);
        paid.AddCssClass("k-prices-e11");
        paid.PlaceholderText = T.PricesPaid;
        paid.MarginStart = UiKit.Px(4);
        UiKit.Tip(paid, T.PricesPaid);
        // a kit entry's natural width is its placeholder's (+ the inner GtkText's expand): in a row with a spare-width label the entries grew
        // 54 → 80, 100 → 127 px (measured in the nested sway, 2026-10-13) and squeezed the holding value to "…"; pinned to the request
        Pin(qty); Pin(paid);
        void ApplyHolding()
        {
            var q = int.TryParse(qty.GetText().Trim(), out var qq) ? qq : 0;
            var p = ItemKit.TryParsePrice(paid.GetText(), out var pp) ? pp : 0;
            m.Prices.SetHolding(w.Item, q, p);
        }
        OnCommit(qty, ApplyHolding);
        OnCommit(paid, ApplyHolding);
        line2.Append(qty);
        line2.Append(paid);
        var (valueText, valueTone) = PriceRowText.Holding(w.Quantity, w.LastPrice, w.PaidEach);
        var value = PricesKit.SetTone(UiKit.Line(valueText, 11.5), valueTone);
        value.MarginStart = UiKit.Px(8);
        value.MaxWidthChars = -1;
        line2.Append(value);
        var mode = PricesKit.Small(w.Sell ? "≥" : "≤", () => m.Prices.SetSell(w.Item, !w.Sell));
        mode.AddCssClass("k-prices-mode");
        mode.MarginStart = UiKit.Px(6);
        UiKit.Tip(mode, T.PricesSell);
        line2.Append(mode);
        var max = UiKit.Input(ItemKit.N(w.MaxPrice), 100);
        max.MarginStart = UiKit.Px(4);
        Pin(max);
        UiKit.Tip(max, T.PricesMax);
        OnCommit(max, () => { if (ItemKit.TryParsePrice(max.GetText(), out var v)) m.Prices.SetMax(w.Item, v); });
        line2.Append(max);
        g.Attach(line2, 2, 1, 6, 1);

        var frame = UiKit.VBox();
        frame.AddCssClass("k-prices-row");
        if (w.Hit) frame.AddCssClass("k-prices-hit");
        frame.Append(g);
        // click anywhere on the row but its inputs and buttons toggles it (the open detail is outside the grid); one row open at a time
        g.SetCursorFromName("pointer");
        var click = Gtk.GestureClick.New();
        click.OnReleased += (gesture, e) =>
        {
            if (e.NPress != 1 || OnControl(g, g.Pick(e.X, e.Y, Gtk.PickFlags.Default))) return;
            _expanded = _expanded == w.Item ? 0 : w.Item;
            Reload();
        };
        g.AddController(click);
        if (expanded) frame.Append(Detail(m, w));
        return frame;
    }

    private static void Pin(Gtk.Entry e)
    {
        e.Hexpand = false;
        e.MaxWidthChars = 1;
    }

    private static bool OnControl(Gtk.Widget row, Gtk.Widget? hit)
    {
        for (var v = hit; v is not null && !ReferenceEquals(v, row); v = v.GetParent())
            if (v is Gtk.Entry or Gtk.Text or Gtk.Button) return true;
        return false;
    }

    /// <summary>Avalonia LostFocus + Enter: the value is applied when the entry loses the keyboard or on Enter.</summary>
    private static void OnCommit(Gtk.Entry box, Action apply)
    {
        var focus = Gtk.EventControllerFocus.New();
        focus.OnLeave += (_, _) => apply();
        box.AddController(focus);
        box.OnActivate += (_, _) => apply();
    }

    private Gtk.Widget Detail(MeterEngine m, PriceWatch w)
    {
        // aion2.exchange serves no per-listing data (the depth/ladder call is empty for Vaizel and most servers, and the in-game
        // broker is HTTPS NcMarketPlaceAPI the capture never sees), so the expand is the price chart alone — no "cheapest listings"
        // section that would only ever say "no snapshot" (A2M-23, 2026-10-13).
        var root = UiKit.VBox();
        UiKit.Margin(root, 0, 10, 0, 2);
        var bar = UiKit.HBox();
        bar.MarginBottom = UiKit.Px(6);
        var collecting = UiKit.Line("", 11, Fg.Mute);
        collecting.Xalign = 1;
        collecting.MarginStart = UiKit.Px(10);
        var chart = new PriceChart(176);
        PriceRange[] ranges = [PriceRange.Day, PriceRange.Week, PriceRange.Month];
        string[] labels = [T.PricesRange24h, T.PricesRange7d, T.PricesRange30d];
        string[] shapes = ["k-prices-segl", "k-prices-segm", "k-prices-segr"];
        var buttons = new Gtk.Button[3];
        void PaintSegments()
        {
            var now = PriceHistory.ParseRange(Shell.Settings.PricesRange);
            for (var i = 0; i < 3; i++) PricesKit.SetOn(buttons[i], ranges[i] == now);
        }
        for (var i = 0; i < 3; i++)
        {
            var r = ranges[i];
            buttons[i] = PricesKit.Toggle(labels[i], shapes[i], () =>
            {
                if (PriceHistory.RangeKey(r) == Shell.Settings.PricesRange) return;
                Shell.RefreshSettings();
                Shell.Settings.PricesRange = PriceHistory.RangeKey(r);
                Shell.SaveSettings();
                PaintSegments();
                FillChart(m, chart, collecting, w);
            });
            bar.Append(buttons[i]);
        }
        PaintSegments();
        bar.Append(collecting);
        root.Append(bar);
        root.Append(chart.Widget);
        FillChart(m, chart, collecting, w);
        return root;
    }

    private void FillChart(MeterEngine m, PriceChart chart, Gtk.Label collecting, PriceWatch w)
    {
        var history = History(m);
        var range = PriceHistory.ParseRange(Shell.Settings.PricesRange);
        var now = DateTimeOffset.Now;
        var win = history.Window(w.Item, range, now);
        var from = now.ToUnixTimeSeconds() - PriceHistory.SpanSeconds(range);
        chart.SetData(PriceHistory.Downsample(win), PriceHistory.Stats(win), from, now.ToUnixTimeSeconds(), range, w.MaxPrice, w.Sell, T.PricesChartEmpty, T.PricesBuyBelow, T.PricesSellAbove);
        // the series does not reach back over the whole range yet: say since when it exists
        collecting.SetText(history.First(w.Item) is { } first && first.ToUnixTimeSeconds() > from + 7200
            ? string.Format(T.PricesCollecting, first.ToLocalTime().ToString("d MMM HH:mm", T.Culture)) : "");
    }

    // ── add ──────────────────────────────────────────────────────────────────────────────────────

    private void SetAddHint(string text)
    {
        _addHint.SetText(text);
        _addHint.Visible = text.Length > 0;
    }

    private void Add()
    {
        if (Shell.Engine is not { } m) return;
        if (!int.TryParse(_itemBox.GetText().Trim(), out var item) || item <= 0) { SetAddHint(T.PricesItemId); return; }
        if (!ItemKit.TryParsePrice(_maxBox.GetText(), out var max)) { SetAddHint(T.PricesMax); return; }
        SetAddHint("");
        // the viewer never asks aion2hub (its items cache is shared with the daemon): an id the catalogue lacks stays "Item n" until a poll names it
        var name = m.Names.Name(item, Lang) ?? m.Items.Cached(item)?.Name ?? $"Item {item}";
        m.Prices.Add(item, name, max);
        if (_sellBox.Active) m.Prices.SetSell(item, true);
        _itemBox.SetText(""); _maxBox.SetText(""); _sellBox.Active = false;
        // a new watch is read at once whatever the manual cooldown (WPF PollPricesAfterAdd)
        Shell.Daemon.Send("price-poll", "mode", "added");
    }

    private void SetLang(string lang)
    {
        if (Lang == lang) return;
        // A2M-47: the one names language; SaveSettings raises LanguageChanged on every view (this one re-syncs, searches and reloads there)
        UiLoop.Post(() =>
        {
            Shell.RefreshSettings();
            Shell.Settings.PickNames(lang);
            Shell.SaveSettings();
        });
    }

    private void SyncLang()
    {
        PricesKit.SetOn(_langEn, Lang == "en");
        PricesKit.SetOn(_langEs, Lang == "es");
    }

    /// <summary>Search result: name in the search language, its grade chip right after it, then "· other-language name", the id on the right.</summary>
    private void RunSearch()
    {
        while (_results.GetFirstChild() is { } child) _results.Remove(child);
        _hits.Clear();
        var q = _search.GetText();
        if (q.Trim().Length < 2 || Shell.Engine is not { } m) { _resultsBox.Visible = false; _searchHint.SetText(""); return; }
        var lang = Lang;
        foreach (var (id, en, es) in m.Names.Search(q, lang, 40))
        {
            var main = lang == "es" && es.Length > 0 ? es : en;
            var alt = lang == "es" ? en : es;
            var hit = (Id: id, Main: main, Alt: alt.Length > 0 && alt != main ? "  ·  " + alt : "", Grade: m.Names.GradeOf(id));
            _hits.Add(hit);
            var row = UiKit.HBox();
            row.AddCssClass("k-prices-hitrow");
            row.SetCursorFromName("pointer");
            var mainLabel = UiKit.Line(hit.Main);
            mainLabel.Hexpand = false;
            mainLabel.MaxWidthChars = -1;
            row.Append(mainLabel);
            if (ItemKit.GradeChip(hit.Grade, lang) is { } chip) row.Append(chip);
            row.Append(UiKit.Line(hit.Alt, 12.5, Fg.Mute));
            var idLabel = UiKit.SetFg(Gtk.Label.New(id.ToString(CultureInfo.InvariantCulture)), Fg.Mute);
            UiKit.Margin(idLabel, 12, 0, 4, 0);
            row.Append(idLabel);
            var click = Gtk.GestureClick.New();
            click.OnReleased += (_, e) => { if (e.NPress == 1) Pick(hit.Id, hit.Main, hit.Alt, hit.Grade); };
            row.AddController(click);
            _results.Append(row);
        }
        _resultsBox.Visible = _hits.Count > 0;
        _searchHint.SetText(PriceRowText.SearchCount(_hits.Count, 40));
    }

    private void Pick(int id, string main, string alt, Grade grade)
    {
        _itemBox.SetText(id.ToString(CultureInfo.InvariantCulture));
        SetAddHint(PriceRowText.PickHint(main, alt, grade, Lang));
        _resultsBox.Visible = false;
        _maxBox.GrabFocus();
    }

    // ── actions ──────────────────────────────────────────────────────────────────────────────────

    private void OpenItem(MeterEngine m, PriceWatch w)
    {
        var (url, routed) = PriceRowText.ItemUrl(Shell.Settings.PriceItemUrl, w.Item, m.Prices.ExchangeServerId);
        if (!routed) SetClipboard(m.Names.Name(w.Item) ?? w.Name);   // no item route known: the name goes to the clipboard for the site's search box
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { Log.Info($"xdg-open {url}: {e.Message}"); }
    }

    private void CopyItems()
    {
        if (Shell.Engine is not { } m) return;
        var watches = m.Prices.All();
        CopyScript("aion2exchange-item-servers.js", src => PriceRowText.SpliceItems(src, watches));
    }

    /// <summary>The browser-console export scripts ride along in data/gamedb; the watched-items one gets the current ids spliced in. Read
    /// synchronously (a few KB) so the clipboard is set inside the click: Wayland only takes a selection with a recent input serial.</summary>
    private void CopyScript(string file, Func<string, string>? edit)
    {
        try
        {
            var src = File.ReadAllText(Path.Combine(MeterEngine.DataDirectory, "gamedb", file));
            if (SetClipboard(edit is null ? src : edit(src))) _status.SetText(T.PricesCopyHint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.SetText(ex.Message); Log.Error("Copy script", ex); }
    }

    private bool SetClipboard(string text)
    {
        try { _status.GetClipboard().SetText(text); return true; }
        catch (GLib.GException e) { _status.SetText(e.Message); return false; }
    }

    // The button shows the remaining cooldown (m:ss) and stays disabled until it ends; the deadline is the daemon's, from its last answer.
    private void CheckNow()
    {
        if (!Shell.Daemon.Connected) { _status.SetText(T.PricesNoDaemon); return; }
        _polling = true;
        Shell.Daemon.Send("price-poll");
        CooldownTick();
    }

    private void OnPolled(PricePollReply r)
    {
        _polling = false;
        _manualUntil = r.Next > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.Next) : DateTimeOffset.MinValue;
        if (Shell.Engine is { } m) { m.Prices.ReloadIfChanged(); _history = default; }
        if (!IsShown) return;
        Reload();
        if (!r.Ran && r.Reason != "cooldown") _status.SetText(string.Format(T.PricesPollSkipped, r.Reason));
        CooldownTick();
    }

    private void CooldownTick()
    {
        var (label, enabled, tip) = PriceRowText.CheckButton(_polling, _manualUntil, DateTimeOffset.Now, Shell.Daemon.Connected, T);
        UiKit.SetText(_check, label);
        _check.Sensitive = enabled;
        UiKit.Tip(_check, tip);
    }
}

/// <summary>The Prices tab's texts and rules that need no widget (display-free: tested without GTK).</summary>
internal static class PriceRowText
{
    private static readonly Regex ItemsLine = new(@"const ITEMS = \[[^\]]*\];", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The catalogue name, else the stored one (old watches stored "en · es": the part before the dot).</summary>
    public static string DisplayName(string? catalog, string stored)
    {
        if (!string.IsNullOrEmpty(catalog)) return catalog;
        var cut = stored.IndexOf(" · ", StringComparison.Ordinal);
        return cut > 0 ? stored[..cut] : stored;
    }

    public static string NameTip(string name, string? es, Grade grade)
    {
        var tip = grade != Grade.Unknown ? name + " · " + ItemGrades.Word(grade, "en") : name;
        if (es is not { Length: > 0 }) return tip;
        var alt = ItemGrades.Word(grade, "es");
        return tip + "\n" + (alt.Length > 0 ? es + " · " + alt : es);
    }

    private static string Hm(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"#id · site sample HH:mm · site snapshot HH:mm[ · error]": two clocks, the site's sample and our read of it (up to an hour apart).</summary>
    public static string Sub(PriceWatch w, UiText t)
    {
        var when = w.LastCheck is { } c ? string.Format(t.PricesChecked, Hm(c)) : t.PricesNotChecked;
        var sample = w.SampledAt is { } sa ? string.Format(t.PricesSampled, Hm(sa)) + " · " : "";
        return w.LastError is null ? $"#{w.Item} · {sample}{when}" : $"#{w.Item} · {sample}{when} · {w.LastError}";
    }

    public static DateTimeOffset? LastCheck(List<PriceWatch> watches)
    {
        DateTimeOffset? last = null;
        for (var i = 0; i < watches.Count; i++)
            if (watches[i].LastCheck is { } c && (last is null || c > last)) last = c;
        return last;
    }

    public static string Status(DateTimeOffset? last, UiText t) =>
        last is { } l ? string.Format(t.PricesLastPoll, Hm(l)) + " · " + t.PricesHourlyNote : t.PricesNotChecked;

    /// <summary>Green at or past the threshold, red otherwise, grey without a price.</summary>
    public static PriceTone PriceToneOf(PriceWatch w) => w.Hit ? PriceTone.Down : w.LastPrice is null ? PriceTone.Flat : PriceTone.Up;

    /// <summary>Value of the units held at the last price, plus the gain over the price paid (green up, red down).</summary>
    public static (string Text, PriceTone Tone) Holding(int quantity, long? last, long paidEach)
    {
        if (quantity <= 0 || last is not { } p) return ("", PriceTone.Flat);
        var text = ItemFormat.N(quantity * p);
        if (paidEach <= 0) return (text, PriceTone.Flat);
        var gain = (p - paidEach) * 100.0 / paidEach;
        return (text + "  " + gain.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%", gain > 0 ? PriceTone.Down : gain < 0 ? PriceTone.Up : PriceTone.Flat);
    }

    /// <summary>24 h trend: ▲/▼ beyond ±3 %, → inside (red when dearer, green when cheaper).</summary>
    public static (string Text, PriceTone Tone) Trend(long? last, long? dayAgo)
    {
        if (last is not { } p || dayAgo is not { } ago || ago <= 0) return ("", PriceTone.Flat);
        var pct = (p - ago) * 100.0 / ago;
        var text = (pct > 3 ? "▲ " : pct < -3 ? "▼ " : "→ ") + pct.ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
        return (text, pct > 3 ? PriceTone.Up : pct < -3 ? PriceTone.Down : PriceTone.Flat);
    }

    public static string SearchCount(int hits, int max) => hits == 0 ? "0" : hits >= max ? max.ToString(CultureInfo.InvariantCulture) + "+" : hits.ToString(CultureInfo.InvariantCulture);

    public static string PickHint(string main, string alt, Grade grade, string lang) =>
        grade != Grade.Unknown ? main + " [" + ItemGrades.Word(grade, lang) + "]" + alt : main + alt;

    /// <summary>Settings.PriceItemUrl with {id}/{server}; Routed false when the template has no item route (the name goes to the clipboard).</summary>
    public static (string Url, bool Routed) ItemUrl(string template, int item, int server)
    {
        var id = item.ToString(CultureInfo.InvariantCulture);
        var url = template.Replace("{id}", id).Replace("{server}", server.ToString(CultureInfo.InvariantCulture));
        return (url, url.Contains(id, StringComparison.Ordinal));
    }

    /// <summary>The watched-items script with `const ITEMS = [...]` replaced by the current ids.</summary>
    public static string SpliceItems(string src, List<PriceWatch> watches)
    {
        var ids = new string[watches.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = watches[i].Item.ToString(CultureInfo.InvariantCulture);
        var line = "const ITEMS = [" + string.Join(", ", ids) + "];";
        return ItemsLine.Replace(src, line.Replace("$", "$$"));
    }

    /// <summary>Check now: "Checking…" while asked, "Check now (m:ss)" disabled during the daemon's cooldown, else enabled when connected.</summary>
    public static (string Label, bool Enabled, string? Tip) CheckButton(bool polling, DateTimeOffset until, DateTimeOffset now, bool connected, UiText t)
    {
        if (polling) return (t.PricesChecking, false, null);
        var left = until - now;
        if (left <= TimeSpan.Zero) return (t.PricesCheckNow, connected, connected ? null : t.PricesNoDaemon);
        return ($"{t.PricesCheckNow} ({(int)left.TotalMinutes}:{left.Seconds:00})", false, null);
    }
}
