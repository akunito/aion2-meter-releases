using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;

namespace AionMeter.App.Views;

/// <summary>
/// Build tab, Daevanion sub-tab (A2M-21 phase 4): the five boards of the class with the active build's plan over the nodes open on
/// the official profile. Clicks toggle plan nodes under <see cref="DaevanionPlan"/>'s connection rule; every edit goes out through
/// <see cref="PlanEdited"/> and the hosting BuildView saves it with its own debounce. No build → read-only board of the open nodes.
/// </summary>
public partial class DaevanionView : UserControl
{
    private static UiText T => UiText.Current;
    private const double Step = NodeTile.Size + 2;   // mock .board: 32 px cells, 2 px gap
    private static readonly Brush Text = SkillTile.Frozen(0xFA, 0xFA, 0xFA), Dim = SkillTile.Frozen(0xAF, 0xAF, 0xAF), Mute = SkillTile.Frozen(0x7D, 0x7D, 0x7D),
        Purple = SkillTile.Frozen(0xA9, 0x79, 0xCB), Green = SkillTile.Frozen(0x3D, 0xDC, 0x84), RowBack = SkillTile.Frozen(0x14, 0x14, 0x14),
        BdBack = SkillTile.Frozen(0x2B, 0x2B, 0x2B), BdLine = SkillTile.Frozen(0x37, 0x37, 0x37), BdOnBack = SkillTile.Frozen(0x33, 0x33, 0x33), BdOnLine = SkillTile.Frozen(0x55, 0x55, 0x55);
    // one client of our own: an import is rare and the 400 ms pace is per client
    private static readonly Lazy<QuestlogClient> Questlog = new(() => new QuestlogClient());

    private MeterService? _meter;
    private IconCache? _icons;
    private Build? _build;
    private CharacterProfile? _profile;
    private Dictionary<int, List<int>>? _shownPlan;
    private bool _sample, _pending, _showOpen = true, _skillIcons = true;
    private string _lang = "en", _classId = "";
    private DaevClass _class = DaevClass.Empty;
    private HashSet<int> _open = new();
    private int _boardId;
    private readonly List<NodeTile> _tiles = new();
    private readonly Dictionary<int, SkillInfo?> _nodeSkill = new();
    private readonly DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>The whole plan (board → node ids, plan ∪ open of every edited board) after an edit; the host saves it into its build.</summary>
    public event Action<Dictionary<int, List<int>>>? PlanEdited;

