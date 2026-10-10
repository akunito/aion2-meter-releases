using System.Globalization;
using System.Windows;
using System.Windows.Input;
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
        public int Output { get; init; } public bool Fav { get; init; } public ImageSource? Icon { get; init; }
        public string Star => Fav ? "\u2605" : "\u2606"; public Brush StarBrush => Fav ? Near : CraftView.Frame;
    }

    public CraftView(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        Professions.ItemsSource = new[] { new Prof("", T.CraftAll, true), new Prof("blacksmithing", "Blacksmith", false), new Prof("tailoring", "Tailor", false), new Prof("jewelcrafting", "Jewel", false), new Prof("alchemy", "Alchemy", false), new Prof("cooking", "Cooking", false) };
        foreach (ComboBoxItem it in ScopeBox.Items) if ((string)it.Tag == meter.Settings.CraftScope) { ScopeBox.SelectedItem = it; break; }
        if (ScopeBox.SelectedItem is null) ScopeBox.SelectedIndex = 0;
        Loaded += (_, _) => { Reload(); _meter.Inventory.Changed += OnChanged; _meter.MarketChanged += OnChanged; _meter.ItemIconCache.Loaded += OnIcon; };
        Unloaded += (_, _) => { _meter.Inventory.Changed -= OnChanged; _meter.MarketChanged -= OnChanged; _meter.ItemIconCache.Loaded -= OnIcon; _iconTick.Stop(); };
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); Reload(); };
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Reload);
    // icons land one per 400 ms: redraw at most every 600 ms instead of per icon
    private readonly System.Windows.Threading.DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private void OnIcon() { if (!_iconTick.IsEnabled) _iconTick.Start(); }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { ClearSearch.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; Reload(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; SearchBox.Focus(); }
    private void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Text = ""; e.Handled = true; } }
    private void Filter_Click(object sender, RoutedEventArgs e) => Reload();
    private string Scope => ScopeBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : "now";
    private void Scope_Changed(object sender, SelectionChangedEventArgs e) { if (!IsLoaded) return; _meter.Settings.CraftScope = Scope; _meter.Settings.Save(); Reload(); }
    /// <summary>Star on a row: pin or unpin the recipe (favourites always come first).</summary>
    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int output }) return;
        if (!_meter.Settings.CraftFavourites.Remove(output)) _meter.Settings.CraftFavourites.Add(output);
        _meter.Settings.Save(); Reload();
    }
    // aion2hub lists 41 recipe pairs with two output ids for one name and identical materials (faction/version twins, e.g. 510716007/510716019):
    // one row per name + materials + profession + level, so the list never shows the same craft twice (Diego, 2026-10-11)
    private static string TwinKey(Core.GameDb.Recipe r, string name) => name + "|" + r.Profession + "|" + r.Level + "|" + string.Join(",", r.Materials.Select(m => m.Item + "x" + m.Qty));
    private static bool Match(string q, params string[] names) { foreach (var n in names) if (n.Length > 0 && ItemNames.Normalize(n).Contains(q, StringComparison.Ordinal)) return true; return false; }
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
        var rows = new List<Row>(120); var shown = 0; var favs = _meter.Settings.CraftFavourites; var seen = new HashSet<string>();
        foreach (var p in all.OrderByDescending(x => favs.Contains(x.Recipe.Output)))
        {
            var name = Nm(p.Recipe.Output); var (en, es) = _meter.Names.Both(p.Recipe.Output);
            if (!seen.Add(TwinKey(p.Recipe, name))) continue;
            if (q.Length > 0 && !Match(q, name, en ?? "", es ?? "", p.Recipe.Name)) continue;
            if (++shown > 150) break;
            var mats = string.Join(", ", p.Recipe.Materials.Select(x => $"{Nm(x.Item)} ×{x.Qty}" + (m.Price(x.Item) is { } mp ? $" @{Kinah(mp)}" : " @?")));
            var margin = p.Cost > 0 ? (double)p.Profit / p.Cost * 100 : 0;
            var sub = $"{p.Recipe.Profession} {p.Recipe.Level} · {p.Recipe.Grade} · {T.CraftSells} {Kinah(p.OutUnit)}" + (p.Recipe.Yield > 1 ? $" ×{p.Recipe.Yield}" : "") +
                      $" → {Kinah(p.Value)} {T.CraftAfterFee} · {T.CraftCost} {Kinah(p.Cost)}" + (p.UnpricedMaterials > 0 ? $" ({p.UnpricedMaterials} {T.CraftUnpriced})" : "") +
                      (p.Times > 0 ? $" · ×{p.Times} {T.CraftNow}" : "") + "\n" + mats;
            var good = p.Profit > 0;
            rows.Add(new Row { Name = name, Sub = sub, Times = (good ? "+" : "") + Kinah(p.Profit) + (p.Cost > 0 ? $" ({margin:+0;-0}%)" : ""), TimesBrush = good ? Ok : Bad, Frame = good && p.Times > 0 ? OkFrame : Frame, Output = p.Recipe.Output, Fav = favs.Contains(p.Recipe.Output), Icon = _meter.ItemIconCache.Get(p.Recipe.Output) });
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
        // Typing searches the whole book (Diego 2026-10-11): the scope only applies while the box is empty
        var scope = q.Length > 0 ? "all" : Scope; var favs = _meter.Settings.CraftFavourites;
        // one pass over every recipe (1,684: cheap); the scope filters, favourites bypass it and lead the list
        var opts = _meter.Recipes.Evaluate(_meter.Inventory.CountOf, 99, _profession.Length == 0 ? null : _profession);
        var rows = new List<Row>(opts.Count); var craftable = 0; var seen = new HashSet<string>();
        foreach (var o in opts.OrderByDescending(x => favs.Contains(x.Recipe.Output)))
        {
            var fav = favs.Contains(o.Recipe.Output);
            if (!seen.Add(TwinKey(o.Recipe, Nm(o.Recipe.Output)))) continue;
            if (!fav && scope == "now" && o.Times == 0) continue;
            if (!fav && scope == "near" && o.Times == 0 && o.Missing.Length > 2) continue;
            var name = Nm(o.Recipe.Output); var (en, es) = _meter.Names.Both(o.Recipe.Output);
            if (q.Length > 0 && !Match(q, name, en ?? "", es ?? "", o.Recipe.Name)) continue;
            var mats = string.Join(", ", o.Recipe.Materials.Select(m => $"{Nm(m.Item)} ×{m.Qty}"));
            var sub = $"{o.Recipe.Profession} {o.Recipe.Level} · {o.Recipe.Grade} · {mats}";
            if (o.Times > 0) { craftable++; rows.Add(new Row { Name = name, Sub = sub, Times = "×" + o.Times.ToString(CultureInfo.InvariantCulture), TimesBrush = Ok, Frame = OkFrame, Output = o.Recipe.Output, Fav = fav, Icon = _meter.ItemIconCache.Get(o.Recipe.Output) }); }
            else rows.Add(new Row { Output = o.Recipe.Output, Fav = fav, Icon = _meter.ItemIconCache.Get(o.Recipe.Output), Name = name, Sub = sub + "  —  " + T.CraftMissing + ": " + string.Join(", ", o.Missing.Select(m => $"{Nm(m.Item)} {m.Have}/{m.Need}" + (names.Source(m.Item) is { } src ? $" ({src})" : ""))), Times = "", TimesBrush = Near });
        }
        List.ItemsSource = rows;
        StatusText.Text = q.Length > 0 ? string.Format(T.CraftMatches, rows.Count, craftable) : string.Format(T.CraftStatus, craftable, rows.Count - craftable);
    }

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp(sender is FrameworkElement { Tag: string t } ? t : "meter");
    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
