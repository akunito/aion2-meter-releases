using System.Globalization;
using System.Text;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using C = AionMeter.Overlay.Views.BuildColors.C;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Build tab, Daevanion sub-tab on the layer-shell card (A2M-50, port of the Avalonia DaevanionPage = WPF Views/DaevanionView, A2M-21 phase 4):
/// the class's boards with the active build's plan over the nodes open on the captured profile. Clicks toggle plan nodes under
/// <see cref="DaevanionPlan"/>'s connection rule; every edit goes out through <see cref="PlanEdited"/> and the BuildView saves it with its own
/// debounce. No build → read-only board of the open nodes. Open nodes off the plan get the red outline (Diego 2026-10-11). The board is one
/// DrawingArea (nodes drawn with cairo, hit-tested by cell) that shrinks to the card's width like the key bar.
/// </summary>
internal sealed class DaevanionPage
{
    private static UiText T => UiText.Current;
    // one client of our own: an import is rare and the 400 ms pace is per client
    private static readonly Lazy<QuestlogClient> Questlog = new(() => new QuestlogClient());

    private struct NodeLook
    {
        public bool Open, Planned, OffPlan, Dim;
        public Gdk.Texture? Sprite, Icon;
    }

    private readonly AppShell _shell;
    private Build? _build;
    private CharacterProfile? _profile;
    private Dictionary<int, List<int>>? _shownPlan;
    private bool _showOpen, _skillIcons, _drawn, _wide;
    private string _lang = "en", _classId = "", _planName = "";
    private DaevClass _class = DaevClass.Empty;
    private HashSet<int> _open = new();
    private int _boardId, _tipIndex = -1, _tipGen = -1, _gen;
    private Gtk.Widget? _tip;
    private DaevBoard? _shownBoard;
    private NodeLook[] _looks = [];
    private readonly Dictionary<int, int> _cellIndex = new();
    private readonly Dictionary<int, SkillInfo?> _nodeSkill = new();
    private readonly List<(int Id, Gtk.Button Button, Gtk.Label Name, Gtk.Label Line)> _boardButtons = new();
    private readonly List<Gtk.Label> _groupPool = new();
    private readonly List<(Gtk.Box Row, Gtk.Label Name, Gtk.Label Plan, Gtk.Label Now)> _rowPool = new();

    private readonly Gtk.Box _root = UiKit.VBox(), _narrow = UiKit.VBox(), _wideRow = UiKit.HBox(10);
    private readonly Gtk.Box _boardsCard, _statsCard, _importRow, _stats = UiKit.VBox(), _statsHead;
    private readonly Gtk.Label _title, _points, _note, _missing, _legend, _statsPts;
    private readonly Gtk.Grid _boardRow = Gtk.Grid.New();
    private readonly Gtk.Box _boardFrame;
    private readonly Gtk.DrawingArea _board = Gtk.DrawingArea.New();
    private readonly Gtk.Entry _importBox;

    /// <summary>The whole plan (board → node ids) after an edit; the host saves it into its build.</summary>
    public event Action<Dictionary<int, List<int>>>? PlanEdited;
    /// <summary>A plan was imported from questlog (plan, author slug): the host records where it came from and questlog's date.</summary>
    public event Action<QuestlogDaevPlan, string>? PlanImported;

    public Gtk.Widget Widget => _root;

