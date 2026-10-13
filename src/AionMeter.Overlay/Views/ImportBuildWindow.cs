using System.Globalization;
using System.Text;
using AionMeter.Core.Builds;
using AionMeter.Core.Events;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using C = AionMeter.Overlay.Views.BuildColors.C;

namespace AionMeter.Overlay.Views;

/// <summary>
/// "Import from questlog.gg" (WPF Windows/ImportBuildWindow, A2M-21) on the card's output (A2M-50, port of the Avalonia ImportBuildWindow): a
/// build link, the user's own builds ("My builds": skill and planner builds of the questlog user in <see cref="AppSettings.QuestlogSlug"/>, public
/// tRPC, no login) or a search of the public builds (current class by default). A layer surface (<see cref="DialogCard"/>), not modal: what is
/// picked goes to the Build tab through the callbacks, which close it (a planner build of "My builds" goes into the active build and the dialog
/// stays open, as WPF).
/// </summary>
internal sealed class ImportBuildWindow : DialogCard
{
    private static UiText T => UiText.Current;

    private readonly string _classId;
    private readonly Gtk.Entry _url, _user, _search;
    private readonly Gtk.CheckButton _allClasses;
    private readonly Gtk.Button _tabMine, _tabSearch, _prev, _next, _go;
    private readonly Gtk.Widget _minePanel, _searchPanel;
    private readonly Gtk.Box _results = UiKit.VBox();
    private readonly Gtk.ScrolledWindow _scroll;
    private readonly Gtk.Label _status;
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

    public ImportBuildWindow(AppShell shell, string classId) : base(shell, "import-build", UiText.Current.BuildImportTitle, 760, 640)
    {
        BuildColors.EnsureCss();
        _classId = classId;
        var root = UiKit.VBox();

        var link = UiKit.VBox();
        UiKit.Margin(link, 12, 0, 12, 12);
        link.Append(UiKit.SectionLabel(T.ImportUrlLabel));
        _url = UiKit.Input("", double.NaN);
        _url.PlaceholderText = "https://questlog.gg/aion-2/en/skill-builder/…";
        _url.OnActivate += (_, _) => ImportUrl();
        _go = UiKit.Primary(T.ImportGo, ImportUrl);
        _go.MarginStart = UiKit.Px(8);
        _go.MarginEnd = 0;
        link.Append(UiKit.Row("*,Auto", _url, _go));
        var hint = UiKit.Text(T.BuildImportHint, 11.5, Fg.Mute);
        hint.MarginTop = UiKit.Px(4);
        link.Append(hint);
        root.Append(link);

        var pick = UiKit.VBox();
        pick.AddCssClass("k-build-topline");
        var pickInner = UiKit.VBox();
        UiKit.Margin(pickInner, 12, 6, 12, 8);
        _tabMine = BuildUi.SubTab(T.ImportMineTab, () => SetTab(true));
        _tabSearch = BuildUi.SubTab(T.ImportSearchLabel, () => SetTab(false));
        _allClasses = UiKit.Check(T.ImportAllClasses, classId.Length == 0);
        _allClasses.Hexpand = false;
        // the kit's check label wraps with a 1-char natural width: beside the tabs that broke "All classes" into one letter per line (measured)
        if (_allClasses.GetChild() is Gtk.Label acl) { acl.Wrap = false; acl.MaxWidthChars = -1; acl.Hexpand = false; }
        _allClasses.Sensitive = classId.Length > 0;
        _allClasses.OnToggled += (_, _) =>
        {
            if (_mineTab) { if (_mine is not null) ShowMine(_mine); return; }
            _page = 1; _ = SearchAsync();
        };
        var tabs = UiKit.HBox();
        tabs.Append(_tabMine); tabs.Append(_tabSearch);
        pickInner.Append(UiKit.Row("*,Auto", tabs, _allClasses));
        _user = UiKit.Input(Slug, double.NaN);
        _user.PlaceholderText = T.ImportUserHint;
        _user.SetTooltipText(T.ImportUserHint);
        _user.OnActivate += (_, _) => { if (CommitUser()) _ = LoadMineAsync(false); };
        var focus = Gtk.EventControllerFocus.New();
        focus.OnLeave += (_, _) => { var before = Slug; if (CommitUser() && Slug != before && _mineTab) _ = LoadMineAsync(false); };
        _user.AddController(focus);
        var refresh = UiKit.Ghost("↻", () => { if (CommitUser()) _ = LoadMineAsync(true); });
        refresh.SetTooltipText(T.ImportMineRefresh);
        refresh.MarginStart = UiKit.Px(8);
        refresh.MarginEnd = 0;
        var userLabel = UiKit.SectionLabel(T.ImportUserLabel);
        userLabel.MarginEnd = UiKit.Px(10);
        userLabel.Valign = Gtk.Align.Center;
        _minePanel = UiKit.Row("Auto,*,Auto", userLabel, _user, refresh);
        _minePanel.MarginTop = UiKit.Px(6);
        _search = UiKit.Input("", double.NaN);
        _search.PlaceholderText = T.ImportSearchHint;
        _search.OnActivate += (_, _) => Search();
        var go = UiKit.Ghost("⌕", Search);
        go.MarginStart = UiKit.Px(8);
        go.MarginEnd = 0;
        _searchPanel = UiKit.Row("*,Auto", _search, go);
        _searchPanel.MarginTop = UiKit.Px(6);
        pickInner.Append(_minePanel);
        pickInner.Append(_searchPanel);
        pick.Append(pickInner);
        root.Append(pick);

        _results.MarginTop = UiKit.Px(4);
        _results.MarginBottom = UiKit.Px(8);
        _scroll = UiKit.Scroll(_results, gutters: false);
        UiKit.Margin(_scroll, 12, 0, 12, 0);
        root.Append(_scroll);

        _status = UiKit.Line("", 12.5, Fg.Dim);
        _status.MaxWidthChars = 1;
        _status.Hexpand = true;
        _prev = UiKit.Ghost(T.ImportPrev, () => { if (_page > 1) { _page--; _ = SearchAsync(); } });
        _next = UiKit.Ghost(T.ImportNext, () => { _page++; _ = SearchAsync(); });
        _next.MarginEnd = 0;
        _prev.Visible = _next.Visible = false;
        var foot = UiKit.Row("*,Auto,Auto", _status, _prev, _next);
        UiKit.Margin(foot, 12, 8, 12, 4);
        var footBar = UiKit.VBox();
        footBar.AddCssClass("k-build-topline");
        footBar.Append(foot);
        root.Append(footBar);
        SetContent(root);
    }

