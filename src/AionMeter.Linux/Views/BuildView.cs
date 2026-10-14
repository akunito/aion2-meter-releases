using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// Build tab on Linux (WPF Views/BuildView, A2M-21; A2M-48 phase 3c): the character's skill build in questlog's planner look — class skill
/// library, 12×4 key bar, detail panel with level, specs, notes and relations, priority chains, macros, description — against the profile
/// the daemon captured (current level badge per icon, Missing summary), and the Daevanion sub-tab (<see cref="DaevanionPage"/>). Builds are
/// the UI's own files (characters/&lt;key&gt;/builds, same format as WPF), saved 500 ms after the last edit. The character comes from a
/// selector (default: the one the daemon saw logged in, state/summary.json) and never moves the viewer engine's selection (that would
/// rebind and re-save the live stores over the daemon's files: <see cref="CharacterFolder"/>).
/// </summary>
public sealed class BuildView : HostedView
{
    private static UiText T => UiText.Current;
    /// <summary>The level slider spans 1..20 as questlog's planner does; further only for a build level above 20, never past the catalogue max.</summary>
    public const int SliderMax = 20;
    private const double Step = 54;   // 48 px cells, 6 px gap (questlog's bar)

    private sealed class SkillVm(SkillInfo info)
    {
        public SkillInfo Info { get; } = info;
        public BuildSkill? B;
        public SkillProgress? P;
        public Avalonia.Media.Imaging.Bitmap? Icon;
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
    private bool _dirty, _syncing, _showDone, _descOpen, _importing, _checking, _shown, _daevTab;
    private string _suggestFor = "";
    private QuestlogCheck? _check;
    private int _selected, _key;
    private EquipMode _equipMode;
    private SkillRelationCache? _relCache;
    private readonly Dictionary<int, SkillVm> _vms = new();
    private readonly Dictionary<int, SkillProgress> _prog = new();
    private readonly List<SkillTile> _tiles = new();
    private readonly List<SkillTile> _barTiles = new();
    private readonly List<(Image Img, SkillSpec Spec)> _specIcons = new();
    private readonly Dictionary<(int Id, int Level, string Lang), TextSeg[]> _descCache = new();
    private (string? Html, string Text) _descHtml = (null, "");
    private readonly List<int> _macroSteps = new();
    private readonly DispatcherTimer _saveTick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // icons land one per 400 ms: refresh at most every 600 ms instead of per icon (as WPF)
    private readonly DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };
    // the daemon re-writes profile.json / character.json at a login or a sync: re-read when their stamp moves
    private readonly DispatcherTimer _fileTick = new() { Interval = TimeSpan.FromSeconds(3) };