    public DaevanionPage(AppShell shell)
    {
        _shell = shell;
        _showOpen = shell.Settings.DaevShowOpen;
        _skillIcons = shell.Settings.DaevSkillIcons;

        var main = UiKit.VBox();
        _title = BuildUi.BandTitle("");
        _points = BuildUi.Tight("", 12, C.Mute);
        _points.MarginStart = UiKit.Px(8);
        var import = BuildUi.TextBtn(T.DaevImport, ToggleImport, "k-build-blue", T.DaevImportTip);
        import.MarginStart = UiKit.Px(10);
        var bandRight = UiKit.HBox();
        bandRight.Append(_points); bandRight.Append(import);
        main.Append(BuildUi.Band(_title, bandRight));
        _importBox = UiKit.Input("", double.NaN);
        _importBox.PlaceholderText = "https://questlog.gg/aion-2/en/daevanion-planner/…?build-id=…";
        _importBox.SetTooltipText(T.DaevImportHint);
        _importBox.OnActivate += (_, _) => ImportFromBox();
        var go = BuildUi.Flat(T.DaevImport.TrimEnd('…'), ImportFromBox);
        go.MarginStart = UiKit.Px(6);
        var cancel = BuildUi.TextBtn(T.BuildMacroCancel, () => _importRow!.Visible = false);
        cancel.MarginStart = UiKit.Px(8);
        _importRow = UiKit.HBox();
        _importRow.Append(_importBox); _importRow.Append(go); _importRow.Append(cancel);
        UiKit.Margin(_importRow, 12, 10, 12, 0);
        _importRow.Visible = false;
        main.Append(_importRow);
        _boardRow.ColumnHomogeneous = true;
        _boardRow.ColumnSpacing = UiKit.Px(6);
        _boardRow.RowSpacing = UiKit.Px(6);
        UiKit.Margin(_boardRow, 12, 10, 12, 10);
        main.Append(_boardRow);
        _board.Hexpand = true;
        _board.SetDrawFunc((_, cr, w, _) => DrawBoard(cr, w));
        _board.OnResize += (_, e) => SizeBoard(e.Width);
        _board.SetCursorFromName("pointer");
        var click = Gtk.GestureClick.New();
        click.OnPressed += (_, e) => NodePressed(e.X, e.Y);
        _board.AddController(click);
        _board.HasTooltip = true;
        _board.OnQueryTooltip += (_, e) =>
        {
            var i = NodeAt(e.X, e.Y);
            if (i < 0) return false;
            if (i != _tipIndex || _tipGen != _gen) { _tip = BuildUi.TipFrame(TipContent(i), 300); _tipIndex = i; _tipGen = _gen; }
            e.Tooltip.SetCustom(_tip);
            return true;
        };
        _boardFrame = UiKit.VBox();
        _boardFrame.AddCssClass("k-build-board");
        _boardFrame.Append(_board);
        main.Append(_boardFrame);
        _note = BuildUi.Label("", 12.5, C.Amber);
        _note.Xalign = 0.5f;
        _note.Justify = Gtk.Justification.Center;
        UiKit.Margin(_note, 12, 8, 12, 0);
        _note.Visible = false;
        main.Append(_note);
        var hint = BuildUi.Label(T.DaevHint, 12, C.Mute);
        hint.AddCssClass("k-build-italic");
        hint.Xalign = 0.5f;
        hint.Justify = Gtk.Justification.Center;
        UiKit.Margin(hint, 12, 10, 12, 6);
        main.Append(hint);
        // Avalonia centres these in a WrapPanel; a wrapping GTK label has a 1-char natural width, so they go one per line from the left
        var checks = UiKit.VBox();
        UiKit.Margin(checks, 12, 0, 12, 10);
        var showOpenBox = UiKit.Check(T.DaevShowOpen, _showOpen);
        showOpenBox.OnToggled += (b, _) => { _showOpen = b.Active; SaveFlag(s => s.DaevShowOpen = _showOpen); UpdateTiles(); };
        var skillIconsBox = UiKit.Check(T.DaevSkillIcons, _skillIcons);
        skillIconsBox.OnToggled += (b, _) => { _skillIcons = b.Active; SaveFlag(s => s.DaevSkillIcons = _skillIcons); UpdateTiles(); };
        _missing = BuildUi.Label("", 13, C.Amber);
        _missing.MarginTop = UiKit.Px(2);
        checks.Append(showOpenBox); checks.Append(skillIconsBox); checks.Append(_missing);
        main.Append(checks);
        var selectAll = BuildUi.Flat(T.DaevSelectAll, SelectAll, true, T.DaevSelectAllTip);
        selectAll.AddCssClass("k-build-selectall");
        var reset = BuildUi.Flat(T.DaevReset, Reset, true, T.DaevResetTip);
        selectAll.Hexpand = reset.Hexpand = true;
        var buttons = UiKit.HBox(6);
        buttons.Homogeneous = true;
        buttons.Append(selectAll); buttons.Append(reset);
        UiKit.Margin(buttons, 12, 0, 12, 12);
        main.Append(buttons);
        _legend = BuildUi.Label("", 11, C.Mute);
        _legend.UseMarkup = true;
        UiKit.Margin(_legend, 12, 0, 12, 10);
        main.Append(_legend);
        _boardsCard = BuildUi.Card(main);
        _boardsCard.Valign = Gtk.Align.Start;

        var side = UiKit.VBox();
        _statsPts = BuildUi.Tight("", 12, C.Mute);
        side.Append(BuildUi.Band(BuildUi.BandTitle(T.DaevStats), _statsPts));
        _statsHead = StatHead();
        _stats.MarginBottom = UiKit.Px(6);
        side.Append(_stats);
        _statsCard = BuildUi.Card(side);
        _statsCard.Valign = Gtk.Align.Start;

        // a zero-height strip over both cards reports the page width: stats beside the boards from 760 px (the WPF 250 px column)
        var probe = Gtk.DrawingArea.New();
        probe.Hexpand = true;
        probe.OnResize += (_, e) =>
        {
            var wide = e.Width >= UiKit.Px(760);
            if (wide != _wide) { _wide = wide; UiLoop.Post(Layout); }
        };
        _root.Append(probe);
        _root.Append(_narrow);
        _root.Append(_wideRow);
        Layout();
    }

