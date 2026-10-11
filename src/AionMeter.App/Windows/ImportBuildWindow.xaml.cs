using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.Core.Builds;
using AionMeter.Core.Events;

namespace AionMeter.App.Windows;

/// <summary>
/// "Import from questlog.gg" (A2M-21): a build link, the user's own builds ("My builds": skill builds and planner builds of the
/// questlog user in <see cref="AppSettings.QuestlogSlug"/>, no login), or a search of the public builds (current class by default).
/// Search rows carry no chosen specs, so Add fetches the author's builds and takes the full one; the user's list already holds them.
/// <see cref="Result"/> is the build to save (with <see cref="PairedPlan"/> when a planner build goes with it); the Build tab saves it.
/// A planner build of "My builds" goes straight into the active build through <see cref="ImportPlan"/> and the dialog stays open.
/// </summary>
public partial class ImportBuildWindow : Window
{
    private static UiText T => UiText.Current;
    private static readonly Brush Mute = SkillTile.Frozen(0x6B, 0x75, 0x90), Dim = SkillTile.Frozen(0xA3, 0xAC, 0xBF), Gold = SkillTile.Frozen(0xC9, 0xA4, 0x5C),
        RowBack = SkillTile.Frozen(0x11, 0x17, 0x26), RowLine = SkillTile.Frozen(0x2A, 0x33, 0x50), TagBack = SkillTile.Frozen(0x19, 0x20, 0x3A), Like = SkillTile.Frozen(0xEF, 0x4D, 0x56);

    private readonly string _classId;
    private readonly AppSettings _settings;
    private string _term = "";
    private int _page = 1, _seq;
    private bool _busy;
    private QuestlogService.MyBuilds? _mine;

    public Build? Result { get; private set; }
    /// <summary>A daevanion-planner link pasted in the build box: the view imports it as the plan of the active build.</summary>
    public string? PlanUrl { get; private set; }
    /// <summary>The user's planner build that goes with <see cref="Result"/> (<see cref="QuestlogMine.PairPlan"/>), and its slug.</summary>
    public QuestlogDaevPlan? PairedPlan { get; private set; }
    public string? PairedSlug { get; private set; }
    /// <summary>Set by the Build tab: puts a planner build into the active build (made from the profile when there is none); returns the status text.</summary>
    public Func<QuestlogDaevPlan, string, string>? ImportPlan { get; init; }

    public ImportBuildWindow(string classId, AppSettings settings)
    {
        _classId = classId;
        _settings = settings;
        InitializeComponent();
        DragAnywhere.Attach(this);
        AllClasses.IsChecked = classId.Length == 0;
        AllClasses.IsEnabled = classId.Length > 0;
        PrevButton.Visibility = NextButton.Visibility = Visibility.Collapsed;
        UserBox.Text = Slug;
        (Slug.Length > 0 ? TabMine : TabSearch).IsChecked = true;
        Loaded += (_, _) =>
        {
            if (_sample) return;
            UrlBox.Focus();
            if (TabMine.IsChecked == true) _ = LoadMineAsync(false); else _ = SearchAsync();
        };
    }

    private bool _sample;
    private string Slug => _settings.QuestlogSlug ?? "";

    /// <summary>Render tool: a link in the box and this page of results, nothing fetched.</summary>
    public void ShowSample(string url, BuildSearchPage page)
    {
        _sample = true;
        UrlBox.Text = url;
        TabSearch.IsChecked = true;
        ShowPage(page);
    }

    /// <summary>Render tool: "My builds" of this user, nothing fetched (the settings are throw-away).</summary>
    public void ShowSampleMine(string slug, QuestlogService.MyBuilds mine)
    {
        _sample = true;
        _settings.QuestlogSlug = slug;
        UserBox.Text = slug;
        TabMine.IsChecked = true;
        ShowMine(mine);
    }

