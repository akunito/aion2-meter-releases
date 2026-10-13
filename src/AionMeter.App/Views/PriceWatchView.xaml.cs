using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.Core.GameDb;
using AionMeter.Core.Market;

namespace AionMeter.App.Views;

/// <summary>Watched items with their maximum price, last lowest listing, trend and listing count; aion2.exchange polled hourly.</summary>
public partial class PriceWatchView : UserControl
{
    private static UiText T => UiText.Current;
    private readonly MeterService _meter;
    private static readonly Brush Up = Frozen(0xF6, 0x7A, 0x6C), Down = Frozen(0x8E, 0xE0, 0x8A), Flat = Frozen(0x9A, 0xA0, 0xB0), HitFrame = Frozen(0x5E, 0xB8, 0x5A), Frame = Frozen(0x3A, 0x40, 0x55);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    public sealed class Row
    {
        public Row(PriceWatch w, string catalogName)
        {
            Watch = w;
            // English name on the row (old watches stored "en · es" in Name: keep the part before the dot), Spanish only on hover (Diego, 2026-10-10)
            Name = catalogName.Length > 0 ? catalogName : (w.Name.IndexOf(" · ", StringComparison.Ordinal) is var cut && cut > 0 ? w.Name[..cut] : w.Name);
            // Two clocks: the site's sample (what the price really is as of) and our read of it. They differ by up to an hour.
            var when = w.LastCheck is { } c ? string.Format(T.PricesChecked, c.ToLocalTime().ToString("HH:mm")) : T.PricesNotChecked;
            var sample = w.SampledAt is { } sa ? string.Format(T.PricesSampled, sa.ToLocalTime().ToString("HH:mm")) + " · " : "";
            Sub = w.LastError is null ? $"#{w.Item} · {sample}{when}" : $"#{w.Item} · {sample}{when} · {w.LastError}";
            MaxText = w.MaxPrice.ToString("N0", CultureInfo.InvariantCulture);
            LastText = w.LastPrice is { } p ? p.ToString("N0", CultureInfo.InvariantCulture) : "—";
            Listings = w.LastListings is { } n ? n.ToString() : "";
            PriceBrush = w.Hit ? Down : (w.LastPrice is null ? Flat : Up);
            Frame = w.Hit ? HitFrame : PriceWatchView.Frame;
            ModeText = w.Sell ? "≥" : "≤";
            QtyText = w.Quantity > 0 ? w.Quantity.ToString() : "";
            PaidText = w.PaidEach > 0 ? w.PaidEach.ToString("N0", CultureInfo.InvariantCulture) : "";
            ValueText = ""; ValueBrush = Flat;
            if (w.Quantity > 0 && w.LastPrice is { } lastp)
            {
                ValueText = (w.Quantity * lastp).ToString("N0", CultureInfo.InvariantCulture);
                if (w.PaidEach > 0) { var gain = (lastp - w.PaidEach) * 100.0 / w.PaidEach; ValueText += "  " + gain.ToString("+0;-0;0") + "%"; ValueBrush = gain > 0 ? Down : gain < 0 ? Up : Flat; }
            }
            if (w.LastPrice is { } lp && w.DayAgoPrice is { } ago && ago > 0)
            {
                var pct = (lp - ago) * 100.0 / ago;
                Trend = (pct > 3 ? "▲ " : pct < -3 ? "▼ " : "→ ") + pct.ToString("+0;-0;0") + "%";
                TrendBrush = pct > 3 ? Up : pct < -3 ? Down : Flat;
            }
            else { Trend = ""; TrendBrush = Flat; }
        }
        public PriceWatch Watch { get; }
        public bool Expanded { get; init; }
        public string Chevron => Expanded ? "\uE70E" : "\uE70D";
        /// <summary>Listings + chart, only on the expanded row.</summary>
        public FrameworkElement? Detail { get; init; }
        public string Name { get; }
        public string Alt { get; init; } = "";
        /// <summary>Chip after the English name; the Spanish tooltip carries the Spanish grade word.</summary>
        public GradeTag? GradeChip { get; init; }
        public string AltGrade { get; init; } = "";
        /// <summary>Full name (it trims on a narrow card) with its grade, then the Spanish name with the Spanish grade word.</summary>
        public string NameTip => (GradeChip is { } g ? Name + " · " + g.Text : Name) + (Alt.Length > 0 ? "\n" + (AltGrade.Length > 0 ? Alt + " · " + AltGrade : Alt) : "");
        public ImageSource? Icon { get; init; }
        public string Sub { get; }
        public string MaxText { get; }
        public string LastText { get; }
        public string Listings { get; }
        public string Trend { get; }
        public Brush PriceBrush { get; }
        public string ModeText { get; }
        public string QtyText { get; }
        public string PaidText { get; }
        public string ValueText { get; }
        public Brush ValueBrush { get; }
        public Brush TrendBrush { get; }
        public Brush Frame { get; }
    }

