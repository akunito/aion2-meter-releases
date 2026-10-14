using System.Globalization;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Craft tab (WPF CraftView, A2M-12) on the layer-shell card (A2M-50, port of the Avalonia CraftView, behaviour unchanged): recipes the viewed
/// character can craft now (bag + both warehouses, counted from its inventory.json), the ones short by one or two materials, or the whole book;
/// profit mode over the loaded market file; favourites pinned first (Settings.CraftFavourites, saved through the shell). The list is a
/// GtkListView: only the visible rows exist, are reused while scrolling and ask for their icon. Row rules: <see cref="CraftRows"/>.
/// </summary>
internal sealed class CraftView : HostedView
{
    private static UiText T => UiText.Current;

    /// <summary>Items → "Open in Craft →": the text for the Craft search box (a live view takes it at once, a new one on its first Shown).</summary>
    private static string? _pendingSearch;
    private static event Action<string>? SearchRequested;
    public static void RequestSearch(string text) { _pendingSearch = text; SearchRequested?.Invoke(text); }

    private readonly DataWatch _watch;
    private readonly Gtk.Entry _search;
    private readonly Gtk.DropDown _scope;
    private readonly Gtk.CheckButton _profit;
    private readonly Gtk.Button _load;
    private readonly Gtk.Button[] _profs = new Gtk.Button[CraftRows.ProfKeys.Length];
    private readonly Gtk.Label _status;
    private readonly Gtk.StringList _model;
    private readonly Gtk.ListView _list;
    private readonly Dictionary<IntPtr, RowWidget> _rowWidgets = new();
    private readonly UiTimer _searchTick = new(TimeSpan.FromMilliseconds(150));
    private readonly UiTimer _iconTick = new(TimeSpan.FromMilliseconds(600));
    private List<CraftRow> _rows = new();
    private string[] _blank = [];
    private string _profession = "";
    private bool _syncing, _detached;
    private Dictionary<int, int> _counts = new();
    private string _countsKey = "";
    private DateTime _countsStamp;