    private void Layout()
    {
        if (_boardsCard.GetParent() is Gtk.Box p1) p1.Remove(_boardsCard);
        if (_statsCard.GetParent() is Gtk.Box p2) p2.Remove(_statsCard);
        var host = _wide ? _wideRow : _narrow;
        _boardsCard.Hexpand = true;
        _statsCard.Hexpand = !_wide;
        _statsCard.SetSizeRequest(_wide ? UiKit.Px(250) : -1, -1);
        host.Append(_boardsCard);
        host.Append(_statsCard);
        _narrow.Visible = !_wide;
        _wideRow.Visible = _wide;
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
        _boardFrame.Visible = _class.Boards.Length > 0;
        Note(_class.Boards.Length == 0 ? T.DaevNoClass : _build is null ? T.DaevNoBuild : "");
        RenderBoard();
        RenderCounts();
    }

    private List<int>? RawPlan(int board) => _build?.Daevanion.GetValueOrDefault(board);

    private void RenderTitle()
    {
        var who = _profile is { } p ? string.Format(T.DaevTitle, p.Ref.Name) : T.DaevTitleNoChar;
        _title.SetText(_planName.Length > 0 ? who + " · " + string.Format(T.DaevPlanFrom, _planName) : who);
        _title.SetTooltipText(_title.GetText());
    }

    /// <summary>The chosen board's cells (col, row → node index) and its size; the tiles are drawn from <see cref="_looks"/>.</summary>
    private void RenderBoard()
    {
        _cellIndex.Clear();
        _shownBoard = _class.Board(_boardId);
        if (_shownBoard is { } b)
        {
            if (_looks.Length < b.Nodes.Length) _looks = new NodeLook[b.Nodes.Length];
            for (var i = 0; i < b.Nodes.Length; i++) _cellIndex[(b.Nodes[i].Col - b.MinCol) * 100 + (b.Nodes[i].Row - b.MinRow)] = i;
        }
        SizeBoard(_board.GetWidth());
        UpdateTiles();
    }

    private (double W, double H) BoardSize(DaevBoard b) => BuildGeometry.Board(b.MinCol, b.MaxCol, b.MinRow, b.MaxRow);

    /// <summary>Scale that fits the board into the frame (never above 1) and the left offset that centres it, both in unscaled px.</summary>
    private (double K, double X) Fit(int width, DaevBoard b)
    {
        var (bw, _) = BoardSize(b);
        var avail = width > 0 ? width / Icons.Scale : bw;
        var k = Math.Min(1, avail / bw);
        return (k, Math.Max(0, (avail - bw * k) / 2));
    }