    /// <summary>Search result: name in the search language, its grade chip right after it, then "· other-language name".</summary>
    public sealed record Hit(int Id, string Main, string Alt, GradeTag? GradeChip)
    {
        public string Label => Alt.Length > 0 ? Main + Alt : Main;
    }

    public PriceWatchView(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        if (_meter.Settings.PriceSearchLanguage == "es") LangEs.IsChecked = true; else LangEn.IsChecked = true;
        Loaded += (_, _) => { Reload(); _meter.Prices.Changed += OnChanged; _meter.ItemIconCache.Loaded += OnChanged; _cooldown.Start(); Cooldown_Tick(null, EventArgs.Empty); };
        Unloaded += (_, _) => { _meter.Prices.Changed -= OnChanged; _meter.ItemIconCache.Loaded -= OnChanged; _cooldown.Stop(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && _stale) { _stale = false; Reload(); } };
        _cooldown.Tick += Cooldown_Tick;
    }

    private bool _stale;
    private void OnChanged() { if (IsVisible) Dispatcher.BeginInvoke(Reload); else _stale = true; }

    private bool _sample;
    private PriceHistory? _sampleHistory;
    private IListingsSource? _sampleListings;
    private PriceHistory History => _sampleHistory ?? _meter.Prices.History;
    private IListingsSource ListingsSource => _sampleListings ?? _meter.Prices.Listings;

    /// <summary>Render tool (--window prices): these watches instead of the store, icons from <paramref name="icons"/>; nothing polled or saved.
    /// <paramref name="expanded"/> (--tab expanded): the first row open on 30 days of made-up hourly samples and five made-up listings.</summary>
    public void ShowSample(IReadOnlyList<PriceWatch> watches, IconCache icons, DateTimeOffset lastPoll, bool expanded = false)
    {
        _sample = true;
        if (expanded && watches.Count > 0)
        {
            var w0 = watches[0];
            _expanded = w0.Item;
            _sampleHistory = SampleHistory(w0.Item, w0.LastPrice ?? 1_000_000, DateTimeOffset.Now);
            var p = w0.LastPrice ?? 1_000_000;
            _sampleListings = new StaticListingsSource(
            [
                MarketListing.FromTotal(p, 1, "Lyrael"), MarketListing.FromTotal((p + 9_000) * 2, 2, "Kharun"), MarketListing.FromTotal(p + 30_000, 1, "Mosswen"),
                MarketListing.FromTotal((p + 50_010) * 3, 3, "Teodric"), MarketListing.FromTotal(p + 88_000, 1, null), MarketListing.FromTotal(p + 200_000, 1, "Velka"),
            ], DateTimeOffset.Now.AddMinutes(-3));
            _meter.Settings.PricesRange = "30d";
        }
        List.ItemsSource = BuildRows(watches, w => w.Name, icons.Get);
        StatusText.Text = string.Format(T.PricesLastPoll, lastPoll.ToString("HH:mm")) + " · " + T.PricesHourlyNote;
    }