    public CraftView(AppShell shell) : base(shell, "craft", UiText.Current.CraftTitle)
    {
        CraftCss.Ensure(shell.Host.Scale);
        _watch = DataWatch.Of(shell);
        var (picker, unsubscribe) = _watch.Picker();
        HeaderRight.Append(picker);
        OnDetach(unsubscribe);

        var top = UiKit.VBox();
        UiKit.Margin(top, 18, 0, 18, 8);
        // Avalonia: [search][professions] / [scope][Profit][Load…] in WrapPanels; at 560 px that is three lines, and "Load market file…" sits by
        // the search (scope + Profit + Load is ~515 px in Spanish, past the ~480 px the card leaves)
        var bar1 = UiKit.HBox();
        bar1.MarginBottom = UiKit.Px(6);
        var (searchHost, search) = ItemKit.SearchBox(T.CraftSearch, 250, () => _searchTick.Restart());
        _search = search;
        UiKit.Tip(_search, T.CraftSearch);
        searchHost.MarginEnd = UiKit.Px(10);
        bar1.Append(searchHost);
        _load = UiKit.Chip(T.CraftLoadMarket, () => _ = LoadMarketAsync(), T.CraftLoadMarketTip);
        _load.Hexpand = false;
        bar1.Append(_load);
        top.Append(bar1);

        var profs = UiKit.HBox();
        profs.MarginBottom = UiKit.Px(6);
        for (var i = 0; i < _profs.Length; i++)
        {
            var key = CraftRows.ProfKeys[i];
            var b = Gtk.Button.NewWithLabel(ProfLabel(i));
            b.AddCssClass("k-craft-prof");
            b.SetCursorFromName("pointer");
            b.FocusOnClick = false;
            b.Valign = Gtk.Align.Center;
            b.OnClicked += (_, _) => { _profession = key; MarkProfessions(); Reload(); };
            _profs[i] = b;
            profs.Append(b);
        }
        MarkProfessions();
        top.Append(profs);

        var bar2 = UiKit.HBox();
        _syncing = true;
        _scope = UiKit.Combo([T.CraftScopeNow, T.CraftScopeNear, T.CraftScopeAll], CraftRows.ScopeIndex(shell.Settings.CraftScope), 250);
        _scope.MarginEnd = UiKit.Px(10);
        UiKit.Tip(_scope, T.CraftOnlyNowTip);
        UiKit.OnSelected(_scope, () =>
        {
            if (_syncing) return;
            Shell.RefreshSettings();
            Shell.Settings.CraftScope = CraftRows.ScopeKey(UiKit.Selected(_scope));
            Shell.SaveSettings();
            Reload();
        });
        _syncing = false;
        bar2.Append(_scope);
        _profit = UiKit.Check(T.CraftProfit, false);
        _profit.Valign = Gtk.Align.Center;
        // the kit's wrapping label (1-char natural width) wrapped "Profit" one letter per line beside the combo (measured): one line, natural width
        if (_profit.GetChild() is Gtk.Label profitLabel) { profitLabel.Wrap = false; profitLabel.MaxWidthChars = -1; profitLabel.Hexpand = false; }
        UiKit.Tip(_profit, T.CraftProfitTip);
        _profit.OnToggled += (_, _) => Reload();
        bar2.Append(_profit);
        top.Append(bar2);

        _status = UiKit.Line("", 12, Fg.Mute);
        _status.MarginTop = UiKit.Px(6);
        top.Append(_status);

        // "All recipes" is 1,684 rows: only the visible ones are built, and only they ask for their icon (Diego 2026-10-11: the full list lagged the PC)
        _model = Gtk.StringList.New([]);
        var factory = Gtk.SignalListItemFactory.New();
        factory.OnSetup += (_, a) =>
        {
            var item = (Gtk.ListItem)a.Object;
            var w = new RowWidget(this);
            item.SetChild(w.Root);
            item.SetActivatable(false);
            item.SetFocusable(false);
            _rowWidgets[w.Root.Handle.DangerousGetHandle()] = w;
        };
        factory.OnBind += (_, a) =>
        {
            var item = (Gtk.ListItem)a.Object;
            if (item.GetChild() is not { } c || !_rowWidgets.TryGetValue(c.Handle.DangerousGetHandle(), out var w)) return;
            var pos = (int)item.GetPosition();
            if (pos >= 0 && pos < _rows.Count) w.Bind(_rows[pos]);
        };
        factory.OnTeardown += (_, a) =>
        {
            if (((Gtk.ListItem)a.Object).GetChild() is { } c) _rowWidgets.Remove(c.Handle.DangerousGetHandle());
        };
        _list = Gtk.ListView.New(Gtk.NoSelection.New(_model), factory);
        _list.AddCssClass("k-craft-list");
        var scroll = UiKit.Scroll(_list, false);
        UiKit.Margin(scroll, 14, 0, 14, 10);

        var body = UiKit.VBox();
        body.Append(top);
        body.Append(scroll);
        SetBody(body);

        _searchTick.Tick += () => { _searchTick.Stop(); Reload(); };
        _iconTick.Tick += () => { _iconTick.Stop(); RefreshIcons(); };
        OnDetach(() => { _detached = true; _searchTick.Stop(); _iconTick.Stop(); });
        Action iconsLoaded = () => { if (IsShown && !_iconTick.IsEnabled) _iconTick.Start(); };
        Listen(() => Shell.Icons.Loaded += iconsLoaded, () => Shell.Icons.Loaded -= iconsLoaded);
        Action<DataChange> changed = c => { if (IsShown && (c & (DataChange.Characters | DataChange.Market)) != 0) Reload(); };
        Listen(() => _watch.Changed += changed, () => _watch.Changed -= changed);
        Action selection = () => { if (IsShown) Reload(); };
        Listen(() => _watch.SelectionChanged += selection, () => _watch.SelectionChanged -= selection);
        Action<string> requested = SetSearch;
        Listen(() => SearchRequested += requested, () => SearchRequested -= requested);
        shell.WhenEngine(e =>
        {
            if (_detached) return;
            // raised on the name resolver's thread
            Action names = () => UiLoop.Post(() => { if (!_detached && IsShown) Reload(); });
            Listen(() => e.Names.Changed += names, () => e.Names.Changed -= names);
            if (IsShown) Reload();
        });
    }

