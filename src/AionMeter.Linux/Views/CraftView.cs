using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Core.Market;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// Craft tab (WPF CraftView, A2M-12) on Linux: recipes the viewed character can craft now (bag + both warehouses, counted from its
/// inventory.json), the ones short by one or two materials, or the whole book; profit mode over the loaded market file; favourites pinned
/// first (Settings.CraftFavourites, saved through the shell). Virtualised: only the visible rows are built and ask for their icon.
/// </summary>
public sealed class CraftView : HostedView
{
    private static UiText T => UiText.Current;
    private static readonly IBrush Ok = Visuals.Solid(0xFF8EE08A), Near = Visuals.Solid(0xFFF2C25A), Frame = Visuals.Solid(0xFF3A4055), OkFrame = Visuals.Solid(0xFF5EB85A),
        Bad = Visuals.Solid(0xFFF67A6C), RowFill = Visuals.Solid(0x0DFFFFFF);
    private static readonly string[] ProfKeys = ["", "blacksmithing", "tailoring", "jewelcrafting", "alchemy", "cooking"];

    private sealed record Row(int Output, string Name, Grade Grade, string Sub, string Times, IBrush TimesBrush, IBrush Frame, bool Fav);

    /// <summary>Items → "Open in Craft →": the text for the Craft search box (a live view takes it at once, a new one on its first Shown).</summary>
    private static string? _pendingSearch;
    private static event Action<string>? SearchRequested;
    public static void RequestSearch(string text) { _pendingSearch = text; SearchRequested?.Invoke(text); }

    private readonly DataWatch _watch;
    private readonly TextBox _search;
    private readonly ComboBox _scope;
    private readonly CheckBox _profit;
    private readonly StackPanel _profs;
    private readonly TextBlock _status;
    private readonly ItemsControl _list;
    private readonly List<(Image Img, int Item)> _pendingIcons = new();
    private readonly DispatcherTimer _searchTick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string _profession = "";
    private bool _shown, _syncing;
    private Dictionary<int, int> _counts = new();
    private string _countsKey = "";
    private DateTime _countsStamp;

    public CraftView(AppShell shell) : base(shell, "craft", UiText.Current.CraftTitle)
    {
        _watch = DataWatch.Of(shell);
        HeaderRight.Children.Add(_watch.Picker());

        var bar1 = new WrapPanel();
        var (searchHost, search) = ItemKit.SearchBox(T.CraftSearch, 250, () => { _searchTick.Stop(); _searchTick.Start(); });
        _search = search;
        UiKit.Tip(_search, T.CraftSearch);
        searchHost.Margin = new Thickness(0, 0, 10, 6);
        bar1.Children.Add(searchHost);
        _profs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        bar1.Children.Add(_profs);
        BuildProfessions();

        var bar2 = new WrapPanel();
        _syncing = true;
        var scopeIx = Array.IndexOf(new[] { "now", "near", "all" }, shell.Settings.CraftScope);
        _scope = UiKit.Combo([T.CraftScopeNow, T.CraftScopeNear, T.CraftScopeAll], scopeIx < 0 ? 0 : scopeIx, 250);
        _scope.Height = 28; _scope.Margin = new Thickness(0, 0, 10, 0);
        UiKit.Tip(_scope, T.CraftOnlyNowTip);
        _scope.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            Shell.RefreshSettings();
            Shell.Settings.CraftScope = Scope;
            Shell.SaveSettings();
            Reload();
        };
        _syncing = false;
        bar2.Children.Add(_scope);
        _profit = UiKit.Check(T.CraftProfit, false);
        _profit.VerticalAlignment = VerticalAlignment.Center;
        _profit.Margin = new Thickness(0, 0, 10, 0);
        UiKit.Tip(_profit, T.CraftProfitTip);
        _profit.IsCheckedChanged += (_, _) => Reload();
        bar2.Children.Add(_profit);
        var load = UiKit.Chip(T.CraftLoadMarket, () => _ = LoadMarketAsync(), T.CraftLoadMarketTip);
        load.Height = 28;
        bar2.Children.Add(load);

