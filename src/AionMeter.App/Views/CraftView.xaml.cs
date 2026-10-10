using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.Core.GameDb;

namespace AionMeter.App.Views;

/// <summary>Recipes the current character can craft now (bag + both warehouses) and the ones short by one or two materials.</summary>
public partial class CraftView : UserControl
{
    private static UiText T => UiText.Current;
    private readonly MeterService _meter;
    private string _profession = "";
    private static readonly Brush Ok = Frozen(0x8E, 0xE0, 0x8A), Near = Frozen(0xF2, 0xC2, 0x5A), Frame = Frozen(0x3A, 0x40, 0x55), OkFrame = Frozen(0x5E, 0xB8, 0x5A), Bad = Frozen(0xF6, 0x7A, 0x6C);
    private static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    public sealed class Prof(string key, string label, bool selected) { public string Key { get; } = key; public string Label { get; } = label; public bool Selected { get; set; } = selected; }
    public sealed class Row
    {
        public string Name { get; init; } = ""; public string Sub { get; init; } = ""; public string Times { get; init; } = ""; public Brush TimesBrush { get; init; } = Ok; public Brush Frame { get; init; } = CraftView.Frame;
    }

    public CraftView(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        Professions.ItemsSource = new[] { new Prof("", T.CraftAll, true), new Prof("blacksmithing", "Blacksmith", false), new Prof("tailoring", "Tailor", false), new Prof("jewelcrafting", "Jewel", false), new Prof("alchemy", "Alchemy", false), new Prof("cooking", "Cooking", false) };
        Loaded += (_, _) => { Reload(); _meter.Inventory.Changed += OnChanged; _meter.MarketChanged += OnChanged; };
        Unloaded += (_, _) => { _meter.Inventory.Changed -= OnChanged; _meter.MarketChanged -= OnChanged; };
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Reload);
    private void Filter_Changed(object sender, TextChangedEventArgs e) => Reload();
    private void Filter_Click(object sender, RoutedEventArgs e) => Reload();
    private void Profession_Checked(object sender, RoutedEventArgs e) { if (IsLoaded && sender is RadioButton { Tag: string key }) { _profession = key; Reload(); } }

    /// <summary>Craft → Load market file…: the JSON the browser-console script saved in Downloads (Prices → Copy market script).</summary>
    private void LoadMarket_Click(object sender, RoutedEventArgs e)
    {
        var downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "aion2.exchange export (*.json)|*.json", InitialDirectory = System.IO.Directory.Exists(downloads) ? downloads : "", FileName = "aion2exchange-" };
        if (dlg.ShowDialog() != true) return;
        if (!_meter.ImportMarket(dlg.FileName)) StatusText.Text = T.CraftMarketBad;
        else { ProfitBox.IsChecked = true; Reload(); }
    }

    private static string Kinah(long v) => v >= 1_000_000 ? (v / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M" : v >= 1_000 ? (v / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k" : v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Profit mode: every recipe whose output is on the market, most profitable first (cheapest listings, fee from settings).</summary>
    private void ReloadProfit(Func<int, string> Nm, string q)
    {
        var m = _meter.Market;
        if (m.Count == 0) { List.ItemsSource = Array.Empty<Row>(); StatusText.Text = T.CraftNoMarket; return; }
        var fee = _meter.Settings.BrokerFeePercent;
        var all = _meter.Recipes.Profit(m.Price, fee, _meter.Inventory.CountOf, _profession.Length == 0 ? null : _profession);
        var rows = new List<Row>(120); var shown = 0;
        foreach (var p in all)
        {
            var name = Nm(p.Recipe.Output);
            if (q.Length > 0 && !ItemNames.Normalize(name).Contains(q, StringComparison.Ordinal) && !ItemNames.Normalize(p.Recipe.Name).Contains(q, StringComparison.Ordinal)) continue;
            if (++shown > 150) break;
            var mats = string.Join(", ", p.Recipe.Materials.Select(x => $"{Nm(x.Item)} ×{x.Qty}" + (m.Price(x.Item) is { } mp ? $" @{Kinah(mp)}" : " @?")));
            var margin = p.Cost > 0 ? (double)p.Profit / p.Cost * 100 : 0;
            var sub = $"{p.Recipe.Profession} {p.Recipe.Level} · {p.Recipe.Grade} · {T.CraftSells} {Kinah(p.OutUnit)}" + (p.Recipe.Yield > 1 ? $" ×{p.Recipe.Yield}" : "") +
                      $" → {Kinah(p.Value)} {T.CraftAfterFee} · {T.CraftCost} {Kinah(p.Cost)}" + (p.UnpricedMaterials > 0 ? $" ({p.UnpricedMaterials} {T.CraftUnpriced})" : "") +
                      (p.Times > 0 ? $" · ×{p.Times} {T.CraftNow}" : "") + "\n" + mats;
            var good = p.Profit > 0;
            rows.Add(new Row { Name = name, Sub = sub, Times = (good ? "+" : "") + Kinah(p.Profit) + (p.Cost > 0 ? $" ({margin:+0;-0}%)" : ""), TimesBrush = good ? Ok : Bad, Frame = good && p.Times > 0 ? OkFrame : Frame });
        }
        List.ItemsSource = rows;
        StatusText.Text = string.Format(T.CraftMarketFrom, m.Count, m.Server, m.CapturedAt?.ToLocalTime().ToString("dd MMM HH:mm") ?? "?", fee.ToString("0.#", CultureInfo.InvariantCulture));
    }

    private void Reload()
    {
        var names = _meter.Names; var lang = _meter.Settings.PriceSearchLanguage;
        string Nm(int id) => names.Name(id, lang) ?? _meter.Items.NameOf(id);
        var q = ItemNames.Normalize(SearchBox.Text);
        if (ProfitBox.IsChecked == true) { ReloadProfit(Nm, q); return; }
        var near = NearBox.IsChecked == true;
        var opts = _meter.Recipes.Evaluate(_meter.Inventory.CountOf, near ? 2 : 0, _profession.Length == 0 ? null : _profession);
        var rows = new List<Row>(opts.Count); var craftable = 0;
        foreach (var o in opts)
        {
            var name = Nm(o.Recipe.Output);
            if (q.Length > 0 && !ItemNames.Normalize(name).Contains(q, StringComparison.Ordinal) && !ItemNames.Normalize(o.Recipe.Name).Contains(q, StringComparison.Ordinal)) continue;
            var mats = string.Join(", ", o.Recipe.Materials.Select(m => $"{Nm(m.Item)} ×{m.Qty}"));
            var sub = $"{o.Recipe.Profession} {o.Recipe.Level} · {o.Recipe.Grade} · {mats}";
            if (o.Times > 0) { craftable++; rows.Add(new Row { Name = name, Sub = sub, Times = "×" + o.Times.ToString(CultureInfo.InvariantCulture), TimesBrush = Ok, Frame = OkFrame }); }
            else rows.Add(new Row { Name = name, Sub = sub + "  —  " + T.CraftMissing + ": " + string.Join(", ", o.Missing.Select(m => $"{Nm(m.Item)} {m.Have}/{m.Need}" + (names.Source(m.Item) is { } src ? $" ({src})" : ""))), Times = "", TimesBrush = Near });
        }
        List.ItemsSource = rows;
        StatusText.Text = string.Format(T.CraftStatus, craftable, rows.Count - craftable);
    }

    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