    public override void Shown()
    {
        _watch.Acquire();
        if (_pendingSearch is { } q) SetSearch(q);
        else Reload();
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
        Title = T.CraftTitle;
        _profs[0].SetLabel(ProfLabel(0));
        _search.PlaceholderText = T.CraftSearch;
        UiKit.Tip(_search, T.CraftSearch);
        UiKit.SetChipText(_load, T.CraftLoadMarket);
        UiKit.Tip(_load, T.CraftLoadMarketTip);
        if (_profit.GetChild() is Gtk.Label pl) pl.SetText(T.CraftProfit);
        UiKit.Tip(_profit, T.CraftProfitTip);
        _syncing = true;
        try { UiKit.SetItems(_scope, [T.CraftScopeNow, T.CraftScopeNear, T.CraftScopeAll], UiKit.Selected(_scope)); }
        finally { _syncing = false; }
        UiKit.Tip(_scope, T.CraftOnlyNowTip);
        if (IsShown) Reload();
    }

    private static string ProfLabel(int i) => i switch { 0 => T.CraftAll, 1 => "Blacksmith", 2 => "Tailor", 3 => "Jewel", 4 => "Alchemy", _ => "Cooking" };

    private void MarkProfessions()
    {
        for (var i = 0; i < _profs.Length; i++)
            if (CraftRows.ProfKeys[i] == _profession) _profs[i].AddCssClass("k-craft-on");
            else _profs[i].RemoveCssClass("k-craft-on");
    }

    private void SetSearch(string text)
    {
        _pendingSearch = null;
        _search.SetText(text);
        _search.SetPosition(text.Length);
        _searchTick.Stop();
        Reload();
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

    private void Reload()
    {
        if (_detached) return;
        if (Shell.Engine is not { } m) { _status.SetText("…"); return; }
        var names = m.Names;
        var lang = Shell.Settings.ResolvedNamesLanguage;
        var n = new CraftNames(
            id => names.Name(id, lang) ?? m.Items.NameOf(id),
            id => names.Label(id, lang) ?? m.Items.NameOf(id),   // materials: "(legendary)" only on an ambiguous name
            names.Both, names.Source, m.Grades.Of);
        var q = ItemQuery.Parse(_search.GetText());
        var countOf = CountOf(m);
        var favs = Shell.Settings.CraftFavourites;
        var prof = _profession.Length == 0 ? null : _profession;
        if (_profit.Active)
        {
            var mk = m.Market;
            if (mk.Count == 0) { Show([]); _status.SetText(T.CraftNoMarket); return; }
            var fee = Shell.Settings.BrokerFeePercent;
            Show(CraftRows.Profit(m.Recipes.Profit(mk.Price, fee, countOf, prof), n, mk.Price, q, favs, T));
            _status.SetText(string.Format(T.CraftMarketFrom, mk.Count, mk.Server, mk.CapturedAt?.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) ?? "?", fee.ToString("0.#", CultureInfo.InvariantCulture)));
            return;
        }
        // one pass over every recipe (1,684: cheap); the scope filters, favourites bypass it and lead the list
        var rows = CraftRows.Book(m.Recipes.Evaluate(countOf, 99, prof), n, q, CraftRows.ScopeKey(UiKit.Selected(_scope)), favs, T, out var craftable);
        Show(rows);
        _status.SetText(!q.IsEmpty ? string.Format(T.CraftMatches, rows.Count, craftable) : string.Format(T.CraftStatus, craftable, rows.Count - craftable));
    }

    /// <summary>New rows: the model is n placeholder strings (the rows live in <see cref="_rows"/>, a row binds by position); replacing them rebinds the visible rows.</summary>
    private void Show(List<CraftRow> rows)
    {
        _rows = rows;
        if (_blank.Length != rows.Count)
        {
            _blank = new string[rows.Count];
            Array.Fill(_blank, "");   // GirCore passes a NULL-terminated char**: a null entry would end the list there
        }
        _model.Splice(0, _model.GetNItems(), _blank);
    }

