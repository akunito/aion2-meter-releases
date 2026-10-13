using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Core.Events;
using AionMeter.Core.Game;

namespace AionMeter.App.Views;

/// <summary>
/// One fight's breakdown for one player (party list, stat tiles, DPS / Accuracy / Rotation / Defense). Shown inside the overlay card
/// as the "fight" tab (<paramref name="hosted"/>: Back + Esc return to the DPS tab) and as the content of the stand-alone
/// <see cref="Windows.BreakdownWindow"/> (A2M-43).
/// </summary>
public partial class BreakdownView : UserControl
{
    private IFightView? _view;
    private readonly DispatcherTimer _timer;
    private readonly Func<int, string> _skillName;
    private uint _actor;
    private bool _syncingList;
    private readonly bool _hosted;

    public sealed record RankStyle(Brush Bg, Brush Fg, Brush Border);

    public sealed record PartyItem(uint ActorId, string Name, GameClass Class, string ShareText, int Place, RankStyle Rank, bool IsSelf)
    {
        public string PlaceText => Place > 0 ? Place.ToString() : "·";
        public Brush NameBrush => IsSelf ? SelfBrush : Brushes.WhiteSmoke;
        public FontWeight NameWeight => IsSelf ? FontWeights.Bold : FontWeights.Normal;
        private static readonly Brush SelfBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xEC, 0xCB, 0x82)));
    }

    public sealed record SkillItem(
        string Name, Brush Swatch, ImageSource? Icon, string Hits, string Damage, string Dps, string Avg, string Max, string Crit,
        string Share, double ShareWidth, string DotTag, string Tooltip,
        string Back, string Front, string Perfect, string Heavy, string Multi);

    private static UiText T => UiText.Current;
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    // Each table sorts by a clicked column header; a new breakdown starts with the order chosen last (while the app runs).
    private static SkillOrder _lastSkillOrder = SkillOrder.Default;
    private static SkillOrder _lastAccuracyOrder = SkillOrder.Default;
    private SkillOrder _skillOrder = _lastSkillOrder;
    private SkillOrder _accuracyOrder = _lastAccuracyOrder;

    private readonly SkillIcons _icons;
    private readonly Func<int, string> _serverName;
    private bool _iconRefreshQueued;

    private readonly BossPortraits? _portraits;
    private readonly Func<Guid, string?>? _fightNote; // A2M-38 Abyss Logs line by fight id
    private int _noteWaits;
    private Guid _fightId;

    /// <summary>Back / Esc in the overlay, the X in the window.</summary>
    public event Action? CloseRequested;
    public event Action? MinimizeRequested;
    /// <summary>Overlay only: the "Fights ▾" chip asks for the overlay's fight list, anchored at the element given.</summary>
    public event Action<FrameworkElement>? FightsRequested;
    /// <summary>"Player — fight · app" for the window's title bar.</summary>
    public event Action<string>? TitleChanged;

    public IFightView? Fight => _view;
    public uint Actor => _actor;

    public BreakdownView(GameData data, SkillIcons icons, BossPortraits? portraits, Func<Guid, string?>? fightNote, bool hosted)
    {
        _fightNote = fightNote;
        _skillName = data.SkillName;
        _serverName = data.ServerName;
        _icons = icons;
        _portraits = portraits;
        _hosted = hosted;
        InitializeComponent();
        ShowSortHeads();
        BackButton.Visibility = hosted ? Visibility.Visible : Visibility.Collapsed;
        MinimizeButton.Visibility = CloseButton.Visibility = hosted ? Visibility.Collapsed : Visibility.Visible;
        AppTitle.Visibility = hosted ? Visibility.Collapsed : Visibility.Visible;   // the card already says what it is
        // A hidden tab (another tab in front, overlay hidden) neither ticks nor reloads; it reloads once when shown again.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_view is null || !IsVisible) _timer.Stop();
            else if (_view.IsLive) Reload();
            // the Abyss Logs line arrives a second or two after the kill: keep looking for up to 30 s
            else if (_fightNote is not null && FightRank.Visibility != Visibility.Visible && ++_noteWaits <= 30) ShowRank(_fightNote(_fightId));
            else _timer.Stop();
        };
        Loaded += (_, _) =>
        {
            _icons.IconReady += OnIconReady;
            if (_portraits is not null) _portraits.PortraitReady += OnIconReady;
            if (IsVisible) Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _icons.IconReady -= OnIconReady;
            if (_portraits is not null) _portraits.PortraitReady -= OnIconReady;
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Start();
            else _timer.Stop();
        };
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    private void Start()
    {
        if (_view is null) return;
        Reload();
        if (_view.IsLive || _fightNote is not null) _timer.Start();
    }

    /// <summary>Shows <paramref name="view"/> for <paramref name="actorId"/>; the same fight only moves the selection.</summary>
    public void SetFight(IFightView view, uint actorId)
    {
        if (ReferenceEquals(view, _view)) { Select(actorId); return; }
        _view = view;
        _actor = actorId;
        _noteWaits = 0;
        ShowRank(null);
        SetPartyOpen(false);
        if (IsLoaded && IsVisible) Start();
    }

    public bool Shows(IFightView view) => ReferenceEquals(view, _view);

    /// <summary>Overlay only: shows the "Fights ▾" chip (the overlay's fight list opens this view on the fight picked).</summary>
    public void EnableFightPicker() => FightsButton.Visibility = Visibility.Visible;

    /// <summary>Icons arrive one by one from the CDN: coalesce them into one redraw.</summary>
    private void OnIconReady()
    {
        if (_iconRefreshQueued) return;
        _iconRefreshQueued = true;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(400);
            _iconRefreshQueued = false;
            if (IsVisible) Reload();
        });
    }

    /// <summary>dps | accuracy | rotation | defense</summary>
    public void ShowTab(string name)
    {
        var tab = name.ToLowerInvariant() switch
        {
            "accuracy" => TabAccuracy,
            "rotation" => TabRotation,
            "defense" => TabDefense,
            _ => TabDps,
        };
        tab.IsChecked = true;
    }

    public void Select(uint actorId)
    {
        _actor = actorId;
        if (IsVisible) Reload();
    }

    private void ShowRank(string? rank)
    {
        FightRank.Text = rank ?? "";
        FightRank.Visibility = rank is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Fills the view without it being shown (offscreen render tool).</summary>
    public void ReloadNow() => Reload();

    /// <summary>Render tool (--tab party): the narrow layout's party list dropped down.</summary>
    public void SampleOpenParty() => SetPartyOpen(true);

    private void Reload()
    {
        var summary = _view?.Summary();
        if (summary is null) return;

        FightTitle.Text = summary.Title;
        FightPortrait.Source = _portraits?.Of(summary);
        FightMeta.Text = $"{summary.StartedAt.ToString("dd MMM HH:mm", T.Culture)} · {Format.Clock(summary.CombatMs)} · {Format.Compact(summary.PartyDps)}/s {T.PartyDpsSuffix}"
                         + (summary.MostDamageUnsent ? $"\n{T.DamageUnsentLine(summary)}" : "")
                         + (summary.Zone is { } z ? $"\n{z}" : "");
        FightMeta.ToolTip = summary.MostDamageUnsent ? T.DamageUnsentTip : null;
        _fightId = summary.Id;
        ShowRank(_fightNote?.Invoke(summary.Id));

        _syncingList = true;
        var combatants = summary.Combatants;
        var party = new List<PartyItem>(combatants.Count);
        PartyItem? selected = null;
        for (var i = 0; i < combatants.Count; i++)
        {
            var c = combatants[i];
            var place = c.IsUnknownSummons ? 0 : i + 1;
            var (bg, fg, border) = RankBrushes.For(place);
            var item = new PartyItem(c.ActorId, T.CombatantLabel(c.ActorId, c.Name, c.Class), c.Class, Format.Share(c.Share), place, new RankStyle(bg, fg, border), c.IsSelf);
            if (c.ActorId == _actor) selected = item;
            party.Add(item);
        }
        PartyList.ItemsSource = party;
        PartyList.SelectedItem = selected;
        _syncingList = false;

        var detail = _view!.Detail(_actor);
        if (detail is null) return;
        ShowDetail(summary, detail);
        if (ActualWidth > 0) ApplyWidth(ActualWidth);   // the tiles' natural width changes with their numbers
    }

    private void ShowDetail(EncounterSnapshot summary, CombatantDetail d)
    {
        TitleChanged?.Invoke($"{d.Name} — {summary.Title} · {AppInfo.Name}");
        HeaderGlyph.Class = d.Class;
        PlayerName.Text = T.CombatantLabel(d.ActorId, d.Name, d.Class);
        var server = d.ServerId != 0 ? $" · {_serverName(d.ServerId)} {GameData.RegionOf(d.ServerId)}".TrimEnd() : "";
        var gear = (d.GearScore > 0 ? $" · {T.Chat.GearScore} {d.GearScore}" : "") +
                   (d.CombatPower > 0 ? $" · {T.Chat.CombatPower} {Format.Grouped(d.CombatPower)}" : "");
        PlayerMeta.Text = $"{T.ClassName(d.Class)}{server}{gear} · {summary.Title}" + (d.IsSelf ? $" · {T.You}" : "");
        var place = 0;
        if (d.ActorId != Combatant.UnknownSummonsId)
            for (var i = 0; i < summary.Combatants.Count; i++)
                if (summary.Combatants[i].ActorId == d.ActorId) { place = i + 1; break; }
        StatPlace.Text = place > 0 ? $"{place} / {summary.PlayerCount}" : "—";
        var (placeBg, placeFg, placeBorder) = RankBrushes.For(place);
        PlaceTile.Background = place is >= 1 and <= 3 ? placeBg : (Brush)FindResource("Bg1");
        PlaceTile.BorderBrush = placeBorder;
        StatPlace.Foreground = place is >= 1 and <= 3 ? placeFg : (Brush)FindResource("Text");
        PlaceLabel.Foreground = place is >= 1 and <= 3 ? placeFg : (Brush)FindResource("Gold");
        StatDamage.Text = Format.Compact(d.Damage);
        StatDps.Text = Format.Compact(d.Dps);
        StatShare.Text = Format.Share(d.Share);
        StatTime.Text = Format.Clock(d.CombatMs);

        var maxShare = d.Skills.Count > 0 ? d.Skills.Max(s => s.Share) : 1;
        SkillList.ItemsSource = _skillOrder.Apply(d.Skills, T.Culture).Select(s => ToItem(s, maxShare)).ToList();
        AccuracyList.ItemsSource = _accuracyOrder.Apply(d.Skills.Where(s => s.Hits > 0), T.Culture).Select(s => ToItem(s, maxShare)).ToList();
        ShowSortHeads();

        var q = d.Quality;
        QualityTiles.Children.Clear();
        AddTile(QualityTiles, T.ThCrit, Format.Percent(q.CritRate), string.Format(T.QHitsMeasured, q.Hits));
        AddTile(QualityTiles, T.ThBack, Format.Percent(q.BackRate), T.QPositional);
        AddTile(QualityTiles, T.ThFront, Format.Percent(q.FrontRate), T.QPositional);
        AddTile(QualityTiles, T.ThPerfect, Format.Percent(q.PerfectRate), "");
        AddTile(QualityTiles, T.ThSmite, Format.Percent(q.HeavyRate), "");
        AddTile(QualityTiles, T.ThMulti, Format.Percent(q.MultiRate), T.QExtraHits);
        AddTile(QualityTiles, T.QBiggestHit, Format.Compact(q.MaxHit), q.MaxHitSkill ?? "");

        DefenseTiles.Children.Clear();
        AddTile(DefenseTiles, T.DefTaken, Format.Compact(d.DamageTaken), string.Format(T.DefHits, d.HitsTaken));
        AddTile(DefenseTiles, T.DefTakenPerSec, Format.Compact(d.DamageTaken / Math.Max(1.0, d.CombatMs / 1000.0)), "");
        AddTile(DefenseTiles, T.DefDotTicks, q.DotTicks.ToString(), T.DefDot);

        Rotation.SetData(d.Casts, _skillName, _icons.Get);

        // Party DPS timeline: the selected player filled, everyone else as thin class-coloured lines.
        var series = new List<ChartSeries> { new(d.Name, d.PerSecond, ClassVisuals.ColorOf(d.Class), true) };
        foreach (var other in _view!.AllDetails())
            if (other.ActorId != d.ActorId) series.Add(new ChartSeries(other.Name, other.PerSecond, ClassVisuals.ColorOf(other.Class), false));
        Chart.SetData(series);
    }

    private SkillItem ToItem(SkillRow s, double maxShare)
    {
        var c = RotationStrip.SkillColor(s.SkillCode);
        var swatch = new SolidColorBrush(c);
        swatch.Freeze();
        var hits = s.DotTicks > 0 && s.Hits > 0 ? $"{s.Hits}+{s.DotTicks}" : (s.Hits + s.DotTicks).ToString();
        var tooltip = $"{s.Name} ({s.SkillCode})\n{T.TipSkillDamage} {Format.Grouped(s.Damage)} · {Format.Percent(s.Share)}\n" +
                      $"{T.TipSkillHits} {s.Hits} · {T.TipSkillDotTicks} {s.DotTicks}\n" +
                      $"{T.TipSkillMin} {Format.Compact(s.Min)} · {T.TipSkillAvg} {Format.Compact(s.Average)} · {T.TipSkillMax} {Format.Compact(s.Max)}";
        return new SkillItem(
            s.Name, swatch, _icons.Get(s.SkillCode), hits, Format.Compact(s.Damage), Format.Compact(s.Dps), Format.Compact(s.Average),
            Format.Compact(s.Max), Format.Percent(s.CritRate), Format.Percent(s.Share),
            maxShare > 0 ? _shareBar * s.Share / maxShare : 0, s.Hits == 0 && s.DotTicks > 0 ? "DoT" : "", tooltip,
            Format.Percent(s.BackRate), Format.Percent(s.FrontRate), Format.Percent(s.PerfectRate),
            Format.Percent(s.HeavyRate), Format.Percent(s.MultiRate));
    }

    private void AddTile(Panel panel, string label, string value, string caption)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("TileLabel") });
        stack.Children.Add(new TextBlock { Text = value, Style = (Style)FindResource("TileValue"), FontSize = 22, Margin = new Thickness(0, 2, 0, 0) });
        if (caption.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = caption, FontSize = 10.5, Foreground = (Brush)FindResource("TextMute"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        panel.Children.Add(new Border { Style = (Style)FindResource("StatTile"), Child = stack, Padding = new Thickness(14, 10, 14, 10), MinWidth = 96 });
    }

    // ------------------------------------------------------------ width (the overlay card is 560–1100 px wide)

    private const double NarrowBelow = 760;      // the party panel becomes a dropdown
    private const double CompactTablesBelow = 700; // main area: Avg/Max (DPS) and Front/Multi/Damage (Accuracy) drop out
    private const double NameMin = 140;          // the player name keeps at least this beside the tiles, else the tiles get a row
    private const double SideWidth = 190;
    private int _layout = -1;                    // bit 0 narrow, bit 1 compact tables, bit 2 tiles on their own row
    private double _shareBar = 96;

    private void ApplyWidth(double width)
    {
        if (width <= 0) return;
        var narrow = width < NarrowBelow;
        var main = width - 2 - (narrow ? 0 : SideWidth);
        var compact = main < CompactTablesBelow;
        // natural width of the tiles beside everything else on the header line
        Tiles.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        HeaderLeft.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        HeaderButtons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var leftW = (_hosted ? BackButton.DesiredSize.Width + 8 : 0) + (narrow ? Math.Max(PartyToggle.DesiredSize.Width, 70) + 8 : 0);   // collapsed before the first narrow pass: 0 wide
        var fixedW = 20 + leftW + 44 + 20 + HeaderButtons.DesiredSize.Width + NameMin;
        var tilesBelow = main < fixedW + Tiles.DesiredSize.Width;
        var layout = (narrow ? 1 : 0) | (compact ? 2 : 0) | (tilesBelow ? 4 : 0);
        if (layout == _layout) return;
        var compactChanged = _layout < 0 || ((layout ^ _layout) & 2) != 0;
        _layout = layout;

        SideColumn.Width = new GridLength(narrow ? 0 : SideWidth);
        PartyToggle.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        if (narrow)
        {
            // a dropdown under the header, over the tabs and the body
            Grid.SetColumn(SidePanel, 1); Grid.SetRow(SidePanel, 1); Grid.SetRowSpan(SidePanel, 2);
            SidePanel.HorizontalAlignment = HorizontalAlignment.Left;
            SidePanel.Width = Math.Min(260, width - 24);
            SidePanel.Margin = new Thickness(12, 0, 0, 12);
            SidePanel.BorderThickness = new Thickness(1);
            SidePanel.BorderBrush = (Brush)FindResource("GoldDim");
            SidePanel.Effect = DropShadow;
            SidePanel.Visibility = PartyToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            Grid.SetColumn(SidePanel, 0); Grid.SetRow(SidePanel, 0); Grid.SetRowSpan(SidePanel, 3);
            SidePanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidePanel.ClearValue(WidthProperty);
            SidePanel.Margin = new Thickness(0);
            SidePanel.BorderThickness = new Thickness(0, 0, 1, 0);
            SidePanel.BorderBrush = (Brush)FindResource("Line");
            SidePanel.Effect = null;
            SidePanel.Visibility = Visibility.Visible;
            PartyToggle.IsChecked = false;
        }

        var star = tilesBelow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        foreach (var col in Tiles.ColumnDefinitions) col.Width = star;
        Grid.SetRow(Tiles, tilesBelow ? 1 : 0);
        Grid.SetColumn(Tiles, tilesBelow ? 0 : 3);
        Grid.SetColumnSpan(Tiles, tilesBelow ? 5 : 1);
        Tiles.HorizontalAlignment = tilesBelow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        Tiles.Margin = tilesBelow ? new Thickness(-6, 0, 4, 10) : new Thickness(0);

        if (!compactChanged) return;
        Resources["ColAvg"] = new GridLength(compact ? 0 : 64);
        Resources["ColMax"] = new GridLength(compact ? 0 : 64);
        Resources["ColShare"] = new GridLength(compact ? 64 : 110);
        Resources["ShareGap"] = new Thickness(compact ? 8 : 14, 0, 0, 0);
        Resources["ColFront"] = new GridLength(compact ? 0 : 60);
        Resources["ColMulti"] = new GridLength(compact ? 0 : 60);
        Resources["ColAccDamage"] = new GridLength(compact ? 0 : 78);
        QualityTiles.Rows = compact ? 0 : 1;
        QualityTiles.Columns = compact ? 4 : 0;
        _shareBar = compact ? 52 : 96;
        Reload();
    }

    private static readonly System.Windows.Media.Effects.DropShadowEffect DropShadow = MakeShadow();
    private static System.Windows.Media.Effects.DropShadowEffect MakeShadow()
    {
        var e = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.6, Color = Colors.Black };
        e.Freeze();
        return e;
    }

    private void SetPartyOpen(bool open)
    {
        PartyToggle.IsChecked = open;
        if ((_layout & 1) != 0) SidePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PartyToggle_Click(object sender, RoutedEventArgs e) => SetPartyOpen(PartyToggle.IsChecked == true);

    /// <summary>The dropped-down party list closes on any click outside it; Esc / Back need the view to hold keyboard focus.</summary>
    private void View_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (PartyToggle.IsChecked == true && (_layout & 1) != 0 && e.OriginalSource is DependencyObject src
            && !IsInside(src, SidePanel) && !IsInside(src, PartyToggle))
            SetPartyOpen(false);
        if (_hosted && !IsKeyboardFocusWithin) Focus();
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var n = d; n is not null; n = n is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (PartyToggle.IsChecked == true && (_layout & 1) != 0) SetPartyOpen(false);
        else if (_hosted) CloseRequested?.Invoke();
        else return;
        e.Handled = true;
    }

    // ------------------------------------------------------------ sorting

    /// <summary>A column header was clicked: sort its table by that column, or turn the order around.</summary>
    private void SortHead_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } head || !Enum.TryParse<SkillColumn>(tag, out var column)) return;
        if (head.Parent == AccuracyHead) _lastAccuracyOrder = _accuracyOrder = _accuracyOrder.Toggle(column);
        else _lastSkillOrder = _skillOrder = _skillOrder.Toggle(column);
        Reload();
    }

    /// <summary>Header captions, with an arrow and gold on the column each table is sorted by.</summary>
    private void ShowSortHeads()
    {
        Show(SkillHead, _skillOrder);
        Show(AccuracyHead, _accuracyOrder);

        void Show(Grid head, SkillOrder order)
        {
            foreach (var child in head.Children)
            {
                if (child is not Button { Tag: string tag } button || !Enum.TryParse<SkillColumn>(tag, out var column)) continue;
                var label = HeadLabel(column);
                var arrow = order.Descending ? "▾" : "▴";
                // The arrow goes on the outer side, so a caption stays lined up with its column's numbers.
                button.Content = column != order.Column ? label : column == SkillColumn.Name ? $"{label} {arrow}" : $"{arrow} {label}";
                button.Foreground = (Brush)FindResource(column == order.Column ? "Gold" : "TextMute");
                button.ToolTip = T.SortTip;
            }
        }
    }

    private static string HeadLabel(SkillColumn column) => column switch
    {
        SkillColumn.Name => T.ThSkill,
        SkillColumn.Hits => T.ThHits,
        SkillColumn.Damage => T.ThDamage,
        SkillColumn.Dps => T.ThDps,
        SkillColumn.Average => T.ThAvg,
        SkillColumn.Max => T.ThMax,
        SkillColumn.Crit => T.ThCrit,
        SkillColumn.Share => T.ThShare,
        SkillColumn.Back => T.ThBack,
        SkillColumn.Front => T.ThFront,
        SkillColumn.Perfect => T.ThPerfect,
        SkillColumn.Heavy => T.ThSmite,
        SkillColumn.Multi => T.ThMulti,
        _ => column.ToString(),
    };

    private void PartyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList || PartyList.SelectedItem is not PartyItem item) return;
        if ((_layout & 1) != 0) SetPartyOpen(false);
        if (item.ActorId == _actor) return;
        _actor = item.ActorId;
        Reload();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageDps is null) return;
        PageDps.Visibility = TabDps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAccuracy.Visibility = TabAccuracy.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageRotation.Visibility = TabRotation.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageDefense.Visibility = TabDefense.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------ header buttons

    /// <summary>Copies the whole view as an image — ready to paste into Discord.</summary>
    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (ActualWidth <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap(
            (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(this);
        try
        {
            Clipboard.SetImage(bmp);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy (another app holds it) — ignore, the user can click again.
        }
    }

    /// <summary>The selected player's result or the party ranking for the game chat, or the ranking as a Discord table.</summary>
    private void CopyChat_Click(object sender, RoutedEventArgs e)
    {
        if (_view is not { } view) return;
        var button = (FrameworkElement)sender;
        var player = _actor == Combatant.UnknownSummonsId ? null : (uint?)_actor;
        ChatCopy.ShowMenu(button, button, view.Summary, player, PlayerName.Text);
    }

    private void Fights_Click(object sender, RoutedEventArgs e) => FightsRequested?.Invoke((FrameworkElement)sender);

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp("meter");

    private void Back_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Minimize_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