    // chrome
    private readonly Border _subSkills, _subDaev;
    private readonly ComboBox _charBox, _langBox, _buildBox;
    private readonly Control _copyQuestlog, _deleteBuild;
    private readonly TextBlock _status;
    private readonly StackPanel _pageSkills;
    private readonly DaevanionPage _pageDaev;
    private readonly List<string> _charKeys = new();
    private readonly List<string> _buildIds = new();
    // suggestions
    private readonly Border _suggestCard;
    private readonly TextBlock _suggestTitle;
    private readonly StackPanel _suggestList = new() { Margin = new Thickness(12, 0, 12, 4) };
    // build card
    private readonly StackPanel _buildCards = new();
    private readonly WrapPanel _sourceLine = new() { Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _showDoneBox;
    private readonly TextBlock _missing, _noClass, _stigmaCount, _lblActive, _lblStigma, _lblPassive;
    private readonly WrapPanel _target = new() { Margin = new Thickness(12, 4, 12, 0), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly WrapPanel _rowActive = Lib(), _rowStigma = Lib(), _rowPassive = Lib();
    // bar + detail
    private readonly Canvas _bar = new() { Width = BuildEditor.Keys * Step - 6, Height = BuildEditor.Rows * Step - 6, Background = Brushes.Transparent };
    private readonly Canvas _keyRow = new() { Width = BuildEditor.Keys * Step - 6, Height = 18, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Border[] _keys = new Border[BuildEditor.Keys];
    private readonly TextBlock _detailHint, _detailName, _detailSub, _detailDesc, _levelValue, _specCount, _noteCount;
    private readonly StackPanel _detail = new() { IsVisible = false };
    private readonly SkillTile _detailTile = new();
    private readonly Button _equip, _remove;
    private readonly Slider _level;
    private readonly UniformGrid _ticks = new() { Rows = 1, Margin = new Thickness(0, 4, 0, 12) };
    private readonly Control _levelBlock;
    private readonly StackPanel _specList = new(), _noteBlock = new(), _relBlock = new() { Margin = new Thickness(0, 12, 0, 0) }, _relList = new();
    private readonly TextBox _note;
    // priority, macros, description, footer
    private readonly WrapPanel _prioActive = new(), _prioStigma = new(), _prioPassive = new();
    private readonly TextBlock _prioActiveLbl, _prioStigmaLbl, _prioPassiveLbl;
    private readonly WrapPanel _macroList = new() { Margin = new Thickness(12, 10, 12, 6) };
    private readonly StackPanel _macroEditor = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 10), IsVisible = false };
    private readonly TextBox _macroName;
    private readonly TextBlock _macroStepsText, _descText, _saved;
    private readonly Avalonia.Controls.Shapes.Path _descChevron;

    private static WrapPanel Lib() => new() { Margin = new Thickness(12, 0, 6, 4) };

    public BuildView(AppShell shell) : base(shell, "build", UiText.Current.BuildTitle)
    {
        var s = shell.Settings;
        _lang = s.ResolvedNamesLanguage;
        _showDone = s.BuildShowDone;

        // --- sub-tabs | character, skill language (the header's right side stays free for the card's narrow width)
        _subSkills = BuildUi.SubTab(T.BuildSubSkills, () => SetSub(false));
        _subDaev = BuildUi.SubTab(T.BuildSubDaevanion, () => SetSub(true));
        _charBox = new ComboBox { MinWidth = 150, FontSize = 12, Height = 28, Margin = new Thickness(0, 0, 6, 0), Focusable = false };
        _charBox.SelectionChanged += (_, _) => { if (!_syncing && _charBox.SelectedIndex is >= 0 and var i && i < _charKeys.Count) SwitchCharacter(_charKeys[i]); };
        _langBox = UiKit.Combo(["Skills: English", "Skills: Español"], _lang == "es" ? 1 : 0, 140);
        _langBox.Height = 28; _langBox.FontSize = 12; _langBox.Focusable = false;
        UiKit.Tip(_langBox, T.BuildSkillLangTip);
        _langBox.SelectionChanged += (_, _) => { if (!_syncing) LangChanged(_langBox.SelectedIndex == 1 ? "es" : "en"); };
        var subRow = new WrapPanel { Margin = new Thickness(14, 0, 8, 0) };
        subRow.Children.Add(_subSkills); subRow.Children.Add(_subDaev);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_charBox); right.Children.Add(_langBox);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 14, 0) };
        top.Place(subRow, 0, 0); top.Place(right, 0, 1);

        // --- build picker row
        _buildBox = new ComboBox { Width = 300, FontSize = 12, Height = 28, Focusable = false, MaxDropDownHeight = 360 };
        UiKit.Tip(_buildBox, T.BuildPickTip);
        _buildBox.SelectionChanged += (_, _) => BuildBoxChanged();
        var picker = new WrapPanel { Margin = new Thickness(14, 8, 14, 0) };
        picker.Children.Add(_buildBox);
        picker.Children.Add(Gap(BuildUi.Flat(T.BuildNewFromProfile, NewFromProfile, tip: T.BuildNewFromProfileTip)));
        picker.Children.Add(Gap(BuildUi.Flat(T.BuildImport, OpenImport, tip: T.BuildImportTip)));
        picker.Children.Add(_copyQuestlog = Gap(BuildUi.Flat(T.BuildCopyQuestlog, CopyQuestlog, tip: T.BuildCopyQuestlogTip)));
        _deleteBuild = UiKit.IconButton(Visuals.IconClose, T.BuildDeleteTip, DeleteBuild);
        _deleteBuild.Margin = new Thickness(6, 3, 0, 0);
        picker.Children.Add(_deleteBuild);
        _status = new TextBlock { FontSize = 12, Foreground = BuildColors.Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        picker.Children.Add(_status);

        // --- suggestions (no active build): the class's most-liked questlog builds instead of an empty planner
        _suggestTitle = BuildUi.BandTitle("");
        var suggest = new StackPanel();
        suggest.Children.Add(BuildUi.Band(_suggestTitle));
        suggest.Children.Add(new TextBlock { Text = T.BuildSuggestHint, FontSize = 12.5, Foreground = BuildColors.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 10) });
        suggest.Children.Add(_suggestList);
        var sugButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 12) };
        sugButtons.Children.Add(BuildUi.Flat(T.BuildSearch, OpenImport));
        sugButtons.Children.Add(Gap(BuildUi.Flat(T.BuildNewFromProfile, NewFromProfile, tip: T.BuildNewFromProfileTip)));
        suggest.Children.Add(sugButtons);
        _suggestCard = BuildUi.Card(suggest);
        _suggestCard.IsVisible = false;

        // --- build card: library rows against the profile
        var card1 = new StackPanel();
        var band1 = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        band1.Place(BuildUi.BandTitle(T.BuildCard), 0, 0);
        band1.Place(_sourceLine, 0, 1);
        card1.Children.Add(BuildUi.Band(band1));
        _showDoneBox = UiKit.Check(T.BuildShowDone, _showDone);
        _showDoneBox.Margin = new Thickness(0, 0, 16, 0);
        _showDoneBox.IsCheckedChanged += (_, _) => { if (!_syncing) ShowDoneChanged(_showDoneBox.IsChecked == true); };
        _missing = new TextBlock { FontSize = 12.5, Foreground = BuildColors.Amber, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var doneRow = new WrapPanel { Margin = new Thickness(12, 10, 12, 0) };
        doneRow.Children.Add(_showDoneBox); doneRow.Children.Add(_missing);
        card1.Children.Add(doneRow);
        card1.Children.Add(_target);
        _noClass = new TextBlock { Text = T.BuildNoClass, FontSize = 12.5, Foreground = BuildColors.Mute, Margin = new Thickness(12, 12, 12, 4), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        card1.Children.Add(_noClass);
        _lblActive = BuildUi.Lbl(T.BuildActive.ToUpper(T.Culture));
        _lblStigma = BuildUi.Lbl(T.BuildStigma.ToUpper(T.Culture));
        _lblPassive = BuildUi.Lbl(T.BuildPassive.ToUpper(T.Culture));
        card1.Children.Add(new Border { Margin = new Thickness(12, 10, 12, 6), Child = _lblActive });
        card1.Children.Add(_rowActive);
        _stigmaCount = new TextBlock { FontSize = 11, Foreground = BuildColors.Purple };
        var stigmaHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 6, 12, 6) };
        stigmaHead.Children.Add(_lblStigma);
        stigmaHead.Children.Add(new Border { Background = Visuals.Solid(0xFF3A3A3A), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = _stigmaCount });
        card1.Children.Add(stigmaHead);
        card1.Children.Add(_rowStigma);
        card1.Children.Add(new Border { Margin = new Thickness(12, 6, 12, 6), Child = _lblPassive });
        card1.Children.Add(_rowPassive);
        var legend = new WrapPanel { Margin = new Thickness(12, 2, 12, 10) };
        legend.Children.Add(LegendItem(BuildColors.Green, T.BuildLegendOk));
        legend.Children.Add(LegendItem(BuildColors.Amber, T.BuildLegendNear));
        legend.Children.Add(LegendItem(BuildColors.Bad, T.BuildLegendFar));
        legend.Children.Add(BuildUi.Muted(T.BuildLegendOff, 11));
        card1.Children.Add(legend);
        _buildCards.Children.Add(BuildUi.Card(card1));

        // --- key bar + detail
        BuildBarCells();
        var barStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        barStack.Children.Add(_bar);
        barStack.Children.Add(_keyRow);
        // the 642 px bar shrinks to the card's width (560 px card); clicks scale with it
        var barBox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(12, 10, 12, 8), Child = barStack };
        var card2 = new StackPanel();
        card2.Children.Add(barBox);
        var detailHost = new Grid { Margin = new Thickness(12) };
        _detailHint = new TextBlock { Text = T.BuildPickSkill, FontSize = 12.5, Foreground = BuildColors.Mute, TextWrapping = TextWrapping.Wrap };
        detailHost.Children.Add(_detailHint);
        detailHost.Children.Add(_detail);
        _detailName = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = BuildColors.Text, TextWrapping = TextWrapping.Wrap };
        _detailSub = new TextBlock { FontSize = 12, Foreground = BuildColors.Blue };
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_detailName); names.Children.Add(_detailSub);
        var closeDetail = UiKit.IconButton(Visuals.IconClose, T.TipClose, () => { _selected = 0; RefreshSelection(); });
        _detail.Children.Add(UiKit.Row("Auto,*,Auto", _detailTile, names, closeDetail));
        _equip = BuildUi.Flat("", EquipClick, 13);
        _equip.Padding = new Thickness(8); _equip.HorizontalAlignment = HorizontalAlignment.Stretch; _equip.HorizontalContentAlignment = HorizontalAlignment.Center;
        _remove = BuildUi.Flat(T.BuildRemove, () => { if (_vms.TryGetValue(_selected, out var vm) && NeedBuild()) Edit(BuildEditor.RemoveSkill(_build!, vm.Info.Id)); }, 13);
        _remove.Padding = new Thickness(12, 8); _remove.Margin = new Thickness(6, 0, 0, 0);
        var equipRow = UiKit.Row("*,Auto", _equip, _remove);
        equipRow.Margin = new Thickness(0, 12);
        _detail.Children.Add(equipRow);
        _detailDesc = new TextBlock { FontSize = 12, Foreground = BuildColors.Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 18.6 };
        _detail.Children.Add(_detailDesc);
        _levelValue = new TextBlock { FontSize = 13, FontWeight = FontWeight.Bold, Foreground = BuildColors.Text };
        _levelBlock = UiKit.Row("*,Auto", new TextBlock { Text = T.BuildLevel, FontSize = 13, Foreground = BuildColors.Text }, _levelValue);
        _levelBlock.Margin = new Thickness(0, 12, 0, 4);
        _detail.Children.Add(_levelBlock);
        _level = new Slider { Minimum = 1, Maximum = SliderMax, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(4, 0), Focusable = false };
        _level.ValueChanged += (_, e) => LevelChanged(e.NewValue);
        _detail.Children.Add(_level);
        _detail.Children.Add(_ticks);
        _specCount = new TextBlock { FontSize = 13, Foreground = BuildColors.Mute };
        var specHead = UiKit.Row("*,Auto", new TextBlock { Text = T.BuildSpecs, FontSize = 13, Foreground = BuildColors.Text }, _specCount);
        specHead.Margin = new Thickness(0, 0, 0, 4);
        _detail.Children.Add(specHead);
        _detail.Children.Add(_specList);
        _noteCount = new TextBlock { FontSize = 11, Foreground = BuildColors.Mute, VerticalAlignment = VerticalAlignment.Bottom };
        var noteHead = UiKit.Row("*,Auto", new TextBlock { Text = T.BuildTipNotes, FontSize = 13, Foreground = BuildColors.Text }, _noteCount);
        noteHead.Margin = new Thickness(0, 12, 0, 4);
        _noteBlock.Children.Add(noteHead);
        _note = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 62, MaxHeight = 160, MaxLength = 600, FontSize = 12.5, Watermark = T.BuildNoteHint,
            Background = BuildColors.Field, Foreground = BuildColors.Text, BorderBrush = BuildColors.Line2, CaretBrush = BuildColors.Text, Padding = new Thickness(6, 5),
        };
        _note.TextChanged += (_, _) => NoteChanged();
        _noteBlock.Children.Add(_note);
        _detail.Children.Add(_noteBlock);
        _relBlock.Children.Add(new TextBlock { Text = T.BuildRelations, FontSize = 13, Foreground = BuildColors.Text, Margin = new Thickness(0, 0, 0, 4) });
        _relBlock.Children.Add(_relList);
        _detail.Children.Add(_relBlock);
        card2.Children.Add(detailHost);
        _buildCards.Children.Add(BuildUi.Card(card2));

        // --- priority
        var card3 = new StackPanel();
        var clear = BuildUi.TextBtn("✕ " + T.BuildClear, ClearPriority);
        card3.Children.Add(BuildUi.Band(UiKit.Row("*,Auto", BuildUi.BandTitle(T.BuildPriority), clear)));
        _prioActiveLbl = BuildUi.Lbl(T.BuildActive.ToUpper(T.Culture));
        _prioStigmaLbl = BuildUi.Lbl(T.BuildStigma.ToUpper(T.Culture));
        _prioPassiveLbl = BuildUi.Lbl(T.BuildPassive.ToUpper(T.Culture));
        card3.Children.Add(PrioRow(_prioActiveLbl, _prioActive));
        card3.Children.Add(new Border { Height = 0.8, Background = BuildColors.Line });
        card3.Children.Add(PrioRow(_prioStigmaLbl, _prioStigma));
        card3.Children.Add(new Border { Height = 0.8, Background = BuildColors.Line });
        card3.Children.Add(PrioRow(_prioPassiveLbl, _prioPassive));
        _buildCards.Children.Add(BuildUi.Card(card3));

        // --- macros
        var card4 = new StackPanel();
        card4.Children.Add(BuildUi.Band(BuildUi.BandTitle(T.BuildMacros)));
        card4.Children.Add(_macroList);
        _macroName = UiKit.Input("", 150);
        _macroName.Background = BuildColors.Band; _macroName.BorderBrush = BuildColors.Line2;
        _macroName.Watermark = T.BuildMacroName;
        _macroStepsText = new TextBlock { FontSize = 12.5, Foreground = BuildColors.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis };
        _macroEditor.Children.Add(_macroName);
        _macroEditor.Children.Add(_macroStepsText);
        Button? addStep = null;
        addStep = BuildUi.Flat(T.BuildMacroAddStep, () => MacroAddStep(addStep!));
        _macroEditor.Children.Add(addStep);
        _macroEditor.Children.Add(Gap(BuildUi.Flat(T.BuildMacroSave, MacroSave)));
        var cancel = BuildUi.TextBtn(T.BuildMacroCancel, MacroCancel);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        _macroEditor.Children.Add(cancel);
        card4.Children.Add(_macroEditor);
        _buildCards.Children.Add(BuildUi.Card(card4));

        // --- description (collapsed until the band is clicked)
        var card5 = new StackPanel();
        _descChevron = Visuals.Icon(Visuals.IconChevron, BuildColors.Mute, 11, 1.6);
        _descChevron.VerticalAlignment = VerticalAlignment.Center;
        var descBand = BuildUi.Band(UiKit.Row("*,Auto", BuildUi.BandTitle(T.BuildDescription), _descChevron));
        descBand.Cursor = new Cursor(StandardCursorType.Hand);
        descBand.PointerPressed += (_, e) => { e.Handled = true; _descOpen = !_descOpen; RenderDescription(); };
        card5.Children.Add(descBand);
        _descText = new TextBlock { Margin = new Thickness(12, 10), FontSize = 12.5, Foreground = BuildColors.Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 19, IsVisible = false };
        card5.Children.Add(_descText);
        _buildCards.Children.Add(BuildUi.Card(card5));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        footer.Children.Add(BuildUi.TextBtn("💾 " + T.BuildSave, FlushSave, 13));
        _saved = new TextBlock { FontSize = 13, Foreground = BuildColors.Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        footer.Children.Add(_saved);
        _buildCards.Children.Add(footer);

        _pageSkills = new StackPanel();
        _pageSkills.Children.Add(_suggestCard);
        _pageSkills.Children.Add(_buildCards);
        _pageDaev = new DaevanionPage(shell) { IsVisible = false };
        // Daevanion edits ride on this view's build and its 500 ms save (the page only hands over the new plan)
        _pageDaev.PlanImported += (plan, slug) =>
        {
            if (_build is null) return;
            _build = _build with { PlanSource = new BuildSource(plan.Id, slug, null), PlanName = plan.Name, PlanSourceUpdatedAt = plan.UpdatedAt };
            MarkDirty();
        };
        _pageDaev.PlanEdited += plan => { if (_build is null) return; _build = _build with { Daevanion = plan }; MarkDirty(); };

        var pages = new Grid { Margin = new Thickness(14, 0, 14, 14) };
        pages.Children.Add(_pageSkills);
        pages.Children.Add(_pageDaev);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        body.Place(top, 0, 0);
        body.Place(picker, 1, 0);
        body.Place(new ScrollViewer { Content = pages, Margin = new Thickness(0, 10, 0, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 2, 0);
        SetBody(body);
        SetSub(false);

        shell.ImportRequested += () => { if (_shown) OpenImport(); };
        _saveTick.Tick += (_, _) => FlushSave();
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); RefreshIcons(); };
        _fileTick.Tick += (_, _) => { if (CharacterFolder.ProfileStamp(AppSettings.AppDataDir, _charKey) != _profileStamp) { FlushSave(); ReloadProfile(); Render(); } };
    }

    private static T Gap<T>(T c) where T : Control { c.Margin = new Thickness(6, 0, 0, 0); return c; }

    private static Control LegendItem(IBrush color, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = color, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(BuildUi.Muted(text, 11));
        return sp;
    }

    private static Control PrioRow(TextBlock label, WrapPanel panel)
    {
        var g = new Grid { Margin = new Thickness(12, 8), ColumnDefinitions = new ColumnDefinitions("70,*") };
        g.Place(label, 0, 0);
        g.Place(panel, 0, 1);
        return g;
    }

    // ------------------------------------------------------------------ lifecycle

    public override void Shown()
    {
        _shown = true;
        Shell.Icons.Loaded += OnIcon;
        Shell.WhenEngine(_ => { if (_shown) { Shell.RefreshSettings(); FillCharacters(); Reload(); } });
        _fileTick.Start();
    }

    public override void Hidden()
    {
        _shown = false;
        FlushSave();
        Shell.Icons.Loaded -= OnIcon;
        _iconTick.Stop();
        _fileTick.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.BuildTitle;
        if (_shown) Render();
    }

    public override IHostedView PopOut()
    {
        FlushSave();
        var copy = new BuildView(Shell);
        copy._charKey = _charKey;
        copy._selected = _selected;
        copy._daevTab = _daevTab;
        copy.SetSub(_daevTab);
        return copy;
    }

    private void OnIcon() { if (!_iconTick.IsEnabled) _iconTick.Start(); }

    private void SetSub(bool daev)
    {
        _daevTab = daev;
        BuildUi.SetSubTab(_subSkills, !daev);
        BuildUi.SetSubTab(_subDaev, daev);
        _pageSkills.IsVisible = !daev;
        _pageDaev.IsVisible = daev;
        HelpKey = "build";
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
            _charBox.Items.Clear();
            foreach (var k in _charKeys)
            {
                var label = k;
                if (CharacterFolder.ParseKey(k) is { } p) label = engine is null ? p.Name : $"{p.Name} · {engine.Data.ServerName(p.Server)}";
                _charBox.Items.Add(new ComboBoxItem { Content = label });
            }
            _charBox.SelectedIndex = _charKeys.IndexOf(_charKey);
            _charBox.IsVisible = _charKeys.Count > 0;
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
            _buildBox.Items.Clear();
            _buildIds.Add("");
            _buildBox.Items.Add(new ComboBoxItem { Content = T.BuildNone });
            var sel = 0;
            foreach (var b in builds)
            {
                _buildIds.Add(b.Id);
                _buildBox.Items.Add(new ComboBoxItem { Content = b.Name.Length > 0 ? b.Name : b.Id });
                if (b.Id == activeId) sel = _buildIds.Count - 1;
            }
            _buildBox.SelectedIndex = sel;
            _deleteBuild.IsVisible = _copyQuestlog.IsVisible = _build is not null;
        }
        finally { _syncing = false; }
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTick.Stop();
        _saveTick.Start();
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
            _status.Text = ex.Message;
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
        _status.Text = T.BuildNoBuild;
        return false;
    }

    private void QueueTelemetry(Build saved) =>
        Telemetry.QueueBuild(Shell.Settings, saved, BuildEditor.ClassIdOf(_profile?.ClassName) ?? _classId, _profile?.Level ?? 0, _charKey);

    // asked once per skill; a missing icon is asked again only when the provider reports a download (RefreshIcons)
    private Avalonia.Media.Imaging.Bitmap? IconOf(SkillVm vm)
    {
        if (!vm.IconAsked) { vm.IconAsked = true; vm.Icon = FetchIcon(vm.Info); }
        return vm.Icon;
    }

    private Avalonia.Media.Imaging.Bitmap? FetchIcon(SkillInfo s) => s.Icon.Length > 0 ? Shell.Icons.Url("skill:" + s.Icon, SkillBook.IconUrl(s.Icon), 48) : null;

    private Avalonia.Media.Imaging.Bitmap? SpecIcon(SkillSpec sp) => sp.Icon is { Length: > 0 } stem ? Shell.Icons.Url("spec:" + stem, SkillBook.SpecIconUrl(stem), 48) : null;

    /// <summary>Icons that landed since the last draw: swapped into the existing tiles, nothing rebuilt.</summary>
    private void RefreshIcons()
    {
        var any = false;
        foreach (var vm in _vms.Values)
            if (vm.Icon is null && vm.IconAsked && (vm.Icon = FetchIcon(vm.Info)) is not null) any = true;
        if (any)
        {
            foreach (var t in _tiles) if (_vms.TryGetValue(t.SkillId, out var vm)) t.SetIcon(vm.Icon);
            if (_vms.TryGetValue(_detailTile.SkillId, out var d)) _detailTile.SetIcon(d.Icon);
        }
        foreach (var (img, spec) in _specIcons)
            if (img.Source is null && SpecIcon(spec) is { } src) { img.Source = src; img.IsVisible = true; }
        _pageDaev.IconsLanded();
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
        _noClass.IsVisible = library.Count == 0;

        _tiles.Clear();
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
        _sourceLine.Children.Clear();
        if (_build is not null)
        {
            _sourceLine.Children.Add(BuildUi.Muted(_build.Name, 12));
            if (_build.Source is { Id: > 0, Slug: { Length: > 0 } slug } s)
            {
                _sourceLine.Children.Add(BuildUi.Muted("  ·  ", 12));
                var url = QuestlogBuilds.BuildUrl(slug, s.Id, _lang);
                _sourceLine.Children.Add(BuildUi.Link(T.BuildOpenSource, () => BuildUi.OpenUrl(url)));
            }
            RenderCheck();
        }
        _missing.Text = progress is not null && _profile is not null
            ? string.Format(T.BuildMissing, progress.MissingSkills, progress.TotalSkills, progress.MissingLevels, progress.NotLearned) : "";
        _target.Children.Clear();
        var text = new StringBuilder();
        if (_build is not null)
            text.Append(string.Format(T.BuildTarget, _build.Source is { Id: > 0 } bs ? string.Format(T.BuildTargetQuestlog, bs.Id) : _build.Name)).Append("  ·  ");
        text.Append(_profile is not null ? string.Format(T.BuildLearned, _profile.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) : T.BuildNoProfile);
        _target.Children.Add(BuildUi.Muted(text.ToString(), 11.5));
        // the viewer never fetches: "refresh" re-reads the files the daemon writes at a login and every 6 h of play
        _target.Children.Add(Gap(BuildUi.Link(T.BuildRefresh, () => { FlushSave(); ReloadProfile(); Render(); _status.Text = ""; })));
        var stigmas = 0;
        if (_build is not null)
            foreach (var b in _build.Skills)
                if (b.Slot is not null && _vms.TryGetValue(b.SkillId, out var vm) && vm.Info.Sub == "stigma") stigmas++;
        _stigmaCount.Text = stigmas.ToString(CultureInfo.InvariantCulture) + "/" + BuildEditor.MaxSpecsStigma.ToString(CultureInfo.InvariantCulture);
    }

    private void RenderLibrary(IReadOnlyList<SkillInfo> library)
    {
        _rowActive.Children.Clear(); _rowStigma.Children.Clear(); _rowPassive.Children.Clear();
        foreach (var info in library)
        {
            var vm = _vms[info.Id];
            if (!_showDone && vm.B is not null && vm.P?.State == SkillState.AtTarget) continue;
            var t = NewTile(vm, "lib");
            t.Margin = new Thickness(0, 0, 6, 6);
            (info.Sub == "stigma" ? _rowStigma : info.Sub == "passive" ? _rowPassive : _rowActive).Children.Add(t);
        }
    }

    private void BuildBarCells()
    {
        for (var key = 1; key <= BuildEditor.Keys; key++)
            for (var row = 0; row < BuildEditor.Rows; row++)
            {
                var cell = new Border { Width = 48, Height = 48, Background = BuildColors.Cell, CornerRadius = new CornerRadius(6), Tag = key * 10 + row };
                Canvas.SetLeft(cell, (key - 1) * Step);
                Canvas.SetTop(cell, (BuildEditor.Rows - 1 - row) * Step);   // row 0 (first chain link) at the bottom, as questlog
                cell.PointerPressed += CellPressed;
                _bar.Children.Add(cell);
            }
        for (var i = 0; i < BuildEditor.Keys; i++)
        {
            var label = new TextBlock { Text = BuildEditor.KeyName(i + 1), FontSize = 10, Foreground = BuildColors.KeyText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var k = new Border { Width = 48, Height = 18, CornerRadius = new CornerRadius(4), Background = BuildColors.KeyBack, Child = label, Tag = i + 1, Cursor = new Cursor(StandardCursorType.Hand) };
            Canvas.SetLeft(k, i * Step);
            k.PointerPressed += (s, e) =>
            {
                if (s is not Border { Tag: int n }) return;
                e.Handled = true;
                _key = _key == n ? 0 : n;
                RenderKeys();
            };
            _keys[i] = k;
            _keyRow.Children.Add(k);
        }
    }

    private void RenderBar()
    {
        foreach (var t in _barTiles) _bar.Children.Remove(t);
        _barTiles.Clear();
        if (_build is null) return;
        foreach (var b in _build.Skills)
        {
            if (!BuildEditor.TryParseSlot(b.Slot, out var key, out var row) || !_vms.TryGetValue(b.SkillId, out var vm)) continue;
            var t = NewTile(vm, "bar");
            Canvas.SetLeft(t, (key - 1) * Step);
            Canvas.SetTop(t, (BuildEditor.Rows - 1 - row) * Step);
            _barTiles.Add(t);
            _bar.Children.Add(t);
        }
    }

    private void RenderKeys()
    {
        for (var i = 0; i < _keys.Length; i++)
        {
            var on = _key == i + 1;
            _keys[i].Background = on ? BuildColors.KeyOn : BuildColors.KeyBack;
            ((TextBlock)_keys[i].Child!).Foreground = on ? BuildColors.White : BuildColors.KeyText;
        }
    }

    private static SkillTile.Look LookOf(SkillVm vm, bool showDone) =>
        vm.B is null ? SkillTile.Look.Off : !showDone && vm.P?.State == SkillState.AtTarget ? SkillTile.Look.Done : SkillTile.Look.Normal;

    private static IBrush StateBrush(SkillProgress? p) => p?.State switch
    {
        SkillState.AtTarget => BuildColors.Green,
        SkillState.Near => BuildColors.Amber,
        _ => BuildColors.Bad,
    };

    private SkillTile NewTile(SkillVm vm, string where)
    {
        var t = new SkillTile { Tag = where, Cursor = new Cursor(StandardCursorType.Hand) };
        ApplyTile(t, vm, where != "prio" && vm.Info.Id == _selected);
        t.PointerPressed += TilePressed;
        t.PointerReleased += TileReleased;
        // the tooltip is built when it opens (it reads the note and the relations of that moment)
        ToolTip.SetTip(t, "");
        ToolTip.SetShowDelay(t, 250);
        ToolTip.SetPlacement(t, PlacementMode.Pointer);
        t.AddHandler(ToolTip.ToolTipOpeningEvent, (_, _) => { if (_vms.TryGetValue(t.SkillId, out var v)) ToolTip.SetTip(t, BuildUi.TipFrame(TipContent(v))); });
        _tiles.Add(t);
        return t;
    }

    private void ApplyTile(SkillTile t, SkillVm vm, bool selected, bool badges = true)
    {
        var b = vm.B;
        string? cur = null;
        if (b is not null && _profile is not null) cur = vm.P?.Current is int c ? c.ToString(CultureInfo.InvariantCulture) : "—";
        t.Set(vm.Info.Id, IconOf(vm), vm.Info.Sub, LookOf(vm, _showDone), b?.Specs.Count ?? 0, selected,
              b?.Level.ToString(CultureInfo.InvariantCulture), cur, StateBrush(vm.P), badges);
    }

    private void RefreshSelection()
    {
        foreach (var t in _tiles) t.SetSelected((string?)t.Tag != "prio" && t.SkillId == _selected);
        RenderKeys();
        RenderDetail();
    }

    private static string SubLabel(string sub) => sub == "stigma" ? T.BuildStigma : sub == "passive" ? T.BuildPassive : T.BuildActive;

    private void RenderDetail()
    {
        _specIcons.Clear();
        if (_selected == 0 || !_vms.TryGetValue(_selected, out var vm))
        {
            _detail.IsVisible = false; _detailHint.IsVisible = true;
            return;
        }
        _detail.IsVisible = true; _detailHint.IsVisible = false;
        var info = vm.Info; var b = vm.B;
        ApplyTile(_detailTile, vm, false);
        _detailName.Text = vm.Name;
        _detailSub.Text = SubLabel(info.Sub) + " · " + (info.DamageType ?? "physic");
        _equipMode = b is null ? EquipMode.Add : b.Slot is not null ? EquipMode.Unequip : info.Sub == "passive" ? EquipMode.Remove : EquipMode.Equip;
        _equip.Content = _equipMode switch { EquipMode.Add => T.BuildAddToBuild, EquipMode.Unequip => "⏏  " + T.BuildUnequip, EquipMode.Remove => T.BuildRemove, _ => T.BuildEquip };
        _remove.IsVisible = b is not null && _equipMode != EquipMode.Remove;
        var level = b?.Level ?? 1;
        BuildUi.SetRuns(_detailDesc, Desc(info, level));

        var inBuild = b is not null;
        _levelBlock.IsVisible = _level.IsVisible = _ticks.IsVisible = inBuild;
        _levelValue.Text = level.ToString(CultureInfo.InvariantCulture);
        var max = Math.Min(info.MaxLevel, Math.Max(SliderMax, level));
        _syncing = true;
        _level.Maximum = max;
        _level.Value = level;
        _syncing = false;
        if (_ticks.Children.Count != max)
        {
            _ticks.Children.Clear();
            _ticks.Columns = max;
            for (var i = 0; i < max; i++) _ticks.Children.Add(new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        }
        for (var i = 0; i < _ticks.Children.Count; i++)
        {
            var on = i + 1 == level;
            var tb = (TextBlock)_ticks.Children[i];
            tb.Foreground = on ? BuildColors.White : BuildColors.Dim;
            tb.FontWeight = on ? FontWeight.Bold : FontWeight.Normal;
        }

        var cap = BuildEditor.SpecCap(info);
        _specCount.Text = (b?.Specs.Count ?? 0).ToString(CultureInfo.InvariantCulture) + "/" + cap.ToString(CultureInfo.InvariantCulture);
        _specList.Children.Clear();
        foreach (var sp in info.Specs)
        {
            var on = b?.Specs.Contains(sp.Id) == true;
            var locked = sp.RequiredLevel > level;
            var marker = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Background = on ? BuildColors.Blue : BuildColors.Marker, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = on ? "✓" : "◆", FontSize = 12, Foreground = on ? BuildColors.Black : BuildColors.MarkerText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var icon = new Image { Width = 26, Height = 26, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Source = SpecIcon(sp) };
            icon.IsVisible = icon.Source is not null;
            if (sp.Icon is not null) _specIcons.Add((icon, sp));
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = sp.Name(_lang), FontSize = 13, Foreground = BuildColors.Text, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = string.Format(T.BuildSpecReq, sp.RequiredLevel), FontSize = 11, Foreground = BuildColors.Mute });
            var row = UiKit.Row("Auto,Auto,*", marker, icon, text);
            var border = new Border
            {
                Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0.8), Margin = new Thickness(0, 0, 0, 4),
                Background = on ? BuildColors.SpecOnBack : Brushes.Transparent, BorderBrush = on ? BuildColors.SpecOnLine : Brushes.Transparent, Opacity = locked ? 0.45 : 1,
                Child = row, Tag = sp.Id, Cursor = b is null ? Cursor.Default : new Cursor(StandardCursorType.Hand),
            };
            if (locked) UiKit.Tip(border, string.Format(T.BuildSpecLocked, sp.RequiredLevel));
            border.PointerPressed += SpecPressed;
            _specList.Children.Add(border);
        }

        _noteBlock.IsVisible = _build is not null;
        var note = _build is not null && _build.Notes.TryGetValue(info.Id, out var n) ? n : "";
        if ((_note.Text ?? "") != note) { _syncing = true; _note.Text = note; _syncing = false; }   // equal text untouched: the caret stays while typing
        _noteCount.Text = note.Length.ToString(CultureInfo.InvariantCulture) + "/600";
        _relList.Children.Clear();
        foreach (var r in Relations.For(info.Id)) _relList.Children.Add(RelationLine(r, 12));
        _relBlock.IsVisible = _relList.Children.Count > 0;
    }

    /// <summary>One compact relation line: a green arrow for what the skill gives, blue for what it takes, grey for chain/priority.</summary>
    private static TextBlock RelationLine(SkillRelation r, double size)
    {
        var (glyph, brush) = r.Kind switch
        {
            RelationKind.Grants or RelationKind.Applies or RelationKind.Shortens or RelationKind.Boosts or RelationKind.ChainSkill => ("→ ", BuildColors.Green),
            RelationKind.Consumes or RelationKind.Needs or RelationKind.ShortenedBy or RelationKind.BoostedBy => ("← ", BuildColors.Blue),
            _ => ("· ", BuildColors.Mute),
        };
        var tb = new TextBlock { FontSize = size, Foreground = BuildColors.Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2), Inlines = new Avalonia.Controls.Documents.InlineCollection() };
        tb.Inlines.Add(new Avalonia.Controls.Documents.Run(glyph) { Foreground = brush, FontWeight = FontWeight.Bold });
        tb.Inlines.Add(new Avalonia.Controls.Documents.Run(r.Text));
        return tb;
    }

    private void RenderPriority()
    {
        foreach (var (cat, panel) in new[] { ("active", _prioActive), ("stigma", _prioStigma), ("passive", _prioPassive) })
        {
            panel.Children.Clear();
            if (_build is not null)
                foreach (var id in _build.Priority)
                {
                    if (!_vms.TryGetValue(id, out var vm) || vm.Info.Sub != cat) continue;
                    if (panel.Children.Count > 0) panel.Children.Add(new TextBlock { Text = "›", FontSize = 12, Foreground = BuildColors.Gt, Margin = new Thickness(2, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center });
                    var t = NewTile(vm, "prio");
                    t.Margin = new Thickness(0, 0, 4, 4);
                    panel.Children.Add(t);
                }
            var add = new Grid { Width = 48, Height = 48, Margin = new Thickness(panel.Children.Count > 0 ? 6 : 0, 0, 0, 4), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Tag = cat };
            add.Children.Add(new Rectangle { Stroke = BuildColors.AddLine, StrokeThickness = 0.8, StrokeDashArray = [4, 3], RadiusX = 6, RadiusY = 6 });
            add.Children.Add(new TextBlock { Text = "+", FontSize = 18, Foreground = BuildColors.AddText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            add.PointerPressed += (s, e) => { e.Handled = true; AddPriority(add, cat); };
            panel.Children.Add(add);
        }
    }

    private void RenderMacros()
    {
        _macroList.Children.Clear();
        if (_build is not null)
            for (var i = 0; i < _build.Macros.Count; i++)
            {
                var m = _build.Macros[i];
                var sb = new StringBuilder(m.Name).Append(" → ");
                for (var j = 0; j < m.Steps.Count; j++) { if (j > 0) sb.Append(" → "); sb.Append(_vms.TryGetValue(m.Steps[j], out var vm) ? vm.Name : m.Steps[j].ToString(CultureInfo.InvariantCulture)); }
                var index = i;
                var del = UiKit.IconButton(Visuals.IconClose, T.BuildRemoveItem, () => { if (_build is not null) Edit(BuildEditor.RemoveMacro(_build, index)); }, 20);
                del.Margin = new Thickness(6, 0, 0, 0);
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = sb.ToString(), FontSize = 13, Foreground = BuildColors.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
                sp.Children.Add(del);
                _macroList.Children.Add(new Border { Background = BuildColors.Btn, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 3, 4, 3), Margin = new Thickness(0, 0, 8, 6), Child = sp });
            }
        if (_macroList.Children.Count == 0) _macroList.Children.Add(new TextBlock { Text = T.BuildNoMacros, FontSize = 13, Foreground = BuildColors.Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) });
        var addBtn = BuildUi.TextBtn(T.BuildAddMacro, AddMacro, 13, BuildColors.AddText);
        addBtn.Margin = new Thickness(4, 0, 0, 6);
        _macroList.Children.Add(addBtn);
        _macroStepsText.Text = StepsText();
    }

    private void RenderDescription()
    {
        var html = _build?.Description;
        if (!ReferenceEquals(html, _descHtml.Html)) _descHtml = (html, BuildText.HtmlToText(html));
        _descText.Text = _descHtml.Text.Length > 0 ? _descHtml.Text : T.BuildNoDescription;
        _descText.IsVisible = _descOpen;
        _descChevron.RenderTransform = _descOpen ? new RotateTransform(180) : null;
    }

    private void RenderSaved() =>
        _saved.Text = "· " + (_build is null || _build.UpdatedAt == default ? T.BuildNotSaved : string.Format(T.BuildSaved, _build.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));

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

    private Control TipContent(SkillVm vm)
    {
        var info = vm.Info; var b = vm.B;
        var root = new StackPanel();
        var icon = new SkillTile { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        ApplyTile(icon, vm, false, badges: false);
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = vm.Name, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = BuildColors.Text, TextWrapping = TextWrapping.Wrap });
        names.Children.Add(new TextBlock { Text = info.Name(_lang == "es" ? "en" : "es"), FontSize = 11, Foreground = BuildColors.Mute });
        var tags = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        tags.Children.Add(BuildUi.TagChip(info.Sub));
        if (info.DamageType is { Length: > 0 } dmg) tags.Children.Add(BuildUi.TagChip(dmg));
        names.Children.Add(tags);
        root.Children.Add(UiKit.Row("Auto,*", icon, names));

        var level = b?.Level ?? 1;
        var desc = new TextBlock { FontSize = 12, Foreground = BuildColors.Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 18.6, Margin = new Thickness(0, 8) };
        BuildUi.SetRuns(desc, Desc(info, level));
        root.Children.Add(desc);

        if (b is not null)
        {
            var cmp = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var cur = vm.P?.Current;
            var curText = _profile is null ? "—" : cur is int c ? c.ToString(CultureInfo.InvariantCulture) : T.BuildTipNotLearned;
            var key = BuildEditor.KeyOf(b.Slot);
            var specs = new StringBuilder();
            foreach (var id in b.Specs) if (BuildEditor.Spec(info, id) is { } sp) { if (specs.Length > 0) specs.Append(" · "); specs.Append(sp.Name(_lang)); }
            CmpRow(cmp, 0, _profile is not null ? string.Format(T.BuildTipNow, _profile.Ref.Name) : T.BuildTipNowAnon, curText, _profile is null ? BuildColors.Mute : StateBrush(vm.P), true);
            CmpRow(cmp, 1, T.BuildTipBuild, b.Level.ToString(CultureInfo.InvariantCulture) + (key > 0 ? " · " + string.Format(T.BuildTipKey, BuildEditor.KeyName(key)) : ""), BuildColors.Text, false);
            CmpRow(cmp, 2, T.BuildTipSpecs, specs.Length > 0 ? specs.ToString() : T.BuildTipNone, BuildColors.Text, false);
            root.Children.Add(new Border { Background = BuildColors.CmpBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6), Margin = new Thickness(0, 0, 0, 8), Child = cmp });
            if (cur is int now && now != b.Level)
            {
                root.Children.Add(BuildUi.TipSection(string.Format(T.BuildTipAtLevel, now)));
                var at = new TextBlock { FontSize = 12, Foreground = BuildColors.Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                BuildUi.SetRuns(at, BuildText.FirstLine(Desc(info, now)));
                root.Children.Add(at);
            }
        }
        if (_build is not null && _build.Notes.TryGetValue(info.Id, out var note) && note.Length > 0)
        {
            root.Children.Add(BuildUi.TipSection(T.BuildTipNotes));
            root.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = BuildColors.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 17.4 });
        }
        var rels = Relations.For(info.Id);
        if (rels.Count > 0)
        {
            root.Children.Add(BuildUi.TipSection(T.BuildRelations));
            foreach (var r in rels) root.Children.Add(RelationLine(r, 11.5));
        }
        return root;
    }

    private static void CmpRow(Grid g, int row, string label, string value, IBrush valueBrush, bool bold)
    {
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        g.Place(new TextBlock { Text = label, FontSize = 12, Foreground = BuildColors.Mute, Margin = new Thickness(0, 1, 10, 1) }, row, 0);
        g.Place(new TextBlock { Text = value, FontSize = 12, Foreground = valueBrush, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1) }, row, 1);
    }

    // ------------------------------------------------------------------ input

    private void Select(int id)
    {
        _selected = id;
        if (_build?.Skill(id) is { Slot: { } slot }) _key = BuildEditor.KeyOf(slot);
        RefreshSelection();
    }

    private void TilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not SkillTile t || !_vms.TryGetValue(t.SkillId, out var vm)) return;
        var props = e.GetCurrentPoint(t).Properties;
        if (!props.IsLeftButtonPressed) return;
        e.Handled = true;
        var where = (string?)t.Tag;
        if (e.ClickCount == 2)
        {
            if (where == "bar") { if (NeedBuild()) Edit(BuildEditor.Unequip(_build!, vm.Info.Id)); }
            else if (where == "lib") EquipSkill(vm);
            return;
        }
        Select(vm.Info.Id);
    }

    private void TileReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || sender is not SkillTile t || _build is null) return;
        e.Handled = true;
        var id = t.SkillId;
        if ((string?)t.Tag == "bar") { Edit(BuildEditor.Unequip(_build, id)); return; }
        if ((string?)t.Tag != "prio") return;
        var menu = new MenuFlyout { Placement = PlacementMode.Pointer };
        menu.Items.Add(Item(T.BuildMoveLeft, () => Edit(BuildEditor.MovePriority(_build, id, -1, Category))));
        menu.Items.Add(Item(T.BuildMoveRight, () => Edit(BuildEditor.MovePriority(_build, id, 1, Category))));
        menu.Items.Add(Item(T.BuildRemoveItem, () => Edit(BuildEditor.RemovePriority(_build, id))));
        menu.ShowAt(t);
    }

    private string? Category(int id) => _vms.TryGetValue(id, out var vm) ? vm.Info.Sub : Shell.Engine?.Skills.Get(id)?.Sub;

    private static MenuItem Item(object header, Action click)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => click();
        return mi;
    }

    /// <summary>Double-click in the library: onto the highlighted key (key 1 when none); passives only join the build.</summary>
    private void EquipSkill(SkillVm vm)
    {
        if (!NeedBuild()) return;
        if (vm.Info.Sub == "passive") { Edit(BuildEditor.AddSkill(_build!, vm.Info.Id)); return; }
        var key = _key > 0 ? _key : 1;
        _selected = vm.Info.Id;
        if (!Edit(BuildEditor.Equip(_build!, vm.Info, key)) && BuildEditor.KeyOf(_build!.Skill(vm.Info.Id)?.Slot) != key) _status.Text = string.Format(T.BuildKeyFull, key);
        else { _key = key; RenderKeys(); }
    }

    private void CellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: int tag } cell || !e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        int key = tag / 10, row = tag % 10;
        if (_selected != 0 && _build is not null && _vms.TryGetValue(_selected, out var vm) && vm.Info.Sub != "passive")
        {
            if (Edit(BuildEditor.MoveTo(_build, vm.Info, key, row))) { _key = key; RenderKeys(); }
            return;
        }
        _key = key;
        RenderKeys();
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
        var text = _note.Text ?? "";
        var has = _build.Notes.TryGetValue(_selected, out var cur);
        var empty = string.IsNullOrWhiteSpace(text);
        _noteCount.Text = text.Length.ToString(CultureInfo.InvariantCulture) + "/600";
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

    private void SpecPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: int specId } || _build is null || !_vms.TryGetValue(_selected, out var vm)) return;
        e.Handled = true;
        var next = BuildEditor.ToggleSpec(_build, vm.Info, specId, out var r);
        if (r == SpecToggle.Locked) _status.Text = string.Format(T.BuildSpecLocked, BuildEditor.Spec(vm.Info, specId)?.RequiredLevel ?? 0);
        else if (r == SpecToggle.Full) _status.Text = string.Format(T.BuildSpecFull, BuildEditor.SpecCap(vm.Info));
        else { _status.Text = ""; Edit(next); }
    }

    private void AddPriority(Control anchor, string cat)
    {
        if (!NeedBuild()) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var s in _build!.Skills)
        {
            if (_build.Priority.Contains(s.SkillId) || !_vms.TryGetValue(s.SkillId, out var vm) || vm.Info.Sub != cat) continue;
            var id = s.SkillId;
            menu.Items.Add(Item(SkillHeader(vm), () => Edit(BuildEditor.AddPriority(_build, id))));
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = T.BuildNothingToAdd, IsEnabled = false });
        menu.ShowAt(anchor);
    }

    private Control SkillHeader(SkillVm vm)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Image { Source = IconOf(vm), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) });
        sp.Children.Add(new TextBlock { Text = vm.Name, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void ClearPriority()
    {
        if (!NeedBuild() || _build!.Priority.Count == 0) return;
        BuildUi.Confirm(_prioActive, T.BuildClearConfirm, () => { if (_build is not null) Edit(_build with { Priority = [] }); });
    }

    private void AddMacro()
    {
        if (!NeedBuild()) return;
        _macroSteps.Clear();
        _macroName.Text = "";
        _macroStepsText.Text = "";
        _macroEditor.IsVisible = true;
        _macroName.Focus();
    }

    private void MacroCancel() { _macroSteps.Clear(); _macroEditor.IsVisible = false; }

    private void MacroAddStep(Control anchor)
    {
        if (_build is null) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var s in _build.Skills)
        {
            if (!_vms.TryGetValue(s.SkillId, out var vm) || vm.Info.Sub == "passive") continue;
            var id = s.SkillId;
            menu.Items.Add(Item(SkillHeader(vm), () => { _macroSteps.Add(id); _macroStepsText.Text = StepsText(); }));
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = T.BuildNothingToAdd, IsEnabled = false });
        menu.ShowAt(anchor);
    }

    private void MacroSave()
    {
        if (_build is null || _macroSteps.Count == 0) return;
        var name = _macroName.Text?.Trim() ?? "";
        if (name.Length == 0) name = "Macro " + (_build.Macros.Count + 1).ToString(CultureInfo.InvariantCulture);
        _macroEditor.IsVisible = false;
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
        Shell.Settings.PickNames(lang);
        Shell.SaveSettings();
        Render();
    }

    private void BuildBoxChanged()
    {
        if (_syncing || _buildBox.SelectedIndex is not (>= 0 and var i) || i >= _buildIds.Count) return;
        var id = _buildIds[i];
        FlushSave();
        try { _store.ActiveId = id.Length > 0 ? id : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
        _build = id.Length > 0 ? _store.Load(id) : null;
        _selected = 0;
        _deleteBuild.IsVisible = _copyQuestlog.IsVisible = _build is not null;
        Render();
    }

    private void NewFromProfile() => _status.Text = CreateFromProfile(out var error) ? "" : error;

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
        BuildUi.Confirm(_deleteBuild, string.Format(T.BuildDeleteConfirm, _build.Name), () =>
        {
            if (_build is null) return;
            _saveTick.Stop(); _dirty = false;
            try { _store.Delete(_build.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; return; }
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
        _status.Text = string.Format(T.BuildImporting, id);
        try
        {
            var b = await QuestlogService.FetchAsync(slug, id);
            if (b is null) _status.Text = string.Format(T.BuildImportNotFound, id);
            else
            {
                if (fromLink && QuestlogService.LearnSlug(Shell.Settings, slug)) Shell.SaveSettings();
                SaveImported(b);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"Build import {slug}/{id} failed: {ex.Message}");
            _status.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Build save", ex); _status.Text = ex.Message; return null; }
        Log.Info($"Build imported from questlog: {saved.Source?.Id} → {saved.Id} ({saved.ClassId}, {saved.Skills.Count} skills) for {_charKey}");
        QueueTelemetry(saved);
        _selected = 0;
        Reload();
        _status.Text = _classId.Length > 0 && saved.ClassId.Length > 0 && saved.ClassId != _classId
            ? string.Format(T.BuildImportedOtherClass, saved.Name, T.ClassNameOf(saved.ClassId), T.ClassNameOf(_classId))
            : string.Format(T.BuildImported, saved.Name);
        return saved;
    }

    /// <summary>A skill build of the user's own list (already complete, nothing fetched) plus the planner build paired with it, reported on one status line.</summary>
    private void ImportMine(Build build, QuestlogDaevPlan? paired, string? slug)
    {
        if (SaveImported(build) is null || paired is null) return;
        var status = _status.Text;
        var (ok, msg) = ImportPlan(paired, slug ?? build.Source?.Slug);
        _status.Text = status + (ok ? string.Format(T.BuildPlanPaired, paired.Name) : "; " + msg);
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

    private void RenderCheck()
    {
        if (_build is null) return;
        if (_build.Source is not { Slug: { Length: > 0 } } && _build.PlanSource is not { Slug: { Length: > 0 } }) return;
        _sourceLine.Children.Add(BuildUi.Muted("  ·  ", 12));
        var c = _check is { } k && k.BuildId == _build.Id ? k : null;
        if (c is { Note: { } note }) _sourceLine.Children.Add(BuildUi.Muted(note + "  ", 12));
        if (c is not null && (c.Skill is not null || c.Plan is not null))
        {
            var newest = Nullable.Compare(c.Skill?.UpdatedAt, c.Plan?.UpdatedAt) >= 0 ? c.Skill?.UpdatedAt : c.Plan?.UpdatedAt;
            _sourceLine.Children.Add(BuildUi.Muted(string.Format(T.BuildQuestlogNewer, newest?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?") + " — ", 12, BuildColors.Amber));
            _sourceLine.Children.Add(BuildUi.Link(T.BuildQuestlogUpdate, QuestlogUpdate, T.BuildQuestlogUpdateTip));
            return;
        }
        _sourceLine.Children.Add(BuildUi.Link(_checking ? T.BuildChecking : T.BuildCheckQuestlog, () => _ = CheckQuestlogAsync(), T.BuildCheckQuestlogTip));
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
        _status.Text = T.BuildChecking;
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
            _status.Text = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"questlog check of build {b.Id} failed: {ex.Message}");
            _status.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex));
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
        _status.Text = string.Format(T.BuildUpdatedFromQuestlog, string.Join(" + ", parts));
    }

    private async void CopyQuestlog()
    {
        if (!NeedBuild()) return;
        FlushSave();
        _status.Text = await BuildUi.CopyText(this, QuestlogExport.ToJson(_build!)) ? T.BuildCopied : T.BuildCopyFailed;
    }

    // ------------------------------------------------------------------ suggestions

    private void RenderSuggestions()
    {
        var show = _build is null;
        _suggestCard.IsVisible = show;
        _buildCards.IsVisible = !show;
        if (!show) return;
        _suggestTitle.Text = string.Format(T.BuildSuggestTitle, _classId.Length > 0 ? T.ClassNameOf(_classId) : "?");
        if (_classId.Length == 0) { _suggestFor = ""; SuggestMessage(T.BuildSuggestNoClass); return; }
        var slug = Shell.Settings.QuestlogSlug ?? "";
        if (QuestlogService.CachedSuggestions(_classId) is { } hit && (slug.Length == 0 || QuestlogService.CachedMine(slug) is not null))
        {
            _suggestFor = _classId;
            FillSuggestions(hit, MineOf(slug, _classId));
            return;
        }
        if (_suggestFor == _classId && _suggestList.Children.Count > 0) return;   // already loading for this class
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
            if (mine is { Count: > 0 } && _build is null) { FillSuggestions([], mine); _suggestList.Children.Add(BuildUi.Muted(T.BuildSuggestFailed)); }
            else SuggestMessage(T.BuildSuggestFailed);
            _suggestFor = "";
        }
    }

    /// <summary>The user's questlog skill builds of this class from the session cache, at most 5; null without a slug or a cached list.</summary>
    private static List<BuildSummary>? MineOf(string slug, string classId) =>
        slug.Length > 0 && QuestlogService.CachedMine(slug) is { } m ? QuestlogMine.OfClass(m.Skills, static b => b.ClassId, classId, BuildSuggestions.Take) : null;

    private void SuggestMessage(string text)
    {
        _suggestList.Children.Clear();
        var t = BuildUi.Muted(text);
        t.Margin = new Thickness(0, 0, 0, 6);
        _suggestList.Children.Add(t);
    }

    private void FillSuggestions(List<BuildSummary> list, List<BuildSummary>? mine)
    {
        _suggestList.Children.Clear();
        if (mine is { Count: > 0 })
        {
            _suggestList.Children.Add(SuggestHeader(T.BuildSuggestMine));
            foreach (var b in mine) _suggestList.Children.Add(ImportBuildWindow.Row(b, AddMineSuggestion, BuildColors.CmpBack, BuildColors.TagBack, BuildColors.Text, BuildColors.Mute, BuildColors.TagBack, true));
            _suggestList.Children.Add(SuggestHeader(T.BuildSuggestTop));
        }
        if (list.Count == 0) { _suggestList.Children.Add(BuildUi.Muted(T.BuildSuggestEmpty)); return; }
        foreach (var b in list) _suggestList.Children.Add(ImportBuildWindow.Row(b, AddSuggestion, BuildColors.CmpBack, BuildColors.TagBack, BuildColors.Text, BuildColors.Mute, BuildColors.TagBack, true));
    }

    private static TextBlock SuggestHeader(string text) => new() { Text = text.ToUpper(T.Culture), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = BuildColors.Dim, Margin = new Thickness(0, 4, 0, 6) };

    private void AddMineSuggestion(BuildSummary s)
    {
        var slug = Shell.Settings.QuestlogSlug ?? "";
        var plans = QuestlogService.CachedMine(slug)?.Plans;
        ImportMine(s.Build, plans is null ? null : QuestlogMine.PairPlan(s.Name, s.ClassId, plans), slug);
    }

    private void AddSuggestion(BuildSummary s)
    {
        if (s.AuthorSlug is { Length: > 0 } slug) _ = ImportAsync(slug, s.Id);
        else _status.Text = string.Format(T.BuildImportNotFound, s.Id);
    }
}