    /// <summary>Hourly made-up series for the render tool: 27 days (so "collecting since" shows on 30 days) climbing to a spike and settling on <paramref name="last"/>.</summary>
    private static PriceHistory SampleHistory(int item, long last, DateTimeOffset now)
    {
        var h = new PriceHistory(null);
        var rnd = new Random(42);
        var end = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).ToUniversalTime();
        var hours = 27 * 24;
        var pts = new List<PricePoint>(hours);
        for (var i = 0; i < hours; i++)
        {
            var t = i / (double)(hours - 1);
            var trend = 0.34 + 0.55 * t + 0.42 * Math.Exp(-Math.Pow((t - 0.72) / 0.06, 2));   // rise, spike at ~day 19, settle
            var price = (long)(last * trend * (1 + (rnd.NextDouble() - 0.5) * 0.06));
            if (i == hours - 1) price = last;
            pts.Add(new PricePoint(end.AddHours(i - hours + 1), price, price + 40_000, price, price, 12 + rnd.Next(30)));
        }
        h.Merge(item, pts, now);
        return h;
    }

    private void Reload()
    {
        if (_sample) return;
        List.ItemsSource = BuildRows(_meter.Prices.All(), w => _meter.Items.NameOf(w.Item) ?? "", _meter.ItemIconCache.Get);
        var last = _meter.Prices.LastPollAt;
        StatusText.Text = last is { } l ? string.Format(T.PricesLastPoll, l.ToString("HH:mm")) + " · " + T.PricesHourlyNote : T.PricesNotChecked;
    }

    private List<Row> BuildRows(IReadOnlyList<PriceWatch> watches, Func<PriceWatch, string> fallbackName, Func<int, ImageSource?> icon)
    {
        var rows = new List<Row>(watches.Count);
        FrameworkElement? scrollTo = null;
        foreach (var w in watches)
        {
            var (en, es) = _meter.Names.Both(w.Item);
            var open = w.Item == _expanded;
            var detail = open ? BuildDetail(w) : null;
            scrollTo ??= detail;
            var grade = _meter.Names.GradeOf(w.Item);
            if (grade == Grade.Unknown) grade = _meter.Grades.Of(w.Item);
            rows.Add(new Row(w, en ?? fallbackName(w)) { Alt = es ?? "", GradeChip = GradeTag.Of(grade, "en"), AltGrade = ItemGrades.Word(grade, "es"), Icon = icon(w.Item), Expanded = open, Detail = detail });
        }
        if (scrollTo is { } d && !_sample) d.Loaded += static (o, _) => ((FrameworkElement)o).BringIntoView();
        return rows;
    }

    // ── expanded row ─────────────────────────────────────────────────────────────────────────────
    private int _expanded;
    private static readonly object DetailTag = new();
    private readonly Dictionary<int, (DateTimeOffset At, ListingsResult Result)> _listingsCache = new();

    /// <summary>Click anywhere on a row but its inputs, buttons and the open detail toggles it; one row open at a time.</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Row row }) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, sender); d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBoxBase or ButtonBase || (d is FrameworkElement { Tag: var tag } && ReferenceEquals(tag, DetailTag))) return;
        _expanded = _expanded == row.Watch.Item ? 0 : row.Watch.Item;
        if (_sample) return;
        Reload();
    }

    private FrameworkElement BuildDetail(PriceWatch w)
    {
        var root = new Grid { Tag = DetailTag, Margin = new Thickness(0, 10, 0, 2), Cursor = Cursors.Arrow, Background = Brushes.Transparent };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // left: cheapest listings
        var left = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        left.Children.Add(Caption(T.PricesCheapest));
        var table = new Grid();
        left.Children.Add(table);
        FillListings(table, w);

        // right: range switch, "collecting since", chart
        var right = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
        var seg = new StackPanel { Orientation = Orientation.Horizontal };
        var collecting = new TextBlock { FontSize = 11, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
        var chart = new PriceChart { Height = 176 };
        var current = PriceHistory.ParseRange(_meter.Settings.PricesRange);
        var group = "pr" + w.Item;
        (PriceRange R, string Label, CornerRadius C)[] opts = [(PriceRange.Day, T.PricesRange24h, new CornerRadius(6, 0, 0, 6)), (PriceRange.Week, T.PricesRange7d, new CornerRadius(0)), (PriceRange.Month, T.PricesRange30d, new CornerRadius(0, 6, 6, 0))];
        foreach (var (r, label, c) in opts)
        {
            var rb = new RadioButton { Content = label, GroupName = group, Style = (Style)FindResource("Segment"), Tag = c, IsChecked = r == current, Margin = new Thickness(r == PriceRange.Day ? 0 : -1, 0, 0, 0) };
            rb.Checked += (_, _) =>
            {
                if (PriceHistory.RangeKey(r) == _meter.Settings.PricesRange) return;
                _meter.Settings.PricesRange = PriceHistory.RangeKey(r);
                if (!_sample) _meter.Settings.Save();
                FillChart(chart, collecting, w);
            };
            seg.Children.Add(rb);
        }
        DockPanel.SetDock(seg, Dock.Left);
        bar.Children.Add(seg);
        bar.Children.Add(collecting);
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        right.Children.Add(chart);
        FillChart(chart, collecting, w);

        root.Children.Add(left);
        root.Children.Add(right);
        // side by side when the card is wide enough (Diego's card is ~940 wide), stacked in the default 560
        void Layout(double width)
        {
            var wide = width >= 640;
            root.ColumnDefinitions[0].Width = wide ? new GridLength(Math.Min(330, width * 0.4)) : new GridLength(1, GridUnitType.Star);
            root.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(right, wide ? 1 : 0); Grid.SetRow(right, wide ? 0 : 1);
            left.Margin = wide ? new Thickness(0, 0, 16, 0) : new Thickness(0, 0, 0, 10);
        }
        Layout(0);
        root.SizeChanged += (_, e) => { if (e.WidthChanged && (e.NewSize.Width >= 640) != (e.PreviousSize.Width >= 640)) Layout(e.NewSize.Width); else if (e.WidthChanged && e.NewSize.Width >= 640) root.ColumnDefinitions[0].Width = new GridLength(Math.Min(330, e.NewSize.Width * 0.4)); };
        return root;
    }

    private static readonly Brush Mute = Frozen(0x6B, 0x75, 0x90), Dim = Frozen(0xA3, 0xAC, 0xBF), Ink = Frozen(0xF1, 0xE9, 0xD8), Gold = Frozen(0xC9, 0xA4, 0x5C), RuleBrush = Frozen(0x2A, 0x33, 0x50);

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Gold, Margin = new Thickness(0, 3, 0, 6) };

    private void FillChart(PriceChart chart, TextBlock collecting, PriceWatch w)
    {
        var range = PriceHistory.ParseRange(_meter.Settings.PricesRange);
        var now = DateTimeOffset.Now;
        var win = History.Window(w.Item, range, now);
        var from = now.ToUnixTimeSeconds() - PriceHistory.SpanSeconds(range);
        chart.SetData(PriceHistory.Downsample(win), PriceHistory.Stats(win), from, now.ToUnixTimeSeconds(), range, w.MaxPrice, w.Sell, T.PricesChartEmpty, T.PricesBuyBelow, T.PricesSellAbove);
        // the series does not reach back over the whole range yet: say since when it exists
        collecting.Text = History.First(w.Item) is { } first && first.ToUnixTimeSeconds() > from + 7200
            ? string.Format(T.PricesCollecting, first.ToLocalTime().ToString("d MMM HH:mm", T.Culture)) : "";
    }

    private void FillListings(Grid table, PriceWatch w)
    {
        var src = ListingsSource;
        if (!src.Configured) { ShowListings(table, ListingsResult.NotConfigured); return; }
        if (_listingsCache.TryGetValue(w.Item, out var c) && DateTimeOffset.Now - c.At < TimeSpan.FromMinutes(1)) { ShowListings(table, c.Result); return; }
        var server = _meter.Prices.ExchangeServerId;
        var direct = src.GetAsync(w.Item, server);
        if (direct.IsCompletedSuccessfully) { _listingsCache[w.Item] = (DateTimeOffset.Now, direct.Result); ShowListings(table, direct.Result); return; }   // in-memory sources (render tool)
        ShowListings(table, new ListingsResult([], null, "…"));
        _ = Task.Run(async () =>
        {
            ListingsResult r;
            try { r = await direct.ConfigureAwait(false); }
            catch (Exception ex) { r = new ListingsResult([], null, ex.Message); Log.Error("Listings", ex); }
            await Dispatcher.InvokeAsync(() => { _listingsCache[w.Item] = (DateTimeOffset.Now, r); ShowListings(table, r); });
        });
    }

    private static void ShowListings(Grid table, ListingsResult r)
    {
        table.Children.Clear();
        table.RowDefinitions.Clear();
        table.ColumnDefinitions.Clear();
        if (r.Status is not null || r.Cheapest.Count == 0)
        {
            var msg = r.Status switch { "not-configured" => T.PricesListingsOff, null => T.PricesListingsNone, var s => s };
            table.Children.Add(new TextBlock { Text = msg, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = Mute, Margin = new Thickness(0, 2, 0, 0) });
            return;
        }
        foreach (var star in new[] { false, false, false, true })
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Cell(int row, int col, string text, Brush fg, bool right, FontWeight weight)
        {
            var tb = new TextBlock { Text = text, Foreground = fg, FontWeight = weight, FontSize = row == 0 ? 10.5 : 12, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
                                     Margin = new Thickness(col == 0 ? 0 : 14, row == 0 ? 0 : 3, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(tb, row); Grid.SetColumn(tb, col);
            table.Children.Add(tb);
        }
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(0, 0, T.PricesColPrice, Mute, true, FontWeights.Normal); Cell(0, 1, T.PricesColQty, Mute, true, FontWeights.Normal);
        Cell(0, 2, T.PricesColUnit, Mute, true, FontWeights.Normal); Cell(0, 3, T.PricesColSeller, Mute, false, FontWeights.Normal);
        var rule = new Border { Height = 1, Background = RuleBrush, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumnSpan(rule, 4);
        table.Children.Add(rule);
        for (var i = 0; i < r.Cheapest.Count; i++)
        {
            var l = r.Cheapest[i];
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(i + 1, 0, l.Price.ToString("N0", CultureInfo.InvariantCulture), i == 0 ? Ink : Dim, true, i == 0 ? FontWeights.SemiBold : FontWeights.Normal);
            Cell(i + 1, 1, l.Quantity.ToString(CultureInfo.InvariantCulture), Dim, true, FontWeights.Normal);
            Cell(i + 1, 2, l.Unit.ToString("N0", CultureInfo.InvariantCulture), i == 0 ? Ink : Dim, true, i == 0 ? FontWeights.SemiBold : FontWeights.Normal);
            Cell(i + 1, 3, l.Seller ?? "—", Mute, false, FontWeights.Normal);
        }
        if (r.At is { } at)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // The ladder is a site capture that is usually hours or days old (A2M-23, 2026-10-13): show the day once it is not today's.
            var local = at.ToLocalTime();
            var tb = new TextBlock { Text = string.Format(T.PricesChecked, local.ToString(local.Date == DateTime.Today ? "HH:mm" : "dd MMM HH:mm", CultureInfo.InvariantCulture)), FontSize = 10.5, Foreground = Mute, Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetRow(tb, r.Cheapest.Count + 1); Grid.SetColumnSpan(tb, 4);
            table.Children.Add(tb);
        }
    }

    /// <summary>"1200000", "1,200,000", "1.2m", "850k" → Kinah.</summary>
    internal static bool TryParsePrice(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant().Replace(",", "").Replace(" ", "").Replace("_", "");
        double mul = 1;
        if (t.EndsWith('k')) { mul = 1_000; t = t[..^1]; }
        else if (t.EndsWith('m')) { mul = 1_000_000; t = t[..^1]; }
        else if (t.EndsWith('b')) { mul = 1_000_000_000; t = t[..^1]; }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0) return false;
        value = (long)Math.Round(d * mul);
        return true;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Add();
    private void Add_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Add(); }

    private void Add()
    {
        if (!int.TryParse(ItemBox.Text.Trim(), out var item) || item <= 0) { AddHint.Text = T.PricesItemId; return; }
        if (!TryParsePrice(MaxBox.Text, out var max)) { AddHint.Text = T.PricesMax; return; }
        AddHint.Text = "";
        var cached = _meter.Names.Name(item, Lang) ?? _meter.Items.Cached(item)?.Name;
        _meter.Prices.Add(item, cached ?? $"Item {item}", max);
        if (SellBox.IsChecked == true) _meter.Prices.SetSell(item, true);
        ItemBox.Text = ""; MaxBox.Text = ""; SellBox.IsChecked = false;
        if (cached is null)
            _ = Task.Run(async () => { var info = await _meter.Items.GetAsync(item); if (info is not null) _meter.Prices.Rename(item, info.Name); });
        _meter.PollPricesAfterAdd();
    }

    private void Max_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && sender is TextBox tb) { ApplyMax(tb); Keyboard.ClearFocus(); } }
    private void Max_LostFocus(object sender, RoutedEventArgs e) { if (sender is TextBox tb) ApplyMax(tb); }
    private void ApplyMax(TextBox tb) { if (tb.Tag is PriceWatch w && TryParsePrice(tb.Text, out var max)) _meter.Prices.SetMax(w.Item, max); }

    private void Mode_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: PriceWatch w }) _meter.Prices.SetSell(w.Item, !w.Sell); }

    private void Holding_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && sender is TextBox tb) { ApplyHolding(tb); Keyboard.ClearFocus(); } }
    private void Holding_LostFocus(object sender, RoutedEventArgs e) { if (sender is TextBox tb) ApplyHolding(tb); }

    /// <summary>The qty and paid boxes are siblings in one panel: read both whichever was edited.</summary>
    private void ApplyHolding(TextBox tb)
    {
        if (tb.Tag is not PriceWatch w || tb.Parent is not StackPanel panel) return;
        TextBox? qtyBox = null, paidBox = null;
        foreach (var child in panel.Children) if (child is TextBox b) { if (qtyBox is null) qtyBox = b; else paidBox ??= b; }
        var qty = int.TryParse((qtyBox?.Text ?? "").Trim(), out var q) ? q : 0;
        var paid = TryParsePrice(paidBox?.Text, out var p) ? p : 0;
        _meter.Prices.SetHolding(w.Item, qty, paid);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: PriceWatch w }) _meter.Prices.Remove(w.Item); }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PriceWatch w }) return;
        var url = _meter.Settings.PriceItemUrl.Replace("{id}", w.Item.ToString()).Replace("{server}", _meter.Prices.ExchangeServerId.ToString());
        if (!url.Contains(w.Item.ToString())) { try { Clipboard.SetText(_meter.Names.Name(w.Item) ?? w.Name); } catch (Exception) { } }   // no item route known: the name goes to the clipboard for the site's search box
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("Open item page", ex); }
    }

    // The button shows the remaining cooldown (m:ss) and stays disabled until it ends; the deadline lives in the service, so switching tabs keeps it.
    private readonly System.Windows.Threading.DispatcherTimer _cooldown = new() { Interval = TimeSpan.FromSeconds(1) };
    private void Check_Click(object sender, RoutedEventArgs e) { _meter.PollPricesNow(); Cooldown_Tick(null, EventArgs.Empty); }
    private void Cooldown_Tick(object? sender, EventArgs e)
    {
        var left = _meter.PricesManualUntil - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) { CheckButton.IsEnabled = true; CheckButton.Content = T.PricesCheckNow; return; }
        CheckButton.IsEnabled = false;
        CheckButton.Content = $"{T.PricesCheckNow} ({(int)left.TotalMinutes}:{left.Seconds:00})";
    }

    /// <summary>The browser-console export scripts ride along in data/gamedb; the watched-items one gets the current ids spliced in.</summary>
    private void CopyMarket_Click(object sender, RoutedEventArgs e) => CopyScript("aion2exchange-export.js", null);
    private void CopyItems_Click(object sender, RoutedEventArgs e)
    {
        var ids = string.Join(", ", _meter.Prices.All().Select(w => w.Item));
        CopyScript("aion2exchange-item-servers.js", src => System.Text.RegularExpressions.Regex.Replace(src, @"const ITEMS = \[[^\]]*\];", $"const ITEMS = [{ids}];"));
    }
    private void CopyScript(string file, Func<string, string>? edit)
    {
        try
        {
            var src = File.ReadAllText(Path.Combine(MeterService.DataDirectory, "gamedb", file));
            Clipboard.SetText(edit is null ? src : edit(src));
            StatusText.Text = T.PricesCopyHint;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; Log.Error("Copy script", ex); }
    }

    private string Lang => _meter.Settings.PriceSearchLanguage;

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string lang }) return;
        if (_meter.Settings.PriceSearchLanguage == lang) return;
        _meter.Settings.PriceSearchLanguage = lang;
        _meter.Settings.Save();
        RunSearch();
        Reload();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();

    /// <summary>Render tool (--window prices --tab search): the add-item search box filled as a user would type it.</summary>
    public void SampleSearch(string text) { SearchBox.Text = text; RunSearch(); }

    private void RunSearch()
    {
        var q = SearchBox.Text;
        if (q.Trim().Length < 2) { Results.Visibility = Visibility.Collapsed; Results.ItemsSource = null; SearchHint.Text = ""; return; }
        var hits = _meter.Names.Search(q, Lang, 40);
        var list = new List<Hit>(hits.Count);
        foreach (var (id, en, es) in hits)
        {
            var main = Lang == "es" && es.Length > 0 ? es : en;
            var alt = Lang == "es" ? en : es;
            list.Add(new Hit(id, main, alt.Length > 0 && alt != main ? "  ·  " + alt : "", GradeTag.Of(_meter.Names.GradeOf(id), Lang)));
        }
        Results.ItemsSource = list;
        Results.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Text = list.Count == 0 ? "0" : list.Count >= 40 ? "40+" : list.Count.ToString();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Results.Items.Count > 0) { Results.SelectedIndex = 0; ((ListBoxItem)Results.ItemContainerGenerator.ContainerFromIndex(0))?.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && Results.Items.Count > 0) { Pick((Hit)Results.Items[0]!); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Text = ""; }
    }

    private void Result_Click(object sender, MouseButtonEventArgs e) { if (Results.SelectedItem is Hit h) Pick(h); }
    private void Result_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Results.SelectedItem is Hit h) { Pick(h); e.Handled = true; } }

    private void Pick(Hit h)
    {
        ItemBox.Text = h.Id.ToString();
        AddHint.Text = h.GradeChip is { } g ? h.Main + " [" + g.Text + "]" + h.Alt : h.Label;
        Results.Visibility = Visibility.Collapsed;
        MaxBox.Focus();
    }
    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp(sender is FrameworkElement { Tag: string t } ? t : "meter");
    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