    private string? ClassFilter => AllClasses.IsChecked == true || _classId.Length == 0 ? null : _classId;

    private async Task SearchAsync()
    {
        var seq = ++_seq;
        StatusText.Text = T.ImportSearching;
        try
        {
            var page = await QuestlogService.SearchAsync(_term, _page, ClassFilter);
            if (seq == _seq && TabSearch.IsChecked == true) ShowPage(page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog search failed: {ex.Message}");
            if (seq == _seq) StatusText.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex));
        }
    }

    private void ShowPage(BuildSearchPage page)
    {
        Results.Children.Clear();
        var filter = ClassFilter;
        foreach (var b in page.Builds)
            if (filter is null || string.Equals(b.ClassId, filter, StringComparison.OrdinalIgnoreCase)) Results.Children.Add(Row(b));
        if (Results.Children.Count == 0) Results.Children.Add(new TextBlock { Text = T.ImportNoResults, Foreground = Mute, Margin = new Thickness(0, 12, 0, 0) });
        StatusText.Text = string.Format(T.ImportPage, page.Page, Math.Max(page.PageCount, 1), page.TotalHits);
        PrevButton.Visibility = page.Page > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = page.PageCount > page.Page ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroll.ScrollToTop();
    }

    /// <summary>One result: class emblem, name, author and date, tags, likes, Add. Shared with the Build tab's suggestions card.</summary>
    public static FrameworkElement Row(BuildSummary b, Action<BuildSummary> add, Brush back, Brush line, Brush text, Brush mute, Brush tagBack, Style addStyle) =>
        Row(b.Name.Length > 0 ? b.Name : "#" + b.Id.ToString(CultureInfo.InvariantCulture), b.ClassId, b.Author is { Length: > 0 } a ? string.Format(T.BuildByAuthor, a) : null,
            b.UpdatedAt, b.Tags, b.LikeCount, () => add(b), back, line, text, mute, tagBack, addStyle);

    /// <summary>A planner build of "My builds": no likes on questlog's planner, "Daevanion plan" in place of the author.</summary>
    public static FrameworkElement Row(QuestlogDaevPlan p, Action<QuestlogDaevPlan> add, Brush back, Brush line, Brush text, Brush mute, Brush tagBack, Style addStyle) =>
        Row(p.Name.Length > 0 ? p.Name : "#" + p.Id.ToString(CultureInfo.InvariantCulture), p.ClassId, T.ImportPlanKind, p.UpdatedAt, p.Tags ?? [], null, () => add(p),
            back, line, text, mute, tagBack, addStyle);

    private static FrameworkElement Row(string name, string classId, string? by, DateTimeOffset? updated, IReadOnlyList<string> tagList, int? likeCount, Action add,
                                        Brush back, Brush line, Brush text, Brush mute, Brush tagBack, Style addStyle)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cls = Enum.TryParse<GameClass>(classId, true, out var c) ? c : GameClass.Unknown;
        FrameworkElement emblem = ClassVisuals.EmblemOf(cls) is { } img
            ? new Image { Source = img, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, ToolTip = ClassVisuals.NameOf(cls) }
            : new TextBlock { Text = classId, FontSize = 10, Foreground = mute, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(emblem);
        var mid = new StackPanel { Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = ClassVisuals.NameOf(cls);
        if (by is { Length: > 0 }) meta += " · " + by;
        if (updated is { } u) meta += " · " + string.Format(T.BuildUpdatedOn, u.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        mid.Children.Add(new TextBlock { Text = meta, FontSize = 11.5, Foreground = mute, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        if (tagList.Count > 0)
        {
            var tags = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var t in tagList)
                tags.Children.Add(new Border { Background = tagBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 4, 0), Child = new TextBlock { Text = t, FontSize = 10.5, Foreground = mute } });
            mid.Children.Add(tags);
        }
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);
        if (likeCount is { } lc)
        {
            var likes = new TextBlock { FontSize = 12.5, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            likes.Inlines.Add(new System.Windows.Documents.Run("♥ ") { Foreground = Like });
            likes.Inlines.Add(new System.Windows.Documents.Run(lc.ToString(CultureInfo.InvariantCulture)));
            Grid.SetColumn(likes, 2);
            g.Children.Add(likes);
        }
        var btn = new Button { Style = addStyle, Content = T.BuildAdd, MinWidth = 64, VerticalAlignment = VerticalAlignment.Center };
        btn.Click += (_, _) => add();
        Grid.SetColumn(btn, 3);
        g.Children.Add(btn);
        return new Border { Background = back, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6), Child = g };
    }

    private FrameworkElement Row(BuildSummary b) => Row(b, s => _ = AddAsync(s), RowBack, RowLine, (Brush)FindResource("Text"), Mute, TagBack, (Style)FindResource("GhostButton"));

    private async Task AddAsync(BuildSummary s)
    {
        if (s.AuthorSlug is not { Length: > 0 } slug) { StatusText.Text = string.Format(T.BuildImportNotFound, s.Id); return; }
        await FetchAsync(slug, s.Id);
    }

    private async Task FetchAsync(string slug, long id, bool fromLink = false)
    {
        if (_busy || _sample) return;
        _busy = true;
        ImportButton.IsEnabled = false;
        StatusText.Text = T.ImportFetching;
        try
        {
            var b = await QuestlogService.FetchAsync(slug, id);
            if (b is null) { StatusText.Text = string.Format(T.BuildImportNotFound, id); return; }
            if (fromLink) QuestlogService.LearnSlug(_settings, slug);
            Result = b;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog build {id} fetch failed: {ex.Message}");
            StatusText.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex));
        }
        finally { _busy = false; ImportButton.IsEnabled = true; }
    }

    private void ImportUrl_Click(object sender, RoutedEventArgs e)
    {
        if (QuestlogBuilds.ParseUrl(UrlBox.Text) is not { } r)
        {
            if (Core.Builds.DaevanionPlan.ParsePlannerUrl(UrlBox.Text) is not null) { PlanUrl = UrlBox.Text.Trim(); DialogResult = true; Close(); return; }
            StatusText.Text = T.BuildImportBadUrl; return;
        }
        _ = FetchAsync(r.Slug, r.Id, fromLink: true);
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; ImportUrl_Click(sender, e); }
    }

    private void Search_Click(object sender, RoutedEventArgs e) { _term = SearchBox.Text.Trim(); _page = 1; _ = SearchAsync(); }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Search_Click(sender, e); }
    }

    private void AllClasses_Click(object sender, RoutedEventArgs e)
    {
        if (TabMine.IsChecked == true) { if (_mine is not null) ShowMine(_mine); return; }
        _page = 1; _ = SearchAsync();
    }

    private void Next_Click(object sender, RoutedEventArgs e) { _page++; _ = SearchAsync(); }
    private void Prev_Click(object sender, RoutedEventArgs e) { if (_page > 1) { _page--; _ = SearchAsync(); } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ my builds

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (MinePanel is null || SearchPanel is null) return;   // raised during InitializeComponent
        var mine = TabMine.IsChecked == true;
        MinePanel.Visibility = mine ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = mine ? Visibility.Collapsed : Visibility.Visible;
        if (!IsLoaded || _sample) return;
        if (mine) _ = LoadMineAsync(false); else _ = SearchAsync();
    }

    private async Task LoadMineAsync(bool refresh)
    {
        var seq = ++_seq;
        PrevButton.Visibility = NextButton.Visibility = Visibility.Collapsed;
        var slug = Slug;
        if (slug.Length == 0) { _mine = null; Message(T.ImportMineEmpty); StatusText.Text = ""; return; }
        if (!refresh && QuestlogService.CachedMine(slug) is { } hit) { ShowMine(hit); return; }
        Message(T.ImportMineLoading);
        StatusText.Text = T.ImportMineLoading;
        try
        {
            var m = await QuestlogService.MineAsync(slug, refresh);
            if (seq == _seq && TabMine.IsChecked == true && slug == Slug) ShowMine(m);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog builds of {slug} failed: {ex.Message}");
            if (seq == _seq) { Message(""); StatusText.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex)); }
        }
    }

    private void Message(string text)
    {
        Results.Children.Clear();
        if (text.Length > 0) Results.Children.Add(new TextBlock { Text = text, Foreground = Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
    }

    private void ShowMine(QuestlogService.MyBuilds m)
    {
        _mine = m;
        Results.Children.Clear();
        var filter = ClassFilter;
        var skills = QuestlogMine.OfClass(m.Skills, static b => b.ClassId, filter);
        var plans = QuestlogMine.OfClass(m.Plans, static p => p.ClassId, filter);
        var text = (Brush)FindResource("Text");
        var ghost = (Style)FindResource("GhostButton");
        Results.Children.Add(new TextBlock { Text = string.Format(T.ImportMineTitle, Slug), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 8, 0, 2) });
        Header(string.Format(T.ImportMineSkills, skills.Count));
        foreach (var b in skills) Results.Children.Add(Row(b, AddMine, RowBack, RowLine, text, Mute, TagBack, ghost));
        if (skills.Count == 0) None();
        Header(string.Format(T.ImportMinePlans, plans.Count));
        foreach (var p in plans) Results.Children.Add(Row(p, AddPlan, RowBack, RowLine, text, Mute, TagBack, ghost));
        if (plans.Count == 0) None();
        StatusText.Text = string.Format(T.ImportMineCount, m.Skills.Count, m.Plans.Count);
        PrevButton.Visibility = NextButton.Visibility = Visibility.Collapsed;
        ResultsScroll.ScrollToTop();

        void Header(string t) => Results.Children.Add(new TextBlock { Text = t.ToUpper(T.Culture), Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(0, 10, 0, 6) });
        void None() => Results.Children.Add(new TextBlock { Text = T.ImportMineNone, Foreground = Mute, Margin = new Thickness(0, 0, 0, 6) });
    }

    /// <summary>The user's own skill build: the list already holds it in full, so no fetch; the planner build of the same character rides along.</summary>
    private void AddMine(BuildSummary s)
    {
        if (_busy || _sample || _mine is null) return;
        PairedPlan = QuestlogMine.PairPlan(s.Name, s.ClassId, _mine.Plans);
        PairedSlug = Slug;
        Result = s.Build;
        DialogResult = true;
    }

    private void AddPlan(QuestlogDaevPlan p)
    {
        if (_busy || _sample) return;
        StatusText.Text = ImportPlan is null ? "" : ImportPlan(p, Slug);
    }

    private void MineRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!CommitUser()) return;
        _ = LoadMineAsync(true);
    }

    private void UserBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (CommitUser()) _ = LoadMineAsync(false);
    }

    private void UserBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var before = Slug;
        if (CommitUser() && Slug != before && TabMine.IsChecked == true) _ = LoadMineAsync(false);
    }

    /// <summary>Box → setting: a bare slug or any of the user's links; empty clears it. False (and a status) when the text is neither.</summary>
    private bool CommitUser()
    {
        var text = UserBox.Text.Trim();
        string slug;
        if (text.Length == 0) slug = "";
        else if (QuestlogMine.ParseUserSlug(text) is { } s) slug = s;
        else { StatusText.Text = T.ImportMineBadUser; return false; }
        if (UserBox.Text != slug) UserBox.Text = slug;
        if (slug == Slug) return true;
        _settings.QuestlogSlug = slug;
        if (!_sample) try { _settings.Save(); } catch (IOException ex) { Log.Info($"settings save failed: {ex.Message}"); }
        return true;
    }
}