    private void RefreshIcons()
    {
        foreach (var w in _rowWidgets.Values) if (w.IconPending) w.AskIcon();
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

    /// <summary>
    /// Craft → Load market file…: the JSON the browser-console script saved in Downloads (Prices → Copy market script). GtkFileDialog (the
    /// portal on the desktop, GTK's own chooser without one); when it fails rather than being dismissed, the newest ~/Downloads/aion2exchange-*.json.
    /// </summary>
    private async Task LoadMarketAsync()
    {
        if (Shell.Engine is not { } m) return;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string? path = null;
        var picked = false;
        if (!PickerSafe()) Log.Info("Craft: no file picker (no portal on the session bus, no GTK FileChooser schema)");
        else
            try
            {
                var dialog = Gtk.FileDialog.New();
                dialog.SetTitle(T.CraftLoadMarket);
                if (Directory.Exists(downloads)) dialog.SetInitialFolder(Gio.FileHelper.NewForPath(downloads));
                var filter = Gtk.FileFilter.New();
                filter.SetName("aion2.exchange export (*.json)");
                filter.AddPattern("*.json");
                dialog.SetDefaultFilter(filter);
                // no parent: the card is a layer surface, not an xdg toplevel a dialog can be transient for
                var file = await dialog.OpenAsync(null);
                picked = true;
                path = file?.GetPath();
                if (path is null) return;
            }
            catch (GLib.GException e)
            {
                if (CraftRows.IsDismissal(e.Message, GLib.Functions.Dgettext("gtk40", "Dismissed by user"), GLib.Functions.Dgettext("gtk40", "Cancelled by application"))) return;
                Log.Info("Craft: no file picker (" + e.Message + ")");
            }
        if (_detached) return;
        if (!picked) path = CraftRows.NewestExport(downloads);
        if (path is null || !m.ImportMarket(path)) { _status.SetText(T.CraftMarketBad); return; }
        if (_profit.Active) Reload();
        else _profit.Active = true;   // toggled → Reload
    }

    /// <summary>
    /// Whether GtkFileDialog can open without killing the process. With no portal GTK falls back to its own chooser, which aborts the whole
    /// card with g_error "No GSettings schemas are installed" when org.gtk.gtk4.Settings.FileChooser is not on XDG_DATA_DIRS (measured in the
    /// nested sway with the bare LD_LIBRARY_PATH wrapper, 2026-10-13). So: the schema is installed, or the portal is on the session bus
    /// (running or activatable). Otherwise the caller takes the newest export in Downloads, as Avalonia did without a picker.
    /// </summary>
    private static bool PickerSafe()
    {
        try { if (Gio.SettingsSchemaSource.GetDefault()?.Lookup("org.gtk.gtk4.Settings.FileChooser", true) is not null) return true; }
        catch (GLib.GException) { }
        const string portal = "org.freedesktop.portal.Desktop";
        try
        {
            var bus = Gio.Functions.BusGetSync(Gio.BusType.Session, null);
            var owner = bus.CallSync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner",
                GLib.Variant.NewTuple([GLib.Variant.NewString(portal)]), GLib.VariantType.New("(b)"), Gio.DBusCallFlags.None, 500, null);
            if (owner.GetChildValue(0).GetBoolean()) return true;
            var names = bus.CallSync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListActivatableNames",
                null, GLib.VariantType.New("(as)"), Gio.DBusCallFlags.None, 500, null);
            return Array.IndexOf(names.GetChildValue(0).GetStrv(out _), portal) >= 0;
        }
        catch (GLib.GException) { return false; }
    }

    /// <summary>One reusable list row (the Avalonia RowView): icon, name + grade chip, the wrapped detail line, ×N / profit and the star.</summary>
    private sealed class RowWidget
    {
        private readonly CraftView _owner;
        public readonly Gtk.Box Root;
        private readonly Gtk.Image _icon;
        private readonly Gtk.Label _name, _chipText, _sub, _times, _starText;
        private readonly Gtk.Box _chip;
        private readonly Gtk.Button _star;
        private string _chipClass = "", _toneClass = "";
        private int _output;
        public bool IconPending { get; private set; }

        public RowWidget(CraftView owner)
        {
            _owner = owner;
            Root = UiKit.HBox();
            Root.AddCssClass("k-craft-row");
            _icon = ItemKit.Image(null, 30);
            _icon.MarginEnd = UiKit.Px(10);
            _icon.Valign = Gtk.Align.Center;
            Root.Append(_icon);
            var text = UiKit.VBox();
            text.Hexpand = true;
            text.Valign = Gtk.Align.Center;
            var nameLine = UiKit.HBox();
            nameLine.Halign = Gtk.Align.Start;
            // the scroll does not propagate natural width (GtkScrolledWindow default), so a full-width name cannot widen the card
            _name = UiKit.Line("", 12.5);
            _name.AddCssClass("k-craft-name");
            _name.MaxWidthChars = -1;
            _name.Hexpand = false;
            nameLine.Append(_name);
            _chip = UiKit.HBox();
            _chip.AddCssClass("k-grade");
            _chip.Valign = Gtk.Align.Center;
            _chipText = Gtk.Label.New("");
            _chip.Append(_chipText);
            nameLine.Append(_chip);
            text.Append(nameLine);
            _sub = UiKit.Text("", 11, Fg.Mute);
            _sub.AddCssClass("k-craft-sub");
            text.Append(_sub);
            Root.Append(text);
            _times = Gtk.Label.New("");
            _times.AddCssClass("k-craft-times");
            _times.Valign = Gtk.Align.Center;
            Root.Append(_times);
            _starText = Gtk.Label.New("☆");
            _star = Gtk.Button.New();
            _star.Child = _starText;
            _star.AddCssClass("k-craft-star");
            _star.SetCursorFromName("pointer");
            _star.FocusOnClick = false;
            _star.Valign = Gtk.Align.Center;
            _star.SetTooltipText(T.CraftFavTip);
            _star.OnClicked += (_, _) => { if (_output != 0) _owner.ToggleFavourite(_output); };
            Root.Append(_star);
        }

        public void Bind(CraftRow r)
        {
            _output = r.Output;
            AskIcon();
            _name.SetText(r.Name);
            var lang = _owner.Shell.Settings.ResolvedNamesLanguage;
            if (r.Grade == Grade.Unknown) _chip.Visible = false;
            else
            {
                var cls = "k-g" + ((int)r.Grade < Palette.GradeCount ? (int)r.Grade : 0).ToString(CultureInfo.InvariantCulture);
                if (cls != _chipClass)
                {
                    if (_chipClass.Length > 0) _chip.RemoveCssClass(_chipClass);
                    _chip.AddCssClass(cls);
                    _chipClass = cls;
                }
                _chipText.SetText(ItemGrades.Word(r.Grade, lang));
                _chip.Visible = true;
            }
            _sub.SetText(r.Sub);
            _times.SetText(r.Times);
            _times.Visible = r.Times.Length > 0;
            var tone = r.Tone switch { CraftTone.Ok => "k-craft-ok", CraftTone.Bad => "k-craft-bad", _ => "k-craft-near" };
            if (tone != _toneClass)
            {
                if (_toneClass.Length > 0) _times.RemoveCssClass(_toneClass);
                _times.AddCssClass(tone);
                _toneClass = tone;
            }
            if (r.OkFrame) Root.AddCssClass("k-craft-okframe"); else Root.RemoveCssClass("k-craft-okframe");
            _starText.SetText(r.Fav ? "★" : "☆");
            if (r.Fav) _star.AddCssClass("k-craft-fav"); else _star.RemoveCssClass("k-craft-fav");
        }

        public void AskIcon()
        {
            var tex = _owner.Shell.Icons.Item(_output);
            if (tex is null) _icon.Clear();
            else _icon.SetFromPaintable(tex);
            IconPending = tex is null;
        }
    }
}
