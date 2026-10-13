using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Core.Builds;
using AionMeter.Core.Events;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// "Import from questlog.gg" (WPF Windows/ImportBuildWindow, A2M-21) on Linux: a build link, the user's own builds ("My builds": skill and
/// planner builds of the questlog user in <see cref="AppSettings.QuestlogSlug"/>, public tRPC, no login) or a search of the public builds
/// (current class by default). A pop-out card window, not modal: what is picked goes to the Build tab through the callbacks, which close
/// the window (a planner build of "My builds" goes into the active build and the window stays open, as WPF).
/// </summary>
internal sealed class ImportBuildWindow : DialogCard
{
    private static UiText T => UiText.Current;
    private static readonly IBrush RowBack = Visuals.Bg1, RowLine = Visuals.Line, TagBack = Visuals.Bg2, Like = Visuals.Solid(0xFFEF4D56);

    private readonly string _classId;
    private readonly TextBox _url, _user, _search;
    private readonly CheckBox _allClasses;
    private readonly Border _tabMine, _tabSearch;
    private readonly Control _minePanel, _searchPanel;
    private readonly StackPanel _results = new() { Margin = new Thickness(0, 4, 0, 8) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status;
    private readonly Button _prev, _next, _go;
    private string _term = "";
    private int _page = 1, _seq;
    private bool _busy, _mineTab;
    private QuestlogService.MyBuilds? _mine;

    /// <summary>The build to save (a fetched link / search row, or one of "My builds" with its paired planner build and slug).</summary>
    public Action<Build, QuestlogDaevPlan?, string?>? Picked { get; init; }
    /// <summary>A daevanion-planner link pasted in the link box: the Build tab imports it as the plan of the active build.</summary>
    public Action<string>? PlanUrl { get; init; }
    /// <summary>Puts a planner build into the active build (made from the profile when there is none); returns the status text.</summary>
    public Func<QuestlogDaevPlan, string, string>? ImportPlan { get; init; }

    public ImportBuildWindow(AppShell shell, string classId) : base(shell, "import-build", T.BuildImportTitle, 760, 640)
    {
        _classId = classId;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };

        var link = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        link.Children.Add(UiKit.SectionLabel(T.ImportUrlLabel));
        _url = UiKit.Input("", double.NaN);
        _url.Watermark = "https://questlog.gg/aion-2/en/skill-builder/…";
        _url.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ImportUrl(); } };
        _go = UiKit.Primary(T.ImportGo, ImportUrl);
        _go.Margin = new Thickness(8, 0, 0, 0);
        link.Children.Add(UiKit.Row("*,Auto", _url, _go));
        link.Children.Add(new TextBlock { Text = T.BuildImportHint, FontSize = 11.5, Foreground = Visuals.TextMute, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        root.Place(link, 0, 0);

        var pick = new StackPanel();
        _tabMine = BuildUi.SubTab(T.ImportMineTab, () => SetTab(true));
        _tabSearch = BuildUi.SubTab(T.ImportSearchLabel, () => SetTab(false));
        _allClasses = UiKit.Check(T.ImportAllClasses, classId.Length == 0);
        _allClasses.IsEnabled = classId.Length > 0;
        _allClasses.IsCheckedChanged += (_, _) =>
        {
            if (_mineTab) { if (_mine is not null) ShowMine(_mine); return; }
            _page = 1; _ = SearchAsync();
        };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        tabs.Children.Add(_tabMine); tabs.Children.Add(_tabSearch);
        pick.Children.Add(UiKit.Row("*,Auto", tabs, _allClasses));
        _user = UiKit.Input(Slug, double.NaN);
        _user.Watermark = T.ImportUserHint;
        UiKit.Tip(_user, T.ImportUserHint);
        _user.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; e.Handled = true; if (CommitUser()) _ = LoadMineAsync(false); };
        _user.LostFocus += (_, _) => { var before = Slug; if (CommitUser() && Slug != before && _mineTab) _ = LoadMineAsync(false); };
        var refresh = UiKit.Ghost("↻", () => { if (CommitUser()) _ = LoadMineAsync(true); });
        UiKit.Tip(refresh, T.ImportMineRefresh);
        refresh.Margin = new Thickness(8, 0, 0, 0);
        var userLabel = UiKit.SectionLabel(T.ImportUserLabel, new Thickness(0, 0, 10, 0));
        userLabel.VerticalAlignment = VerticalAlignment.Center;
        _minePanel = UiKit.Row("Auto,*,Auto", userLabel, _user, refresh);
        _minePanel.Margin = new Thickness(0, 6, 0, 0);
        _search = UiKit.Input("", double.NaN);
        _search.Watermark = T.ImportSearchHint;
        _search.KeyDown += (_, e) => { if (e.Key != Key.Enter) return; e.Handled = true; Search(); };
        var go = UiKit.Ghost("⌕", Search);
        go.Margin = new Thickness(8, 0, 0, 0);
        _searchPanel = UiKit.Row("*,Auto", _search, go);
        _searchPanel.Margin = new Thickness(0, 6, 0, 0);
        pick.Children.Add(_minePanel);
        pick.Children.Add(_searchPanel);
        root.Place(new Border { BorderBrush = Visuals.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 6, 12, 8), Child = pick }, 1, 0);

        _scroll = new ScrollViewer { Content = _results, Padding = new Thickness(12, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        root.Place(_scroll, 2, 0);

        _status = new TextBlock { Foreground = Visuals.TextDim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
        _prev = UiKit.Ghost(T.ImportPrev, () => { if (_page > 1) { _page--; _ = SearchAsync(); } });
        _next = UiKit.Ghost(T.ImportNext, () => { _page++; _ = SearchAsync(); });
        _prev.IsVisible = _next.IsVisible = false;
        root.Place(new Border { BorderBrush = Visuals.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 8), Child = UiKit.Row("*,Auto,Auto", _status, _prev, _next) }, 3, 0);
        SetContent(root);
        Opened += (_, _) => { _url.Focus(); SetTab(Slug.Length > 0); };
    }

    private string Slug => Shell.Settings.QuestlogSlug ?? "";
    private string? ClassFilter => _allClasses.IsChecked == true || _classId.Length == 0 ? null : _classId;

    private void SetTab(bool mine)
    {
        _mineTab = mine;
        BuildUi.SetSubTab(_tabMine, mine);
        BuildUi.SetSubTab(_tabSearch, !mine);
        _minePanel.IsVisible = mine;
        _searchPanel.IsVisible = !mine;
        if (mine) _ = LoadMineAsync(false); else _ = SearchAsync();
    }

    private void Search() { _term = _search.Text?.Trim() ?? ""; _page = 1; _ = SearchAsync(); }

    private async Task SearchAsync()
    {
        var seq = ++_seq;
        _status.Text = T.ImportSearching;
        try
        {
            var page = await QuestlogService.SearchAsync(_term, _page, ClassFilter);
            if (seq == _seq && !_mineTab) ShowPage(page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog search failed: {ex.Message}");
            if (seq == _seq) _status.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex));
        }
    }

    private void ShowPage(BuildSearchPage page)
    {
        _results.Children.Clear();
        var filter = ClassFilter;
        foreach (var b in page.Builds)
            if (filter is null || string.Equals(b.ClassId, filter, StringComparison.OrdinalIgnoreCase)) _results.Children.Add(Row(b, s => _ = AddAsync(s), RowBack, RowLine, Visuals.Text, Visuals.TextMute, TagBack, false));
        if (_results.Children.Count == 0) Message(T.ImportNoResults);
        _status.Text = string.Format(T.ImportPage, page.Page, Math.Max(page.PageCount, 1), page.TotalHits);
        _prev.IsVisible = page.Page > 1;
        _next.IsVisible = page.PageCount > page.Page;
        _scroll.Offset = default;
    }

    /// <summary>One result (WPF ImportBuildWindow.Row): class emblem, name, author and date, tags, likes, Add. Shared with the Build tab's suggestions card.</summary>
    public static Control Row(BuildSummary b, Action<BuildSummary> add, IBrush back, IBrush line, IBrush text, IBrush mute, IBrush tagBack, bool questlogLook) =>
        Row(b.Name.Length > 0 ? b.Name : "#" + b.Id.ToString(CultureInfo.InvariantCulture), b.ClassId, b.Author is { Length: > 0 } a ? string.Format(T.BuildByAuthor, a) : null,
            b.UpdatedAt, b.Tags, b.LikeCount, () => add(b), back, line, text, mute, tagBack, questlogLook);

    /// <summary>A planner build of "My builds": no likes on questlog's planner, "Daevanion plan" in place of the author.</summary>
    public static Control Row(QuestlogDaevPlan p, Action<QuestlogDaevPlan> add, IBrush back, IBrush line, IBrush text, IBrush mute, IBrush tagBack, bool questlogLook) =>
        Row(p.Name.Length > 0 ? p.Name : "#" + p.Id.ToString(CultureInfo.InvariantCulture), p.ClassId, T.ImportPlanKind, p.UpdatedAt, p.Tags ?? [], null, () => add(p),
            back, line, text, mute, tagBack, questlogLook);

    private static Control Row(string name, string classId, string? by, DateTimeOffset? updated, IReadOnlyList<string> tagList, int? likeCount, Action add,
                               IBrush back, IBrush line, IBrush text, IBrush mute, IBrush tagBack, bool questlogLook)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("38,*,Auto,Auto") };
        var cls = Enum.TryParse<GameClass>(classId, true, out var c) ? c : GameClass.Unknown;
        if (Visuals.EmblemOf(cls) is { } img) g.Children.Add(UiKit.Tip(new Image { Source = img, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center }, T.ClassNameOf(classId)));
        else g.Children.Add(new TextBlock { Text = classId, FontSize = 10, Foreground = mute, VerticalAlignment = VerticalAlignment.Center });
        var mid = new StackPanel { Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = T.ClassNameOf(classId);
        if (by is { Length: > 0 }) meta += " · " + by;
        if (updated is { } u) meta += " · " + string.Format(T.BuildUpdatedOn, u.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        mid.Children.Add(new TextBlock { Text = meta, FontSize = 11.5, Foreground = mute, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        if (tagList.Count > 0)
        {
            var tags = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var t in tagList)
                tags.Children.Add(new Border { Background = tagBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 4, 2), Child = new TextBlock { Text = t, FontSize = 10.5, Foreground = mute } });
            mid.Children.Add(tags);
        }
        g.Place(mid, 0, 1);
        if (likeCount is { } lc)
        {
            var likes = new TextBlock { FontSize = 12.5, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Inlines = new InlineCollection() };
            likes.Inlines.Add(new Run("♥ ") { Foreground = Like });
            likes.Inlines.Add(new Run(lc.ToString(CultureInfo.InvariantCulture)));
            g.Place(likes, 0, 2);
        }
        var btn = questlogLook ? BuildUi.Flat(T.BuildAdd, add) : UiKit.Ghost(T.BuildAdd, add);
        btn.MinWidth = 64;
        btn.Margin = new Thickness(0);
        btn.HorizontalContentAlignment = HorizontalAlignment.Center;
        g.Place(btn, 0, 3);
        return new Border { Background = back, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7), Margin = new Thickness(0, 0, 0, 6), Child = g };
    }

    private async Task AddAsync(BuildSummary s)
    {
        if (s.AuthorSlug is not { Length: > 0 } slug) { _status.Text = string.Format(T.BuildImportNotFound, s.Id); return; }
        await FetchAsync(slug, s.Id);
    }

    private async Task FetchAsync(string slug, long id, bool fromLink = false)
    {
        if (_busy) return;
        _busy = true;
        _go.IsEnabled = false;
        _status.Text = T.ImportFetching;
        try
        {
            var b = await QuestlogService.FetchAsync(slug, id);
            if (b is null) { _status.Text = string.Format(T.BuildImportNotFound, id); return; }
            if (fromLink && QuestlogService.LearnSlug(Shell.Settings, slug)) Shell.SaveSettings();
            Picked?.Invoke(b, null, null);
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog build {id} fetch failed: {ex.Message}");
            _status.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex));
        }
        finally { _busy = false; _go.IsEnabled = true; }
    }

    private void ImportUrl()
    {
        var text = _url.Text ?? "";
        if (QuestlogBuilds.ParseUrl(text) is not { } r)
        {
            if (DaevanionPlan.ParsePlannerUrl(text) is not null) { PlanUrl?.Invoke(text.Trim()); Close(); return; }
            _status.Text = T.BuildImportBadUrl;
            return;
        }
        _ = FetchAsync(r.Slug, r.Id, fromLink: true);
    }

    // ------------------------------------------------------------------ my builds

    private async Task LoadMineAsync(bool refresh)
    {
        var seq = ++_seq;
        _prev.IsVisible = _next.IsVisible = false;
        var slug = Slug;
        if (slug.Length == 0) { _mine = null; Message(T.ImportMineEmpty, clear: true); _status.Text = ""; return; }
        if (!refresh && QuestlogService.CachedMine(slug) is { } hit) { ShowMine(hit); return; }
        Message(T.ImportMineLoading, clear: true);
        _status.Text = T.ImportMineLoading;
        try
        {
            var m = await QuestlogService.MineAsync(slug, refresh);
            if (seq == _seq && _mineTab && slug == Slug) ShowMine(m);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog builds of {slug} failed: {ex.Message}");
            if (seq == _seq) { _results.Children.Clear(); _status.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex)); }
        }
    }

    private void Message(string text, bool clear = false)
    {
        if (clear) _results.Children.Clear();
        if (text.Length > 0) _results.Children.Add(new TextBlock { Text = text, Foreground = Visuals.TextMute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 12.5 });
    }

    private void ShowMine(QuestlogService.MyBuilds m)
    {
        _mine = m;
        _results.Children.Clear();
        var filter = ClassFilter;
        var skills = QuestlogMine.OfClass(m.Skills, static b => b.ClassId, filter);
        var plans = QuestlogMine.OfClass(m.Plans, static p => p.ClassId, filter);
        _results.Children.Add(new TextBlock { Text = string.Format(T.ImportMineTitle, Slug), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Text, Margin = new Thickness(0, 8, 0, 2) });
        _results.Children.Add(UiKit.SectionLabel(string.Format(T.ImportMineSkills, skills.Count), new Thickness(0, 10, 0, 6)));
        foreach (var b in skills) _results.Children.Add(Row(b, AddMine, RowBack, RowLine, Visuals.Text, Visuals.TextMute, TagBack, false));
        if (skills.Count == 0) Message(T.ImportMineNone);
        _results.Children.Add(UiKit.SectionLabel(string.Format(T.ImportMinePlans, plans.Count), new Thickness(0, 10, 0, 6)));
        foreach (var p in plans) _results.Children.Add(Row(p, AddPlan, RowBack, RowLine, Visuals.Text, Visuals.TextMute, TagBack, false));
        if (plans.Count == 0) Message(T.ImportMineNone);
        _status.Text = string.Format(T.ImportMineCount, m.Skills.Count, m.Plans.Count);
        _scroll.Offset = default;
    }

    /// <summary>The user's own skill build: the list already holds it in full, so no fetch; the planner build of the same character rides along.</summary>
    private void AddMine(BuildSummary s)
    {
        if (_busy || _mine is null) return;
        Picked?.Invoke(s.Build, QuestlogMine.PairPlan(s.Name, s.ClassId, _mine.Plans), Slug);
        Close();
    }

    private void AddPlan(QuestlogDaevPlan p)
    {
        if (_busy) return;
        _status.Text = ImportPlan is null ? "" : ImportPlan(p, Slug);
    }

    /// <summary>Box → setting: a bare slug or any of the user's links; empty clears it. False (and a status) when the text is neither.</summary>
    private bool CommitUser()
    {
        var text = _user.Text?.Trim() ?? "";
        string slug;
        if (text.Length == 0) slug = "";
        else if (QuestlogMine.ParseUserSlug(text) is { } s) slug = s;
        else { _status.Text = T.ImportMineBadUser; return false; }
        if (_user.Text != slug) _user.Text = slug;
        if (slug == Slug) return true;
        Shell.RefreshSettings();
        Shell.Settings.QuestlogSlug = slug;
        Shell.SaveSettings();
        return true;
    }
}
