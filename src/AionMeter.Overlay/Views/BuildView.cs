using System.Globalization;
using System.Text;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Overlay.Shell;
using AionMeter.Service;
using C = AionMeter.Overlay.Views.BuildColors.C;

namespace AionMeter.Overlay.Views;

/// <summary>
/// Build tab on the layer-shell card (A2M-50, port of the Avalonia BuildView = WPF Views/BuildView, A2M-21; behaviour unchanged): the
/// character's skill build in questlog's planner look — class skill library, 12×4 key bar, detail panel with level, specs, notes and relations,
/// priority chains, macros, description — against the profile the daemon captured (current level badge per icon, Missing summary), and the
/// Daevanion sub-tab (<see cref="DaevanionPage"/>). Builds are the UI's own files (characters/&lt;key&gt;/builds, same format as WPF), saved
/// 500 ms after the last edit. The character comes from a selector (default: the one the daemon saw logged in, state/summary.json) and never
/// moves the viewer engine's selection (that would rebind and re-save the live stores over the daemon's files: <see cref="CharacterFolder"/>).
/// Tiles are cairo models in a few DrawingAreas (library rows, bar, priority chains), changed and redrawn in place; the bar shrinks to the
/// card's width like the Avalonia Viewbox.
/// </summary>
internal sealed class BuildView : HostedView
{
    private static UiText T => UiText.Current;
    /// <summary>The level slider spans 1..20 as questlog's planner does; further only for a build level above 20, never past the catalogue max.</summary>
    public const int SliderMax = 20;
    private static readonly Icon ChevronUp = new("M3 10 L8 5 L13 10");

    private sealed class SkillVm(SkillInfo info)
    {
        public SkillInfo Info { get; } = info;
        public BuildSkill? B;
        public SkillProgress? P;
        public Gdk.Texture? Icon;
        public bool IconAsked;
        public string Name = "";
    }

    private enum EquipMode { Add, Equip, Unequip, Remove }
    private sealed record QuestlogCheck(string BuildId, BuildSummary? Skill, QuestlogDaevPlan? Plan, string? PlanSlug, string? Note);

    private BuildStore _store = CharacterFolder.Builds(AppSettings.AppDataDir, "");
    private string _charKey = "";
    private DateTime _profileStamp;
    private Build? _build;
    private CharacterProfile? _profile;
    private string _classId = "", _vmClass = "", _lang;
    private bool _dirty, _syncing, _showDone, _descOpen, _importing, _checking, _daevTab;
    private string _suggestFor = "";
    private QuestlogCheck? _check;
    private int _selected, _key;
    private EquipMode _equipMode;
    private SkillRelationCache? _relCache;
    private readonly Dictionary<int, SkillVm> _vms = new();
    private readonly Dictionary<int, SkillProgress> _prog = new();
    // tile models reused across renders (one per drawn tile); prio tiles never show the selection
    private readonly List<TileModel> _libPool = new(), _barPool = new(), _prioPool = new();
    private int _libUsed, _barUsed, _prioUsed;
    private readonly List<(Gtk.Image Img, SkillSpec Spec)> _specIcons = new();
    private readonly Dictionary<(int Id, int Level, string Lang), TextSeg[]> _descCache = new();
    private (string? Html, string Text) _descHtml = (null, "");
    private readonly List<int> _macroSteps = new();
    private readonly UiTimer _saveTick = new(TimeSpan.FromMilliseconds(500));
    // icons land one per 400 ms: refresh at most every 600 ms instead of per icon (as WPF)
    private readonly UiTimer _iconTick = new(TimeSpan.FromMilliseconds(600));
    // the daemon re-writes profile.json / character.json at a login or a sync: re-read when their stamp moves
    private readonly UiTimer _fileTick = new(TimeSpan.FromSeconds(3));

    // chrome
    private readonly Gtk.Button _subSkills, _subDaev, _copyQuestlog, _deleteBuild;
    private readonly Gtk.DropDown _charBox, _langBox, _buildBox;
    private readonly Gtk.Label _status;
    private readonly Gtk.Box _pageSkills;
    private readonly DaevanionPage _pageDaev;
    private readonly List<string> _charKeys = new();
    private readonly List<string> _buildIds = new();
    // suggestions
    private readonly Gtk.Box _suggestCard;
    private readonly Gtk.Label _suggestTitle;
    private readonly Gtk.Box _suggestList = UiKit.VBox();
    // build card
    private readonly Gtk.Box _buildCards = UiKit.VBox();
    private readonly Gtk.Label _sourceLine, _missing, _noClass, _stigmaCount, _target;
    private readonly Gtk.CheckButton _showDoneBox;
    private readonly TileWrap _rowActive = new(6), _rowStigma = new(6), _rowPassive = new(6);
    // bar + detail
    private readonly KeyBar _bar = new();
    private readonly Gtk.Label _detailHint, _detailName, _detailSub, _detailDesc, _levelValue, _specCount, _noteCount;
    private readonly Gtk.Box _detail = UiKit.VBox();
    private readonly SkillTile _detailTile = new(48);
    private readonly Gtk.Button _equip, _remove;
    private readonly Gtk.Scale _level;
    private readonly Gtk.Box _ticks = UiKit.HBox(), _levelBlock;
    private readonly Gtk.Box _specList = UiKit.VBox(), _noteBlock = UiKit.VBox(), _relBlock = UiKit.VBox(), _relList = UiKit.VBox();
    private readonly Gtk.TextView _note;
    // priority, macros, description, footer
    private readonly TileWrap _prioActive = new(4), _prioStigma = new(4), _prioPassive = new(4);
    private readonly Gtk.Button _clearPrio;
    private readonly Gtk.Box _macroList = UiKit.VBox(), _macroEditor;
    private readonly Gtk.Entry _macroName;
    private readonly Gtk.Label _macroStepsText, _descText, _saved;
    private readonly IconView _descDown, _descUp;

