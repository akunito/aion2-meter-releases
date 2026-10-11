using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.App.Windows;
using AionMeter.Core.Armory;
using AionMeter.Core.Builds;

namespace AionMeter.App.Views;

/// <summary>
/// Build tab (A2M-21): the character's skill build in questlog's planner look — class skill library, 12×4 key bar, detail panel with
/// level and specs, priority chains, macros — against the official profile (current level badge per icon, Missing summary). Edits
/// are local and saved 500 ms after the last one through <see cref="BuildStore"/>. The Daevanion sub-tab is <see cref="DaevanionView"/>.
/// Phase 3: questlog import (link, search dialog, suggestions card when no build is active), "Copy for questlog", build telemetry.
/// </summary>
public partial class BuildView : UserControl
{
    private static UiText T => UiText.Current;
    private static readonly Brush Green = SkillTile.Frozen(0x3D, 0xDC, 0x84), Amber = SkillTile.Frozen(0xF5, 0xB5, 0x44), Bad = SkillTile.Frozen(0xFF, 0x5C, 0x5C),
        Num = SkillTile.Frozen(0xFC, 0xC7, 0x8B), Mute = SkillTile.Frozen(0x7D, 0x7D, 0x7D), Dim = SkillTile.Frozen(0xAF, 0xAF, 0xAF), Text = SkillTile.Frozen(0xFA, 0xFA, 0xFA),
        Blue = SkillTile.Frozen(0x65, 0xB0, 0xFC), KeyBack = SkillTile.Frozen(0x1F, 0x1F, 0x1F), KeyOn = SkillTile.Frozen(0x2F, 0x5F, 0x8F), KeyText = SkillTile.Frozen(0xDC, 0xDC, 0xDC),
        Cell = SkillTile.Frozen(0x1A, 0x1A, 0x1A), SpecOnBack = SkillTile.Frozen(0x65, 0xB0, 0xFC, 0x1A), SpecOnLine = SkillTile.Frozen(0x65, 0xB0, 0xFC, 0x59),
        Marker = SkillTile.Frozen(0x3A, 0x3A, 0x3A), MarkerText = SkillTile.Frozen(0xCC, 0xCC, 0xCC), Black = SkillTile.Frozen(0, 0, 0), TagBack = SkillTile.Frozen(0x2B, 0x2B, 0x2B),
        CmpBack = SkillTile.Frozen(0x1F, 0x1F, 0x1F), Gt = SkillTile.Frozen(0x55, 0x55, 0x55), AddLine = SkillTile.Frozen(0x44, 0x44, 0x44), AddText = SkillTile.Frozen(0x88, 0x88, 0x88),
        Chip = SkillTile.Frozen(0x2B, 0x2B, 0x2B), White = SkillTile.Frozen(0xFF, 0xFF, 0xFF);
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    /// <summary>The level slider spans 1..20 as questlog's planner does; it reaches further only for a build level above 20, never past the catalogue max.</summary>
    public const int SliderMax = 20;

    private sealed class SkillVm(SkillInfo info)
    {
        public SkillInfo Info { get; } = info;
        public BuildSkill? B;
        public SkillProgress? P;
        public ImageSource? Icon;
        public bool IconAsked;
        public string Name = "";
    }

    private readonly record struct Seg(string Text, bool Num);

    private readonly MeterService _meter;
    private readonly IconCache _icons;
    private BuildStore _store;
    private Build? _build;                       // what is shown; edits replace it and the save follows 500 ms later
    private CharacterProfile? _profile;
    private string _classId = "", _vmClass = "", _lang;
    private bool _sample, _dirty, _syncing, _stale, _showDone, _descOpen, _importing;
    private string _suggestFor = "";            // class whose suggestions the card shows (or is loading)
    private List<BuildSummary>? _sampleSuggestions;
    private int _selected, _key;
    private readonly Dictionary<int, SkillVm> _vms = new();
    private readonly Dictionary<int, SkillProgress> _prog = new();
    private readonly List<SkillTile> _tiles = new();
    private readonly List<SkillTile> _barTiles = new();
    private readonly List<(Image Img, SkillSpec Spec)> _specIcons = new();
    private readonly Dictionary<(int Id, int Level, string Lang), Seg[]> _descCache = new();
    private (string? Html, string Text) _descHtml = (null, "");
    private readonly Border[] _keys = new Border[BuildEditor.Keys];
    private TextBlock[] _ticks = [];
    private readonly List<int> _macroSteps = new();
    private readonly SkillTile _detailTile = new();
    private SkillRelationCache? _relCache;
    private readonly DispatcherTimer _saveTick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // icons land one per 400 ms: refresh at most every 600 ms instead of per icon (as Craft)
    private readonly DispatcherTimer _iconTick = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private enum EquipMode { Add, Equip, Unequip, Remove }
    private EquipMode _equipMode;

    public event Action? CloseRequested;

    public BuildView(MeterService meter, IconCache? icons = null)
    {
        _meter = meter;
        _icons = icons ?? meter.ItemIconCache;
        _store = meter.Builds;
        _lang = meter.Settings.SkillLanguage == "es" ? "es" : "en";
        _showDone = meter.Settings.BuildShowDone;
        InitializeComponent();
        PageDaev.Attach(meter, _icons);
        // Daevanion edits ride on this view's build and its 500 ms save (the page only hands over the new plan)
        PageDaev.PlanImported += (src, name) => { if (_build is null) return; _build = _build with { PlanSource = src, PlanName = name }; _dirty = true; if (!_sample) { _saveTick.Stop(); _saveTick.Start(); } };
        PageDaev.PlanEdited += plan => { if (_build is null) return; _build = _build with { Daevanion = plan }; _dirty = true; if (!_sample) { _saveTick.Stop(); _saveTick.Start(); } };
        _syncing = true;
        LangBox.SelectedIndex = _lang == "es" ? 1 : 0;
        ShowDone.IsChecked = _showDone;
        _syncing = false;
        LblActive.Text = PrioActiveLbl.Text = T.BuildActive.ToUpper(T.Culture);
        LblStigma.Text = PrioStigmaLbl.Text = T.BuildStigma.ToUpper(T.Culture);
        LblPassive.Text = PrioPassiveLbl.Text = T.BuildPassive.ToUpper(T.Culture);
        DetailIconHost.Children.Add(_detailTile);
        BuildBarCells();
        _saveTick.Tick += (_, _) => FlushSave();
        _iconTick.Tick += (_, _) => { _iconTick.Stop(); RefreshIcons(); };
        Loaded += (_, _) =>
        {
            if (_sample) return;
            Reload();
            _meter.CharacterChanged += OnCharacterChanged; _icons.Loaded += OnIcon;
        };
        Unloaded += (_, _) =>
        {
            if (_sample) return;
            FlushSave();
            _meter.CharacterChanged -= OnCharacterChanged; _icons.Loaded -= OnIcon; _iconTick.Stop();
        };
        // hidden tab: nothing redraws; coming back re-reads the store and the profile (a sync may have landed meanwhile)
        IsVisibleChanged += (_, e) =>
        {
            if (_sample) return;
            if (e.NewValue is true) { if (_stale || !_dirty) { _stale = false; Reload(); } }
            else FlushSave();
        };
    }

