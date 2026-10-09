using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.Core.Market;

namespace AionMeter.App.Windows;

/// <summary>Watched items with their maximum price, last lowest listing, trend and listing count; aion2.exchange polled hourly.</summary>
public partial class PriceWatchWindow : Window
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
            Name = string.IsNullOrEmpty(w.Name) ? catalogName : w.Name;
            var when = w.LastCheck is { } c ? c.ToLocalTime().ToString("HH:mm") : T.PricesNotChecked;
            Sub = w.LastError is null ? $"#{w.Item} · {when}" : $"#{w.Item} · {when} · {w.LastError}";
            MaxText = w.MaxPrice.ToString("N0", CultureInfo.InvariantCulture);
            LastText = w.LastPrice is { } p ? p.ToString("N0", CultureInfo.InvariantCulture) : "—";
            Listings = w.LastListings is { } n ? n.ToString() : "";
            PriceBrush = w.Hit ? Down : (w.LastPrice is null ? Flat : Up);
            Frame = w.Hit ? HitFrame : PriceWatchWindow.Frame;
            if (w.LastPrice is { } lp && w.DayAgoPrice is { } ago && ago > 0)
            {
                var pct = (lp - ago) * 100.0 / ago;
                Trend = (pct > 3 ? "▲ " : pct < -3 ? "▼ " : "→ ") + pct.ToString("+0;-0;0") + "%";
                TrendBrush = pct > 3 ? Up : pct < -3 ? Down : Flat;
            }
            else { Trend = ""; TrendBrush = Flat; }
        }
        public PriceWatch Watch { get; }
        public string Name { get; }
        public string Alt { get; init; } = "";
        public string Sub { get; }
        public string MaxText { get; }
        public string LastText { get; }
        public string Listings { get; }
        public string Trend { get; }
        public Brush PriceBrush { get; }
        public Brush TrendBrush { get; }
        public Brush Frame { get; }
    }

    public sealed record Hit(int Id, string Label);

    public PriceWatchWindow(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        if (_meter.Settings.PriceSearchLanguage == "es") LangEs.IsChecked = true; else LangEn.IsChecked = true;
        Loaded += (_, _) => { Reload(); _meter.Prices.Changed += OnChanged; };
        Closed += (_, _) => _meter.Prices.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Reload);

    private void Reload()
    {
        var rows = new List<Row>();
        foreach (var w in _meter.Prices.All())
        {
            var (en, es) = _meter.Names.Both(w.Item);
            var other = _meter.Settings.PriceSearchLanguage == "es" ? en : es;   // the row shows the name in the other language under it
            rows.Add(new Row(w, _meter.Names.Name(w.Item, _meter.Settings.PriceSearchLanguage) ?? _meter.Items.NameOf(w.Item)) { Alt = other is not null && other != w.Name ? other : "" });
        }
        List.ItemsSource = rows;
        var last = _meter.Prices.LastPollAt;
        StatusText.Text = last is { } l ? string.Format(T.PricesLastPoll, l.ToString("HH:mm")) : T.PricesNotChecked;
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
        ItemBox.Text = ""; MaxBox.Text = "";
        if (cached is null)
            _ = Task.Run(async () => { var info = await _meter.Items.GetAsync(item); if (info is not null) _meter.Prices.Rename(item, info.Name); });
        _meter.PollPricesNow();
    }

    private void Max_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && sender is TextBox tb) { ApplyMax(tb); Keyboard.ClearFocus(); } }
    private void Max_LostFocus(object sender, RoutedEventArgs e) { if (sender is TextBox tb) ApplyMax(tb); }
    private void ApplyMax(TextBox tb) { if (tb.Tag is PriceWatch w && TryParsePrice(tb.Text, out var max)) _meter.Prices.SetMax(w.Item, max); }

    private void Remove_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: PriceWatch w }) _meter.Prices.Remove(w.Item); }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PriceWatch w }) return;
        try { Process.Start(new ProcessStartInfo(_meter.Prices.ItemUrl(w.Item)) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("Open item page", ex); }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => _meter.PollPricesNow();

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
            list.Add(new Hit(id, alt.Length > 0 && alt != main ? $"{main}  ·  {alt}" : main));
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
        AddHint.Text = h.Label;
        Results.Visibility = Visibility.Collapsed;
        MaxBox.Focus();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