    public BuildView(AppShell shell) : base(shell, "build", UiText.Current.BuildTitle)
    {
        BuildColors.EnsureCss();
        var s = shell.Settings;
        _lang = s.SkillLanguage == "es" ? "es" : "en";
        _showDone = s.BuildShowDone;

        // --- sub-tabs | skill language; character + build; actions (Avalonia's WrapPanels as fixed lines that fit the card's ~490 px)
        _subSkills = BuildUi.SubTab(T.BuildSubSkills, () => SetSub(false));
        _subDaev = BuildUi.SubTab(T.BuildSubDaevanion, () => SetSub(true));
        _langBox = BuildUi.Combo(["Skills: English", "Skills: Español"], _lang == "es" ? 1 : 0, 140, 16);
        _langBox.SetTooltipText(T.BuildSkillLangTip);
        UiKit.OnSelected(_langBox, () => { if (!_syncing) LangChanged(UiKit.Selected(_langBox) == 1 ? "es" : "en"); });
        var top = UiKit.HBox();
        UiKit.Margin(top, 14, 0, 14, 0);
        top.Append(_subSkills); top.Append(_subDaev); top.Append(UiKit.Spacer()); top.Append(_langBox);

        _charBox = BuildUi.Combo([], -1, 150, 20);
        _charBox.MarginEnd = UiKit.Px(6);
        UiKit.OnSelected(_charBox, () => { if (!_syncing && UiKit.Selected(_charBox) is >= 0 and var i && i < _charKeys.Count) SwitchCharacter(_charKeys[i]); });
        _buildBox = BuildUi.Combo([], -1, 200, 30);
        _buildBox.Hexpand = true;
        _buildBox.SetTooltipText(T.BuildPickTip);
        UiKit.OnSelected(_buildBox, BuildBoxChanged);
        var pickRow = UiKit.HBox();
        UiKit.Margin(pickRow, 14, 8, 14, 0);
        pickRow.Append(_charBox); pickRow.Append(_buildBox);

        var actions = UiKit.HBox(6);
        UiKit.Margin(actions, 14, 6, 14, 0);
        actions.Append(BuildUi.Flat(T.BuildNewFromProfile, NewFromProfile, tip: T.BuildNewFromProfileTip));
        actions.Append(BuildUi.Flat(T.BuildImport, OpenImport, tip: T.BuildImportTip));
        actions.Append(_copyQuestlog = BuildUi.Flat(T.BuildCopyQuestlog, CopyQuestlog, tip: T.BuildCopyQuestlogTip));
        _deleteBuild = UiKit.IconButton(Icons.Close, T.BuildDeleteTip, DeleteBuild);
        actions.Append(_deleteBuild);
        _status = BuildUi.Label("", 12, C.Mute);
        UiKit.Margin(_status, 14, 4, 14, 0);
        _status.Visible = false;

        // --- suggestions (no active build): the class's most-liked questlog builds instead of an empty planner
        _suggestTitle = BuildUi.BandTitle("");
        var suggest = UiKit.VBox();
        suggest.Append(BuildUi.Band(_suggestTitle));
        var sugHint = BuildUi.Label(T.BuildSuggestHint, 12.5, C.Dim);
        UiKit.Margin(sugHint, 12, 10, 12, 10);
        suggest.Append(sugHint);
        UiKit.Margin(_suggestList, 12, 0, 12, 4);
        suggest.Append(_suggestList);
        var sugButtons = UiKit.HBox(6);
        UiKit.Margin(sugButtons, 12, 6, 12, 12);
        sugButtons.Append(BuildUi.Flat(T.BuildSearch, OpenImport));
        sugButtons.Append(BuildUi.Flat(T.BuildNewFromProfile, NewFromProfile, tip: T.BuildNewFromProfileTip));
        suggest.Append(sugButtons);
        _suggestCard = BuildUi.Card(suggest);
        _suggestCard.Visible = false;

        // --- build card: library rows against the profile
        var card1 = UiKit.VBox();
        var band1 = UiKit.VBox();
        band1.AddCssClass("k-build-band");
        band1.Append(BuildUi.BandTitle(T.BuildCard));
        _sourceLine = BuildUi.Links(12, C.Mute, SourceLink);
        _sourceLine.MarginTop = UiKit.Px(2);
        band1.Append(_sourceLine);
        card1.Append(band1);
        var doneRow = UiKit.VBox();
        UiKit.Margin(doneRow, 12, 10, 12, 0);
        _showDoneBox = UiKit.Check(T.BuildShowDone, _showDone);
        _showDoneBox.OnToggled += (b, _) => { if (!_syncing) ShowDoneChanged(b.Active); };
        _missing = BuildUi.Label("", 12.5, C.Amber);
        doneRow.Append(_showDoneBox); doneRow.Append(_missing);
        card1.Append(doneRow);
        _target = BuildUi.Links(11.5, C.Mute, SourceLink);
        _target.Xalign = 1;
        _target.Justify = Gtk.Justification.Right;
        UiKit.Margin(_target, 12, 4, 12, 0);
        card1.Append(_target);
        _noClass = BuildUi.Label(T.BuildNoClass, 12.5, C.Mute);
        UiKit.Margin(_noClass, 12, 12, 12, 4);
        _noClass.Visible = false;
        card1.Append(_noClass);
        var lblActive = BuildUi.Lbl(T.BuildActive.ToUpper(T.Culture));
        UiKit.Margin(lblActive, 12, 10, 12, 6);
        card1.Append(lblActive);
        UiKit.Margin(_rowActive.Area, 9, 0, 3, 1);
        card1.Append(_rowActive.Area);
        _stigmaCount = BuildUi.Tight("", 11, C.Purple);
        var count = UiKit.HBox();
        count.AddCssClass("k-build-count");
        count.Valign = Gtk.Align.Center;
        count.Append(_stigmaCount);
        var stigmaHead = UiKit.HBox();
        UiKit.Margin(stigmaHead, 12, 6, 12, 6);
        stigmaHead.Append(BuildUi.Lbl(T.BuildStigma.ToUpper(T.Culture)));
        stigmaHead.Append(count);
        card1.Append(stigmaHead);
        UiKit.Margin(_rowStigma.Area, 9, 0, 3, 1);
        card1.Append(_rowStigma.Area);
        var lblPassive = BuildUi.Lbl(T.BuildPassive.ToUpper(T.Culture));
        UiKit.Margin(lblPassive, 12, 6, 12, 6);
        card1.Append(lblPassive);
        UiKit.Margin(_rowPassive.Area, 9, 0, 3, 1);
        card1.Append(_rowPassive.Area);
        var legend = BuildUi.Label("", 11, C.Mute);
        Span<(uint, string)> legendItems = [(BuildColors.Green, T.BuildLegendOk), (BuildColors.Amber, T.BuildLegendNear), (BuildColors.Bad, T.BuildLegendFar)];
        legend.SetMarkup(BuildMarkup.Legend(legendItems, T.BuildLegendOff));
        UiKit.Margin(legend, 12, 2, 12, 10);
        card1.Append(legend);
        _buildCards.Append(BuildUi.Card(card1));
        foreach (var row in (TileWrap[])[_rowActive, _rowStigma, _rowPassive])
        {
            var w = row;
            w.Pressed = (i, n, button, _, _) => LibPressed(w, i, n, button);
            w.Tip = i => TipFor(w.TileAt(i));
        }

        // --- key bar + detail
        var card2 = UiKit.VBox();
        UiKit.Margin(_bar.Area, 10, 8, 10, 6);
        _bar.CellPressed = BarPressed;
        _bar.KeyPressed = n => { _key = _key == n ? 0 : n; RenderKeys(); };
        _bar.Tip = TipFor;
        card2.Append(_bar.Area);
        var detailHost = UiKit.VBox();
        UiKit.Margin(detailHost, 12, 12, 12, 12);
        _detailHint = BuildUi.Label(T.BuildPickSkill, 12.5, C.Mute);
        detailHost.Append(_detailHint);
        detailHost.Append(_detail);
        _detail.Visible = false;
        _detailName = BuildUi.Label("", 16, C.Text, 600);
        _detailSub = BuildUi.Label("", 12, C.Blue, wrap: false);
        var names = UiKit.VBox();
        names.Hexpand = true;
        names.Valign = Gtk.Align.Center;
        names.MarginStart = UiKit.Px(10);
        names.Append(_detailName); names.Append(_detailSub);
        var closeDetail = UiKit.IconButton(Icons.Close, T.TipClose, () => { _selected = 0; RefreshSelection(); });
        closeDetail.Valign = Gtk.Align.Start;
        var head = UiKit.HBox();
        head.Append(_detailTile.Area); head.Append(names); head.Append(closeDetail);
        _detail.Append(head);
        _equip = BuildUi.Flat("", EquipClick, true);
        _equip.Hexpand = true;
        if (_equip.GetChild() is Gtk.Label el) el.Xalign = 0.5f;
        _remove = BuildUi.Flat(T.BuildRemove, () => { if (_vms.TryGetValue(_selected, out var vm) && NeedBuild()) Edit(BuildEditor.RemoveSkill(_build!, vm.Info.Id)); }, true);
        _remove.MarginStart = UiKit.Px(6);
        var equipRow = UiKit.HBox();
        UiKit.Margin(equipRow, 0, 12, 0, 12);
        equipRow.Append(_equip); equipRow.Append(_remove);
        _detail.Append(equipRow);
        _detailDesc = BuildUi.Label("", 12, C.Dim);
        _detail.Append(_detailDesc);
        _levelValue = BuildUi.Tight("", 13, C.Text, 700);
        _levelBlock = UiKit.HBox();
        UiKit.Margin(_levelBlock, 0, 12, 0, 4);
        _levelBlock.Append(BuildUi.Label(T.BuildLevel, 13, C.Text, wrap: false));
        _levelBlock.Append(_levelValue);
        _detail.Append(_levelBlock);
        _level = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, 1, SliderMax, 1);
        _level.DrawValue = false;
        _level.Hexpand = true;
        UiKit.Margin(_level, 4, 0, 4, 0);
        // snap like IsSnapToTickEnabled (GTK only rounds to `digits`); the range's upper bound moves with the skill
        _level.OnChangeValue += (r, e) =>
        {
            var adj = r.GetAdjustment();
            var v = Math.Clamp(Math.Round(e.Value), adj.GetLower(), adj.GetUpper());
            if (Math.Abs(r.GetValue() - v) > 1e-9) r.SetValue(v);
            return true;
        };
        _level.OnValueChanged += (r, _) => LevelChanged(r.GetValue());
        _detail.Append(_level);
        _ticks.Homogeneous = true;
        UiKit.Margin(_ticks, 4, 4, 4, 12);
        _detail.Append(_ticks);
        _specCount = BuildUi.Tight("", 13, C.Mute);
        var specHead = UiKit.HBox();
        specHead.MarginBottom = UiKit.Px(4);
        specHead.Append(BuildUi.Label(T.BuildSpecs, 13, C.Text, wrap: false));
        specHead.Append(_specCount);
        _detail.Append(specHead);
        _detail.Append(_specList);
        _noteCount = BuildUi.Tight("", 11, C.Mute);
        _noteCount.Valign = Gtk.Align.End;
        var noteHead = UiKit.HBox();
        UiKit.Margin(noteHead, 0, 12, 0, 4);
        noteHead.Append(BuildUi.Label(T.BuildTipNotes, 13, C.Text, wrap: false));
        noteHead.Append(_noteCount);
        _noteBlock.Append(noteHead);
        _note = Gtk.TextView.New();
        _note.WrapMode = Gtk.WrapMode.WordChar;
        _note.AddCssClass("k-build-note");
        _note.AcceptsTab = false;
        _note.GetBuffer().OnChanged += (_, _) => NoteChanged();
        var noteScroll = Gtk.ScrolledWindow.New();
        noteScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        noteScroll.Child = _note;
        noteScroll.MinContentHeight = UiKit.Px(62);
        noteScroll.MaxContentHeight = UiKit.Px(160);
        noteScroll.PropagateNaturalHeight = true;
        noteScroll.SetTooltipText(T.BuildNoteHint);
        _noteBlock.Append(noteScroll);
        _detail.Append(_noteBlock);
        _relBlock.MarginTop = UiKit.Px(12);
        var relTitle = BuildUi.Label(T.BuildRelations, 13, C.Text);
        relTitle.MarginBottom = UiKit.Px(4);
        _relBlock.Append(relTitle);
        _relBlock.Append(_relList);
        _detail.Append(_relBlock);
        card2.Append(detailHost);
        _buildCards.Append(BuildUi.Card(card2));

        // --- priority
        var card3 = UiKit.VBox();
        _clearPrio = BuildUi.TextBtn("✕ " + T.BuildClear, ClearPriority);
        card3.Append(BuildUi.Band(BuildUi.BandTitle(T.BuildPriority), _clearPrio));
        card3.Append(PrioRow(T.BuildActive, _prioActive, "active"));
        card3.Append(BuildUi.Sep());
        card3.Append(PrioRow(T.BuildStigma, _prioStigma, "stigma"));
        card3.Append(BuildUi.Sep());
        card3.Append(PrioRow(T.BuildPassive, _prioPassive, "passive"));
        _buildCards.Append(BuildUi.Card(card3));

