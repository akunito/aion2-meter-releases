using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// Build tab, Daevanion sub-tab on Linux (WPF Views/DaevanionView, A2M-21 phase 4): the class's boards with the active build's plan over the
/// nodes open on the captured profile. Clicks toggle plan nodes under <see cref="DaevanionPlan"/>'s connection rule; every edit goes out
/// through <see cref="PlanEdited"/> and the BuildView saves it with its own debounce. No build → read-only board of the open nodes.
/// Open nodes off the plan get the red outline (Diego 2026-10-11).
/// </summary>
internal sealed class DaevanionPage : UserControl
{
    private static UiText T => UiText.Current;
    private const double Cell = NodeTile.Size + 2;   // mock .board: 32 px cells, 2 px gap
    // one client of our own: an import is rare and the 400 ms pace is per client
    private static readonly Lazy<QuestlogClient> Questlog = new(() => new QuestlogClient());

    private readonly AppShell _shell;
    private Build? _build;
    private CharacterProfile? _profile;
    private Dictionary<int, List<int>>? _shownPlan;
    private bool _showOpen, _skillIcons, _drawn;
    private string _lang = "en", _classId = "", _planName = "";
    private DaevClass _class = DaevClass.Empty;
    private HashSet<int> _open = new();
    private int _boardId;
    private readonly List<NodeTile> _tiles = new();
    private readonly Dictionary<int, SkillInfo?> _nodeSkill = new();

    private readonly Grid _layout = new();
    private readonly Border _boardsCard, _statsCard;
    private readonly TextBlock _title, _points, _note, _missing, _legendOpen, _legendPlan, _legendOff, _statsPts;
    private readonly WrapPanel _boardRow = new() { Margin = new Thickness(12, 10, 6, 4) };
    private readonly Border _boardFrame;
    private readonly Canvas _board = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly CheckBox _showOpenBox, _skillIconsBox;
    private readonly StackPanel _stats = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Grid _importRow;
    private readonly TextBox _importBox;

    /// <summary>The whole plan (board → node ids) after an edit; the host saves it into its build.</summary>
    public event Action<Dictionary<int, List<int>>>? PlanEdited;
    /// <summary>A plan was imported from questlog (plan, author slug): the host records where it came from and questlog's date.</summary>
    public event Action<QuestlogDaevPlan, string>? PlanImported;