    // ------------------------------------------------------------------ data

    /// <summary>Offscreen render (SampleRenderer "build"): this build and profile in memory, nothing read from or written to disk.</summary>
    public void ShowSample(Build build, CharacterProfile? profile, int selected)
    {
        _sample = true;
        _build = build; _profile = profile; _selected = selected;
        _key = BuildEditor.KeyOf(build.Skill(selected)?.Slot);
        FillPicker([build], build.Id);
        Render();
    }

    /// <summary>Offscreen render (SampleRenderer "build --tab suggest"): no active build, these suggestions in the card.</summary>
    public void ShowSampleSuggestions(CharacterProfile? profile, List<BuildSummary> suggestions)
    {
        _sample = true;
        _build = null; _profile = profile; _selected = 0;
        _sampleSuggestions = suggestions;
        FillPicker([], null);
        Render();
    }

    /// <summary>Re-draws from the current state (the render tool calls it around its layout pass).</summary>
    public void RenderNow() => Render();

    /// <summary>Render tool: the "show skills already at the build level" box unticked.</summary>
    public void SetShowDone(bool show) { _showDone = show; _syncing = true; ShowDone.IsChecked = show; _syncing = false; }

    /// <summary>Render tool: one skill's tooltip as a plain element, framed as the ToolTip template frames it.</summary>
    public FrameworkElement TipPreview(int skillId) => new Border
    {
        Background = SkillTile.Frozen(0x14, 0x14, 0x14), BorderBrush = SkillTile.Frozen(0x37, 0x37, 0x37), BorderThickness = new Thickness(0.8), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10), Width = 340, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
        Child = _vms.TryGetValue(skillId, out var vm) ? TipContent(vm) : new TextBlock { Text = "?" },
    };

    private void OnCharacterChanged() => Dispatcher.BeginInvoke(() => { if (IsVisible) Reload(); else _stale = true; });
    private void OnIcon() { if (!_iconTick.IsEnabled) _iconTick.Start(); }

    private void Reload()
    {
        if (_sample) { Render(); return; }
        FlushSave();                              // into the store it was edited for, before a character switch rebinds it
        _store = _meter.Builds;
        _profile = _meter.CurrentProfile;
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
            var items = new List<ComboBoxItem>(builds.Count + 1) { new() { Content = T.BuildNone, Tag = "" } };
            ComboBoxItem? sel = items[0];
            foreach (var b in builds)
            {
                var it = new ComboBoxItem { Content = b.Name.Length > 0 ? b.Name : b.Id, Tag = b.Id };
                items.Add(it);
                if (b.Id == activeId) sel = it;
            }
            BuildBox.ItemsSource = items;
            BuildBox.SelectedItem = sel;
            DeleteBuild.Visibility = _build is null || _sample ? Visibility.Collapsed : Visibility.Visible;
            CopyQuestlog.Visibility = _build is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _syncing = false; }
    }

    private void FlushSave()
    {
        _saveTick.Stop();
        if (!_dirty || _build is null || _sample) return;
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
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>One edit: the new build is shown at once and saved 500 ms after the last edit. False when the edit was refused (same reference back).</summary>
    private bool Edit(Build next)
    {
        if (_build is null || ReferenceEquals(next, _build)) return false;
        _build = next;
        _dirty = true;
        if (!_sample) { _saveTick.Stop(); _saveTick.Start(); }
        Render();
        return true;
    }

    private bool NeedBuild()
    {
        if (_build is not null) return true;
        StatusText.Text = T.BuildNoBuild;
        return false;
    }

    // asked once per skill; a missing icon is asked again only when the cache reports a download (RefreshIcons)
    private ImageSource? IconOf(SkillVm vm)
    {
        if (!vm.IconAsked) { vm.IconAsked = true; vm.Icon = FetchIcon(vm.Info); }
        return vm.Icon;
    }

    private ImageSource? FetchIcon(SkillInfo s) => s.Icon.Length > 0 ? _icons.GetUrl("skill:" + s.Icon, SkillBook.IconUrl(s.Icon), 48) : null;

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
            if (img.Source is null && SpecIcon(spec) is { } src) { img.Source = src; img.Visibility = Visibility.Visible; }
    }

    /// <summary>Relations for the shown build: recomputed only after an edit (a new Build), a class or a language change.</summary>
    private SkillRelationSet Relations => (_relCache ??= new SkillRelationCache(_meter.Skills)).Get(_classId, _build, _lang);

    private ImageSource? SpecIcon(SkillSpec sp) => sp.Icon is { Length: > 0 } stem ? _icons.GetUrl("spec:" + stem, SkillBook.SpecIconUrl(stem), 48) : null;

    // ------------------------------------------------------------------ render

    private void Render()
    {
        _classId = BuildEditor.ClassIdOf(_profile?.ClassName) ?? _build?.ClassId ?? "";
        if (_classId != _vmClass) { _vms.Clear(); _vmClass = _classId; }
        var library = _classId.Length > 0 ? _meter.Skills.ForClass(_classId) : [];
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
        NoClassText.Visibility = library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
        PageDaev.Show(_build, _profile, _sample);
    }

    private void RenderHeader(BuildProgress? progress)
    {
        var src = SourceLine.Inlines;
        src.Clear();
        if (_build is not null)
        {
            src.Add(new Run(_build.Name));
            if (_build.Source is { Id: > 0, Slug: { Length: > 0 } slug } s)
            {
                src.Add(new Run("  ·  "));
                var link = new Hyperlink(new Run(T.BuildOpenSource)) { Foreground = Blue, TextDecorations = null, NavigateUri = new Uri(QuestlogBuilds.BuildUrl(slug, s.Id, _lang)) };
                link.RequestNavigate += (_, e) => OpenUrl(e.Uri.AbsoluteUri);
                src.Add(link);
            }
        }
        MissingText.Text = progress is not null && _profile is not null
            ? string.Format(T.BuildMissing, progress.MissingSkills, progress.TotalSkills, progress.MissingLevels, progress.NotLearned) : "";
        var tgt = TargetText.Inlines;
        tgt.Clear();
        if (_build is not null)
            tgt.Add(new Run(string.Format(T.BuildTarget, _build.Source is { Id: > 0 } bs ? string.Format(T.BuildTargetQuestlog, bs.Id) : _build.Name) + "  ·  "));
        tgt.Add(new Run(_profile is not null ? string.Format(T.BuildLearned, _profile.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)) : T.BuildNoProfile));
        tgt.Add(new Run(" "));
        var refresh = new Hyperlink(new Run(T.BuildRefresh)) { Foreground = Blue, TextDecorations = null, Cursor = Cursors.Hand };
        refresh.Click += Refresh_Click;
        tgt.Add(refresh);
        var stigmas = 0;
        if (_build is not null)
            foreach (var b in _build.Skills)
                if (b.Slot is not null && _vms.TryGetValue(b.SkillId, out var vm) && vm.Info.Sub == "stigma") stigmas++;
        StigmaCount.Text = stigmas.ToString(CultureInfo.InvariantCulture) + "/" + BuildEditor.MaxSpecsStigma.ToString(CultureInfo.InvariantCulture);
    }

    private void RenderLibrary(IReadOnlyList<SkillInfo> library)
    {
        RowActive.Children.Clear(); RowStigma.Children.Clear(); RowPassive.Children.Clear();
        foreach (var info in library)
        {
            var vm = _vms[info.Id];
            if (!_showDone && vm.B is not null && vm.P?.State == SkillState.AtTarget) continue;
            var t = NewTile(vm, "lib");
            t.Margin = new Thickness(0, 0, 6, 6);
            (info.Sub == "stigma" ? RowStigma : info.Sub == "passive" ? RowPassive : RowActive).Children.Add(t);
        }
    }

    private void BuildBarCells()
    {
        for (var key = 1; key <= BuildEditor.Keys; key++)
            for (var row = 0; row < BuildEditor.Rows; row++)
            {
                var cell = new Border { Width = 48, Height = 48, Background = Cell, CornerRadius = new CornerRadius(6), Tag = key * 10 + row };
                Canvas.SetLeft(cell, (key - 1) * 54);
                Canvas.SetTop(cell, (BuildEditor.Rows - 1 - row) * 54);   // row 0 (first chain link) at the bottom, as questlog
                cell.MouseLeftButtonDown += Cell_MouseDown;
                Bar.Children.Add(cell);
            }
        for (var i = 0; i < BuildEditor.Keys; i++)
        {
            var label = new TextBlock { Text = i == 10 ? "" : (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 10, Foreground = KeyText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (i == 10) label.FontFamily = Icons;   // key 11 = left mouse button: the mock's mouse glyph
            var k = new Border { Width = 48, Height = 18, CornerRadius = new CornerRadius(4), Background = KeyBack, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(i * 54, 0, 0, 0), Child = label, Tag = i + 1, Cursor = Cursors.Hand };
            k.MouseLeftButtonDown += Key_MouseDown;
            _keys[i] = k;
            KeyRow.Children.Add(k);
        }
    }

    private void RenderBar()
    {
        foreach (var t in _barTiles) Bar.Children.Remove(t);
        _barTiles.Clear();
        if (_build is null) return;
        foreach (var b in _build.Skills)
        {
            if (!BuildEditor.TryParseSlot(b.Slot, out var key, out var row) || !_vms.TryGetValue(b.SkillId, out var vm)) continue;
            var t = NewTile(vm, "bar");
            Canvas.SetLeft(t, (key - 1) * 54);
            Canvas.SetTop(t, (BuildEditor.Rows - 1 - row) * 54);
            _barTiles.Add(t);
            Bar.Children.Add(t);
        }
    }

    private void RenderKeys()
    {
        for (var i = 0; i < _keys.Length; i++)
        {
            var on = _key == i + 1;
            _keys[i].Background = on ? KeyOn : KeyBack;
            ((TextBlock)_keys[i].Child).Foreground = on ? White : KeyText;
        }
    }

    private static SkillTile.Look LookOf(SkillVm vm, bool showDone) =>
        vm.B is null ? SkillTile.Look.Off : !showDone && vm.P?.State == SkillState.AtTarget ? SkillTile.Look.Done : SkillTile.Look.Normal;

    private static Brush StateBrush(SkillProgress? p) => p?.State switch
    {
        SkillState.AtTarget => Green,
        SkillState.Near => Amber,
        _ => Bad,
    };

    private SkillTile NewTile(SkillVm vm, string where)
    {
        var t = new SkillTile { Tag = where, Cursor = Cursors.Hand };
        ApplyTile(t, vm, where != "prio" && vm.Info.Id == _selected);
        t.MouseLeftButtonDown += Tile_MouseDown;
        t.MouseRightButtonUp += Tile_RightUp;
        AttachTip(t);
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

    private string SubLabel(string sub) => sub == "stigma" ? T.BuildStigma : sub == "passive" ? T.BuildPassive : T.BuildActive;

    private void RenderDetail()
    {
        _specIcons.Clear();
        if (_selected == 0 || !_vms.TryGetValue(_selected, out var vm))
        {
            Detail.Visibility = Visibility.Collapsed; DetailHint.Visibility = Visibility.Visible;
            return;
        }
        Detail.Visibility = Visibility.Visible; DetailHint.Visibility = Visibility.Collapsed;
        var info = vm.Info; var b = vm.B;
        ApplyTile(_detailTile, vm, false);
        DetailName.Text = vm.Name;
        DetailSub.Text = SubLabel(info.Sub) + " · " + (info.DamageType ?? "physic");
        _equipMode = b is null ? EquipMode.Add : b.Slot is not null ? EquipMode.Unequip : info.Sub == "passive" ? EquipMode.Remove : EquipMode.Equip;
        EquipButton.Content = _equipMode switch { EquipMode.Add => T.BuildAddToBuild, EquipMode.Unequip => "⏏  " + T.BuildUnequip, EquipMode.Remove => T.BuildRemove, _ => T.BuildEquip };
        RemoveButton.Visibility = b is not null && _equipMode != EquipMode.Remove ? Visibility.Visible : Visibility.Collapsed;
        var level = b?.Level ?? 1;
        SetRuns(DetailDesc.Inlines, Desc(info, level), false);

        var inBuild = b is null ? Visibility.Collapsed : Visibility.Visible;
        LevelBlock.Visibility = LevelSlider.Visibility = Ticks.Visibility = inBuild;
        LevelValue.Text = level.ToString(CultureInfo.InvariantCulture);
        _syncing = true;
        var max = Math.Min(info.MaxLevel, Math.Max(SliderMax, level));
        LevelSlider.Maximum = max;
        LevelSlider.Value = level;
        _syncing = false;
        if (_ticks.Length != max)
        {
            Ticks.Children.Clear();
            Ticks.Columns = max;
            _ticks = new TextBlock[max];
            for (var i = 0; i < _ticks.Length; i++)
                Ticks.Children.Add(_ticks[i] = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });
        }
        for (var i = 0; i < _ticks.Length; i++)
        {
            var on = i + 1 == level;
            _ticks[i].Foreground = on ? White : Dim;
            _ticks[i].FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }

        var cap = BuildEditor.SpecCap(info);
        SpecCount.Text = (b?.Specs.Count ?? 0).ToString(CultureInfo.InvariantCulture) + "/" + cap.ToString(CultureInfo.InvariantCulture);
        SpecList.Children.Clear();
        foreach (var sp in info.Specs)
        {
            var on = b?.Specs.Contains(sp.Id) == true;
            var locked = sp.RequiredLevel > level;
            var marker = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Background = on ? Blue : Marker, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = on ? "✓" : "◆", FontSize = 12, Foreground = on ? Black : MarkerText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var icon = new Image { Width = 26, Height = 26, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Source = SpecIcon(sp) };
            icon.Visibility = icon.Source is null ? Visibility.Collapsed : Visibility.Visible;
            if (sp.Icon is not null) _specIcons.Add((icon, sp));
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = sp.Name(_lang), FontSize = 13, Foreground = Text, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = string.Format(T.BuildSpecReq, sp.RequiredLevel), FontSize = 11, Foreground = Mute });
            var row = new DockPanel();
            row.Children.Add(marker); row.Children.Add(icon); row.Children.Add(text);
            var border = new Border
            {
                Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0.8), Margin = new Thickness(0, 0, 0, 4),
                Background = on ? SpecOnBack : Brushes.Transparent, BorderBrush = on ? SpecOnLine : Brushes.Transparent, Opacity = locked ? 0.45 : 1,
                Child = row, Tag = sp.Id, Cursor = b is null ? null : Cursors.Hand,
            };
            if (locked) border.ToolTip = string.Format(T.BuildSpecLocked, sp.RequiredLevel);
            border.MouseLeftButtonUp += Spec_Click;
            SpecList.Children.Add(border);
        }

        NoteBlock.Visibility = _build is null ? Visibility.Collapsed : Visibility.Visible;
        var note = _build is not null && _build.Notes.TryGetValue(info.Id, out var n) ? n : "";
        if (NoteBox.Text != note) { _syncing = true; NoteBox.Text = note; _syncing = false; }   // equal text untouched: the caret stays while typing
        NoteCount.Text = note.Length.ToString(CultureInfo.InvariantCulture) + "/" + NoteBox.MaxLength.ToString(CultureInfo.InvariantCulture);
        RelList.Children.Clear();
        foreach (var r in Relations.For(info.Id)) RelList.Children.Add(RelationLine(r, 12));
        RelBlock.Visibility = RelList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One compact relation line: a green arrow for what the skill gives, blue for what it takes, grey for chain/priority.</summary>
    private static TextBlock RelationLine(SkillRelation r, double size)
    {
        var (glyph, brush) = r.Kind switch
        {
            RelationKind.Grants or RelationKind.Applies or RelationKind.Shortens or RelationKind.Boosts or RelationKind.ChainSkill => ("→ ", Green),
            RelationKind.Consumes or RelationKind.Needs or RelationKind.ShortenedBy or RelationKind.BoostedBy => ("← ", Blue),
            _ => ("· ", Mute),
        };
        var tb = new TextBlock { FontSize = size, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        tb.Inlines.Add(new Run(glyph) { Foreground = brush, FontWeight = FontWeights.Bold });
        tb.Inlines.Add(new Run(r.Text));
        return tb;
    }

    private void RenderPriority()
    {
        PrioActive.Children.Clear(); PrioStigma.Children.Clear(); PrioPassive.Children.Clear();
        foreach (var (cat, panel) in new[] { ("active", PrioActive), ("stigma", PrioStigma), ("passive", PrioPassive) })
        {
            if (_build is not null)
                foreach (var id in _build.Priority)
                {
                    if (!_vms.TryGetValue(id, out var vm) || vm.Info.Sub != cat) continue;
                    if (panel.Children.Count > 0) panel.Children.Add(new TextBlock { Text = "›", FontSize = 12, Foreground = Gt, Margin = new Thickness(2, 0, 6, 4), VerticalAlignment = VerticalAlignment.Center });
                    var t = NewTile(vm, "prio");
                    t.Margin = new Thickness(0, 0, 4, 4);
                    panel.Children.Add(t);
                }
            var add = new Grid { Width = 48, Height = 48, Margin = new Thickness(panel.Children.Count > 0 ? 6 : 0, 0, 0, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = cat };
            add.Children.Add(new Rectangle { Stroke = AddLine, StrokeThickness = 0.8, StrokeDashArray = [4, 3], RadiusX = 6, RadiusY = 6 });
            add.Children.Add(new TextBlock { Text = "+", FontSize = 18, Foreground = AddText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            add.MouseLeftButtonUp += AddPriority_Click;
            panel.Children.Add(add);
        }
    }

    private void RenderMacros()
    {
        MacroList.Children.Clear();
        if (_build is not null)
            for (var i = 0; i < _build.Macros.Count; i++)
            {
                var m = _build.Macros[i];
                var sb = new StringBuilder(m.Name).Append(" → ");
                for (var j = 0; j < m.Steps.Count; j++) { if (j > 0) sb.Append(" → "); sb.Append(_vms.TryGetValue(m.Steps[j], out var vm) ? vm.Name : m.Steps[j].ToString(CultureInfo.InvariantCulture)); }
                var del = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 9, Width = 20, Height = 20, Margin = new Thickness(6, 0, 0, 0), Tag = i, ToolTip = T.BuildRemoveItem };
                del.Click += DeleteMacro_Click;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = sb.ToString(), FontSize = 13, Foreground = Text, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(del);
                MacroList.Children.Add(new Border { Background = Chip, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 3, 4, 3), Margin = new Thickness(0, 0, 8, 6), Child = sp });
            }
        if (MacroList.Children.Count == 0) MacroList.Children.Add(new TextBlock { Text = T.BuildNoMacros, FontSize = 13, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) });
        var addBtn = new Button { Style = (Style)FindResource("TextBtn"), Content = T.BuildAddMacro, FontSize = 13, Foreground = AddText, Margin = new Thickness(4, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        addBtn.Click += AddMacro_Click;
        MacroList.Children.Add(addBtn);
        MacroSteps.Text = StepsText();
    }

    private void RenderDescription()
    {
        var html = _build?.Description;
        if (!ReferenceEquals(html, _descHtml.Html)) _descHtml = (html, HtmlToText(html));
        DescText.Text = _descHtml.Text.Length > 0 ? _descHtml.Text : T.BuildNoDescription;
        DescText.Visibility = _descOpen ? Visibility.Visible : Visibility.Collapsed;
        DescChevron.Text = _descOpen ? "" : "";
    }

    private void RenderSaved() =>
        SavedText.Text = "· " + (_build is null || _build.UpdatedAt == default ? T.BuildNotSaved : string.Format(T.BuildSaved, _build.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ text

    /// <summary>Description at a level as runs (numbers marked by &lt;b&gt; or a coloured span), cached per (skill, level, language).</summary>
    private Seg[] Desc(SkillInfo s, int level)
    {
        var key = (s.Id, Math.Clamp(level, 1, s.MaxLevel), _lang);
        if (!_descCache.TryGetValue(key, out var segs)) _descCache[key] = segs = Parse(SkillBook.Resolve(s, key.Item2, _lang));
        return segs;
    }

    private static Seg[] Parse(string s)
    {
        var list = new List<Seg>(8);
        var sb = new StringBuilder(s.Length);
        var num = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '<' && s.IndexOf('>', i) is var end and > 0)
            {
                var tag = s.AsSpan(i + 1, end - i - 1);
                var close = tag.Length > 0 && tag[0] == '/';
                if (close) tag = tag[1..];
                if (tag.StartsWith("br", StringComparison.OrdinalIgnoreCase)) sb.Append('\n');
                else if (tag.StartsWith("b", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("span", StringComparison.OrdinalIgnoreCase))
                {
                    if (sb.Length > 0) { list.Add(new Seg(sb.ToString(), num)); sb.Clear(); }
                    num = !close;
                }
                i = end;
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(new Seg(sb.ToString(), num));
        return list.ToArray();
    }

    private static void SetRuns(InlineCollection inl, Seg[] segs, bool firstLine)
    {
        inl.Clear();
        foreach (var seg in segs)
        {
            var text = seg.Text;
            var nl = firstLine ? text.IndexOf('\n') : -1;
            if (nl >= 0) text = text[..nl];
            var r = new Run(text);
            if (seg.Num) { r.Foreground = Num; r.FontWeight = FontWeights.Medium; }
            inl.Add(r);
            if (nl >= 0) break;
        }
    }

    /// <summary>questlog's build description is HTML (p, ul/li, b): plain text with line breaks and bullets.</summary>
    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var sb = new StringBuilder(html.Length);
        for (var i = 0; i < html.Length; i++)
        {
            var c = html[i];
            if (c == '<' && html.IndexOf('>', i) is var end and > 0)
            {
                var tag = html.AsSpan(i + 1, end - i - 1);
                if (tag.StartsWith("li", StringComparison.OrdinalIgnoreCase)) sb.Append("\n• ");
                else if (tag.StartsWith("br", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("/p", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("/ul", StringComparison.OrdinalIgnoreCase)) sb.Append('\n');
                i = end;
                continue;
            }
            sb.Append(c);
        }
        var text = WebUtility.HtmlDecode(sb.ToString());
        while (text.Contains("\n\n\n", StringComparison.Ordinal)) text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        return text.Trim();
    }

    private string StepsText()
    {
        var sb = new StringBuilder();
        foreach (var id in _macroSteps) { if (sb.Length > 0) sb.Append(" → "); sb.Append(_vms.TryGetValue(id, out var vm) ? vm.Name : id.ToString(CultureInfo.InvariantCulture)); }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ tooltip

    private void AttachTip(FrameworkElement t)
    {
        t.ToolTip = "";   // placeholder: the real ToolTip is made on the first opening, its content on every opening
        ToolTipService.SetInitialShowDelay(t, 250);
        ToolTipService.SetShowDuration(t, 60000);
        ToolTipService.SetPlacement(t, PlacementMode.Mouse);
        t.ToolTipOpening += Tip_Opening;
    }

    private void Tip_Opening(object sender, ToolTipEventArgs e)
    {
        if (sender is not SkillTile t || !_vms.TryGetValue(t.SkillId, out var vm)) { e.Handled = true; return; }
        var tip = t.ToolTip as ToolTip ?? new ToolTip { Style = (Style)FindResource("SkillTip"), Placement = PlacementMode.Mouse };
        tip.Content = TipContent(vm);
        t.ToolTip = tip;
    }

    private FrameworkElement TipContent(SkillVm vm)
    {
        var info = vm.Info; var b = vm.B;
        var root = new StackPanel();
        var head = new DockPanel();
        var icon = new SkillTile { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        ApplyTile(icon, vm, false, badges: false);
        head.Children.Add(icon);
        var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = vm.Name, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Text, TextWrapping = TextWrapping.Wrap });
        names.Children.Add(new TextBlock { Text = info.Name(_lang == "es" ? "en" : "es"), FontSize = 11, Foreground = Mute });
        var tags = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        tags.Children.Add(TagChip(info.Sub));
        if (info.DamageType is { Length: > 0 } dmg) tags.Children.Add(TagChip(dmg));
        names.Children.Add(tags);
        head.Children.Add(names);
        root.Children.Add(head);

        var level = b?.Level ?? 1;
        var desc = new TextBlock { FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, LineHeight = 18.6, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(0, 8, 0, 8) };
        SetRuns(desc.Inlines, Desc(info, level), false);
        root.Children.Add(desc);

        if (b is not null)
        {
            var cmp = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            cmp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cmp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cur = vm.P?.Current;
            var curText = _profile is null ? "—" : cur is int c ? c.ToString(CultureInfo.InvariantCulture) : T.BuildTipNotLearned;
            var key = BuildEditor.KeyOf(b.Slot);
            var specs = new StringBuilder();
            foreach (var id in b.Specs) if (BuildEditor.Spec(info, id) is { } sp) { if (specs.Length > 0) specs.Append(" · "); specs.Append(sp.Name(_lang)); }
            CmpRow(cmp, 0, _profile is not null ? string.Format(T.BuildTipNow, _profile.Ref.Name) : T.BuildTipNowAnon, curText, _profile is null ? Mute : StateBrush(vm.P), true);
            CmpRow(cmp, 1, T.BuildTipBuild, b.Level.ToString(CultureInfo.InvariantCulture) + (key > 0 ? " · " + string.Format(T.BuildTipKey, BuildEditor.KeyName(key)) : ""), Text, false);
            CmpRow(cmp, 2, T.BuildTipSpecs, specs.Length > 0 ? specs.ToString() : T.BuildTipNone, Text, false);
            root.Children.Add(new Border { Background = CmpBack, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 8), Child = cmp });
            if (cur is int now && now != b.Level)
            {
                root.Children.Add(Section(string.Format(T.BuildTipAtLevel, now)));
                var at = new TextBlock { FontSize = 12, Foreground = Mute, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                SetRuns(at.Inlines, Desc(info, now), true);
                root.Children.Add(at);
            }
        }
        if (_build is not null && _build.Notes.TryGetValue(info.Id, out var note) && note.Length > 0)
        {
            root.Children.Add(Section(T.BuildTipNotes));
            root.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Text, TextWrapping = TextWrapping.Wrap, LineHeight = 17.4, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
        }
        var rels = Relations.For(info.Id);
        if (rels.Count > 0)
        {
            root.Children.Add(Section(T.BuildRelations));
            foreach (var r in rels) root.Children.Add(RelationLine(r, 11.5));
        }
        return root;
    }

    private static Border TagChip(string text) => new()
    {
        Background = TagBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(0, 0, 4, 0),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = Dim },
    };

    private static TextBlock Section(string text) => new() { Text = text.ToUpper(T.Culture), FontSize = 11, Foreground = Mute, Margin = new Thickness(0, 4, 0, 3) };

    private static void CmpRow(Grid g, int row, string label, string value, Brush valueBrush, bool bold)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, FontSize = 12, Foreground = Mute, Margin = new Thickness(0, 1, 10, 1) };
        var v = new TextBlock { Text = value, FontSize = 12, Foreground = valueBrush, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
        Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
        g.Children.Add(l); g.Children.Add(v);
    }

    // ------------------------------------------------------------------ input

    private void Select(int id)
    {
        _selected = id;
        if (_build?.Skill(id) is { Slot: { } slot }) _key = BuildEditor.KeyOf(slot);
        RefreshSelection();
    }

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not SkillTile t || !_vms.TryGetValue(t.SkillId, out var vm)) return;
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

    private void Tile_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not SkillTile t || _build is null) return;
        e.Handled = true;
        var id = t.SkillId;
        if ((string?)t.Tag == "bar") { Edit(BuildEditor.Unequip(_build, id)); return; }
        if ((string?)t.Tag != "prio") return;
        var menu = new ContextMenu { PlacementTarget = t };
        menu.Items.Add(Item(T.BuildMoveLeft, () => Edit(BuildEditor.MovePriority(_build, id, -1, Category))));
        menu.Items.Add(Item(T.BuildMoveRight, () => Edit(BuildEditor.MovePriority(_build, id, 1, Category))));
        menu.Items.Add(Item(T.BuildRemoveItem, () => Edit(BuildEditor.RemovePriority(_build, id))));
        menu.IsOpen = true;
    }

    private string? Category(int id) => _vms.TryGetValue(id, out var vm) ? vm.Info.Sub : _meter.Skills.Get(id)?.Sub;

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
        if (!Edit(BuildEditor.Equip(_build!, vm.Info, key)) && BuildEditor.KeyOf(_build!.Skill(vm.Info.Id)?.Slot) != key) StatusText.Text = string.Format(T.BuildKeyFull, key);
        else { _key = key; RenderKeys(); }
    }

    private void Cell_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int tag }) return;
        int key = tag / 10, row = tag % 10;
        if (_selected != 0 && _build is not null && _vms.TryGetValue(_selected, out var vm) && vm.Info.Sub != "passive")
        {
            if (Edit(BuildEditor.MoveTo(_build, vm.Info, key, row))) { _key = key; RenderKeys(); }
            return;
        }
        _key = key;
        RenderKeys();
    }

    private void Key_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int k }) return;
        _key = _key == k ? 0 : k;
        RenderKeys();
    }

    private void Equip_Click(object sender, RoutedEventArgs e)
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

    private void RemoveSkill_Click(object sender, RoutedEventArgs e)
    {
        if (_vms.TryGetValue(_selected, out var vm) && NeedBuild()) Edit(BuildEditor.RemoveSkill(_build!, vm.Info.Id));
    }

    /// <summary>Typing in the note box: only the build changes (no redraw, the caret stays); the 500 ms save follows; tooltips read the note when they open.</summary>
    private void Note_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _build is null || _selected == 0) return;
        var text = NoteBox.Text;
        var has = _build.Notes.TryGetValue(_selected, out var cur);
        var empty = string.IsNullOrWhiteSpace(text);
        NoteCount.Text = text.Length.ToString(CultureInfo.InvariantCulture) + "/" + NoteBox.MaxLength.ToString(CultureInfo.InvariantCulture);
        if (empty ? !has : cur == text) return;
        var notes = new Dictionary<int, string>(_build.Notes);
        if (empty) notes.Remove(_selected); else notes[_selected] = text;
        _build = _build with { Notes = notes };
        _dirty = true;
        if (!_sample) { _saveTick.Stop(); _saveTick.Start(); }
    }

    private void DetailClose_Click(object sender, RoutedEventArgs e) { _selected = 0; RefreshSelection(); }

    private void Level_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _build is null || !_vms.TryGetValue(_selected, out var vm)) return;
        Edit(BuildEditor.SetLevel(_build, vm.Info, (int)Math.Round(e.NewValue)));
    }

    private void Spec_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: int specId } || _build is null || !_vms.TryGetValue(_selected, out var vm)) return;
        var next = BuildEditor.ToggleSpec(_build, vm.Info, specId, out var r);
        if (r == SpecToggle.Locked) StatusText.Text = string.Format(T.BuildSpecLocked, BuildEditor.Spec(vm.Info, specId)?.RequiredLevel ?? 0);
        else if (r == SpecToggle.Full) StatusText.Text = string.Format(T.BuildSpecFull, BuildEditor.SpecCap(vm.Info));
        else { StatusText.Text = ""; Edit(next); }
    }

    private void AddPriority_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid { Tag: string cat } box || !NeedBuild()) return;
        var menu = new ContextMenu { PlacementTarget = box, Placement = PlacementMode.Bottom };
        foreach (var s in _build!.Skills)
        {
            if (_build.Priority.Contains(s.SkillId) || !_vms.TryGetValue(s.SkillId, out var vm) || vm.Info.Sub != cat) continue;
            var id = s.SkillId;
            menu.Items.Add(Item(SkillHeader(vm), () => Edit(BuildEditor.AddPriority(_build, id))));
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = T.BuildNothingToAdd, IsEnabled = false });
        menu.IsOpen = true;
    }

    private FrameworkElement SkillHeader(SkillVm vm)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Image { Source = IconOf(vm), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) });
        sp.Children.Add(new TextBlock { Text = vm.Name, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void ClearPriority_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedBuild() || _build!.Priority.Count == 0) return;
        if (MessageBox.Show(Window.GetWindow(this)!, T.BuildClearConfirm, T.BuildTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Edit(_build with { Priority = [] });
    }

    private void AddMacro_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedBuild()) return;
        _macroSteps.Clear();
        MacroName.Text = "";
        MacroSteps.Text = "";
        MacroEditor.Visibility = Visibility.Visible;
        MacroName.Focus();
    }

    private void MacroAddStep_Click(object sender, RoutedEventArgs e)
    {
        if (_build is null) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var s in _build.Skills)
        {
            if (!_vms.TryGetValue(s.SkillId, out var vm) || vm.Info.Sub == "passive") continue;
            var id = s.SkillId;
            menu.Items.Add(Item(SkillHeader(vm), () => { _macroSteps.Add(id); MacroSteps.Text = StepsText(); }));
        }
        menu.IsOpen = true;
    }

    private void MacroSave_Click(object sender, RoutedEventArgs e)
    {
        if (_build is null || _macroSteps.Count == 0) return;
        var name = MacroName.Text.Trim();
        if (name.Length == 0) name = "Macro " + (_build.Macros.Count + 1).ToString(CultureInfo.InvariantCulture);
        MacroEditor.Visibility = Visibility.Collapsed;
        Edit(BuildEditor.AddMacro(_build, new BuildMacro(name, [.. _macroSteps])));
        _macroSteps.Clear();
    }

    private void MacroCancel_Click(object sender, RoutedEventArgs e) { _macroSteps.Clear(); MacroEditor.Visibility = Visibility.Collapsed; }

    private void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int i } && _build is not null) Edit(BuildEditor.RemoveMacro(_build, i));
    }

    private void Description_Toggle(object sender, MouseButtonEventArgs e) { _descOpen = !_descOpen; RenderDescription(); }

    private void SaveNow_Click(object sender, RoutedEventArgs e) => FlushSave();

    private void ShowDone_Click(object sender, RoutedEventArgs e)
    {
        _showDone = ShowDone.IsChecked == true;
        if (!_sample) { _meter.Settings.BuildShowDone = _showDone; _meter.Settings.Save(); }
        Render();
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || LangBox.SelectedItem is not ComboBoxItem { Tag: string l }) return;
        _lang = l;
        if (!_sample) { _meter.Settings.SkillLanguage = l; _meter.Settings.Save(); }
        Render();
    }

    private void Sub_Checked(object sender, RoutedEventArgs e)
    {
        if (PageSkills is null || PageDaev is null) return;   // raised during InitializeComponent
        var skills = SubSkills.IsChecked == true;
        PageSkills.Visibility = skills ? Visibility.Visible : Visibility.Collapsed;
        PageDaev.Visibility = skills ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _sample || BuildBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        FlushSave();
        _store.ActiveId = id.Length > 0 ? id : null;
        _build = id.Length > 0 ? _store.Load(id) : null;
        _selected = 0;
        DeleteBuild.Visibility = CopyQuestlog.Visibility = _build is null ? Visibility.Collapsed : Visibility.Visible;
        Render();
    }

    private void NewFromProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_sample) return;
        var p = _meter.CurrentProfile ?? _profile;
        if (p is null) { StatusText.Text = T.BuildNoProfileForNew; return; }
        FlushSave();
        var b = BuildEditor.FromProfile(p, string.Format(T.BuildProfileName, p.Ref.Name, p.FetchedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        try
        {
            var saved = _store.Save(b);
            _store.ActiveId = saved.Id;
            QueueTelemetry(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Build save", ex); StatusText.Text = ex.Message; return; }
        StatusText.Text = "";
        Reload();
    }

    private void DeleteBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_sample || _build is null) return;
        if (MessageBox.Show(Window.GetWindow(this)!, string.Format(T.BuildDeleteConfirm, _build.Name), T.BuildTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _saveTick.Stop(); _dirty = false;
        _store.Delete(_build.Id);
        _build = null; _selected = 0;
        Reload();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_sample) return;
        var dlg = new ImportBuildWindow(_classId) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (dlg.Result is { } b) SaveImported(b);
        else if (dlg.PlanUrl is { } planUrl) { SubDaev.IsChecked = true; PageDaev.ImportPlanFromUrl(planUrl); }
    }

    /// <summary>Fetches the questlog build behind a skill-builder link and saves it as the active build of this character; errors go to the status line.</summary>
    public async void ImportFromUrl(string url)
    {
        if (QuestlogBuilds.ParseUrl(url) is not { } r)
        {
            if (Core.Builds.DaevanionPlan.ParsePlannerUrl(url) is not null) { SubDaev.IsChecked = true; PageDaev.ImportPlanFromUrl(url); return; }
            StatusText.Text = T.BuildImportBadUrl; return;
        }
        await ImportAsync(r.Slug, r.Id);
    }

    private async Task ImportAsync(string slug, long id)
    {
        if (_sample || _importing) return;
        _importing = true;
        StatusText.Text = string.Format(T.BuildImporting, id);
        try
        {
            var b = await QuestlogService.FetchAsync(slug, id);
            if (b is null) StatusText.Text = string.Format(T.BuildImportNotFound, id);
            else SaveImported(b);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"Build import {slug}/{id} failed: {ex.Message}");
            StatusText.Text = string.Format(T.BuildImportFailed, QuestlogService.Reason(ex));
        }
        finally { _importing = false; }
    }

    private void SaveImported(Build imported)
    {
        FlushSave();
        Build saved;
        try
        {
            saved = _store.Save(QuestlogImport.Prepare(imported, _store.List()));
            _store.ActiveId = saved.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Build save", ex); StatusText.Text = ex.Message; return; }
        Log.Info($"Build imported from questlog: {saved.Source?.Id} → {saved.Id} ({saved.ClassId}, {saved.Skills.Count} skills)");
        QueueTelemetry(saved);
        _selected = 0;
        Reload();
        StatusText.Text = _classId.Length > 0 && saved.ClassId.Length > 0 && saved.ClassId != _classId
            ? string.Format(T.BuildImportedOtherClass, saved.Name, ClassName(saved.ClassId), ClassName(_classId))
            : string.Format(T.BuildImported, saved.Name);
    }

    private void QueueTelemetry(Build saved)
    {
        if (_sample) return;
        var p = _meter.CurrentProfile;
        Telemetry.QueueBuild(_meter.Settings, saved, BuildEditor.ClassIdOf(p?.ClassName) ?? _classId, p?.Level ?? 0, _meter.CharacterKey);
    }

    private void CopyQuestlog_Click(object sender, RoutedEventArgs e)
    {
        if (!NeedBuild()) return;
        FlushSave();
        try
        {
            Clipboard.SetText(QuestlogExport.ToJson(_build!));
            StatusText.Text = T.BuildCopied;
        }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = T.BuildCopyFailed; }   // another app holds the clipboard
    }

    private static string ClassName(string classId) => classId.Length > 0 ? char.ToUpperInvariant(classId[0]) + classId[1..] : classId;

    // ------------------------------------------------------------------ suggestions

    private void RenderSuggestions()
    {
        var show = _build is null && (_sample ? _sampleSuggestions is not null : true);
        SuggestCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BuildCards.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;
        SuggestTitle.Text = string.Format(T.BuildSuggestTitle, _classId.Length > 0 ? ClassName(_classId) : "?");
        if (_classId.Length == 0) { _suggestFor = ""; SuggestMessage(T.BuildSuggestNoClass); return; }
        if (_sample) { FillSuggestions(_sampleSuggestions!); return; }
        if (QuestlogService.CachedSuggestions(_classId) is { } hit) { _suggestFor = _classId; FillSuggestions(hit); return; }
        if (_suggestFor == _classId && SuggestList.Children.Count > 0) return;   // already loading for this class
        _suggestFor = _classId;
        SuggestMessage(T.BuildSuggestLoading);
        _ = LoadSuggestionsAsync(_classId);
    }

    private async Task LoadSuggestionsAsync(string classId)
    {
        try
        {
            var list = await QuestlogService.SuggestionsAsync(classId);
            if (_suggestFor == classId && _build is null) FillSuggestions(list);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            Log.Info($"Build suggestions for {classId} failed: {ex.Message}");
            if (_suggestFor == classId) { SuggestMessage(T.BuildSuggestFailed); _suggestFor = ""; }
        }
    }

    private void SuggestMessage(string text)
    {
        SuggestList.Children.Clear();
        SuggestList.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Mute, Margin = new Thickness(0, 0, 0, 6) });
    }

    private void FillSuggestions(List<BuildSummary> list)
    {
        SuggestList.Children.Clear();
        if (list.Count == 0) { SuggestMessage(T.BuildSuggestEmpty); return; }
        var style = (Style)FindResource("Flat");
        foreach (var b in list)
            SuggestList.Children.Add(ImportBuildWindow.Row(b, AddSuggestion, CmpBack, TagBack, Text, Mute, TagBack, style));
    }

    private void AddSuggestion(BuildSummary s)
    {
        if (s.AuthorSlug is { Length: > 0 } slug) _ = ImportAsync(slug, s.Id);
        else StatusText.Text = string.Format(T.BuildImportNotFound, s.Id);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_sample) return;
        StatusText.Text = T.BuildRefreshing;
        bool ok;
        try { ok = await Task.Run(() => _meter.RefreshProfileAsync()); }
        catch (Exception ex) { Log.Error("Build: profile refresh", ex); ok = false; }
        StatusText.Text = ok ? "" : T.BuildRefreshFailed;
        Reload();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("Open " + url, ex); }
    }

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp(sender is FrameworkElement { Tag: string t } ? t : "build");
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
