using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using AionMeter.App.Controls;
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
    public sealed class Row : System.ComponentModel.INotifyPropertyChanged
    {
        public string Name { get; init; } = ""; public string Sub { get; init; } = ""; public string Times { get; init; } = ""; public Brush TimesBrush { get; init; } = Ok; public Brush Frame { get; init; } = CraftView.Frame;
        public int Output { get; init; } public bool Fav { get; init; }
        public GradeTag? GradeChip { get; init; }
        public IconCache? Icons { get; init; }
        private ImageSource? _icon; private bool _asked;
        // asked only when the row is realised by the virtualised list, so a 1,684-row scope queues icons for the visible rows alone
        public ImageSource? Icon { get { if (!_asked) { _asked = true; _icon = Icons?.Get(Output); } return _icon; } }
        public void RefreshIcon() { if (_asked && _icon is null && Icons?.Get(Output) is { } img) { _icon = img; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string Star => Fav ? "\u2605" : "\u2606"; public Brush StarBrush => Fav ? Near : CraftView.Frame;
    }

    public CraftView(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        Professions.ItemsSource = new[] { new Prof("", T.CraftAll, true), new Prof("blacksmithing", "Blacksmith", false), new Prof("tailoring", "Tailor", false), new Prof("jewelcrafting", "Jewel", false), new Prof("alchemy", "Alchemy", false), new Prof("cooking", "Cooking", false) };
        foreach (ComboBoxItem it in ScopeBox.Items) if ((string)it.Tag == meter.Settings.CraftScope) { ScopeBox.SelectedItem = it; break; }
        if (ScopeBox.SelectedItem is null) ScopeBox.SelectedIndex = 0;
        Loaded += (_, _) => { Reload(); _meter.ViewInventoryChanged += OnChanged; _meter.MarketChanged += OnChanged; _meter.CharacterChanged += OnChanged; _meter.Names.Changed += OnNames; _meter.ItemIconCache.Loaded += OnIcon; };
        Unloaded += (_, _) => { _meter.ViewInventoryChanged -= OnChanged; _meter.MarketChanged -= OnChanged; _meter.CharacterChanged -= OnChanged; _meter.Names.Changed -= OnNames; _meter.ItemIconCache.Loaded -= OnIcon; _iconTick.Stop(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && _stale) { _stale = false; Reload(); } };
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); if (List.ItemsSource is IEnumerable<Row> rows) foreach (var r in rows) r.RefreshIcon(); };
        _searchTick.Tick += (_, _) => { _searchTick.Stop(); Reload(); };
    }

    // render tool (--window craft): stock, market and icons of the sample instead of the live character
    private Func<int, int>? _sampleCount; private Core.Market.MarketSnapshot? _sampleMarket; private IconCache? _sampleIcons;
    public void ShowSample(Func<int, int> count, Core.Market.MarketSnapshot market, IconCache icons, string scope, bool profit)
    {
        _sampleCount = count; _sampleMarket = market; _sampleIcons = icons;
        foreach (ComboBoxItem it in ScopeBox.Items) if ((string)it.Tag == scope) { ScopeBox.SelectedItem = it; break; }
        ProfitBox.IsChecked = profit;
        Reload();
    }
    private Func<int, int> CountOf => _sampleCount ?? _meter.ViewInventory.CountOf;
    private Core.Market.MarketSnapshot MarketNow => _sampleMarket ?? _meter.Market;
    private IconCache RowIcons => _sampleIcons ?? _meter.ItemIconCache;

    private bool _stale;
    // questlog names arrive on the resolver thread; IsVisible must be read on the UI thread
    private void OnNames() => Dispatcher.BeginInvoke(OnChanged);
    private void OnChanged() { if (IsVisible) Dispatcher.BeginInvoke(Reload); else _stale = true; }   // hidden tab: one reload when it comes back, not one per packet
    // icons land one per 400 ms: redraw at most every 600 ms instead of per icon
    private readonly System.Windows.Threading.DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private void OnIcon() { if (!_iconTick.IsEnabled) _iconTick.Start(); }
    // typing: one rebuild 150 ms after the last key, not one per key (1,684 rows when the box searches the whole book)
    private readonly System.Windows.Threading.DispatcherTimer _searchTick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private void Filter_Changed(object sender, TextChangedEventArgs e) { ClearSearch.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; _searchTick.Stop(); _searchTick.Start(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; SearchBox.Focus(); }
    /// <summary>Items tab → "Open in Craft →": search the whole book for this recipe.</summary>
    public void SetSearch(string text) { SearchBox.Text = text; SearchBox.CaretIndex = text.Length; }
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
        Telemetry.QueueState(_meter);
    }
    // aion2hub lists 41 recipe pairs with two output ids for one name and identical materials (faction/version twins, e.g. 510716007/510716019):
    // one row per name + materials + profession + level, so the list never shows the same craft twice (Diego, 2026-10-11)
    // 602 of the 1,684 hub recipes share their name with another one: the Elyos/Asmodian item pair (same materials, or the base item
    // of the other faction) and the two routes of the top gear (level 90 from the previous tier vs level 0 from a token). One row per
    // name and profession (Diego 2026-10-11, "están duplicadas otra vez"): the variant the character can craft, else the one missing least.
    // grade in the key: same-named outputs of different grades ("Artisan's Refining Stone" rare/legendary in EN) are different recipes, not variants
    private string TwinKey(Core.GameDb.Recipe r, string name) => name + "|" + r.Profession + "|" + (int)_meter.Grades.Of(r.Output);
    /// <summary>Output ids to show: the best variant per twin key, plus how many variants the key has (for the "+N" marker).</summary>
    private (HashSet<int> Keep, Dictionary<int, int> Variants) PickTwins(IReadOnlyList<Core.GameDb.CraftOption> opts, Func<int, string> Nm)
    {
        var best = new Dictionary<string, Core.GameDb.CraftOption>(opts.Count);
        var count = new Dictionary<string, int>(opts.Count);
        foreach (var o in opts)
        {
            var key = TwinKey(o.Recipe, Nm(o.Recipe.Output));
            count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
            if (!best.TryGetValue(key, out var b) || Better(o, b)) best[key] = o;
        }
        var keep = new HashSet<int>(best.Count); var variants = new Dictionary<int, int>(best.Count);
        foreach (var (key, o) in best) { keep.Add(o.Recipe.Output); variants[o.Recipe.Output] = count[key]; }
        return (keep, variants);
    }
    private static bool Better(Core.GameDb.CraftOption a, Core.GameDb.CraftOption b) =>
        a.Times != b.Times ? a.Times > b.Times : a.Missing.Length != b.Missing.Length ? a.Missing.Length < b.Missing.Length : a.Recipe.Level < b.Recipe.Level;
    private bool Match(ItemQuery q, int output, string name, string en, string es, string recipe) =>
        q.Matches(_meter.Grades.Of(output), ItemNames.Normalize(name), ItemNames.Normalize(en), ItemNames.Normalize(es), ItemNames.Normalize(recipe));
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
    private void ReloadProfit(Func<int, string> Nm, Func<int, string> Lb, ItemQuery q, string lang)
    {
        var m = MarketNow;
        if (m.Count == 0) { List.ItemsSource = Array.Empty<Row>(); StatusText.Text = T.CraftNoMarket; return; }
        var fee = _meter.Settings.BrokerFeePercent;
        var all = _meter.Recipes.Profit(m.Price, fee, CountOf, _profession.Length == 0 ? null : _profession);
        var rows = new List<Row>(120); var shown = 0; var favs = _meter.Settings.CraftFavourites; var seen = new HashSet<string>();
        foreach (var p in all.OrderByDescending(x => favs.Contains(x.Recipe.Output)))
        {
            var name = Nm(p.Recipe.Output); var (en, es) = _meter.Names.Both(p.Recipe.Output);
            if (!seen.Add(TwinKey(p.Recipe, name))) continue;
            if (!q.IsEmpty && !Match(q, p.Recipe.Output, name, en ?? "", es ?? "", p.Recipe.Name)) continue;
            if (++shown > 150) break;
            var mats = string.Join(", ", p.Recipe.Materials.Select(x => $"{Lb(x.Item)} ×{x.Qty}" + (m.Price(x.Item) is { } mp ? $" @{Kinah(mp)}" : " @?")));
            var margin = p.Cost > 0 ? (double)p.Profit / p.Cost * 100 : 0;
            var sub = $"{p.Recipe.Profession} {p.Recipe.Level} · {p.Recipe.Grade} · {T.CraftSells} {Kinah(p.OutUnit)}" + (p.Recipe.Yield > 1 ? $" ×{p.Recipe.Yield}" : "") +
                      $" → {Kinah(p.Value)} {T.CraftAfterFee} · {T.CraftCost} {Kinah(p.Cost)}" + (p.UnpricedMaterials > 0 ? $" ({p.UnpricedMaterials} {T.CraftUnpriced})" : "") +
                      (p.Times > 0 ? $" · ×{p.Times} {T.CraftNow}" : "") + "\n" + mats;
            var good = p.Profit > 0;
            rows.Add(new Row { Name = name, GradeChip = GradeTag.Of(_meter.Grades.Of(p.Recipe.Output), lang), Sub = sub, Times = (good ? "+" : "") + Kinah(p.Profit) + (p.Cost > 0 ? $" ({margin:+0;-0}%)" : ""), TimesBrush = good ? Ok : Bad, Frame = good && p.Times > 0 ? OkFrame : Frame, Output = p.Recipe.Output, Fav = favs.Contains(p.Recipe.Output), Icons = RowIcons });
        }
        List.ItemsSource = rows;
        StatusText.Text = string.Format(T.CraftMarketFrom, m.Count, m.Server, m.CapturedAt?.ToLocalTime().ToString("dd MMM HH:mm") ?? "?", fee.ToString("0.#", CultureInfo.InvariantCulture));
    }

    private void Reload()
    {
        var names = _meter.Names; var lang = _meter.Settings.PriceSearchLanguage;
        string Nm(int id) => names.Name(id, lang) ?? _meter.Items.NameOf(id);
        string Lb(int id) => names.Label(id, lang) ?? _meter.Items.NameOf(id);   // materials: "(legendary)" only on an ambiguous name
        var q = ItemQuery.Parse(SearchBox.Text);
        if (ProfitBox.IsChecked == true) { ReloadProfit(Nm, Lb, q, lang); return; }
        // Typing searches the whole book (Diego 2026-10-11): the scope only applies while the box is empty
        var scope = !q.IsEmpty ? "all" : Scope; var favs = _meter.Settings.CraftFavourites;
        // one pass over every recipe (1,684: cheap); the scope filters, favourites bypass it and lead the list
        var opts = _meter.Recipes.Evaluate(CountOf, 99, _profession.Length == 0 ? null : _profession);
        var rows = new List<Row>(opts.Count); var craftable = 0; var (keep, variants) = PickTwins(opts, Nm);
        foreach (var o in opts.OrderByDescending(x => favs.Contains(x.Recipe.Output)))
        {
            var fav = favs.Contains(o.Recipe.Output);
            if (!keep.Contains(o.Recipe.Output)) continue;
            if (!fav && scope == "now" && o.Times == 0) continue;
            if (!fav && scope == "near" && o.Times == 0 && o.Missing.Length > 2) continue;
            var name = Nm(o.Recipe.Output); var (en, es) = _meter.Names.Both(o.Recipe.Output);
            if (!q.IsEmpty && !Match(q, o.Recipe.Output, name, en ?? "", es ?? "", o.Recipe.Name)) continue;
            var chip = GradeTag.Of(_meter.Grades.Of(o.Recipe.Output), lang);
            var mats = string.Join(", ", o.Recipe.Materials.Select(m => $"{Lb(m.Item)} ×{m.Qty}"));
            var sub = $"{o.Recipe.Profession} {o.Recipe.Level} · {o.Recipe.Grade}" + (variants[o.Recipe.Output] > 1 ? $" · +{variants[o.Recipe.Output] - 1} {T.CraftVariant}" : "") + $" · {mats}";
            if (o.Times > 0) { craftable++; rows.Add(new Row { Name = name, GradeChip = chip, Sub = sub, Times = "×" + o.Times.ToString(CultureInfo.InvariantCulture), TimesBrush = Ok, Frame = OkFrame, Output = o.Recipe.Output, Fav = fav, Icons = RowIcons }); }
            else rows.Add(new Row { Output = o.Recipe.Output, Fav = fav, Icons = RowIcons, Name = name, GradeChip = chip, Sub = sub + "  —  " + T.CraftMissing + ": " + string.Join(", ", o.Missing.Select(m => $"{Lb(m.Item)} {m.Have}/{m.Need}" + (names.Source(m.Item) is { } src ? $" ({src})" : ""))), Times = "", TimesBrush = Near });
        }
        List.ItemsSource = rows;
        StatusText.Text = !q.IsEmpty ? string.Format(T.CraftMatches, rows.Count, craftable) : string.Format(T.CraftStatus, craftable, rows.Count - craftable);
    }

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp(sender is FrameworkElement { Tag: string t } ? t : "meter");
    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