        _status = new TextBlock { FontSize = 12, Foreground = Visuals.TextMute, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var top = new StackPanel { Margin = new Thickness(18, 0, 18, 8), Children = { bar1, bar2, _status } };

        // "All recipes" is 1,684 rows: only the visible ones are built, and only they ask for their icon (Diego 2026-10-11: the full list lagged the PC)
        _list = new ItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<Row>((r, _) => r is null ? new Border() : RowView(r)),
        };
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Place(top, 0, 0);
        body.Place(new ScrollViewer { Content = _list, Margin = new Thickness(14, 0, 14, 10), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 1, 0);
        SetBody(body);

        _searchTick.Tick += (_, _) => { _searchTick.Stop(); Reload(); };
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); RefreshIcons(); };
        shell.Icons.Loaded += () => { if (_shown && !_iconTick.IsEnabled) _iconTick.Start(); };
        _watch.Changed += c => { if (_shown && (c & (DataChange.Characters | DataChange.Market)) != 0) Reload(); };
        _watch.SelectionChanged += () => { if (_shown) Reload(); };
        SearchRequested += SetSearch;
        shell.WhenEngine(e => { e.Names.Changed += () => Dispatcher.UIThread.Post(() => { if (_shown) Reload(); }); if (_shown) Reload(); });
    }

    public override void Shown()
    {
        _shown = true;
        _watch.Acquire();
        if (!_argsApplied) Shell.WhenEngine(_ => ApplyViewArgs());
        if (_pendingSearch is { } q) SetSearch(q);
        else Reload();
    }

    private bool _argsApplied;   // per instance: --popout makes a second view after the card's

    /// <summary>--view search=…, profit=1, prof=alchemy (screenshots).</summary>
    private void ApplyViewArgs()
    {
        if (_argsApplied) return;
        _argsApplied = true;
        var a = App.ViewArgs;
        if (a.TryGetValue("prof", out var p)) { _profession = p; BuildProfessions(); }
        if (a.TryGetValue("profit", out var pr) && pr == "1") _profit.IsChecked = true;
        if (a.TryGetValue("search", out var q)) SetSearch(q);
        else Reload();
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
        Title = T.CraftTitle;
        BuildProfessions();
        if (_shown) Reload();
    }

    private void SetSearch(string text)
    {
        _pendingSearch = null;
        _search.Text = text;
        _search.CaretIndex = text.Length;
        _searchTick.Stop();
        Reload();
    }

    private string Scope => _scope.SelectedIndex switch { 1 => "near", 2 => "all", _ => "now" };

    private void BuildProfessions()
    {
        _profs.Children.Clear();
        string[] labels = [T.CraftAll, "Blacksmith", "Tailor", "Jewel", "Alchemy", "Cooking"];
        for (var i = 0; i < ProfKeys.Length; i++)
        {
            var key = ProfKeys[i];
            var on = key == _profession;
            var text = new TextBlock { Text = labels[i], FontSize = 12, FontWeight = FontWeight.SemiBold, FontFamily = Visuals.Number, Foreground = on ? Visuals.Text : Visuals.TextDim };
            var b = new Border
            {
                Child = text, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 4, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                BorderBrush = on ? Visuals.Gold : Visuals.GoldDim, Background = on ? Visuals.Solid(0x55C9A45C) : Visuals.Solid(0x66111726), Cursor = new Cursor(StandardCursorType.Hand),
            };
            b.PointerEntered += (_, _) => text.Foreground = Visuals.GoldBright;
            b.PointerExited += (_, _) => text.Foreground = key == _profession ? Visuals.Text : Visuals.TextDim;
            b.PointerReleased += (_, e) => { if (e.InitialPressMouseButton != MouseButton.Left) return; _profession = key; BuildProfessions(); Reload(); };
            _profs.Children.Add(b);
        }
    }

    /// <summary>Counts of the viewed character over every container but the equipped set (WPF InventoryStore.CountOf), from its file.</summary>
    private Func<int, int> CountOf(MeterEngine m)
    {
        var key = _watch.Viewed;
        var path = Path.Combine(AppSettings.AppDataDir, "characters", key, "inventory.json");
        DateTime w = default;
        try { if (key.Length > 0 && File.Exists(path)) w = File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (key != _countsKey || w != _countsStamp)
        {
            _countsKey = key; _countsStamp = w;
            var counts = new Dictionary<int, int>(256);
            if (key.Length > 0 && m.OtherInventories.Get(path) is { } f)
                foreach (var s in f.Stacks) if (s.Container != Containers.Equipped) counts[s.Item] = counts.GetValueOrDefault(s.Item) + s.Count;
            _counts = counts;
        }
        var c = _counts;
        return id => c.GetValueOrDefault(id);
    }

    // aion2hub lists 41 recipe pairs with two output ids for one name and identical materials (faction/version twins, e.g. 510716007/510716019):
    // one row per name + materials + profession + level, so the list never shows the same craft twice (Diego, 2026-10-11)
    // 602 of the 1,684 hub recipes share their name with another one: the Elyos/Asmodian item pair (same materials, or the base item
    // of the other faction) and the two routes of the top gear (level 90 from the previous tier vs level 0 from a token). One row per
    // name and profession (Diego 2026-10-11, "están duplicadas otra vez"): the variant the character can craft, else the one missing least.
    // grade in the key: same-named outputs of different grades ("Artisan's Refining Stone" rare/legendary in EN) are different recipes, not variants
    private static string TwinKey(MeterEngine m, Recipe r, string name) => name + "|" + r.Profession + "|" + (int)m.Grades.Of(r.Output);

    private static (HashSet<int> Keep, Dictionary<int, int> Variants) PickTwins(MeterEngine m, List<CraftOption> opts, Func<int, string> nm)
    {
        var best = new Dictionary<string, CraftOption>(opts.Count);
        var count = new Dictionary<string, int>(opts.Count);
        foreach (var o in opts)
        {
            var key = TwinKey(m, o.Recipe, nm(o.Recipe.Output));
            count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
            if (!best.TryGetValue(key, out var b) || Better(o, b)) best[key] = o;
        }
        var keep = new HashSet<int>(best.Count); var variants = new Dictionary<int, int>(best.Count);
        foreach (var (key, o) in best) { keep.Add(o.Recipe.Output); variants[o.Recipe.Output] = count[key]; }
        return (keep, variants);
    }

    private static bool Better(CraftOption a, CraftOption b) =>
        a.Times != b.Times ? a.Times > b.Times : a.Missing.Length != b.Missing.Length ? a.Missing.Length < b.Missing.Length : a.Recipe.Level < b.Recipe.Level;

    private static bool Match(MeterEngine m, ItemQuery q, int output, string name, string en, string es, string recipe) =>
        q.Matches(m.Grades.Of(output), ItemNames.Normalize(name), ItemNames.Normalize(en), ItemNames.Normalize(es), ItemNames.Normalize(recipe));

    private void Reload()
    {
        _pendingIcons.Clear();
        if (Shell.Engine is not { } m) { _status.Text = "…"; return; }
        var names = m.Names; var lang = Shell.Settings.PriceSearchLanguage;
        string Nm(int id) => names.Name(id, lang) ?? m.Items.NameOf(id);
        string Lb(int id) => names.Label(id, lang) ?? m.Items.NameOf(id);   // materials: "(legendary)" only on an ambiguous name
        var q = ItemQuery.Parse(_search.Text);
        var countOf = CountOf(m);
        var favs = Shell.Settings.CraftFavourites;
        if (_profit.IsChecked == true) { ReloadProfit(m, Nm, Lb, q, countOf, favs); return; }
        // Typing searches the whole book (Diego 2026-10-11): the scope only applies while the box is empty
        var scope = !q.IsEmpty ? "all" : Scope;
        // one pass over every recipe (1,684: cheap); the scope filters, favourites bypass it and lead the list
        var opts = m.Recipes.Evaluate(countOf, 99, _profession.Length == 0 ? null : _profession);
        var (keep, variants) = PickTwins(m, opts, Nm);
        var rows = new List<Row>(keep.Count);
        var craftable = 0;
        for (var pass = 0; pass < 2; pass++)   // favourites first, then the rest, each in the book's order (WPF OrderByDescending is stable)
            foreach (var o in opts)
            {
                var fav = favs.Contains(o.Recipe.Output);
                if (fav != (pass == 0) || !keep.Contains(o.Recipe.Output)) continue;
                if (!fav && scope == "now" && o.Times == 0) continue;
                if (!fav && scope == "near" && o.Times == 0 && o.Missing.Length > 2) continue;
                var name = Nm(o.Recipe.Output); var (en, es) = names.Both(o.Recipe.Output);
                if (!q.IsEmpty && !Match(m, q, o.Recipe.Output, name, en ?? "", es ?? "", o.Recipe.Name)) continue;
                var mats = Join(o.Recipe.Materials, x => $"{Lb(x.Item)} ×{x.Qty}", ", ");
                var sub = $"{o.Recipe.Profession} {o.Recipe.Level} · {o.Recipe.Grade}" + (variants[o.Recipe.Output] > 1 ? $" · +{variants[o.Recipe.Output] - 1} {T.CraftVariant}" : "") + $" · {mats}";
                var grade = m.Grades.Of(o.Recipe.Output);
                if (o.Times > 0) { craftable++; rows.Add(new Row(o.Recipe.Output, name, grade, sub, "×" + o.Times.ToString(CultureInfo.InvariantCulture), Ok, OkFrame, fav)); }
                else rows.Add(new Row(o.Recipe.Output, name, grade, sub + "  —  " + T.CraftMissing + ": " + Join(o.Missing, x => $"{Lb(x.Item)} {x.Have}/{x.Need}" + (names.Source(x.Item) is { } src ? $" ({src})" : ""), ", "), "", Near, Frame, fav));
            }
        _list.ItemsSource = rows;
        _status.Text = !q.IsEmpty ? string.Format(T.CraftMatches, rows.Count, craftable) : string.Format(T.CraftStatus, craftable, rows.Count - craftable);
    }

    private static string Join<TItem>(TItem[] items, Func<TItem, string> f, string sep)
    {
        var parts = new string[items.Length];
        for (var i = 0; i < items.Length; i++) parts[i] = f(items[i]);
        return string.Join(sep, parts);
    }

    /// <summary>Profit mode: every recipe whose output is on the market, most profitable first (cheapest listings, fee from settings).</summary>
    private void ReloadProfit(MeterEngine m, Func<int, string> nm, Func<int, string> lb, ItemQuery q, Func<int, int> countOf, List<int> favs)
    {
        var mk = m.Market;
        if (mk.Count == 0) { _list.ItemsSource = Array.Empty<Row>(); _status.Text = T.CraftNoMarket; return; }
        var fee = Shell.Settings.BrokerFeePercent;
        var all = m.Recipes.Profit(mk.Price, fee, countOf, _profession.Length == 0 ? null : _profession);
        var rows = new List<Row>(120); var shown = 0; var seen = new HashSet<string>();
        for (var pass = 0; pass < 2 && shown <= 150; pass++)
            foreach (var p in all)
            {
                var fav = favs.Contains(p.Recipe.Output);
                if (fav != (pass == 0)) continue;
                var name = nm(p.Recipe.Output); var (en, es) = m.Names.Both(p.Recipe.Output);
                if (!seen.Add(TwinKey(m, p.Recipe, name))) continue;
                if (!q.IsEmpty && !Match(m, q, p.Recipe.Output, name, en ?? "", es ?? "", p.Recipe.Name)) continue;
                if (++shown > 150) break;
                var mats = Join(p.Recipe.Materials, x => $"{lb(x.Item)} ×{x.Qty}" + (mk.Price(x.Item) is { } mp ? $" @{ItemKit.Kinah(mp)}" : " @?"), ", ");
                var margin = p.Cost > 0 ? (double)p.Profit / p.Cost * 100 : 0;
                var sub = $"{p.Recipe.Profession} {p.Recipe.Level} · {p.Recipe.Grade} · {T.CraftSells} {ItemKit.Kinah(p.OutUnit)}" + (p.Recipe.Yield > 1 ? $" ×{p.Recipe.Yield}" : "") +
                          $" → {ItemKit.Kinah(p.Value)} {T.CraftAfterFee} · {T.CraftCost} {ItemKit.Kinah(p.Cost)}" + (p.UnpricedMaterials > 0 ? $" ({p.UnpricedMaterials} {T.CraftUnpriced})" : "") +
                          (p.Times > 0 ? $" · ×{p.Times} {T.CraftNow}" : "") + "\n" + mats;
                var good = p.Profit > 0;
                rows.Add(new Row(p.Recipe.Output, name, m.Grades.Of(p.Recipe.Output), sub, (good ? "+" : "") + ItemKit.Kinah(p.Profit) + (p.Cost > 0 ? $" ({margin:+0;-0}%)" : ""),
                    good ? Ok : Bad, good && p.Times > 0 ? OkFrame : Frame, fav));
            }
        _list.ItemsSource = rows;
        _status.Text = string.Format(T.CraftMarketFrom, mk.Count, mk.Server, mk.CapturedAt?.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) ?? "?", fee.ToString("0.#", CultureInfo.InvariantCulture));
    }

    private Control RowView(Row r)
    {
        var icon = new Image { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        if (Shell.Icons.Item(r.Output) is { } img) icon.Source = img;
        else _pendingIcons.Add((icon, r.Output));
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        nameLine.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = Visuals.Text });
        if (ItemKit.GradeChip(r.Grade, Shell.Settings.PriceSearchLanguage) is { } chip) nameLine.Children.Add(chip);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(nameLine);
        text.Children.Add(new TextBlock { Text = r.Sub, FontSize = 11, Foreground = Visuals.TextMute, TextWrapping = TextWrapping.Wrap });
        var left = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        left.Children.Add(icon);
        left.Children.Add(text);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = r.Times, Foreground = r.TimesBrush, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        var star = new TextBlock { Text = r.Fav ? "★" : "☆", FontSize = 15, Foreground = r.Fav ? Near : Frame, VerticalAlignment = VerticalAlignment.Center };
        var starBtn = new Border { Child = star, Padding = new Thickness(4, 0), Margin = new Thickness(8, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent };
        starBtn.PointerEntered += (_, _) => star.Foreground = Visuals.GoldBright;
        starBtn.PointerExited += (_, _) => star.Foreground = r.Fav ? Near : Frame;
        starBtn.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) { e.Handled = true; ToggleFavourite(r.Output); } };
        UiKit.Tip(starBtn, T.CraftFavTip);
        right.Children.Add(starBtn);
        var g = UiKit.Row("*,Auto", left, right);
        return new Border { Background = RowFill, BorderBrush = r.Frame, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 6), Child = g };
    }

    private void RefreshIcons()
    {
        for (var i = _pendingIcons.Count - 1; i >= 0; i--)
        {
            var (img, item) = _pendingIcons[i];
            if (Shell.Icons.Item(item) is not { } bmp) continue;
            img.Source = bmp;
            _pendingIcons.RemoveAt(i);
        }
    }

    /// <summary>Star on a row: pin or unpin the recipe (favourites always come first). settings.json is the UI's to write; the daemon reloads it.</summary>
    private void ToggleFavourite(int output)
    {
        Shell.RefreshSettings();
        var favs = Shell.Settings.CraftFavourites;
        if (!favs.Remove(output)) favs.Add(output);
        Shell.SaveSettings();
        Reload();
    }

    /// <summary>Craft → Load market file…: the JSON the browser-console script saved in Downloads (Prices → Copy market script). Without a
    /// file picker (no portal under Xwayland) the newest ~/Downloads/aion2exchange-*.json is taken.</summary>
    private async Task LoadMarketAsync()
    {
        if (Shell.Engine is not { } m) return;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string? path = null;
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is { CanOpen: true } sp)
            {
                var start = Directory.Exists(downloads) ? await sp.TryGetFolderFromPathAsync(downloads) : null;
                var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = T.CraftLoadMarket, AllowMultiple = false, SuggestedStartLocation = start, SuggestedFileName = "aion2exchange-",
                    FileTypeFilter = [new FilePickerFileType("aion2.exchange export (*.json)") { Patterns = ["*.json"] }],
                });
                if (files.Count == 0) return;
                path = files[0].TryGetLocalPath();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or NotSupportedException or IOException) { Log.Info("Craft: no file picker (" + e.Message + ")"); }
        if (path is null && Directory.Exists(downloads))
        {
            var newest = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(downloads, "aion2exchange-*.json"))
            {
                var w = File.GetLastWriteTimeUtc(f);
                if (w > newest) { newest = w; path = f; }
            }
        }
        if (path is null || !m.ImportMarket(path)) { _status.Text = T.CraftMarketBad; return; }
        _profit.IsChecked = true;
        Reload();
    }
}
