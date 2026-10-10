using System.Windows;
using System.Windows.Controls;
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

namespace AionMeter.App.Windows;

public partial class BreakdownWindow : Window
{
    private readonly IFightView _view;
    private readonly DispatcherTimer _timer;
    private readonly Func<int, string> _skillName;
    private uint _actor;
    private bool _syncingList;

    public sealed record RankStyle(Brush Bg, Brush Fg, Brush Border);

    public sealed record PartyItem(uint ActorId, string Name, GameClass Class, string ShareText, int Place, RankStyle Rank, bool IsSelf)
    {
        public string PlaceText => Place > 0 ? Place.ToString() : "·";
        public Brush NameBrush => IsSelf ? SelfBrush : Brushes.WhiteSmoke;
        public FontWeight NameWeight => IsSelf ? FontWeights.Bold : FontWeights.Normal;
        private static readonly Brush SelfBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xCB, 0x82));
    }

    public sealed record SkillItem(
        string Name, Brush Swatch, ImageSource? Icon, string Hits, string Damage, string Dps, string Avg, string Max, string Crit,
        string Share, double ShareWidth, string DotTag, string Tooltip,
        string Back, string Front, string Perfect, string Heavy, string Multi);

    private static UiText T => UiText.Current;

    // Each table sorts by a clicked column header; a new breakdown starts with the order chosen last (while the app runs).
    private static SkillOrder _lastSkillOrder = SkillOrder.Default;
    private static SkillOrder _lastAccuracyOrder = SkillOrder.Default;
    private SkillOrder _skillOrder = _lastSkillOrder;
    private SkillOrder _accuracyOrder = _lastAccuracyOrder;

    private readonly SkillIcons _icons;
    private readonly Func<int, string> _serverName;
    private bool _iconRefreshQueued;

    private readonly BossPortraits? _portraits;

    public BreakdownWindow(IFightView view, uint actorId, GameData data, SkillIcons icons, BossPortraits? portraits = null)
    {
        _view = view;
        _actor = actorId;
        _skillName = data.SkillName;
        _serverName = data.ServerName;
        _icons = icons;
        _portraits = portraits;
        InitializeComponent();
        ShowSortHeads();
        DragAnywhere.Attach(this);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_view.IsLive) Reload();
            else _timer.Stop();
        };
        Loaded += (_, _) =>
        {
            Reload();
            if (_view.IsLive) _timer.Start();
        };
        _icons.IconReady += OnIconReady;
        if (_portraits is not null) _portraits.PortraitReady += OnIconReady;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _icons.IconReady -= OnIconReady;
            if (_portraits is not null) _portraits.PortraitReady -= OnIconReady;
        };
    }

    /// <summary>Icons arrive one by one from the CDN: coalesce them into one redraw.</summary>
    private void OnIconReady()
    {
        if (_iconRefreshQueued) return;
        _iconRefreshQueued = true;
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(400);
            _iconRefreshQueued = false;
            Reload();
        });
    }

    public bool Shows(IFightView view) => ReferenceEquals(view, _view);

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
        Reload();
    }

    /// <summary>Fills the window without it being shown (offscreen render tool).</summary>
    public void ReloadNow() => Reload();

    private void Reload()
    {
        var summary = _view.Summary();
        if (summary is null) return;

        FightTitle.Text = summary.Title;
        FightPortrait.Source = _portraits?.Of(summary);
        FightMeta.Text = $"{summary.StartedAt.ToString("dd MMM HH:mm", T.Culture)} · {Format.Clock(summary.CombatMs)} · {Format.Compact(summary.PartyDps)}/s {T.PartyDpsSuffix}"
                         + (summary.MostDamageUnsent ? $"\n{T.DamageUnsentLine(summary)}" : "")
                         + (summary.Zone is { } z ? $"\n{z}" : "");
        FightMeta.ToolTip = summary.MostDamageUnsent ? T.DamageUnsentTip : null;

        _syncingList = true;
        PartyList.ItemsSource = summary.Combatants
            .Select((c, i) =>
            {
                var place = c.IsUnknownSummons ? 0 : i + 1;
                var (bg, fg, border) = RankBrushes.For(place);
                return new PartyItem(c.ActorId, T.CombatantLabel(c.ActorId, c.Name, c.Class), c.Class, Format.Share(c.Share), place, new RankStyle(bg, fg, border), c.IsSelf);
            })
            .ToList();
        PartyList.SelectedItem = ((IEnumerable<PartyItem>)PartyList.ItemsSource).FirstOrDefault(p => p.ActorId == _actor);
        _syncingList = false;

        var detail = _view.Detail(_actor);
        if (detail is null) return;
        ShowDetail(summary, detail);
    }

    private void ShowDetail(EncounterSnapshot summary, CombatantDetail d)
    {
        Title = $"{d.Name} — {summary.Title} · {AppInfo.Name}";
        HeaderGlyph.Class = d.Class;
        PlayerName.Text = T.CombatantLabel(d.ActorId, d.Name, d.Class);
        var server = d.ServerId != 0 ? $" · {_serverName(d.ServerId)} {GameData.RegionOf(d.ServerId)}".TrimEnd() : "";
        var gear = (d.GearScore > 0 ? $" · {T.Chat.GearScore} {d.GearScore}" : "") +
                   (d.CombatPower > 0 ? $" · {T.Chat.CombatPower} {Format.Grouped(d.CombatPower)}" : "");
        PlayerMeta.Text = $"{T.ClassName(d.Class)}{server}{gear} · {summary.Title}" + (d.IsSelf ? $" · {T.You}" : "");
        var place = d.ActorId == Combatant.UnknownSummonsId ? 0 : summary.Combatants.ToList().FindIndex(c => c.ActorId == d.ActorId) + 1;
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
        foreach (var other in _view.AllDetails().Where(o => o.ActorId != d.ActorId))
            series.Add(new ChartSeries(other.Name, other.PerSecond, ClassVisuals.ColorOf(other.Class), false));
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
            maxShare > 0 ? 96 * s.Share / maxShare : 0, s.Hits == 0 && s.DotTicks > 0 ? "DoT" : "", tooltip,
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
            foreach (var button in head.Children.OfType<Button>())
            {
                if (button.Tag is not string tag || !Enum.TryParse<SkillColumn>(tag, out var column)) continue;
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
        if (_syncingList || PartyList.SelectedItem is not PartyItem item || item.ActorId == _actor) return;
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

    /// <summary>Copies the whole window as an image — ready to paste into Discord.</summary>
    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (Content is not FrameworkElement root) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
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
        var button = (FrameworkElement)sender;
        var player = _actor == Combatant.UnknownSummonsId ? null : (uint?)_actor;
        ChatCopy.ShowMenu(button, button, _view.Summary, player, PlayerName.Text);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