    protected override void Opened()
    {
        _url.GrabFocus();
        SetTab(Slug.Length > 0);
    }

    private string Slug => Shell.Settings.QuestlogSlug ?? "";
    private string? ClassFilter => _allClasses.Active || _classId.Length == 0 ? null : _classId;

    private void SetTab(bool mine)
    {
        _mineTab = mine;
        BuildUi.SetSubTab(_tabMine, mine);
        BuildUi.SetSubTab(_tabSearch, !mine);
        _minePanel.Visible = mine;
        _searchPanel.Visible = !mine;
        if (mine) _ = LoadMineAsync(false); else _ = SearchAsync();
    }

    private void Search() { _term = _search.GetText().Trim(); _page = 1; _ = SearchAsync(); }

    private async Task SearchAsync()
    {
        var seq = ++_seq;
        _status.SetText(T.ImportSearching);
        try
        {
            var page = await QuestlogService.SearchAsync(_term, _page, ClassFilter);
            if (seq == _seq && !_mineTab && IsOpen) ShowPage(page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog search failed: {ex.Message}");
            if (seq == _seq) _status.SetText(string.Format(T.BuildImportFailed, QuestlogService.Reason(ex)));
        }
    }

    private void ClearResults()
    {
        while (_results.GetFirstChild() is { } c) _results.Remove(c);
    }

    private void ShowPage(BuildSearchPage page)
    {
        ClearResults();
        var filter = ClassFilter;
        var any = false;
        foreach (var b in page.Builds)
            if (filter is null || string.Equals(b.ClassId, filter, StringComparison.OrdinalIgnoreCase)) { _results.Append(Row(b, s => _ = AddAsync(s), false)); any = true; }
        if (!any) Message(T.ImportNoResults);
        _status.SetText(string.Format(T.ImportPage, page.Page, Math.Max(page.PageCount, 1), page.TotalHits));
        _prev.Visible = page.Page > 1;
        _next.Visible = page.PageCount > page.Page;
        _scroll.GetVadjustment().SetValue(0);
    }

    /// <summary>One result (WPF ImportBuildWindow.Row): class emblem, name, author and date, tags, likes, Add. Shared with the Build tab's
    /// suggestions card (<paramref name="questlogLook"/>: the tab's greys and flat button instead of the dialog's palette).</summary>
    public static Gtk.Widget Row(BuildSummary b, Action<BuildSummary> add, bool questlogLook) =>
        Row(b.Name.Length > 0 ? b.Name : "#" + b.Id.ToString(CultureInfo.InvariantCulture), b.ClassId, b.Author is { Length: > 0 } a ? string.Format(T.BuildByAuthor, a) : null,
            b.UpdatedAt, b.Tags, b.LikeCount, () => add(b), questlogLook);

    /// <summary>A planner build of "My builds": no likes on questlog's planner, "Daevanion plan" in place of the author.</summary>
    public static Gtk.Widget Row(QuestlogDaevPlan p, Action<QuestlogDaevPlan> add, bool questlogLook) =>
        Row(p.Name.Length > 0 ? p.Name : "#" + p.Id.ToString(CultureInfo.InvariantCulture), p.ClassId, T.ImportPlanKind, p.UpdatedAt, p.Tags ?? [], null, () => add(p), questlogLook);

    private static Gtk.Widget Row(string name, string classId, string? by, DateTimeOffset? updated, IReadOnlyList<string> tagList, int? likeCount, Action add, bool questlogLook)
    {
        var row = UiKit.HBox();
        row.AddCssClass("k-build-row");
        if (questlogLook) row.AddCssClass("k-build-ql");
        var cls = Enum.TryParse<GameClass>(classId, true, out var c) ? c : GameClass.Unknown;
        var slot = UiKit.HBox();
        slot.SetSizeRequest(UiKit.Px(38), -1);
        if (BuildKit.EmblemPath(cls) is { } path)
        {
            var img = Gtk.Image.NewFromFile(path);
            img.PixelSize = UiKit.Px(28);
            img.Valign = Gtk.Align.Center;
            img.SetTooltipText(T.ClassNameOf(classId));
            slot.Append(img);
        }
        else slot.Append(BuildUi.Tight(classId, 10, questlogLook ? C.Mute : C.Dim));
        row.Append(slot);
        var mid = UiKit.VBox();
        mid.Hexpand = true;
        mid.Valign = Gtk.Align.Center;
        UiKit.Margin(mid, 6, 0, 10, 0);
        var title = UiKit.Line(name, 13, Fg.Text, 600);
        title.MaxWidthChars = 1;
        mid.Append(title);
        var meta = new StringBuilder(T.ClassNameOf(classId));
        if (by is { Length: > 0 }) meta.Append(" · ").Append(by);
        if (updated is { } u) meta.Append(" · ").Append(string.Format(T.BuildUpdatedOn, u.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var metaLine = UiKit.Line(meta.ToString(), 11.5, Fg.Mute);
        metaLine.MaxWidthChars = 1;
        metaLine.MarginTop = UiKit.Px(1);
        mid.Append(metaLine);
        if (tagList.Count > 0)
        {
            // tags clip at the row's width instead of widening the card (no wrap panel in GTK; External = no bar, no natural width)
            var tags = UiKit.HBox();
            foreach (var t in tagList)
            {
                var chip = UiKit.HBox();
                chip.AddCssClass("k-build-rowtag");
                chip.Append(Gtk.Label.New(t));
                tags.Append(chip);
            }
            var clip = Gtk.ScrolledWindow.New();
            clip.SetPolicy(Gtk.PolicyType.External, Gtk.PolicyType.Never);
            clip.Child = tags;
            clip.MarginTop = UiKit.Px(3);
            mid.Append(clip);
        }
        row.Append(mid);
        if (likeCount is { } lc)
        {
            var likes = Gtk.Label.New(null);
            likes.SetMarkup(BuildMarkup.Span(new StringBuilder(), "♥ ", 0xEF4D56).Append(lc.ToString(CultureInfo.InvariantCulture)).ToString());
            likes.Valign = Gtk.Align.Center;
            likes.MarginEnd = UiKit.Px(12);
            UiKit.SetFg(likes, Fg.Text);
            row.Append(likes);
        }
        var btn = questlogLook ? BuildUi.Flat(T.BuildAdd, add) : UiKit.Ghost(T.BuildAdd, add);
        btn.SetSizeRequest(UiKit.Px(64), -1);
        btn.MarginEnd = 0;
        btn.Valign = Gtk.Align.Center;
        if (btn.GetChild() is Gtk.Label bl) bl.Xalign = 0.5f;
        row.Append(btn);
        return row;
    }

    private async Task AddAsync(BuildSummary s)
    {
        if (s.AuthorSlug is not { Length: > 0 } slug) { _status.SetText(string.Format(T.BuildImportNotFound, s.Id)); return; }
        await FetchAsync(slug, s.Id);
    }

    private async Task FetchAsync(string slug, long id, bool fromLink = false)
    {
        if (_busy) return;
        _busy = true;
        _go.Sensitive = false;
        _status.SetText(T.ImportFetching);
        try
        {
            var b = await QuestlogService.FetchAsync(slug, id);
            if (b is null) { _status.SetText(string.Format(T.BuildImportNotFound, id)); return; }
            if (fromLink && QuestlogService.LearnSlug(Shell.Settings, slug)) Shell.SaveSettings();
            Picked?.Invoke(b, null, null);
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog build {id} fetch failed: {ex.Message}");
            _status.SetText(string.Format(T.BuildImportFailed, QuestlogService.Reason(ex)));
        }
        finally { _busy = false; _go.Sensitive = true; }
    }

    private void ImportUrl()
    {
        var text = _url.GetText();
        if (QuestlogBuilds.ParseUrl(text) is not { } r)
        {
            if (DaevanionPlan.ParsePlannerUrl(text) is not null) { PlanUrl?.Invoke(text.Trim()); Close(); return; }
            _status.SetText(T.BuildImportBadUrl);
            return;
        }
        _ = FetchAsync(r.Slug, r.Id, fromLink: true);
    }

    // ------------------------------------------------------------------ my builds

    private async Task LoadMineAsync(bool refresh)
    {
        var seq = ++_seq;
        _prev.Visible = _next.Visible = false;
        var slug = Slug;
        if (slug.Length == 0) { _mine = null; Message(T.ImportMineEmpty, clear: true); _status.SetText(""); return; }
        if (!refresh && QuestlogService.CachedMine(slug) is { } hit) { ShowMine(hit); return; }
        Message(T.ImportMineLoading, clear: true);
        _status.SetText(T.ImportMineLoading);
        try
        {
            var m = await QuestlogService.MineAsync(slug, refresh);
            if (seq == _seq && _mineTab && slug == Slug && IsOpen) ShowMine(m);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog builds of {slug} failed: {ex.Message}");
            if (seq == _seq) { ClearResults(); _status.SetText(string.Format(T.BuildImportFailed, QuestlogService.Reason(ex))); }
        }
    }

    private void Message(string text, bool clear = false)
    {
        if (clear) ClearResults();
        if (text.Length == 0) return;
        var l = UiKit.Text(text, 12.5, Fg.Mute);
        l.MarginTop = UiKit.Px(12);
        _results.Append(l);
    }

    private void ShowMine(QuestlogService.MyBuilds m)
    {
        _mine = m;
        ClearResults();
        var filter = ClassFilter;
        var skills = QuestlogMine.OfClass(m.Skills, static b => b.ClassId, filter);
        var plans = QuestlogMine.OfClass(m.Plans, static p => p.ClassId, filter);
        var title = UiKit.Text(string.Format(T.ImportMineTitle, Slug), 13.5, Fg.Text, 600);
        UiKit.Margin(title, 0, 8, 0, 2);
        _results.Append(title);
        var sl = UiKit.SectionLabel(string.Format(T.ImportMineSkills, skills.Count));
        UiKit.Margin(sl, 0, 10, 0, 6);
        _results.Append(sl);
        foreach (var b in skills) _results.Append(Row(b, AddMine, false));
        if (skills.Count == 0) Message(T.ImportMineNone);
        var pl = UiKit.SectionLabel(string.Format(T.ImportMinePlans, plans.Count));
        UiKit.Margin(pl, 0, 10, 0, 6);
        _results.Append(pl);
        foreach (var p in plans) _results.Append(Row(p, AddPlan, false));
        if (plans.Count == 0) Message(T.ImportMineNone);
        _status.SetText(string.Format(T.ImportMineCount, m.Skills.Count, m.Plans.Count));
        _scroll.GetVadjustment().SetValue(0);
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
        _status.SetText(ImportPlan is null ? "" : ImportPlan(p, Slug));
    }

    /// <summary>Box → setting: a bare slug or any of the user's links; empty clears it. False (and a status) when the text is neither.</summary>
    private bool CommitUser()
    {
        var text = _user.GetText().Trim();
        string slug;
        if (text.Length == 0) slug = "";
        else if (QuestlogMine.ParseUserSlug(text) is { } s) slug = s;
        else { _status.SetText(T.ImportMineBadUser); return false; }
        if (_user.GetText() != slug) _user.SetText(slug);
        if (slug == Slug) return true;
        Shell.RefreshSettings();
        Shell.Settings.QuestlogSlug = slug;
        Shell.SaveSettings();
        return true;
    }
}