        // --- macros (the editor's row is two lines on the card: name + steps, then the buttons)
        var card4 = UiKit.VBox();
        card4.Append(BuildUi.Band(BuildUi.BandTitle(T.BuildMacros)));
        UiKit.Margin(_macroList, 12, 10, 12, 6);
        card4.Append(_macroList);
        _macroName = UiKit.Input("", 150);
        _macroName.AddCssClass("k-build-field");
        _macroName.PlaceholderText = T.BuildMacroName;
        _macroStepsText = BuildUi.Label("", 12.5, C.Dim, wrap: false);
        _macroStepsText.MarginStart = UiKit.Px(10);
        var editLine = UiKit.HBox();
        editLine.Append(_macroName); editLine.Append(_macroStepsText);
        var editButtons = UiKit.HBox(6);
        editButtons.MarginTop = UiKit.Px(6);
        Gtk.Button? addStep = null;
        addStep = BuildUi.Flat(T.BuildMacroAddStep, () => MacroAddStep(addStep!));
        editButtons.Append(addStep);
        editButtons.Append(BuildUi.Flat(T.BuildMacroSave, MacroSave));
        var cancel = BuildUi.TextBtn(T.BuildMacroCancel, MacroCancel);
        cancel.MarginStart = UiKit.Px(2);
        editButtons.Append(cancel);
        _macroEditor = UiKit.VBox();
        UiKit.Margin(_macroEditor, 12, 0, 12, 10);
        _macroEditor.Append(editLine); _macroEditor.Append(editButtons);
        _macroEditor.Visible = false;
        card4.Append(_macroEditor);
        _buildCards.Append(BuildUi.Card(card4));

        // --- description (collapsed until the band is clicked)
        var card5 = UiKit.VBox();
        _descDown = Icons.View(Icons.Chevron, BuildColors.Mute, UiKit.Px(11), 1.6);
        _descUp = Icons.View(ChevronUp, BuildColors.Mute, UiKit.Px(11), 1.6);
        _descUp.Widget.Visible = false;
        var chev = UiKit.HBox();
        chev.Append(_descDown.Widget); chev.Append(_descUp.Widget);
        var descBand = BuildUi.Band(BuildUi.BandTitle(T.BuildDescription), chev);
        descBand.SetCursorFromName("pointer");
        var descClick = Gtk.GestureClick.New();
        descClick.OnPressed += (_, _) => { _descOpen = !_descOpen; RenderDescription(); };
        descBand.AddController(descClick);
        card5.Append(descBand);
        _descText = BuildUi.Label("", 12.5, C.Dim);
        UiKit.Margin(_descText, 12, 10, 12, 10);
        _descText.Visible = false;
        card5.Append(_descText);
        _buildCards.Append(BuildUi.Card(card5));

        var footer = UiKit.HBox();
        footer.Halign = Gtk.Align.Center;
        footer.MarginBottom = UiKit.Px(4);
        footer.Append(BuildUi.TextBtn("💾 " + T.BuildSave, FlushSave, "k-build-s13"));
        _saved = BuildUi.Tight("", 13, C.Mute);
        _saved.MarginStart = UiKit.Px(6);
        footer.Append(_saved);
        _buildCards.Append(footer);

        _pageSkills = UiKit.VBox();
        _pageSkills.Append(_suggestCard);
        _pageSkills.Append(_buildCards);
        _pageDaev = new DaevanionPage(shell);
        _pageDaev.Widget.Visible = false;
        // Daevanion edits ride on this view's build and its 500 ms save (the page only hands over the new plan)
        _pageDaev.PlanImported += (plan, slug) =>
        {
            if (_build is null) return;
            _build = _build with { PlanSource = new BuildSource(plan.Id, slug, null), PlanName = plan.Name, PlanSourceUpdatedAt = plan.UpdatedAt };
            MarkDirty();
        };
        _pageDaev.PlanEdited += plan => { if (_build is null) return; _build = _build with { Daevanion = plan }; MarkDirty(); };

        var pages = UiKit.VBox();
        pages.Append(_pageSkills);
        pages.Append(_pageDaev.Widget);
        var scroll = UiKit.Scroll(pages);
        scroll.MarginTop = UiKit.Px(10);
        var body = UiKit.VBox();
        body.Append(top); body.Append(pickRow); body.Append(actions); body.Append(_status); body.Append(scroll);
        SetBody(body);
        SetSub(false);