    public DaevanionView()
    {
        InitializeComponent();
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); UpdateTiles(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && _pending) RenderAll(); };
        Unloaded += (_, _) => { if (_icons is not null) _icons.Loaded -= OnIcon; _iconTick.Stop(); };
        Loaded += (_, _) => { if (_icons is not null && !_sample) { _icons.Loaded -= OnIcon; _icons.Loaded += OnIcon; } };
    }

    public void Attach(MeterService meter, IconCache icons)
    {
        _meter = meter;
        _icons = icons;
        _showOpen = meter.Settings.DaevShowOpen;
        _skillIcons = meter.Settings.DaevSkillIcons;
        ShowOpen.IsChecked = _showOpen;
        SkillIconsBox.IsChecked = _skillIcons;
    }

    /// <summary>
    /// The host's current build and profile; cheap when nothing that this page draws changed (the host calls it on every redraw).
    /// Hidden page: drawn on the next showing. <paramref name="sample"/> (render tool) draws at once, nothing is visible offscreen.
    /// </summary>
    public void Show(Build? build, CharacterProfile? profile, bool sample)
    {
        if (_meter is null) return;
        var lang = _meter.Settings.SkillLanguage == "es" ? "es" : "en";
        var same = ReferenceEquals(build?.Daevanion, _shownPlan) && build?.Id == _build?.Id && ReferenceEquals(profile, _profile) && lang == _lang && sample == _sample;
        _build = build; _profile = profile; _lang = lang; _sample = sample;
        if (same && !_pending) return;
        _shownPlan = build?.Daevanion;
        if (sample || IsVisible) RenderAll(); else _pending = true;
    }

    private void OnIcon() { if (!_iconTick.IsEnabled) _iconTick.Start(); }

    // ------------------------------------------------------------------ render

    private void RenderAll()
    {
        _pending = false;
        if (_meter is null) return;
        var cls = BuildEditor.ClassIdOf(_profile?.ClassName) ?? (_build?.ClassId is { Length: > 0 } bc ? bc : "");
        if (cls != _classId) { _classId = cls; _nodeSkill.Clear(); }
        _class = _meter.Daevanion.ForClass(cls);
        _open = DaevanionPlan.OpenSet(_profile, _class);
        if (_class.Board(_boardId) is null) _boardId = _class.Boards.Length > 0 ? _class.Boards[0].Id : 0;
        TitleText.Text = _profile is { } p ? string.Format(T.DaevTitle, p.Ref.Name) : T.DaevTitleNoChar;
        BoardFrame.Visibility = _class.Boards.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Note(_class.Boards.Length == 0 ? T.DaevNoClass : _build is null ? T.DaevNoBuild : "");
        RenderBoard();
        RenderCounts();
    }

    private List<int>? RawPlan(int board) => _build?.Daevanion.GetValueOrDefault(board);

    /// <summary>Tiles of the chosen board, cropped to the box of its nodes (not 15×15 with empty margins).</summary>
    private void RenderBoard()
    {
        BoardCanvas.Children.Clear();
        _tiles.Clear();
        if (_class.Board(_boardId) is not { } b) { BoardCanvas.Width = BoardCanvas.Height = 0; return; }
        BoardCanvas.Width = (b.MaxCol - b.MinCol + 1) * Step - 2;
        BoardCanvas.Height = (b.MaxRow - b.MinRow + 1) * Step - 2;
        foreach (var n in b.Nodes)
        {
            var t = new NodeTile(n);
            Canvas.SetLeft(t, (n.Col - b.MinCol) * Step);
            Canvas.SetTop(t, (n.Row - b.MinRow) * Step);
            t.MouseLeftButtonUp += Node_Click;
            t.ToolTip = "";   // placeholder: the real ToolTip is made on the first opening, its content on every opening
            ToolTipService.SetInitialShowDelay(t, 250);
            ToolTipService.SetShowDuration(t, 60000);
            ToolTipService.SetPlacement(t, PlacementMode.Mouse);
            t.ToolTipOpening += Tip_Opening;
            BoardCanvas.Children.Add(t);
            _tiles.Add(t);
        }
        UpdateTiles();
    }

    private void UpdateTiles()
    {
        if (_class.Board(_boardId) is not { } b) return;
        var plan = new HashSet<int>(DaevanionPlan.Effective(b, RawPlan(b.Id), _open));
        foreach (var t in _tiles)
        {
            var n = t.Node;
            var open = _open.Contains(n.Id);
            t.Set(open, plan.Contains(n.Id), open && !_showOpen && !n.Start, Sprite(n, open), _skillIcons && n.IsSkill ? SkillIcon(n) : null);
        }
    }

    private ImageSource? Sprite(DaevNode n, bool open)
    {
        if (_icons is null || n.Sprite.Length == 0) return null;
        var disabled = !open && !n.Start;   // the CDN has no Start_Disabled
        return _icons.GetUrl(disabled ? "daev:" + n.Sprite + "_Disabled" : "daev:" + n.Sprite, DaevanionBook.SpriteUrl(n.Sprite, disabled), 32);
    }

    private ImageSource? SkillIcon(DaevNode n)
    {
        if (_icons is null || SkillOf(n) is not { Icon.Length: > 0 } s) return null;
        return _icons.GetUrl("skill:" + s.Icon, SkillBook.IconUrl(s.Icon), 48);   // same key and width as the Skills sub-tab: one decode for both
    }

    /// <summary>The class skill a "Skill Level Up" node raises: its English effect is "&lt;skill name&gt; +1".</summary>
    private SkillInfo? SkillOf(DaevNode n)
    {
        if (_nodeSkill.TryGetValue(n.Id, out var s)) return s;
        s = null;
        if (_meter is not null && n.EffectsEn.Length > 0 && DaevanionPlan.TryParseEffect(n.EffectsEn[0], out var name, out _, out _))
            foreach (var x in _meter.Skills.ForClass(_classId))
                if (string.Equals(x.NameEn, name, StringComparison.OrdinalIgnoreCase)) { s = x; break; }
        _nodeSkill[n.Id] = s;
        return s;
    }

    /// <summary>Board buttons, header points, missing line, legend and the stats panel: everything that counts nodes.</summary>
    private void RenderCounts()
    {
        BoardRow.Children.Clear();
        var planAll = new List<int>(400);
        var nowAll = new List<int>(256);
        DaevBoard? cur = null;
        int curOpen = 0, curPlan = 0, curMissing = 0;
        foreach (var b in _class.Boards)
        {
            var plan = DaevanionPlan.Effective(b, RawPlan(b.Id), _open);
            var open = 0;
            foreach (var n in b.Nodes) if (!n.Start && _open.Contains(n.Id)) { open++; nowAll.Add(n.Id); }
            planAll.AddRange(plan);
            if (b.Id == _boardId) { cur = b; curOpen = open; curPlan = plan.Count; curMissing = plan.Count - open; }
            BoardRow.Children.Add(BoardButton(b, open, plan.Count));
        }
        var planCost = DaevanionPlan.Cost(_class, planAll);
        var nowCost = DaevanionPlan.Cost(_class, nowAll);
        PointsText.Text = string.Format(T.DaevPoints, planCost);
        StatsPts.Text = string.Format(T.DaevStatsPts, planCost, nowCost);
        MissingText.Text = cur is not null && _build is not null && _profile is not null ? string.Format(T.DaevMissing, cur.Name(_lang), curMissing, curPlan) : "";
        LegendOpen.Text = string.Format(T.DaevLegendOpen, _profile?.Ref.Name ?? "—", curOpen);
        LegendPlan.Text = string.Format(T.DaevLegendPlan, curPlan);
        RenderStats(DaevanionPlan.Summarize(_class, planAll, _lang), DaevanionPlan.Summarize(_class, nowAll, _lang));
    }

    private Border BoardButton(DaevBoard b, int open, int plan)
    {
        var on = b.Id == _boardId;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = b.Name(_lang), FontSize = 13, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = string.Format(T.DaevBoardLine, open, b.Total, plan), FontSize = 11, Foreground = Mute, HorizontalAlignment = HorizontalAlignment.Center });
        var bd = new Border
        {
            Background = on ? BdOnBack : BdBack, BorderBrush = on ? BdOnLine : BdLine, BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6, 14, 6), MinWidth = 92, Margin = new Thickness(0, 0, 6, 6), Child = panel, Cursor = Cursors.Hand, Tag = b.Id,
        };
        bd.MouseLeftButtonUp += (_, _) =>
        {
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
        StatsList.Children.Clear();
        var nowBy = new Dictionary<string, EffectSum>(now.Count);
        foreach (var e in now) nowBy[e.Key] = e;
        var head = Row(T.DaevColPlan, T.DaevColNow, "", Mute, Mute, header: true);
        StatsList.Children.Add(head);
        DaevGroup? group = null;
        foreach (var e in plan)
        {
            if (e.Group != group)
            {
                group = e.Group;
                StatsList.Children.Add(new TextBlock { Text = GroupName(e.Group), FontSize = 13, Foreground = Dim, Margin = new Thickness(12, 8, 12, 2) });
            }
            var skill = e.Group == DaevGroup.Skill;
            StatsList.Children.Add(Row(e.Amount, nowBy.TryGetValue(e.Key, out var n) ? n.Amount : "—", e.Name, skill ? Purple : Text, skill ? Purple : Dim, header: false));
        }
    }

    private static FrameworkElement Row(string plan, string now, string name, Brush planBrush, Brush nowBrush, bool header)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var size = header ? 11 : 12.5;
        var nameText = new TextBlock { Text = name, FontSize = size, Foreground = Text, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name.Length > 22 ? name : null };
        var planText = new TextBlock { Text = plan, FontSize = size, Foreground = planBrush, HorizontalAlignment = HorizontalAlignment.Right };
        var nowText = new TextBlock { Text = now, FontSize = size, Foreground = nowBrush, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(planText, 1); Grid.SetColumn(nowText, 2);
        g.Children.Add(nameText); g.Children.Add(planText); g.Children.Add(nowText);
        return header
            ? new Border { Padding = new Thickness(12, 8, 12, 0), Margin = new Thickness(8, 0, 8, 0), Child = g }
            : new Border { Background = RowBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 2, 8, 2), Child = g };
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
        NoteText.Text = text;
        NoteText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ tooltip

    private void Tip_Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not NodeTile t) return;
        var tip = t.ToolTip as ToolTip ?? new ToolTip { Style = (Style)FindResource("NodeTip"), Placement = PlacementMode.Mouse };
        tip.Content = TipContent(t);
        t.ToolTip = tip;
    }

    private FrameworkElement TipContent(NodeTile t)
    {
        var n = t.Node;
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = n.Name(_lang), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        foreach (var e in n.Effects(_lang)) p.Children.Add(new TextBlock { Text = e, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        if (!n.Start) p.Children.Add(new TextBlock { Text = string.Format(T.DaevCost, n.Grade, n.Cost), FontSize = 11, Foreground = Mute, Margin = new Thickness(0, 6, 0, 0) });
        var (state, brush) = t.Open ? (T.DaevStateOpen, Green) : t.Planned ? (T.DaevStatePlanned, Green) : (T.DaevStateNot, Mute);
        p.Children.Add(new TextBlock { Text = state, FontSize = 11.5, Foreground = brush, Margin = new Thickness(0, 4, 0, 0) });
        return p;
    }

    // ------------------------------------------------------------------ edits

    private void Node_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not NodeTile t || _class.Board(_boardId) is not { } b) return;
        if (_build is null) { Note(T.DaevNoBuild); return; }
        var next = DaevanionPlan.Toggle(b, RawPlan(b.Id), t.Node.Id, _open, out var r);
        switch (r)
        {
            case DaevToggle.Added or DaevToggle.Removed: Commit(b.Id, next); break;
            case DaevToggle.Open: Note(T.DaevIsOpen); break;
            case DaevToggle.NotConnected: Note(T.DaevNotConnected); break;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_class.Board(_boardId) is not { } b) return;
        if (_build is null) { Note(T.DaevNoBuild); return; }
        Commit(b.Id, DaevanionPlan.SelectAll(b));
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_class.Board(_boardId) is not { } b) return;
        if (_build is null) { Note(T.DaevNoBuild); return; }
        Commit(b.Id, DaevanionPlan.Reset(b, _open));
    }

    private void Commit(int board, List<int> nodes)
    {
        if (_build is null) return;
        var plan = new Dictionary<int, List<int>>(_build.Daevanion) { [board] = nodes };
        Apply(plan);
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

    private void ShowOpen_Click(object sender, RoutedEventArgs e)
    {
        _showOpen = ShowOpen.IsChecked == true;
        if (_meter is not null && !_sample) { _meter.Settings.DaevShowOpen = _showOpen; _meter.Settings.Save(); }
        UpdateTiles();
    }

    private void SkillIcons_Click(object sender, RoutedEventArgs e)
    {
        _skillIcons = SkillIconsBox.IsChecked == true;
        if (_meter is not null && !_sample) { _meter.Settings.DaevSkillIcons = _skillIcons; _meter.Settings.Save(); }
        UpdateTiles();
    }

    // ------------------------------------------------------------------ questlog import

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_build is null) { Note(T.DaevNoBuild); return; }
        var box = new TextBox { Height = 28, ToolTip = T.DaevImportHint, Margin = new Thickness(0, 10, 0, 12), Background = SkillTile.Frozen(0x1F, 0x1F, 0x1F) };
        if (TryFindResource("Field") is Style field) box.Style = field;
        var ok = new Button { Content = T.DaevImport.TrimEnd('…'), IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = T.BuildMacroCancel, IsCancel = true, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
        if (TryFindResource("Flat") is Style flat) { ok.Style = flat; cancel.Style = flat; }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = T.DaevImportTitle, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Text });
        panel.Children.Add(new TextBlock { Text = T.DaevImportHint, FontSize = 11.5, Foreground = Mute, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = T.DaevImportTitle, Content = panel, Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            Background = SkillTile.Frozen(0x14, 0x14, 0x14), Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, Topmost = true,
            FontFamily = FontFamily,
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => box.Focus();
        if (dlg.ShowDialog() == true) ImportPlanFromUrl(box.Text);
    }

    /// <summary>
    /// Replaces the active build's plan with a questlog daevanion-planner build: public tRPC daevanionPlanner.getDaevanionPlannerBySlug
    /// (the user's planner builds, each with selectedNodes per board). Refused when the planner build is for another class.
    /// </summary>
    public async void ImportPlanFromUrl(string url)
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
        catch (Exception ex)   // UI boundary of an async void: network, Cloudflare, cancellation
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
        var cls = _class.Boards.Length > 0 ? _class : _meter!.Daevanion.ForClass(plan.ClassId);
        var next = new Dictionary<int, List<int>>();
        var count = 0;
        foreach (var b in cls.Boards)
        {
            if (!plan.Boards.TryGetValue(b.Id, out var ids)) continue;
            var list = ids.FindAll(id => cls.Node(id) is { Start: false } n && n.Board == b.Id);
            next[b.Id] = list;
            count += list.Count;
        }
        Apply(next);
        Note(string.Format(T.DaevImported, plan.Name, count.ToString(CultureInfo.InvariantCulture), next.Count));
        Log.Info($"Daevanion import: \"{plan.Name}\" {count} nodes on {next.Count} boards into build {buildId}");
    }
}