    private void SizeBoard(int width)
    {
        if (_shownBoard is not { } b) { if (_board.ContentHeight != 0) _board.ContentHeight = 0; return; }
        var (k, _) = Fit(width > 0 ? width : UiKit.Px(400), b);
        var px = UiKit.Px(BoardSize(b).H * k);
        if (_board.ContentHeight != px) _board.ContentHeight = px;
    }

    private void UpdateTiles()
    {
        _gen++;
        if (_shownBoard is not { } b) { _board.QueueDraw(); return; }
        var plan = new HashSet<int>(DaevanionPlan.Effective(b, RawPlan(b.Id), _open));
        var hasPlan = RawPlan(b.Id) is { Count: > 0 };   // without a plan nothing is "off plan"
        for (var i = 0; i < b.Nodes.Length; i++)
        {
            var n = b.Nodes[i];
            var open = _open.Contains(n.Id);
            var planned = plan.Contains(n.Id);
            ref var l = ref _looks[i];
            l.Open = open; l.Planned = planned;
            l.OffPlan = hasPlan && open && !planned && !n.Start;
            l.Dim = open && !_showOpen && !n.Start;
            l.Sprite = Sprite(n, open);
            l.Icon = _skillIcons && n.IsSkill ? SkillIcon(n) : null;
        }
        _board.QueueDraw();
    }

    private void DrawBoard(Cairo.Context cr, int width)
    {
        if (_shownBoard is not { } b) return;
        var (k, ox) = Fit(width, b);
        var s = Icons.Scale;
        cr.Scale(s, s);
        cr.Translate(ox, 0);
        cr.Scale(k, k);
        for (var i = 0; i < b.Nodes.Length; i++)
        {
            var n = b.Nodes[i];
            ref var l = ref _looks[i];
            NodeTilePainter.Draw(cr, n, l.Open, l.Planned, l.OffPlan, l.Dim, l.Sprite, l.Icon, (n.Col - b.MinCol) * BuildGeometry.NodeCell, (n.Row - b.MinRow) * BuildGeometry.NodeCell);
        }
    }

    private int NodeAt(double x, double y)
    {
        if (_shownBoard is not { } b) return -1;
        var (k, ox) = Fit(_board.GetWidth(), b);
        var s = Icons.Scale;
        if (!BuildGeometry.HitNode((x / s - ox) / k, y / s / k, out var col, out var row)) return -1;
        return _cellIndex.TryGetValue(col * 100 + row, out var i) ? i : -1;
    }

    private Gdk.Texture? Sprite(DaevNode n, bool open)
    {
        if (n.Sprite.Length == 0) return null;
        var disabled = !open && !n.Start;   // the CDN has no Start_Disabled
        return _shell.Icons.Url(disabled ? "daev:" + n.Sprite + "_Disabled" : "daev:" + n.Sprite, DaevanionBook.SpriteUrl(n.Sprite, disabled), 32);
    }