        Listen(() => Shell.ImportRequested += OnImportRequested, () => Shell.ImportRequested -= OnImportRequested);
        OnDetach(() => { _saveTick.Stop(); _iconTick.Stop(); _fileTick.Stop(); });
        _saveTick.Tick += FlushSave;
        _iconTick.Tick += () => { _iconTick.Stop(); RefreshIcons(); };
        _fileTick.Tick += () => { if (CharacterFolder.ProfileStamp(AppSettings.AppDataDir, _charKey) != _profileStamp) { FlushSave(); ReloadProfile(); Render(); } };
    }

    private void OnImportRequested() { if (IsShown) OpenImport(); }

    private Gtk.Widget PrioRow(string label, TileWrap wrap, string cat)
    {
        var l = BuildUi.Lbl(label.ToUpper(T.Culture));
        l.SetSizeRequest(UiKit.Px(70), -1);
        l.Valign = Gtk.Align.Start;
        l.MarginTop = UiKit.Px(18);
        l.Ellipsize = Pango.EllipsizeMode.End;
        l.MaxWidthChars = 1;
        var g = UiKit.HBox();
        UiKit.Margin(g, 12, 5, 9, 5);
        g.Append(l);
        g.Append(wrap.Area);
        wrap.Pressed = (i, n, button, x, y) => PrioPressed(wrap, cat, i, n, button);
        wrap.Tip = i => TipFor(wrap.TileAt(i));
        return g;
    }

    // ------------------------------------------------------------------ lifecycle

    public override void Shown()
    {
        Shell.Icons.Loaded += OnIcon;
        Shell.WhenEngine(_ => { if (IsShown) { Shell.RefreshSettings(); FillCharacters(); Reload(); } });
        _fileTick.Start();
    }

    public override void Hidden()
    {
        FlushSave();
        Shell.Icons.Loaded -= OnIcon;
        _iconTick.Stop();
        _fileTick.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.BuildTitle;
        if (IsShown) Render();
    }

    private void OnIcon() { if (!_iconTick.IsEnabled) _iconTick.Start(); }

    private void SetSub(bool daev)
    {
        _daevTab = daev;
        BuildUi.SetSubTab(_subSkills, !daev);
        BuildUi.SetSubTab(_subDaev, daev);
        _pageSkills.Visible = !daev;
        _pageDaev.Widget.Visible = daev;
        HelpKey = "build";
    }

    private void SetStatus(string text)
    {
        _status.SetText(text);
        _status.Visible = text.Length > 0;
    }

    // ------------------------------------------------------------------ data

    /// <summary>The character list: every characters/&lt;key&gt; folder; the current pick, else the one the daemon saw logged in, else the first.</summary>
    private void FillCharacters()
    {
        var engine = Shell.Engine;
        var keys = engine?.CharacterKeys() ?? new List<string>();
        _charKeys.Clear();
        _charKeys.AddRange(keys);
        if (_charKey.Length == 0 || !_charKeys.Contains(_charKey))
            _charKey = CharacterFolder.CurrentKey(AppSettings.AppDataDir) is { } cur && _charKeys.Contains(cur) ? cur
                     : Shell.LastOverlay?.Character is { Length: > 0 } live && _charKeys.Contains(live) ? live
                     : _charKeys.Count > 0 ? _charKeys[0] : "";
        _syncing = true;
        try
        {
            var labels = new List<string>(_charKeys.Count);
            foreach (var k in _charKeys)
            {
                var label = k;
                if (CharacterFolder.ParseKey(k) is { } p) label = engine is null ? p.Name : $"{p.Name} · {engine.Data.ServerName(p.Server)}";
                labels.Add(label);
            }
            UiKit.SetItems(_charBox, labels, _charKeys.IndexOf(_charKey));
            _charBox.Visible = _charKeys.Count > 0;
        }
        finally { _syncing = false; }
    }

    private void SwitchCharacter(string key)
    {
        if (key == _charKey) return;
        FlushSave();
        _charKey = key;
        _selected = 0;
        _check = null;
        Reload();
    }

    private void ReloadProfile()
    {
        _profileStamp = CharacterFolder.ProfileStamp(AppSettings.AppDataDir, _charKey);
        _profile = Shell.Engine is { } e && CharacterFolder.ParseKey(_charKey) is { } p ? e.KnownProfile(p.Name, p.Server) : null;
    }

    private void Reload()
    {
        FlushSave();
        _store = CharacterFolder.Builds(AppSettings.AppDataDir, _charKey);
        ReloadProfile();
        var builds = _store.List();
        var active = _store.ActiveId;
        _build = active is null ? null : builds.Find(b => b.Id == active);
        if (_build is null) _selected = 0;
        FillPicker(builds, _build?.Id);
        Render();
    }

    private void FillPicker(List<Build> builds, string? activeId)
    {
        _syncing = true;
        try
        {
            _buildIds.Clear();
            var labels = new List<string>(builds.Count + 1) { T.BuildNone };
            _buildIds.Add("");
            var sel = 0;
            foreach (var b in builds)
            {
                _buildIds.Add(b.Id);
                labels.Add(b.Name.Length > 0 ? b.Name : b.Id);
                if (b.Id == activeId) sel = _buildIds.Count - 1;
            }
            UiKit.SetItems(_buildBox, labels, sel);
            _deleteBuild.Visible = _copyQuestlog.Visible = _build is not null;
        }
        finally { _syncing = false; }
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTick.Restart();
    }

    private void FlushSave()
    {
        _saveTick.Stop();
        if (!_dirty || _build is null) return;
        _dirty = false;
        try
        {
            var saved = _store.Save(_build);
            var isNew = saved.Id != _build.Id;
            _build = saved;
            if (_store.ActiveId != saved.Id) _store.ActiveId = saved.Id;
            if (isNew) FillPicker(_store.List(), saved.Id);
            RenderSaved();
            QueueTelemetry(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Build save", ex);
            SetStatus(ex.Message);
        }
    }

    /// <summary>One edit: the new build is shown at once and saved 500 ms after the last edit. False when the edit was refused (same reference back).</summary>
    private bool Edit(Build next)
    {
        if (_build is null || ReferenceEquals(next, _build)) return false;
        _build = next;
        MarkDirty();
        Render();
        return true;
    }

    private bool NeedBuild()
    {
        if (_build is not null) return true;
        SetStatus(T.BuildNoBuild);
        return false;
    }

    private void QueueTelemetry(Build saved) =>
        Telemetry.QueueBuild(Shell.Settings, saved, BuildEditor.ClassIdOf(_profile?.ClassName) ?? _classId, _profile?.Level ?? 0, _charKey);

    // asked once per skill; a missing icon is asked again only when the provider reports a download (RefreshIcons)
    private Gdk.Texture? IconOf(SkillVm vm)
    {
        if (!vm.IconAsked) { vm.IconAsked = true; vm.Icon = FetchIcon(vm.Info); }
        return vm.Icon;
    }

    private Gdk.Texture? FetchIcon(SkillInfo s) => s.Icon.Length > 0 ? Shell.Icons.Url("skill:" + s.Icon, SkillBook.IconUrl(s.Icon), 48) : null;

    private Gdk.Texture? SpecIcon(SkillSpec sp) => sp.Icon is { Length: > 0 } stem ? Shell.Icons.Url("spec:" + stem, SkillBook.SpecIconUrl(stem), 48) : null;

    /// <summary>Icons that landed since the last draw: swapped into the existing tile models and redrawn, nothing rebuilt.</summary>
    private void RefreshIcons()
    {
        var any = false;
        foreach (var vm in _vms.Values)
            if (vm.Icon is null && vm.IconAsked && (vm.Icon = FetchIcon(vm.Info)) is not null) any = true;
        if (any)
        {
            for (var i = 0; i < _libUsed; i++) SetIcon(_libPool[i]);
            for (var i = 0; i < _barUsed; i++) SetIcon(_barPool[i]);
            for (var i = 0; i < _prioUsed; i++) SetIcon(_prioPool[i]);
            SetIcon(_detailTile.Model);
            RedrawTiles();
            _detailTile.Redraw();
        }
        foreach (var (img, spec) in _specIcons)
            if (img.GetPaintable() is null && SpecIcon(spec) is { } src) { img.SetFromPaintable(src); img.Visible = true; }
        _pageDaev.IconsLanded();
    }

    private void SetIcon(TileModel m) { if (m.Icon is null && _vms.TryGetValue(m.SkillId, out var vm)) m.Icon = vm.Icon; }

    private void RedrawTiles()
    {
        _rowActive.Redraw(); _rowStigma.Redraw(); _rowPassive.Redraw();
        _prioActive.Redraw(); _prioStigma.Redraw(); _prioPassive.Redraw();
        _bar.Redraw();
    }

    /// <summary>Relations for the shown build: recomputed only after an edit (a new Build), a class or a language change.</summary>
    private SkillRelationSet Relations => Shell.Engine is { } e ? (_relCache ??= new SkillRelationCache(e.Skills)).Get(_classId, _build, _lang) : SkillRelationSet.Empty;

    // ------------------------------------------------------------------ render

    private void Render()
    {
        if (Shell.Engine is not { } engine) return;
        _classId = BuildEditor.ClassIdOf(_profile?.ClassName) ?? _build?.ClassId ?? "";
        if (_classId != _vmClass) { _vms.Clear(); _vmClass = _classId; }
        var library = _classId.Length > 0 ? engine.Skills.ForClass(_classId) : [];
        _prog.Clear();
        BuildProgress? progress = null;
        if (_build is not null)
        {
            progress = Progress.Compare(_build, _profile, null);
            foreach (var p in progress.Skills) _prog[p.SkillId] = p;
        }
        foreach (var info in library)
        {
            if (!_vms.TryGetValue(info.Id, out var vm)) _vms[info.Id] = vm = new SkillVm(info);
            vm.B = _build?.Skill(info.Id);
            vm.P = _prog.GetValueOrDefault(info.Id);
            vm.Name = info.Name(_lang);
        }
        if (_selected != 0 && !_vms.ContainsKey(_selected)) _selected = 0;
        _noClass.Visible = library.Count == 0;

        RenderSuggestions();
        RenderHeader(progress);
        RenderLibrary(library);
        RenderBar();
        RenderKeys();
        RenderDetail();
        RenderPriority();
        RenderMacros();
        RenderDescription();
        RenderSaved();
        _pageDaev.Show(_build, _profile, _lang);
    }

    private void RenderHeader(BuildProgress? progress)
    {
        var src = new StringBuilder(128);
        if (_build is not null)
        {
            src.Append(BuildMarkup.Esc(_build.Name));
            if (_build.Source is { Id: > 0, Slug: { Length: > 0 } })
            {
                src.Append("  ·  ");
                BuildMarkup.Link(src, T.BuildOpenSource, "open");
            }
            RenderCheck(src);
        }
        _sourceLine.SetMarkup(src.ToString());
        _sourceLine.Visible = src.Length > 0;
        _missing.SetText(progress is not null && _profile is not null
            ? string.Format(T.BuildMissing, progress.MissingSkills, progress.TotalSkills, progress.MissingLevels, progress.NotLearned) : "");
        _missing.Visible = _missing.GetText().Length > 0;
        var text = new StringBuilder();
        if (_build is not null)
            text.Append(string.Format(T.BuildTarget, _build.Source is { Id: > 0 } bs ? string.Format(T.BuildTargetQuestlog, bs.Id) : _build.Name)).Append("  ·  ");
        text.Append(_profile is not null ? string.Format(T.BuildLearned, _profile.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) : T.BuildNoProfile);
        // the viewer never fetches: "refresh" re-reads the files the daemon writes at a login and every 6 h of play
        var target = new StringBuilder(BuildMarkup.Esc(text.ToString())).Append("   ");
        BuildMarkup.Link(target, T.BuildRefresh, "refresh");
        _target.SetMarkup(target.ToString());
        var stigmas = 0;
        if (_build is not null)
            foreach (var b in _build.Skills)
                if (b.Slot is not null && _vms.TryGetValue(b.SkillId, out var vm) && vm.Info.Sub == "stigma") stigmas++;
        _stigmaCount.SetText(stigmas.ToString(CultureInfo.InvariantCulture) + "/" + BuildEditor.MaxSpecsStigma.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The inline links of the source and target lines.</summary>
    private void SourceLink(string id)
    {
        switch (id)
        {
            case "open":
                if (_build?.Source is { Id: > 0, Slug: { Length: > 0 } slug } s) BuildUi.OpenUrl(QuestlogBuilds.BuildUrl(slug, s.Id, _lang));
                break;
            case "check": _ = CheckQuestlogAsync(); break;
            case "update": QuestlogUpdate(); break;
            case "refresh": FlushSave(); ReloadProfile(); Render(); SetStatus(""); break;
        }
    }

    private TileModel NextModel(List<TileModel> pool, ref int used)
    {
        if (used == pool.Count) pool.Add(new TileModel());
        return pool[used++];
    }

    private void RenderLibrary(IReadOnlyList<SkillInfo> library)
    {
        _rowActive.Clear(); _rowStigma.Clear(); _rowPassive.Clear();
        _libUsed = 0;
        foreach (var info in library)
        {
            var vm = _vms[info.Id];
            if (!_showDone && vm.B is not null && vm.P?.State == SkillState.AtTarget) continue;
            var m = NextModel(_libPool, ref _libUsed);
            ApplyTile(m, vm, vm.Info.Id == _selected);
            (info.Sub == "stigma" ? _rowStigma : info.Sub == "passive" ? _rowPassive : _rowActive).Add(m);
        }
        _rowActive.Changed(); _rowStigma.Changed(); _rowPassive.Changed();
    }

    private void RenderBar()
    {
        _bar.Clear();
        _barUsed = 0;
        if (_build is not null)
            foreach (var b in _build.Skills)
            {
                if (!BuildEditor.TryParseSlot(b.Slot, out var key, out var row) || !_vms.TryGetValue(b.SkillId, out var vm)) continue;
                var m = NextModel(_barPool, ref _barUsed);
                ApplyTile(m, vm, vm.Info.Id == _selected);
                _bar.Put(key, row, m);
            }
        _bar.Redraw();
    }

    private void RenderKeys() => _bar.Key = _key;

    private static TileModel.Look LookOf(SkillVm vm, bool showDone) =>
        vm.B is null ? TileModel.Look.Off : !showDone && vm.P?.State == SkillState.AtTarget ? TileModel.Look.Done : TileModel.Look.Normal;

    private static uint StateRgb(SkillProgress? p) => p?.State switch
    {
        SkillState.AtTarget => BuildColors.Green,
        SkillState.Near => BuildColors.Amber,
        _ => BuildColors.Bad,
    };

    private static C StateC(SkillProgress? p) => p?.State switch { SkillState.AtTarget => C.Green, SkillState.Near => C.Amber, _ => C.Bad };

    private void ApplyTile(TileModel t, SkillVm vm, bool selected, bool badges = true)
    {
        var b = vm.B;
        string? cur = null;
        if (b is not null && _profile is not null) cur = vm.P?.Current is int c ? c.ToString(CultureInfo.InvariantCulture) : "—";
        t.SkillId = vm.Info.Id;
        t.Icon = IconOf(vm);
        t.Sub = vm.Info.Sub;
        t.State = LookOf(vm, _showDone);
        t.Diamonds = b?.Specs.Count ?? 0;
        t.Selected = selected;
        t.Level = b?.Level.ToString(CultureInfo.InvariantCulture);
        t.Current = cur;
        t.CurrentRgb = StateRgb(vm.P);
        t.Badges = badges;
    }

    private void RefreshSelection()
    {
        for (var i = 0; i < _libUsed; i++) _libPool[i].Selected = _libPool[i].SkillId == _selected;
        for (var i = 0; i < _barUsed; i++) _barPool[i].Selected = _barPool[i].SkillId == _selected;
        RedrawTiles();
        RenderKeys();
        RenderDetail();
    }

    private static string SubLabel(string sub) => sub == "stigma" ? T.BuildStigma : sub == "passive" ? T.BuildPassive : T.BuildActive;

    private void RenderDetail()
    {
        _specIcons.Clear();
        if (_selected == 0 || !_vms.TryGetValue(_selected, out var vm))
        {
            _detail.Visible = false; _detailHint.Visible = true;
            return;
        }
        _detail.Visible = true; _detailHint.Visible = false;
        var info = vm.Info; var b = vm.B;
        ApplyTile(_detailTile.Model, vm, false);
        _detailTile.Redraw();
        _detailName.SetText(vm.Name);
        _detailSub.SetText(SubLabel(info.Sub) + " · " + (info.DamageType ?? "physic"));
        _equipMode = b is null ? EquipMode.Add : b.Slot is not null ? EquipMode.Unequip : info.Sub == "passive" ? EquipMode.Remove : EquipMode.Equip;
        BuildUi.SetText(_equip, _equipMode switch { EquipMode.Add => T.BuildAddToBuild, EquipMode.Unequip => "⏏  " + T.BuildUnequip, EquipMode.Remove => T.BuildRemove, _ => T.BuildEquip });
        _remove.Visible = b is not null && _equipMode != EquipMode.Remove;
        var level = b?.Level ?? 1;
        BuildUi.SetRuns(_detailDesc, Desc(info, level));

        var inBuild = b is not null;
        _levelBlock.Visible = _level.Visible = _ticks.Visible = inBuild;
        _levelValue.SetText(level.ToString(CultureInfo.InvariantCulture));
        var max = Math.Min(info.MaxLevel, Math.Max(SliderMax, level));
        _syncing = true;
        _level.SetRange(1, Math.Max(2, max));
        _level.SetValue(level);
        _syncing = false;
        var ticks = 0;
        for (var c = _ticks.GetFirstChild(); c is not null; c = c.GetNextSibling()) ticks++;
        if (ticks != max)
        {
            while (_ticks.GetFirstChild() is { } c) _ticks.Remove(c);
            for (var i = 0; i < max; i++)
            {
                var tl = BuildUi.Tight((i + 1).ToString(CultureInfo.InvariantCulture), 11, C.Dim);
                tl.Halign = Gtk.Align.Center;
                _ticks.Append(tl);
            }
        }
        var n = 1;
        for (var c = _ticks.GetFirstChild(); c is not null; c = c.GetNextSibling(), n++)
        {
            var on = n == level;
            BuildUi.Fg(c, on ? C.White : C.Dim);
            if (on) c.AddCssClass("k-w7"); else c.RemoveCssClass("k-w7");
        }

        var cap = BuildEditor.SpecCap(info);
        _specCount.SetText((b?.Specs.Count ?? 0).ToString(CultureInfo.InvariantCulture) + "/" + cap.ToString(CultureInfo.InvariantCulture));
        while (_specList.GetFirstChild() is { } old) _specList.Remove(old);
        foreach (var sp in info.Specs)
        {
            var on = b?.Specs.Contains(sp.Id) == true;
            var locked = sp.RequiredLevel > level;
            var marker = UiKit.HBox();
            marker.AddCssClass("k-build-marker");
            if (on) marker.AddCssClass("k-build-on");
            marker.Valign = Gtk.Align.Center;
            marker.SetSizeRequest(UiKit.Px(26), UiKit.Px(26));
            var mark = Gtk.Label.New(on ? "✓" : "◆");
            mark.Hexpand = true;
            mark.Halign = Gtk.Align.Center;
            marker.Append(mark);
            marker.Hexpand = false;   // set explicitly: the label's hexpand would otherwise propagate and stretch the 26 px marker (measured)
            var icon = ItemKit.Image(SpecIcon(sp), 26);
            icon.MarginStart = UiKit.Px(8);
            icon.Valign = Gtk.Align.Center;
            icon.Visible = icon.GetPaintable() is not null;
            if (sp.Icon is not null) _specIcons.Add((icon, sp));
            var text = UiKit.VBox();
            text.MarginStart = UiKit.Px(10);
            text.Valign = Gtk.Align.Center;
            text.Hexpand = true;
            text.Append(BuildUi.Label(sp.Name(_lang), 13, C.Text));
            text.Append(BuildUi.Label(string.Format(T.BuildSpecReq, sp.RequiredLevel), 11, C.Mute));
            var row = UiKit.HBox();
            row.AddCssClass("k-build-spec");
            if (on) row.AddCssClass("k-build-on");
            if (locked) { row.AddCssClass("k-build-locked"); row.SetTooltipText(string.Format(T.BuildSpecLocked, sp.RequiredLevel)); }
            row.Append(marker); row.Append(icon); row.Append(text);
            if (b is not null) row.SetCursorFromName("pointer");
            var specId = sp.Id;
            var click = Gtk.GestureClick.New();
            click.OnPressed += (_, _) => SpecPressed(specId);
            row.AddController(click);
            _specList.Append(row);
        }

        _noteBlock.Visible = _build is not null;
        var note = _build is not null && _build.Notes.TryGetValue(info.Id, out var nt) ? nt : "";
        var buf = _note.GetBuffer();
        if ((buf.Text ?? "") != note) { _syncing = true; buf.Text = note; _syncing = false; }   // equal text untouched: the caret stays while typing
        _noteCount.SetText(note.Length.ToString(CultureInfo.InvariantCulture) + "/600");
        while (_relList.GetFirstChild() is { } r) _relList.Remove(r);
        var rels = Relations.For(info.Id);
        foreach (var rel in rels) _relList.Append(RelationLine(rel, 12));
        _relBlock.Visible = rels.Count > 0;
    }

    private static Gtk.Label RelationLine(SkillRelation r, double size, int maxChars = 1)
    {
        var l = BuildUi.Label("", size, C.Dim, maxChars: maxChars);
        l.SetMarkup(BuildMarkup.Relation(r));
        l.MarginBottom = UiKit.Px(2);
        return l;
    }

    private void RenderPriority()
    {
        _prioUsed = 0;
        foreach (var (cat, wrap) in (ReadOnlySpan<(string, TileWrap)>)[("active", _prioActive), ("stigma", _prioStigma), ("passive", _prioPassive)])
        {
            wrap.Clear();
            var any = false;
            if (_build is not null)
                foreach (var id in _build.Priority)
                {
                    if (!_vms.TryGetValue(id, out var vm) || vm.Info.Sub != cat) continue;
                    if (any) wrap.AddSep();
                    var m = NextModel(_prioPool, ref _prioUsed);
                    ApplyTile(m, vm, false);
                    wrap.Add(m);
                    any = true;
                }
            wrap.AddPlus();
            wrap.Changed();
        }
    }

    private void RenderMacros()
    {
        while (_macroList.GetFirstChild() is { } c) _macroList.Remove(c);
        var any = false;
        if (_build is not null)
            for (var i = 0; i < _build.Macros.Count; i++)
            {
                var m = _build.Macros[i];
                var sb = new StringBuilder(m.Name).Append(" → ");
                for (var j = 0; j < m.Steps.Count; j++) { if (j > 0) sb.Append(" → "); sb.Append(_vms.TryGetValue(m.Steps[j], out var vm) ? vm.Name : m.Steps[j].ToString(CultureInfo.InvariantCulture)); }
                var index = i;
                var del = UiKit.IconButton(Icons.Close, T.BuildRemoveItem, () => { if (_build is not null) Edit(BuildEditor.RemoveMacro(_build, index)); }, 20);
                del.MarginStart = UiKit.Px(6);
                del.Valign = Gtk.Align.Center;
                var chip = UiKit.HBox();
                chip.AddCssClass("k-build-macro");
                chip.Append(BuildUi.Label(sb.ToString(), 13, C.Text));
                chip.Append(del);
                _macroList.Append(chip);
                any = true;
            }
        if (!any)
        {
            var none = BuildUi.Label(T.BuildNoMacros, 13, C.Mute);
            none.MarginBottom = UiKit.Px(6);
            _macroList.Append(none);
        }
        var addBtn = BuildUi.TextBtn(T.BuildAddMacro, AddMacro, "k-build-add");
        addBtn.Halign = Gtk.Align.Start;
        UiKit.Margin(addBtn, 4, 0, 0, 6);
        _macroList.Append(addBtn);
        _macroStepsText.SetText(StepsText());
    }

    private void RenderDescription()
    {
        var html = _build?.Description;
        if (!ReferenceEquals(html, _descHtml.Html)) _descHtml = (html, BuildText.HtmlToText(html));
        _descText.SetText(_descHtml.Text.Length > 0 ? _descHtml.Text : T.BuildNoDescription);
        _descText.Visible = _descOpen;
        _descDown.Widget.Visible = !_descOpen;
        _descUp.Widget.Visible = _descOpen;
    }

    private void RenderSaved() =>
        _saved.SetText("· " + (_build is null || _build.UpdatedAt == default ? T.BuildNotSaved : string.Format(T.BuildSaved, _build.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))));

    /// <summary>Description at a level as runs, cached per (skill, level, language).</summary>
    private TextSeg[] Desc(SkillInfo s, int level)
    {
        var key = (s.Id, Math.Clamp(level, 1, s.MaxLevel), _lang);
        if (!_descCache.TryGetValue(key, out var segs)) _descCache[key] = segs = BuildText.ParseDescription(SkillBook.Resolve(s, key.Item2, _lang));
        return segs;
    }

    private string StepsText()
    {
        var sb = new StringBuilder();
        foreach (var id in _macroSteps) { if (sb.Length > 0) sb.Append(" → "); sb.Append(_vms.TryGetValue(id, out var vm) ? vm.Name : id.ToString(CultureInfo.InvariantCulture)); }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ tooltip

    private Gtk.Widget? TipFor(TileModel? m) => m is not null && _vms.TryGetValue(m.SkillId, out var vm) ? BuildUi.TipFrame(TipContent(vm)) : null;

    private Gtk.Widget TipContent(SkillVm vm)
    {
        var info = vm.Info; var b = vm.B;
        var root = UiKit.VBox();
        var icon = new SkillTile(44);
        ApplyTile(icon.Model, vm, false, badges: false);
        var names = UiKit.VBox();
        names.MarginStart = UiKit.Px(10);
        names.Valign = Gtk.Align.Center;
        names.Hexpand = true;
        names.Append(BuildUi.TipLabel(vm.Name, 14, C.Text, 700));
        names.Append(BuildUi.TipLabel(info.Name(_lang == "es" ? "en" : "es"), 11, C.Mute));
        var tags = UiKit.HBox();
        tags.MarginTop = UiKit.Px(3);
        tags.Append(BuildUi.TagChip(info.Sub));
        if (info.DamageType is { Length: > 0 } dmg) tags.Append(BuildUi.TagChip(dmg));
        names.Append(tags);
        var head = UiKit.HBox();
        head.Append(icon.Area); head.Append(names);
        root.Append(head);

        var level = b?.Level ?? 1;
        var desc = BuildUi.TipLabel("", 12, C.Dim);
        UiKit.Margin(desc, 0, 8, 0, 8);
        BuildUi.SetRuns(desc, Desc(info, level));
        root.Append(desc);

        if (b is not null)
        {
            var cmp = Gtk.Grid.New();
            cmp.AddCssClass("k-build-cmp");
            var cur = vm.P?.Current;
            var curText = _profile is null ? "—" : cur is int c ? c.ToString(CultureInfo.InvariantCulture) : T.BuildTipNotLearned;
            var key = BuildEditor.KeyOf(b.Slot);
            var specs = new StringBuilder();
            foreach (var id in b.Specs) if (BuildEditor.Spec(info, id) is { } sp) { if (specs.Length > 0) specs.Append(" · "); specs.Append(sp.Name(_lang)); }
            CmpRow(cmp, 0, _profile is not null ? string.Format(T.BuildTipNow, _profile.Ref.Name) : T.BuildTipNowAnon, curText, _profile is null ? C.Mute : StateC(vm.P), true);
            CmpRow(cmp, 1, T.BuildTipBuild, b.Level.ToString(CultureInfo.InvariantCulture) + (key > 0 ? " · " + string.Format(T.BuildTipKey, BuildEditor.KeyName(key)) : ""), C.Text, false);
            CmpRow(cmp, 2, T.BuildTipSpecs, specs.Length > 0 ? specs.ToString() : T.BuildTipNone, C.Text, false);
            root.Append(cmp);
            if (cur is int now && now != b.Level)
            {
                root.Append(BuildUi.TipSection(string.Format(T.BuildTipAtLevel, now)));
                var at = BuildUi.TipLabel("", 12, C.Mute);
                at.MarginBottom = UiKit.Px(4);
                BuildUi.SetRuns(at, BuildText.FirstLine(Desc(info, now)));
                root.Append(at);
            }
        }
        if (_build is not null && _build.Notes.TryGetValue(info.Id, out var note) && note.Length > 0)
        {
            root.Append(BuildUi.TipSection(T.BuildTipNotes));
            root.Append(BuildUi.TipLabel(note, 12, C.Text));
        }
        var rels = Relations.For(info.Id);
        if (rels.Count > 0)
        {
            root.Append(BuildUi.TipSection(T.BuildRelations));
            foreach (var r in rels) root.Append(RelationLine(r, 11.5, 44));
        }
        return root;
    }

    private static void CmpRow(Gtk.Grid g, int row, string label, string value, C valueC, bool bold)
    {
        var l = BuildUi.Tight(label, 12, C.Mute);
        l.Xalign = 0;
        UiKit.Margin(l, 0, 1, 10, 1);
        l.Valign = Gtk.Align.Start;
        g.Attach(l, 0, row, 1, 1);
        var v = BuildUi.TipLabel(value, 12, valueC, bold ? 700 : 400);
        UiKit.Margin(v, 0, 1, 0, 1);
        g.Attach(v, 1, row, 1, 1);
    }

    // ------------------------------------------------------------------ input

    private void Select(int id)
    {
        _selected = id;
        if (_build?.Skill(id) is { Slot: { } slot }) _key = BuildEditor.KeyOf(slot);
        RefreshSelection();
    }

    private void LibPressed(TileWrap wrap, int i, int nPress, uint button)
    {
        if (button != 1 || wrap.TileAt(i) is not { } m || !_vms.TryGetValue(m.SkillId, out var vm)) return;
        if (nPress == 2) { EquipSkill(vm); return; }
        Select(vm.Info.Id);
    }

    private void BarPressed(int key, int row, int nPress, uint button)
    {
        if (_bar.TileAt(key, row) is { } m && _vms.TryGetValue(m.SkillId, out var vm))
        {
            if (button == 3) { if (_build is not null) Edit(BuildEditor.Unequip(_build, vm.Info.Id)); return; }
            if (button != 1) return;
            if (nPress == 2) { if (NeedBuild()) Edit(BuildEditor.Unequip(_build!, vm.Info.Id)); return; }
            Select(vm.Info.Id);
            return;
        }
        if (button != 1) return;
        if (_selected != 0 && _build is not null && _vms.TryGetValue(_selected, out var sel) && sel.Info.Sub != "passive")
        {
            if (Edit(BuildEditor.MoveTo(_build, sel.Info, key, row))) { _key = key; RenderKeys(); }
            return;
        }
        _key = key;
        RenderKeys();
    }

    private void PrioPressed(TileWrap wrap, string cat, int i, int nPress, uint button)
    {
        var (x, y, w, h) = wrap.Rect(i);
        if (wrap.KindAt(i) == TileWrap.Kind.Add)
        {
            if (button == 1) AddPriority(wrap.Area, cat, x, y, w, h);
            return;
        }
        if (wrap.TileAt(i) is not { } m || !_vms.TryGetValue(m.SkillId, out var vm)) return;
        if (button == 1) { if (nPress == 1) Select(vm.Info.Id); return; }
        if (button != 3 || _build is null) return;
        var id = vm.Info.Id;
        BuildKit.Menu(wrap.Area,
        [
            new(T.BuildMoveLeft, () => { if (_build is not null) Edit(BuildEditor.MovePriority(_build, id, -1, Category)); }),
            new(T.BuildMoveRight, () => { if (_build is not null) Edit(BuildEditor.MovePriority(_build, id, 1, Category)); }),
            new(T.BuildRemoveItem, () => { if (_build is not null) Edit(BuildEditor.RemovePriority(_build, id)); }),
        ], x, y, w, h);
    }

    private string? Category(int id) => _vms.TryGetValue(id, out var vm) ? vm.Info.Sub : Shell.Engine?.Skills.Get(id)?.Sub;

    /// <summary>Double-click in the library: onto the highlighted key (key 1 when none); passives only join the build.</summary>
    private void EquipSkill(SkillVm vm)
    {
        if (!NeedBuild()) return;
        if (vm.Info.Sub == "passive") { Edit(BuildEditor.AddSkill(_build!, vm.Info.Id)); return; }
        var key = _key > 0 ? _key : 1;
        _selected = vm.Info.Id;
        if (!Edit(BuildEditor.Equip(_build!, vm.Info, key)) && BuildEditor.KeyOf(_build!.Skill(vm.Info.Id)?.Slot) != key) SetStatus(string.Format(T.BuildKeyFull, key));
        else { _key = key; RenderKeys(); }
    }

    private void EquipClick()
    {
        if (!_vms.TryGetValue(_selected, out var vm) || !NeedBuild()) return;
        switch (_equipMode)
        {
            case EquipMode.Add: Edit(BuildEditor.AddSkill(_build!, vm.Info.Id)); break;
            case EquipMode.Unequip: Edit(BuildEditor.Unequip(_build!, vm.Info.Id)); break;
            case EquipMode.Remove: Edit(BuildEditor.RemoveSkill(_build!, vm.Info.Id)); break;
            default: EquipSkill(vm); break;
        }
    }

    /// <summary>Typing in the note box: only the build changes (no redraw, the caret stays); the 500 ms save follows; tooltips read the note when they open.</summary>
    private void NoteChanged()
    {
        if (_syncing || _build is null || _selected == 0) return;
        var buf = _note.GetBuffer();
        var text = buf.Text ?? "";
        if (text.Length > 600)
        {
            // TextBox.MaxLength = 600: the buffer has no cap, so the excess is cut (this handler runs again with the cut text)
            buf.Text = text[..600];
            return;
        }
        var has = _build.Notes.TryGetValue(_selected, out var cur);
        var empty = string.IsNullOrWhiteSpace(text);
        _noteCount.SetText(text.Length.ToString(CultureInfo.InvariantCulture) + "/600");
        if (empty ? !has : cur == text) return;
        var notes = new Dictionary<int, string>(_build.Notes);
        if (empty) notes.Remove(_selected); else notes[_selected] = text;
        _build = _build with { Notes = notes };
        MarkDirty();
    }

    private void LevelChanged(double value)
    {
        if (_syncing || _build is null || !_vms.TryGetValue(_selected, out var vm)) return;
        Edit(BuildEditor.SetLevel(_build, vm.Info, (int)Math.Round(value)));
    }

    private void SpecPressed(int specId)
    {
        if (_build is null || !_vms.TryGetValue(_selected, out var vm)) return;
        var next = BuildEditor.ToggleSpec(_build, vm.Info, specId, out var r);
        if (r == SpecToggle.Locked) SetStatus(string.Format(T.BuildSpecLocked, BuildEditor.Spec(vm.Info, specId)?.RequiredLevel ?? 0));
        else if (r == SpecToggle.Full) SetStatus(string.Format(T.BuildSpecFull, BuildEditor.SpecCap(vm.Info)));
        else { SetStatus(""); Edit(next); }
    }

    private void AddPriority(Gtk.Widget anchor, string cat, double x, double y, double w, double h)
    {
        if (!NeedBuild()) return;
        var items = new List<BuildKit.MenuItem>();
        foreach (var s in _build!.Skills)
        {
            if (_build.Priority.Contains(s.SkillId) || !_vms.TryGetValue(s.SkillId, out var vm) || vm.Info.Sub != cat) continue;
            var id = s.SkillId;
            items.Add(new(vm.Name, () => { if (_build is not null) Edit(BuildEditor.AddPriority(_build, id)); }, IconOf(vm)));
        }
        if (items.Count == 0) items.Add(new(T.BuildNothingToAdd, null));
        BuildKit.Menu(anchor, items, x, y, w, h);
    }

    private void ClearPriority()
    {
        if (!NeedBuild() || _build!.Priority.Count == 0) return;
        BuildKit.Confirm(_clearPrio, T.BuildClearConfirm, () => { if (_build is not null) Edit(_build with { Priority = [] }); });
    }

    private void AddMacro()
    {
        if (!NeedBuild()) return;
        _macroSteps.Clear();
        _macroName.SetText("");
        _macroStepsText.SetText("");
        _macroEditor.Visible = true;
        _macroName.GrabFocus();
    }

    private void MacroCancel() { _macroSteps.Clear(); _macroEditor.Visible = false; }

    private void MacroAddStep(Gtk.Widget anchor)
    {
        if (_build is null) return;
        var items = new List<BuildKit.MenuItem>();
        foreach (var s in _build.Skills)
        {
            if (!_vms.TryGetValue(s.SkillId, out var vm) || vm.Info.Sub == "passive") continue;
            var id = s.SkillId;
            items.Add(new(vm.Name, () => { _macroSteps.Add(id); _macroStepsText.SetText(StepsText()); }, IconOf(vm)));
        }
        if (items.Count == 0) items.Add(new(T.BuildNothingToAdd, null));
        BuildKit.Menu(anchor, items);
    }

    private void MacroSave()
    {
        if (_build is null || _macroSteps.Count == 0) return;
        var name = _macroName.GetText().Trim();
        if (name.Length == 0) name = "Macro " + (_build.Macros.Count + 1).ToString(CultureInfo.InvariantCulture);
        _macroEditor.Visible = false;
        Edit(BuildEditor.AddMacro(_build, new BuildMacro(name, [.. _macroSteps])));
        _macroSteps.Clear();
    }

    private void ShowDoneChanged(bool show)
    {
        _showDone = show;
        Shell.RefreshSettings();
        Shell.Settings.BuildShowDone = show;
        Shell.SaveSettings();
        Render();
    }

    private void LangChanged(string lang)
    {
        _lang = lang;
        Shell.RefreshSettings();
        Shell.Settings.SkillLanguage = lang;
        Shell.SaveSettings();
        Render();
    }

    private void BuildBoxChanged()
    {
        if (_syncing || UiKit.Selected(_buildBox) is not (>= 0 and var i) || i >= _buildIds.Count) return;
        var id = _buildIds[i];
        if (id == (_build?.Id ?? "")) return;
        FlushSave();
        try { _store.ActiveId = id.Length > 0 ? id : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus(ex.Message); }
        _build = id.Length > 0 ? _store.Load(id) : null;
        _selected = 0;
        _deleteBuild.Visible = _copyQuestlog.Visible = _build is not null;
        Render();
    }

    private void NewFromProfile() => SetStatus(CreateFromProfile(out var error) ? "" : error);

    /// <summary>"New from profile": a build of what the character has now becomes the active build. False with the reason when there is no profile or the save failed.</summary>
    private bool CreateFromProfile(out string error)
    {
        var p = _profile;
        if (p is null) { error = T.BuildNoProfileForNew; return false; }
        FlushSave();
        var b = BuildEditor.FromProfile(p, string.Format(T.BuildProfileName, p.Ref.Name, p.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        try
        {
            var saved = _store.Save(b);
            _store.ActiveId = saved.Id;
            QueueTelemetry(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Build save", ex); error = ex.Message; return false; }
        error = "";
        Reload();
        return true;
    }

    private void DeleteBuild()
    {
        if (_build is null) return;
        BuildKit.Confirm(_deleteBuild, string.Format(T.BuildDeleteConfirm, _build.Name), () =>
        {
            if (_build is null) return;
            _saveTick.Stop(); _dirty = false;
            try { _store.Delete(_build.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus(ex.Message); return; }
            _build = null; _selected = 0;
            Reload();
        });
    }

    private void OpenImport()
    {
        DialogCard.ShowSingle(() => new ImportBuildWindow(Shell, _classId)
        {
            Picked = (b, plan, slug) => { if (plan is null) SaveImported(b); else ImportMine(b, plan, slug); },
            PlanUrl = url => { SetSub(true); _ = _pageDaev.ImportPlanFromUrlAsync(url); },
            ImportPlan = (p, slug) => ImportPlan(p, slug).Message,
        });
    }

    /// <summary>Fetches the questlog build behind a skill-builder link and saves it as the active build; errors go to the status line.</summary>
    public async Task ImportAsync(string slug, long id, bool fromLink = false)
    {
        if (_importing) return;
        _importing = true;
        SetStatus(string.Format(T.BuildImporting, id));
        try
        {
            var b = await QuestlogService.FetchAsync(slug, id);
            if (b is null) SetStatus(string.Format(T.BuildImportNotFound, id));
            else
            {
                if (fromLink && QuestlogService.LearnSlug(Shell.Settings, slug)) Shell.SaveSettings();
                SaveImported(b);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"Build import {slug}/{id} failed: {ex.Message}");
            SetStatus(string.Format(T.BuildImportFailed, QuestlogService.Reason(ex)));
        }
        finally { _importing = false; }
    }

    private Build? SaveImported(Build imported)
    {
        FlushSave();
        Build saved;
        try
        {
            saved = _store.Save(QuestlogImport.Prepare(imported, _store.List()));
            _store.ActiveId = saved.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Build save", ex); SetStatus(ex.Message); return null; }
        Log.Info($"Build imported from questlog: {saved.Source?.Id} → {saved.Id} ({saved.ClassId}, {saved.Skills.Count} skills) for {_charKey}");
        QueueTelemetry(saved);
        _selected = 0;
        Reload();
        SetStatus(_classId.Length > 0 && saved.ClassId.Length > 0 && saved.ClassId != _classId
            ? string.Format(T.BuildImportedOtherClass, saved.Name, T.ClassNameOf(saved.ClassId), T.ClassNameOf(_classId))
            : string.Format(T.BuildImported, saved.Name));
        return saved;
    }

    /// <summary>A skill build of the user's own list (already complete, nothing fetched) plus the planner build paired with it, reported on one status line.</summary>
    private void ImportMine(Build build, QuestlogDaevPlan? paired, string? slug)
    {
        if (SaveImported(build) is null || paired is null) return;
        var status = _status.GetText();
        var (ok, msg) = ImportPlan(paired, slug ?? build.Source?.Slug);
        SetStatus(status + (ok ? string.Format(T.BuildPlanPaired, paired.Name) : "; " + msg));
    }

    /// <summary>
    /// A questlog planner build becomes the plan of the active build; with no active build one is made from the profile first
    /// ("New from profile"). Refused for another class. Saved at once (the dialog that asked may stay open).
    /// </summary>
    private (bool Ok, string Message) ImportPlan(QuestlogDaevPlan plan, string? slug)
    {
        if (Shell.Engine is not { } engine) return (false, "");
        if (_classId.Length > 0 && plan.ClassId.Length > 0 && !plan.ClassId.Equals(_classId, StringComparison.OrdinalIgnoreCase))
            return (false, string.Format(T.DaevImportClass, plan.Id, plan.ClassId, _classId));
        var created = false;
        if (_build is null)
        {
            if (!CreateFromProfile(out var error)) return (false, error);
            created = true;
            if (_build is null) return (false, T.BuildNoBuild);
        }
        var cls = engine.Daevanion.ForClass(_classId.Length > 0 ? _classId : plan.ClassId);
        var next = DaevanionPlan.FromQuestlog(cls, plan, out var count);
        _build = _build with { Daevanion = next, PlanSource = new BuildSource(plan.Id, slug, null), PlanName = plan.Name, PlanSourceUpdatedAt = plan.UpdatedAt };
        _dirty = true;
        FlushSave();
        Render();
        Log.Info($"Daevanion import: \"{plan.Name}\" ({plan.Id}) {count} nodes on {next.Count} boards into build {_build.Id}");
        var msg = string.Format(T.ImportPlanDone, plan.Name, _build.Name, count.ToString(CultureInfo.InvariantCulture), next.Count);
        return (true, created ? T.ImportPlanNewBuild + msg : msg);
    }

    // ------------------------------------------------------------------ check questlog (only on click: no network on tab open)

    private void RenderCheck(StringBuilder src)
    {
        if (_build is null) return;
        if (_build.Source is not { Slug: { Length: > 0 } } && _build.PlanSource is not { Slug: { Length: > 0 } }) return;
        src.Append("  ·  ");
        var c = _check is { } k && k.BuildId == _build.Id ? k : null;
        if (c is { Note: { } note }) src.Append(BuildMarkup.Esc(note)).Append("  ");
        if (c is not null && (c.Skill is not null || c.Plan is not null))
        {
            var newest = Nullable.Compare(c.Skill?.UpdatedAt, c.Plan?.UpdatedAt) >= 0 ? c.Skill?.UpdatedAt : c.Plan?.UpdatedAt;
            BuildMarkup.Span(src, string.Format(T.BuildQuestlogNewer, newest?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?") + " — ", BuildColors.Amber);
            BuildMarkup.Link(src, T.BuildQuestlogUpdate, "update", T.BuildQuestlogUpdateTip);
            return;
        }
        BuildMarkup.Link(src, _checking ? T.BuildChecking : T.BuildCheckQuestlog, "check", T.BuildCheckQuestlogTip);
    }

    private void RenderSourceLine() => RenderHeader(_build is not null ? Progress.Compare(_build, _profile, null) : null);

    private async Task CheckQuestlogAsync()
    {
        if (_build is null || _checking) return;
        var b = _build;
        var skillSlug = b.Source is { Id: > 0, Slug: { Length: > 0 } ss } ? ss : null;
        var planSlug = b.PlanSource is { Id: > 0, Slug: { Length: > 0 } ps } ? ps : null;
        if (skillSlug is null && planSlug is null) return;
        _checking = true; _check = null;
        SetStatus(T.BuildChecking);
        RenderSourceLine();
        try
        {
            BuildSummary? skill = null;
            QuestlogDaevPlan? plan = null;
            var gone = false;
            if (skillSlug is not null)
            {
                var m = await QuestlogService.MineAsync(skillSlug, refresh: true);
                var r = m.Skills.Find(x => x.Id == b.Source!.Id);
                if (r is null) gone = true;
                else if (QuestlogMine.IsNewer(r.UpdatedAt, b.SourceUpdatedAt)) skill = r;
            }
            if (planSlug is not null)
            {
                var m = planSlug == skillSlug && QuestlogService.CachedMine(planSlug) is { } same ? same : await QuestlogService.MineAsync(planSlug, refresh: true);
                var r = m.Plans.Find(x => x.Id == b.PlanSource!.Id);
                if (r is null) gone = true;
                else if (QuestlogMine.IsNewer(r.UpdatedAt, b.PlanSourceUpdatedAt)) plan = r;
            }
            _check = new QuestlogCheck(b.Id, skill, plan, planSlug, skill is null && plan is null ? (gone ? T.BuildQuestlogGone : T.BuildQuestlogUpToDate) : null);
            SetStatus("");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog check of build {b.Id} failed: {ex.Message}");
            SetStatus(string.Format(T.BuildImportFailed, QuestlogService.Reason(ex)));
        }
        finally { _checking = false; }
        if (_build?.Id == b.Id) RenderSourceLine();
    }

    /// <summary>Re-imports questlog's newer versions: the skill build keeps the local notes and plan (QuestlogImport.Prepare); the plan is replaced only when it is the newer one.</summary>
    private void QuestlogUpdate()
    {
        if (_check is not { } c || _build?.Id != c.BuildId) return;
        _check = null;
        var parts = new List<string>(2);
        if (c.Skill is { } s)
        {
            if (SaveImported(s.Build) is null) return;
            parts.Add(s.Name);
        }
        if (c.Plan is { } p)
        {
            var (ok, msg) = ImportPlan(p, c.PlanSlug);
            parts.Add(ok ? p.Name : msg);
        }
        SetStatus(string.Format(T.BuildUpdatedFromQuestlog, string.Join(" + ", parts)));
    }

    /// <summary>"Copy for questlog": the build as the JSON tools/questlog-publish.js reads in the user's own questlog session (the app never posts).
    /// Wayland needs the click's input serial for the selection, so the copy happens in the click handler.</summary>
    private void CopyQuestlog()
    {
        if (!NeedBuild()) return;
        FlushSave();
        SetStatus(BuildKit.Copy(_copyQuestlog, QuestlogExport.ToJson(_build!)) ? T.BuildCopied : T.BuildCopyFailed);
    }

    // ------------------------------------------------------------------ suggestions

    private void RenderSuggestions()
    {
        var show = _build is null;
        _suggestCard.Visible = show;
        _buildCards.Visible = !show;
        if (!show) return;
        _suggestTitle.SetText(string.Format(T.BuildSuggestTitle, _classId.Length > 0 ? T.ClassNameOf(_classId) : "?"));
        if (_classId.Length == 0) { _suggestFor = ""; SuggestMessage(T.BuildSuggestNoClass); return; }
        var slug = Shell.Settings.QuestlogSlug ?? "";
        if (QuestlogService.CachedSuggestions(_classId) is { } hit && (slug.Length == 0 || QuestlogService.CachedMine(slug) is not null))
        {
            _suggestFor = _classId;
            FillSuggestions(hit, MineOf(slug, _classId));
            return;
        }
        if (_suggestFor == _classId && _suggestList.GetFirstChild() is not null) return;   // already loading for this class
        _suggestFor = _classId;
        SuggestMessage(T.BuildSuggestLoading);
        _ = LoadSuggestionsAsync(_classId);
    }

    private async Task LoadSuggestionsAsync(string classId)
    {
        var slug = Shell.Settings.QuestlogSlug ?? "";
        List<BuildSummary>? mine = null;
        if (slug.Length > 0)
        {
            try { await QuestlogService.MineAsync(slug); mine = MineOf(slug, classId); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException) { Log.Info($"questlog builds of {slug} failed: {ex.Message}"); }
        }
        try
        {
            var list = await QuestlogService.SuggestionsAsync(classId);
            if (_suggestFor == classId && _build is null) FillSuggestions(list, mine);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"Build suggestions for {classId} failed: {ex.Message}");
            if (_suggestFor != classId) return;
            if (mine is { Count: > 0 } && _build is null) { FillSuggestions([], mine); _suggestList.Append(BuildUi.Label(T.BuildSuggestFailed, 12.5, C.Mute)); }
            else SuggestMessage(T.BuildSuggestFailed);
            _suggestFor = "";
        }
    }

    /// <summary>The user's questlog skill builds of this class from the session cache, at most 5; null without a slug or a cached list.</summary>
    private static List<BuildSummary>? MineOf(string slug, string classId) =>
        slug.Length > 0 && QuestlogService.CachedMine(slug) is { } m ? QuestlogMine.OfClass(m.Skills, static b => b.ClassId, classId, BuildSuggestions.Take) : null;

    private void ClearSuggestions()
    {
        while (_suggestList.GetFirstChild() is { } c) _suggestList.Remove(c);
    }

    private void SuggestMessage(string text)
    {
        ClearSuggestions();
        var t = BuildUi.Label(text, 12.5, C.Mute);
        t.MarginBottom = UiKit.Px(6);
        _suggestList.Append(t);
    }

    private void FillSuggestions(List<BuildSummary> list, List<BuildSummary>? mine)
    {
        ClearSuggestions();
        if (mine is { Count: > 0 })
        {
            _suggestList.Append(SuggestHeader(T.BuildSuggestMine));
            foreach (var b in mine) _suggestList.Append(ImportBuildWindow.Row(b, AddMineSuggestion, true));
            _suggestList.Append(SuggestHeader(T.BuildSuggestTop));
        }
        if (list.Count == 0) { _suggestList.Append(BuildUi.Label(T.BuildSuggestEmpty, 12.5, C.Mute)); return; }
        foreach (var b in list) _suggestList.Append(ImportBuildWindow.Row(b, AddSuggestion, true));
    }

    private static Gtk.Label SuggestHeader(string text)
    {
        var l = BuildUi.Label(text.ToUpper(T.Culture), 11, C.Dim, 600);
        UiKit.Margin(l, 0, 4, 0, 6);
        return l;
    }

    private void AddMineSuggestion(BuildSummary s)
    {
        var slug = Shell.Settings.QuestlogSlug ?? "";
        var plans = QuestlogService.CachedMine(slug)?.Plans;
        ImportMine(s.Build, plans is null ? null : QuestlogMine.PairPlan(s.Name, s.ClassId, plans), slug);
    }

    private void AddSuggestion(BuildSummary s)
    {
        if (s.AuthorSlug is { Length: > 0 } slug) _ = ImportAsync(slug, s.Id);
        else SetStatus(string.Format(T.BuildImportNotFound, s.Id));
    }
}