    public DaevanionPage(AppShell shell)
    {
        _shell = shell;
        _showOpen = shell.Settings.DaevShowOpen;
        _skillIcons = shell.Settings.DaevSkillIcons;

        var main = new StackPanel();
        _title = BuildUi.BandTitle("");
        _points = new TextBlock { FontSize = 12, Foreground = BuildColors.Mute, VerticalAlignment = VerticalAlignment.Center };
        var import = BuildUi.TextBtn(T.DaevImport, ToggleImport, 12, BuildColors.Blue, T.DaevImportTip);
        import.Margin = new Thickness(10, 0, 0, 0);
        var bandRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bandRight.Children.Add(_points); bandRight.Children.Add(import);
        main.Children.Add(BuildUi.Band(UiKit.Row("*,Auto", _title, bandRight)));
        _importBox = UiKit.Input("", double.NaN);
        _importBox.Watermark = "https://questlog.gg/aion-2/en/daevanion-planner/…?build-id=…";
        UiKit.Tip(_importBox, T.DaevImportHint);
        _importBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; ImportFromBox(); } };
        var go = BuildUi.Flat(T.DaevImport.TrimEnd('…'), ImportFromBox);
        go.Margin = new Thickness(6, 0, 0, 0);
        var cancel = BuildUi.TextBtn(T.BuildMacroCancel, () => _importRow!.IsVisible = false);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        _importRow = UiKit.Row("*,Auto,Auto", _importBox, go, cancel);
        _importRow.Margin = new Thickness(12, 10, 12, 0);
        _importRow.IsVisible = false;
        main.Children.Add(_importRow);
        main.Children.Add(_boardRow);
        _boardFrame = new Border { Background = BuildColors.BoardBack, BorderBrush = BuildColors.Line, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(8), Margin = new Thickness(12, 0), Padding = new Thickness(10), Child = _board };
        main.Children.Add(_boardFrame);
        _note = new TextBlock { FontSize = 12.5, Foreground = BuildColors.Amber, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 0), IsVisible = false };
        main.Children.Add(_note);
        main.Children.Add(new TextBlock { Text = T.DaevHint, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = BuildColors.Mute, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10, 12, 6) });
        _showOpenBox = UiKit.Check(T.DaevShowOpen, _showOpen);
        _showOpenBox.Margin = new Thickness(0, 0, 14, 0);
        _showOpenBox.IsCheckedChanged += (_, _) => { _showOpen = _showOpenBox.IsChecked == true; SaveFlag(s => s.DaevShowOpen = _showOpen); UpdateTiles(); };
        _skillIconsBox = UiKit.Check(T.DaevSkillIcons, _skillIcons);
        _skillIconsBox.Margin = new Thickness(0, 0, 14, 0);
        _skillIconsBox.IsCheckedChanged += (_, _) => { _skillIcons = _skillIconsBox.IsChecked == true; SaveFlag(s => s.DaevSkillIcons = _skillIcons); UpdateTiles(); };
        _missing = new TextBlock { FontSize = 13, Foreground = BuildColors.Amber, VerticalAlignment = VerticalAlignment.Center };
        var checks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 0, 12, 10) };
        checks.Children.Add(_showOpenBox); checks.Children.Add(_skillIconsBox); checks.Children.Add(_missing);
        main.Children.Add(checks);
        var selectAll = BuildUi.Flat(T.DaevSelectAll, SelectAll, 13, T.DaevSelectAllTip);
        selectAll.Background = Visuals.Solid(0xFF3B8BE0); selectAll.BorderBrush = Visuals.Solid(0xFF3B8BE0);
        selectAll.Padding = new Thickness(8); selectAll.HorizontalAlignment = HorizontalAlignment.Stretch; selectAll.HorizontalContentAlignment = HorizontalAlignment.Center;
        var reset = BuildUi.Flat(T.DaevReset, Reset, 13, T.DaevResetTip);
        reset.Padding = new Thickness(8); reset.HorizontalAlignment = HorizontalAlignment.Stretch; reset.HorizontalContentAlignment = HorizontalAlignment.Center;
        var buttons = UiKit.Row("*,6,*", selectAll, null, reset);
        buttons.Margin = new Thickness(12, 0, 12, 12);
        main.Children.Add(buttons);
        var legend = new WrapPanel { Margin = new Thickness(12, 0, 12, 10) };
        legend.Children.Add(Legend(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Visuals.Solid(0x99D3AD0F) }, _legendOpen = BuildUi.Muted("", 11)));
        legend.Children.Add(Legend(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), BorderBrush = BuildColors.Green, BorderThickness = new Thickness(1.5) }, _legendPlan = BuildUi.Muted("", 11)));
        legend.Children.Add(Legend(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), BorderBrush = BuildColors.Bad, BorderThickness = new Thickness(1.5) }, _legendOff = BuildUi.Muted("", 11)));
        legend.Children.Add(Legend(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Visuals.Solid(0xFF333333) }, BuildUi.Muted(T.DaevLegendOff, 11)));
        main.Children.Add(legend);
        _boardsCard = BuildUi.Card(main);
        _boardsCard.VerticalAlignment = VerticalAlignment.Top;

        var side = new StackPanel();
        _statsPts = new TextBlock { FontSize = 12, Foreground = BuildColors.Mute, VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(BuildUi.Band(UiKit.Row("*,Auto", BuildUi.BandTitle(T.DaevStats), _statsPts)));
        side.Children.Add(_stats);
        _statsCard = BuildUi.Card(side);
        _statsCard.VerticalAlignment = VerticalAlignment.Top;

        _layout.Children.Add(_boardsCard);
        _layout.Children.Add(_statsCard);
        Content = _layout;
        Layout(true);
        SizeChanged += (_, e) => Layout(e.NewSize.Width >= 760);
    }

    /// <summary>Stats beside the boards from 760 px (the WPF 250 px column); below them on the narrow card.</summary>
    private void Layout(bool wide)
    {
        _layout.ColumnDefinitions = wide ? new ColumnDefinitions("*,10,250") : new ColumnDefinitions("*");
        _layout.RowDefinitions = wide ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
        Grid.SetColumn(_boardsCard, 0); Grid.SetRow(_boardsCard, 0);
        Grid.SetColumn(_statsCard, wide ? 2 : 0); Grid.SetRow(_statsCard, wide ? 0 : 1);
    }

    private static Control Legend(Control swatch, TextBlock text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        swatch.Margin = new Thickness(0, 0, 5, 0);
        swatch.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(swatch);
        sp.Children.Add(text);
        return sp;
    }

    private void SaveFlag(Action<AppSettings> set)
    {
        _shell.RefreshSettings();
        set(_shell.Settings);
        _shell.SaveSettings();
    }

    /// <summary>The host's build and profile; cheap when nothing this page draws changed (the host calls it on every redraw).</summary>
    public void Show(Build? build, CharacterProfile? profile, string lang)
    {
        var same = _drawn && ReferenceEquals(build?.Daevanion, _shownPlan) && build?.Id == _build?.Id && ReferenceEquals(profile, _profile) && lang == _lang;
        _build = build; _profile = profile; _lang = lang;
        if (same) return;
        _shownPlan = build?.Daevanion;
        RenderAll();
    }

    public void IconsLanded() { if (_drawn) UpdateTiles(); }

    // ------------------------------------------------------------------ render

    private void RenderAll()
    {
        if (_shell.Engine is not { } engine) return;
        _drawn = true;
        var cls = BuildEditor.ClassIdOf(_profile?.ClassName) ?? (_build?.ClassId is { Length: > 0 } bc ? bc : "");
        if (cls != _classId) { _classId = cls; _nodeSkill.Clear(); }
        _class = engine.Daevanion.ForClass(cls);
        _open = DaevanionPlan.OpenSet(_profile, _class);
        if (_class.Board(_boardId) is null) _boardId = _class.Boards.Length > 0 ? _class.Boards[0].Id : 0;
        _planName = _build?.PlanName ?? "";
        RenderTitle();
        _boardFrame.IsVisible = _class.Boards.Length > 0;
        Note(_class.Boards.Length == 0 ? T.DaevNoClass : _build is null ? T.DaevNoBuild : "");
        RenderBoard();
        RenderCounts();
    }

    private List<int>? RawPlan(int board) => _build?.Daevanion.GetValueOrDefault(board);

    private void RenderTitle()
    {
        var who = _profile is { } p ? string.Format(T.DaevTitle, p.Ref.Name) : T.DaevTitleNoChar;
        _title.Text = _planName.Length > 0 ? who + " · " + string.Format(T.DaevPlanFrom, _planName) : who;
    }

    /// <summary>Tiles of the chosen board, cropped to the box of its nodes (not 15×15 with empty margins).</summary>
    private void RenderBoard()
    {
        _board.Children.Clear();
        _tiles.Clear();
        if (_class.Board(_boardId) is not { } b) { _board.Width = _board.Height = 0; return; }
        _board.Width = (b.MaxCol - b.MinCol + 1) * Cell - 2;
        _board.Height = (b.MaxRow - b.MinRow + 1) * Cell - 2;
        foreach (var n in b.Nodes)
        {
            var t = new NodeTile(n);
            Canvas.SetLeft(t, (n.Col - b.MinCol) * Cell);
            Canvas.SetTop(t, (n.Row - b.MinRow) * Cell);
            t.PointerPressed += NodePressed;
            ToolTip.SetTip(t, "");
            ToolTip.SetShowDelay(t, 250);
            ToolTip.SetPlacement(t, PlacementMode.Pointer);
            t.AddHandler(ToolTip.ToolTipOpeningEvent, (_, _) => ToolTip.SetTip(t, BuildUi.TipFrame(TipContent(t), 300)));
            _board.Children.Add(t);
            _tiles.Add(t);
        }
        UpdateTiles();
    }

    private void UpdateTiles()
    {
        if (_class.Board(_boardId) is not { } b) return;
        var plan = new HashSet<int>(DaevanionPlan.Effective(b, RawPlan(b.Id), _open));
        var hasPlan = RawPlan(b.Id) is { Count: > 0 };   // without a plan nothing is "off plan"
        foreach (var t in _tiles)
        {
            var n = t.Node;
            var open = _open.Contains(n.Id);
            var planned = plan.Contains(n.Id);
            t.Set(open, planned, hasPlan && open && !planned && !n.Start, open && !_showOpen && !n.Start, Sprite(n, open), _skillIcons && n.IsSkill ? SkillIcon(n) : null);
        }
    }

    private Bitmap? Sprite(DaevNode n, bool open)
    {
        if (n.Sprite.Length == 0) return null;
        var disabled = !open && !n.Start;   // the CDN has no Start_Disabled
        return _shell.Icons.Url(disabled ? "daev:" + n.Sprite + "_Disabled" : "daev:" + n.Sprite, DaevanionBook.SpriteUrl(n.Sprite, disabled), 32);
    }

    private Bitmap? SkillIcon(DaevNode n) =>
        SkillOf(n) is { Icon.Length: > 0 } s ? _shell.Icons.Url("skill:" + s.Icon, SkillBook.IconUrl(s.Icon), 48) : null;   // same key and width as the Skills page: one decode

    /// <summary>The class skill a "Skill Level Up" node raises: its English effect is "&lt;skill name&gt; +1".</summary>
    private SkillInfo? SkillOf(DaevNode n)
    {
        if (_nodeSkill.TryGetValue(n.Id, out var s)) return s;
        s = null;
        if (_shell.Engine is { } e && n.EffectsEn.Length > 0 && DaevanionPlan.TryParseEffect(n.EffectsEn[0], out var name, out _, out _))
            foreach (var x in e.Skills.ForClass(_classId))
                if (string.Equals(x.NameEn, name, StringComparison.OrdinalIgnoreCase)) { s = x; break; }
        _nodeSkill[n.Id] = s;
        return s;
    }

    /// <summary>Board buttons, header points, missing line, legend and the stats panel: everything that counts nodes.</summary>
    private void RenderCounts()
    {
        _boardRow.Children.Clear();
        var planAll = new List<int>(400);
        var nowAll = new List<int>(256);
        DaevBoard? cur = null;
        int curOpen = 0, curPlan = 0, curMissing = 0, curPlanOpen = 0, curOff = 0;
        foreach (var b in _class.Boards)
        {
            var plan = DaevanionPlan.Effective(b, RawPlan(b.Id), _open);
            var open = 0;
            foreach (var n in b.Nodes) if (!n.Start && _open.Contains(n.Id)) { open++; nowAll.Add(n.Id); }
            planAll.AddRange(plan);
            if (b.Id == _boardId)
            {
                cur = b; curOpen = open; curPlan = plan.Count;
                curPlanOpen = 0;
                foreach (var id in plan) if (_open.Contains(id)) curPlanOpen++;
                curMissing = curPlan - curPlanOpen;
                curOff = RawPlan(b.Id) is { Count: > 0 } ? open - curPlanOpen : 0;
            }
            _boardRow.Children.Add(BoardButton(b, open, plan.Count));
        }
        var planCost = DaevanionPlan.Cost(_class, planAll);
        var nowCost = DaevanionPlan.Cost(_class, nowAll);
        _points.Text = string.Format(T.DaevPoints, planCost);
        _statsPts.Text = string.Format(T.DaevStatsPts, planCost, nowCost);
        _missing.Text = cur is not null && _build is not null && _profile is not null ? string.Format(T.DaevMissing, cur.Name(_lang), curMissing, curPlan) : "";
        _legendOpen.Text = string.Format(T.DaevLegendOpen, _profile?.Ref.Name ?? "—", curOpen);
        _legendPlan.Text = string.Format(T.DaevLegendPlan, curPlan, Math.Max(0, curPlan - curPlanOpen));
        _legendOff.Text = string.Format(T.DaevLegendOffPlan, Math.Max(0, curOff));
        RenderStats(DaevanionPlan.Summarize(_class, planAll, _lang), DaevanionPlan.Summarize(_class, nowAll, _lang));
    }

    private Border BoardButton(DaevBoard b, int open, int plan)
    {
        var on = b.Id == _boardId;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = b.Name(_lang), FontSize = 13, Foreground = BuildColors.Text, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = string.Format(T.DaevBoardLine, open, b.Total, plan), FontSize = 11, Foreground = BuildColors.Mute, HorizontalAlignment = HorizontalAlignment.Center });
        var bd = new Border
        {
            Background = on ? BuildColors.BdOnBack : BuildColors.Btn, BorderBrush = on ? BuildColors.BdOnLine : BuildColors.Line2, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6), MinWidth = 92, Margin = new Thickness(0, 0, 6, 6), Child = panel, Cursor = new Cursor(StandardCursorType.Hand),
        };
        bd.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (_boardId == b.Id) return;
            _boardId = b.Id;
            RenderBoard();
            RenderCounts();
        };
        return bd;
    }

    /// <summary>Mock .stats: group label, then one #141414 row per effect with the plan's and today's sums (skills in purple).</summary>
    private void RenderStats(List<EffectSum> plan, List<EffectSum> now)
    {
        _stats.Children.Clear();
        var nowBy = new Dictionary<string, EffectSum>(now.Count);
        foreach (var e in now) nowBy[e.Key] = e;
        _stats.Children.Add(StatRow(T.DaevColPlan, T.DaevColNow, "", BuildColors.Mute, BuildColors.Mute, header: true));
        DaevGroup? group = null;
        foreach (var e in plan)
        {
            if (e.Group != group)
            {
                group = e.Group;
                _stats.Children.Add(new TextBlock { Text = GroupName(e.Group), FontSize = 13, Foreground = BuildColors.Dim, Margin = new Thickness(12, 8, 12, 2) });
            }
            var skill = e.Group == DaevGroup.Skill;
            _stats.Children.Add(StatRow(e.Amount, nowBy.TryGetValue(e.Key, out var n) ? n.Amount : "—", e.Name, skill ? BuildColors.Purple : BuildColors.Text, skill ? BuildColors.Purple : BuildColors.Dim, header: false));
        }
    }

    private static Control StatRow(string plan, string now, string name, IBrush planBrush, IBrush nowBrush, bool header)
    {
        var size = header ? 11 : 12.5;
        var nameText = new TextBlock { Text = name, FontSize = size, Foreground = BuildColors.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        if (name.Length > 22) UiKit.Tip(nameText, name);
        var g = UiKit.Row("*,54,48", nameText,
            new TextBlock { Text = plan, FontSize = size, Foreground = planBrush, HorizontalAlignment = HorizontalAlignment.Right },
            new TextBlock { Text = now, FontSize = size, Foreground = nowBrush, HorizontalAlignment = HorizontalAlignment.Right });
        return header
            ? new Border { Padding = new Thickness(12, 8, 12, 0), Margin = new Thickness(8, 0), Child = g }
            : new Border { Background = BuildColors.RowBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 4), Margin = new Thickness(8, 2), Child = g };
    }

    private static string GroupName(DaevGroup g) => g switch
    {
        DaevGroup.Resource => T.DaevGroupResource,
        DaevGroup.Critical => T.DaevGroupCritical,
        DaevGroup.Attack => T.DaevGroupAttack,
        DaevGroup.Defense => T.DaevGroupDefense,
        DaevGroup.PvP => T.DaevGroupPvP,
        _ => T.DaevGroupSkills,
    };

    private void Note(string text)
    {
        _note.Text = text;
        _note.IsVisible = text.Length > 0;
    }

    private Control TipContent(NodeTile t)
    {
        var n = t.Node;
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = n.Name(_lang), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = BuildColors.Text, TextWrapping = TextWrapping.Wrap });
        foreach (var e in n.Effects(_lang)) p.Children.Add(new TextBlock { Text = e, FontSize = 12, Foreground = BuildColors.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        if (!n.Start) p.Children.Add(new TextBlock { Text = string.Format(T.DaevCost, n.Grade, n.Cost), FontSize = 11, Foreground = BuildColors.Mute, Margin = new Thickness(0, 6, 0, 0) });
        var (state, brush) = t.Open ? (T.DaevStateOpen, BuildColors.Green) : t.Planned ? (T.DaevStatePlanned, BuildColors.Green) : t.OffPlan ? (T.DaevStateOpen, BuildColors.Bad) : (T.DaevStateNot, BuildColors.Mute);
        p.Children.Add(new TextBlock { Text = state, FontSize = 11.5, Foreground = brush, Margin = new Thickness(0, 4, 0, 0) });
        return p;
    }

    // ------------------------------------------------------------------ edits

    private void NodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not NodeTile t || !e.GetCurrentPoint(t).Properties.IsLeftButtonPressed || _class.Board(_boardId) is not { } b) return;
        e.Handled = true;
        if (_build is null) { Note(T.DaevNoBuild); return; }
        var next = DaevanionPlan.Toggle(b, RawPlan(b.Id), t.Node.Id, _open, out var r);
        switch (r)
        {
            case DaevToggle.Added or DaevToggle.Removed: Commit(b.Id, next); break;
            case DaevToggle.Open: Note(T.DaevIsOpen); break;
            case DaevToggle.NotConnected: Note(T.DaevNotConnected); break;
        }
    }

    private void SelectAll()
    {
        if (_class.Board(_boardId) is not { } b) return;
        if (_build is null) { Note(T.DaevNoBuild); return; }
        Commit(b.Id, DaevanionPlan.SelectAll(b));
    }

    private void Reset()
    {
        if (_class.Board(_boardId) is not { } b) return;
        if (_build is null) { Note(T.DaevNoBuild); return; }
        Commit(b.Id, DaevanionPlan.Reset(b, _open));
    }

    private void Commit(int board, List<int> nodes)
    {
        if (_build is null) return;
        Apply(new Dictionary<int, List<int>>(_build.Daevanion) { [board] = nodes });
    }

    private void Apply(Dictionary<int, List<int>> plan)
    {
        if (_build is null) return;
        _build = _build with { Daevanion = plan };
        _shownPlan = plan;
        Note("");
        UpdateTiles();
        RenderCounts();
        PlanEdited?.Invoke(plan);
    }

    // ------------------------------------------------------------------ questlog import

    private void ToggleImport()
    {
        if (_build is null) { Note(T.DaevNoBuild); return; }
        _importRow.IsVisible = !_importRow.IsVisible;
        if (_importRow.IsVisible) _importBox.Focus();
    }

    private void ImportFromBox()
    {
        var url = _importBox.Text ?? "";
        _ = ImportPlanFromUrlAsync(url);
    }

    /// <summary>
    /// Replaces the active build's plan with a questlog daevanion-planner build: public tRPC daevanionPlanner.getDaevanionPlannerBySlug
    /// (the user's planner builds, each with selectedNodes per board). Refused when the planner build is for another class.
    /// </summary>
    public async Task ImportPlanFromUrlAsync(string url)
    {
        if (_build is null) { Note(T.DaevNoBuild); return; }
        if (DaevanionPlan.ParsePlannerUrl(url) is not { } r) { Note(T.DaevImportBadUrl); return; }
        Log.Info($"Daevanion import: questlog {r.Slug} planner build {r.Id}");
        Note(string.Format(T.DaevImporting, r.Id));
        var buildId = _build.Id;
        QuestlogDaevPlan? plan;
        try
        {
            var json = await Questlog.Value.GetAsync(DaevanionPlan.PlannerApiUrl(r.Slug));
            plan = DaevanionPlan.ParsePlanner(json, r.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Log.Error("Daevanion import", ex);
            Note(string.Format(T.DaevImportFailed, ex.Message));
            return;
        }
        if (_build is null || _build.Id != buildId) return;   // another build was picked meanwhile
        if (plan is null) { Note(string.Format(T.DaevImportNotFound, r.Id)); return; }
        if (_classId.Length > 0 && plan.ClassId.Length > 0 && !plan.ClassId.Equals(_classId, StringComparison.OrdinalIgnoreCase))
        {
            Note(string.Format(T.DaevImportClass, r.Id, plan.ClassId, _classId));
            return;
        }
        var cls = _class.Boards.Length > 0 ? _class : _shell.Engine!.Daevanion.ForClass(plan.ClassId);
        var next = DaevanionPlan.FromQuestlog(cls, plan, out var count);
        Apply(next);
        PlanImported?.Invoke(plan, r.Slug);
        if (QuestlogService.LearnSlug(_shell.Settings, r.Slug)) _shell.SaveSettings();
        _planName = plan.Name; RenderTitle();
        _importRow.IsVisible = false;
        Note(string.Format(T.DaevImported, plan.Name, count.ToString(CultureInfo.InvariantCulture), next.Count));
        Log.Info($"Daevanion import: \"{plan.Name}\" {count} nodes on {next.Count} boards into build {buildId}");
    }
}