    private Gdk.Texture? SkillIcon(DaevNode n) =>
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
        SyncBoardButtons();
        var planAll = new List<int>(400);
        var nowAll = new List<int>(256);
        DaevBoard? cur = null;
        int curOpen = 0, curPlan = 0, curMissing = 0, curPlanOpen = 0, curOff = 0;
        for (var bi = 0; bi < _class.Boards.Length; bi++)
        {
            var b = _class.Boards[bi];
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
            var (_, button, name, line) = _boardButtons[bi];
            name.SetText(b.Name(_lang));
            line.SetText(string.Format(T.DaevBoardLine, open, b.Total, plan.Count));
            if (b.Id == _boardId) button.AddCssClass("k-build-on"); else button.RemoveCssClass("k-build-on");
        }
        var planCost = DaevanionPlan.Cost(_class, planAll);
        var nowCost = DaevanionPlan.Cost(_class, nowAll);
        _points.SetText(string.Format(T.DaevPoints, planCost));
        _statsPts.SetText(string.Format(T.DaevStatsPts, planCost, nowCost));
        _missing.SetText(cur is not null && _build is not null && _profile is not null ? string.Format(T.DaevMissing, cur.Name(_lang), curMissing, curPlan) : "");
        _missing.Visible = _missing.GetText().Length > 0;
        Span<(uint, string)> legend =
        [
            (0xD3AD0F, string.Format(T.DaevLegendOpen, _profile?.Ref.Name ?? "—", curOpen)),
            (BuildColors.Green, string.Format(T.DaevLegendPlan, curPlan, Math.Max(0, curPlan - curPlanOpen))),
            (BuildColors.Bad, string.Format(T.DaevLegendOffPlan, Math.Max(0, curOff))),
            (0x333333, T.DaevLegendOff),
        ];
        _legend.SetMarkup(BuildMarkup.Legend(legend, null));
        RenderStats(DaevStats.Rows(DaevanionPlan.Summarize(_class, planAll, _lang), DaevanionPlan.Summarize(_class, nowAll, _lang)));
    }

    /// <summary>One button per board of the class, three per line (five do not fit the card's width in one), made once per class.</summary>
    private void SyncBoardButtons()
    {
        var boards = _class.Boards;
        var same = _boardButtons.Count == boards.Length;
        for (var i = 0; same && i < boards.Length; i++) same = _boardButtons[i].Id == boards[i].Id;
        if (same) return;
        foreach (var (_, b, _, _) in _boardButtons) _boardRow.Remove(b);
        _boardButtons.Clear();
        for (var i = 0; i < boards.Length; i++)
        {
            var id = boards[i].Id;
            var box = UiKit.VBox();
            var name = BuildUi.Tight("", 13);
            name.Ellipsize = Pango.EllipsizeMode.End;
            name.MaxWidthChars = 12;
            var line = BuildUi.Tight("", 11, C.Mute);
            line.Ellipsize = Pango.EllipsizeMode.End;
            line.MaxWidthChars = 16;
            box.Append(name); box.Append(line);
            var b = Gtk.Button.New();
            b.AddCssClass("k-build-bd");
            b.Child = box;
            b.FocusOnClick = false;
            b.SetCursorFromName("pointer");
            b.OnClicked += (_, _) =>
            {
                if (_boardId == id) return;
                _boardId = id;
                RenderBoard();
                RenderCounts();
            };
            _boardRow.Attach(b, i % 3, i / 3, 1, 1);
            _boardButtons.Add((id, b, name, line));
        }
    }

    private static Gtk.Box StatGrid(Gtk.Label name, Gtk.Label plan, Gtk.Label now)
    {
        var row = UiKit.HBox();
        name.Hexpand = true;
        row.Append(name);
        plan.Xalign = 1; plan.SetSizeRequest(UiKit.Px(54), -1);
        now.Xalign = 1; now.SetSizeRequest(UiKit.Px(48), -1);
        row.Append(plan); row.Append(now);
        return row;
    }

    private static Gtk.Box StatHead()
    {
        var b = StatGrid(BuildUi.Label("", 11, C.Text, wrap: false), BuildUi.Tight(T.DaevColPlan, 11, C.Mute), BuildUi.Tight(T.DaevColNow, 11, C.Mute));
        b.AddCssClass("k-build-stathead");
        return b;
    }

    /// <summary>Mock .stats: group label, then one #141414 row per effect with the plan's and today's sums (skills in purple). Rows and group
    /// labels are pooled: a plan edit sets texts and re-appends, it does not rebuild ~120 widgets per click.</summary>
    private void RenderStats(List<DaevStats.Line> lines)
    {
        while (_stats.GetFirstChild() is { } c) _stats.Remove(c);
        _stats.Append(_statsHead);
        int groups = 0, rows = 0;
        foreach (var ln in lines)
        {
            if (ln.IsGroup)
            {
                if (groups == _groupPool.Count)
                {
                    var g = BuildUi.Label("", 13, C.Dim, wrap: false);
                    UiKit.Margin(g, 12, 8, 12, 2);
                    _groupPool.Add(g);
                }
                var gl = _groupPool[groups++];
                gl.SetText(GroupName(ln.Group));
                _stats.Append(gl);
                continue;
            }
            if (rows == _rowPool.Count)
            {
                var name = BuildUi.Label("", 12.5, C.Text, wrap: false);
                var plan = BuildUi.Tight("", 12.5);
                var now = BuildUi.Tight("", 12.5);
                var r = StatGrid(name, plan, now);
                r.AddCssClass("k-build-statrow");
                _rowPool.Add((r, name, plan, now));
            }
            var (row, nl, pl, nw) = _rowPool[rows++];
            nl.SetText(ln.Name);
            nl.SetTooltipText(ln.Name.Length > 22 ? ln.Name : null);
            pl.SetText(ln.Plan);
            nw.SetText(ln.Now);
            var skill = ln.Group == DaevGroup.Skill;
            BuildUi.Fg(pl, skill ? C.Purple : C.Text);
            BuildUi.Fg(nw, skill ? C.Purple : C.Dim);
            _stats.Append(row);
        }
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
        _note.SetText(text);
        _note.Visible = text.Length > 0;
    }

    private Gtk.Widget TipContent(int i)
    {
        var n = _shownBoard!.Nodes[i];
        var l = _looks[i];
        var p = UiKit.VBox();
        p.Append(BuildUi.TipLabel(n.Name(_lang), 13, C.Text, 600));
        foreach (var e in n.Effects(_lang))
        {
            var el = BuildUi.TipLabel(e, 12, C.Dim);
            el.MarginTop = UiKit.Px(3);
            p.Append(el);
        }
        if (!n.Start)
        {
            var cost = BuildUi.TipLabel(string.Format(T.DaevCost, n.Grade, n.Cost), 11, C.Mute);
            cost.MarginTop = UiKit.Px(6);
            p.Append(cost);
        }
        var (state, c) = l.Open ? (T.DaevStateOpen, C.Green) : l.Planned ? (T.DaevStatePlanned, C.Green) : l.OffPlan ? (T.DaevStateOpen, C.Bad) : (T.DaevStateNot, C.Mute);
        var st = BuildUi.TipLabel(state, 11.5, c);
        st.MarginTop = UiKit.Px(4);
        p.Append(st);
        return p;
    }

    // ------------------------------------------------------------------ edits

    private void NodePressed(double x, double y)
    {
        var i = NodeAt(x, y);
        if (i < 0 || _shownBoard is not { } b) return;
        if (_build is null) { Note(T.DaevNoBuild); return; }
        var next = DaevanionPlan.Toggle(b, RawPlan(b.Id), b.Nodes[i].Id, _open, out var r);
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
        _importRow.Visible = !_importRow.Visible;
        if (_importRow.Visible) _importBox.GrabFocus();
    }

    private void ImportFromBox() => _ = ImportPlanFromUrlAsync(_importBox.GetText());

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
        _importRow.Visible = false;
        Note(string.Format(T.DaevImported, plan.Name, count.ToString(CultureInfo.InvariantCulture), next.Count));
        Log.Info($"Daevanion import: \"{plan.Name}\" {count} nodes on {next.Count} boards into build {buildId}");
    }
}

/// <summary>The Daevanion stats panel as lines (display-free: tested): a group header when the group changes, then each planned effect with
/// today's sum of the same key ("—" when none is open).</summary>
internal static class DaevStats
{
    public readonly record struct Line(bool IsGroup, DaevGroup Group, string Name, string Plan, string Now);

    public static List<Line> Rows(List<EffectSum> plan, List<EffectSum> now)
    {
        var nowBy = new Dictionary<string, EffectSum>(now.Count);
        foreach (var e in now) nowBy[e.Key] = e;
        var lines = new List<Line>(plan.Count + 8);
        DaevGroup? group = null;
        foreach (var e in plan)
        {
            if (e.Group != group) { group = e.Group; lines.Add(new Line(true, e.Group, "", "", "")); }
            lines.Add(new Line(false, e.Group, e.Name, e.Amount, nowBy.TryGetValue(e.Key, out var n) ? n.Amount : "—"));
        }
        return lines;
    }
}
