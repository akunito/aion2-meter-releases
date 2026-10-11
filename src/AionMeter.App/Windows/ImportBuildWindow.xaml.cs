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
/// "Import from questlog.gg" (A2M-21): a build link, or a search of the public builds (current class by default). Search rows carry no
/// chosen specs, so Add fetches the author's builds and takes the full one. <see cref="Result"/> is the build to save; the Build tab saves it.
/// </summary>
public partial class ImportBuildWindow : Window
{
    private static UiText T => UiText.Current;
    private static readonly Brush Mute = SkillTile.Frozen(0x6B, 0x75, 0x90), Dim = SkillTile.Frozen(0xA3, 0xAC, 0xBF), Gold = SkillTile.Frozen(0xC9, 0xA4, 0x5C),
        RowBack = SkillTile.Frozen(0x11, 0x17, 0x26), RowLine = SkillTile.Frozen(0x2A, 0x33, 0x50), TagBack = SkillTile.Frozen(0x19, 0x20, 0x3A), Like = SkillTile.Frozen(0xEF, 0x4D, 0x56);

    private readonly string _classId;
    private string _term = "";
    private int _page = 1, _seq;
    private bool _busy;

    public Build? Result { get; private set; }
    /// <summary>A daevanion-planner link pasted in the build box: the view imports it as the plan of the active build.</summary>
    public string? PlanUrl { get; private set; }

    public ImportBuildWindow(string classId)
    {
        _classId = classId;
        InitializeComponent();
        DragAnywhere.Attach(this);
        AllClasses.IsChecked = classId.Length == 0;
        AllClasses.IsEnabled = classId.Length > 0;
        PrevButton.Visibility = NextButton.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { if (!_sample) { UrlBox.Focus(); _ = SearchAsync(); } };
    }

    private bool _sample;

    /// <summary>Render tool: a link in the box and this page of results, nothing fetched.</summary>
    public void ShowSample(string url, BuildSearchPage page)
    {
        _sample = true;
        UrlBox.Text = url;
        ShowPage(page);
    }

    private string? ClassFilter => AllClasses.IsChecked == true || _classId.Length == 0 ? null : _classId;

    private async Task SearchAsync()
    {
        var seq = ++_seq;
        StatusText.Text = T.ImportSearching;
        try
        {
            var page = await QuestlogService.SearchAsync(_term, _page, ClassFilter);
            if (seq == _seq) ShowPage(page);
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
    public static FrameworkElement Row(BuildSummary b, Action<BuildSummary> add, Brush back, Brush line, Brush text, Brush mute, Brush tagBack, Style addStyle)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cls = Enum.TryParse<GameClass>(b.ClassId, true, out var c) ? c : GameClass.Unknown;
        FrameworkElement emblem = ClassVisuals.EmblemOf(cls) is { } img
            ? new Image { Source = img, Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, ToolTip = ClassVisuals.NameOf(cls) }
            : new TextBlock { Text = b.ClassId, FontSize = 10, Foreground = mute, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(emblem);
        var mid = new StackPanel { Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = b.Name.Length > 0 ? b.Name : "#" + b.Id.ToString(CultureInfo.InvariantCulture), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = ClassVisuals.NameOf(cls);
        if (b.Author is { Length: > 0 } a) meta += " · " + string.Format(T.BuildByAuthor, a);
        if (b.UpdatedAt is { } u) meta += " · " + string.Format(T.BuildUpdatedOn, u.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        mid.Children.Add(new TextBlock { Text = meta, FontSize = 11.5, Foreground = mute, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        if (b.Tags.Count > 0)
        {
            var tags = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var t in b.Tags)
                tags.Children.Add(new Border { Background = tagBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 4, 0), Child = new TextBlock { Text = t, FontSize = 10.5, Foreground = mute } });
            mid.Children.Add(tags);
        }
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);
        var likes = new TextBlock { FontSize = 12.5, Foreground = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        likes.Inlines.Add(new System.Windows.Documents.Run("♥ ") { Foreground = Like });
        likes.Inlines.Add(new System.Windows.Documents.Run(b.LikeCount.ToString(CultureInfo.InvariantCulture)));
        Grid.SetColumn(likes, 2);
        g.Children.Add(likes);
        var btn = new Button { Style = addStyle, Content = T.BuildAdd, MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, Tag = b };
        btn.Click += (_, _) => add(b);
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

    private async Task FetchAsync(string slug, long id)
    {
        if (_busy || _sample) return;
        _busy = true;
        ImportButton.IsEnabled = false;
        StatusText.Text = T.ImportFetching;
        try
        {
            var b = await QuestlogService.FetchAsync(slug, id);
            if (b is null) { StatusText.Text = string.Format(T.BuildImportNotFound, id); return; }
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
        _ = FetchAsync(r.Slug, r.Id);
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

    private void AllClasses_Click(object sender, RoutedEventArgs e) { _page = 1; _ = SearchAsync(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _page++; _ = SearchAsync(); }
    private void Prev_Click(object sender, RoutedEventArgs e) { if (_page > 1) { _page--; _ = SearchAsync(); } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
