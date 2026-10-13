using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.Core;
using AionMeter.Core.Combat;
using AionMeter.Core.Storage;

namespace AionMeter.App.Windows;

public partial class HistoryWindow : Window
{
    private static UiText T => UiText.Current;
    private readonly MeterService _meter;
    private readonly Controls.DragAnywhere _drag;
    private HistoryStore Store => _meter.History;
    private FightRecord? _record;
    private RecordFightView? _view;
    private HistoryEntry? _entry;

    public sealed record FightItem(
        HistoryEntry Entry, string Title, string Meta, string Dps, string When, string ResultGlyph, Brush ResultBrush,
        int Place, Brush PlaceBg, Brush PlaceFg, Brush PlaceBorder, Visibility PlaceVisibility) : INotifyPropertyChanged
    {
        private ImageSource? _portrait;

        /// <summary>Boss portrait; filled in later when it was still downloading.</summary>
        public ImageSource? Portrait
        {
            get => _portrait;
            set
            {
                if (ReferenceEquals(_portrait, value)) return;
                _portrait = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Portrait)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        // The list box tracks its items by hash: identity keeps it stable when the portrait arrives.
        public bool Equals(FightItem? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }

    public HistoryWindow(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        _drag = Controls.DragAnywhere.Attach(this);
        Loaded += (_, _) => Reload();
        Store.Changed += OnStoreChanged;
        _meter.Portraits.PortraitReady += OnPortraitReady;
        Closed += (_, _) =>
        {
            Store.Changed -= OnStoreChanged;
            _meter.Portraits.PortraitReady -= OnPortraitReady;
        };
    }

    private void OnStoreChanged() => Dispatcher.BeginInvoke(() => Reload(keepSelection: true));

    private void OnPortraitReady() => Dispatcher.BeginInvoke(() =>
    {
        if (Fights.ItemsSource is not List<FightItem> items) return;
        foreach (var item in items) item.Portrait ??= _meter.PortraitOf(item.Entry);
        if (_entry is not null) DetailPortrait.Source ??= _meter.PortraitOf(_entry);
    });

    public void Reload() => Reload(keepSelection: false);

    private void Reload(bool keepSelection)
    {
        var selected = (Fights.SelectedItem as FightItem)?.Entry.FileName;
        var all = Store.List();
        IEnumerable<HistoryEntry> shown = all;
        if (FilterBosses.IsChecked == true) shown = shown.Where(e => e.IsBoss);
        if (FilterKills.IsChecked == true) shown = shown.Where(e => e.Reason == EncounterEndReason.Kill);
        var list = shown.ToList();

        CountText.Text = list.Count == all.Count ? string.Format(T.SavedCount, all.Count) : string.Format(T.ShownOf, list.Count, all.Count);
        var green = (Brush)FindResource("Green");
        var red = (Brush)FindResource("Red");
        var mute = (Brush)FindResource("TextMute");
        Fights.ItemsSource = list.Select(e =>
        {
            var (bg, fg, border) = RankBrushes.For(e.SelfPlace);
            var dps = e.SelfDps > 0 ? Format.Compact(e.SelfDps) + "/s" : T.Party + " " + Format.Compact(e.PartyDps) + "/s";
            return new FightItem(
                e,
                _meter.DisplayTitle(e),
                $"{ZoneText(e.Zone)} · {Format.Clock(e.CombatMs)} · {e.Players} {T.PlayersShort}",
                dps,
                Relative(e.StartedAt),
                e.Reason switch { EncounterEndReason.Kill => "✔", EncounterEndReason.Wipe => "✖", _ => "•" },
                e.Reason switch { EncounterEndReason.Kill => green, EncounterEndReason.Wipe => red, _ => mute },
                e.SelfPlace, bg, fg, border, e.SelfPlace > 0 ? Visibility.Visible : Visibility.Collapsed)
            {
                Portrait = _meter.PortraitOf(e),
            };
        }).ToList();

        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count == 0)
        {
            Party.ItemsSource = null;
            DetailTitle.Text = DetailMeta.Text = "";
            DetailPortrait.Source = null;
            return;
        }
        var items = (List<FightItem>)Fights.ItemsSource;
        var again = keepSelection && selected is not null ? items.FindIndex(i => i.Entry.FileName == selected) : -1;
        Fights.SelectedIndex = again >= 0 ? again : 0;
    }

    private string ZoneText(string? zone) =>
        _meter.DisplayZone(zone) is { } z ? (z == "Open world" ? T.OpenWorld : z) : "—";

    private static string ReasonText(EncounterEndReason reason) => reason switch
    {
        EncounterEndReason.Kill => T.Kill,
        EncounterEndReason.Wipe => T.Wipe,
        EncounterEndReason.None => T.InProgress,
        _ => T.Ended,
    };

    private static string Relative(DateTimeOffset t)
    {
        var ago = DateTimeOffset.Now - t;
        if (ago.TotalMinutes < 1) return T.JustNow;
        if (ago.TotalHours < 1) return string.Format(T.MinutesAgo, (int)ago.TotalMinutes);
        if (ago.TotalDays < 1) return string.Format(T.HoursAgo, (int)ago.TotalHours);
        if (ago.TotalDays < 7) return t.ToString("ddd HH:mm", T.Culture);
        return t.ToString("dd MMM", T.Culture);
    }

    private void Fights_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Fights.SelectedItem is not FightItem item) return;
        _record = Store.Load(item.Entry.Path);
        if (_record is null) return;
        _entry = item.Entry;
        _view = new RecordFightView(_record);
        var s = _record.Summary;
        DetailTitle.Text = item.Title;
        DetailPortrait.Source = item.Portrait;
        DetailMeta.Text = $"{s.StartedAt.ToString("dd MMM yyyy HH:mm", T.Culture)} · {Format.ClockPrecise(s.CombatMs)} · " +
                          $"{Format.Compact(s.TotalDamage)} {T.DamageWord} · {Format.Compact(s.PartyDps)}/s {T.Party} · {ReasonText(s.Reason)}";
        var vm = new OverlayViewModel();
        vm.Apply(AionMeter.Service.Overlay.OverlayBuilder.Fight(s, 50, relativeToTop: true, showGear: true));
        Party.ItemsSource = vm.Rows;
    }

    private void Member_Click(object sender, MouseButtonEventArgs e)
    {
        if (_drag.JustDragged || _view is null || (sender as FrameworkElement)?.DataContext is not RowViewModel row) return;
        AppHost.Current.ShowBreakdown(_view, row.ActorId);
    }

    private void ShowInOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is not null) AppHost.Current.ShowSavedInOverlay(_entry);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Reload();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Fights.SelectedItem is not FightItem item) return;
        var answer = MessageBox.Show(this, string.Format(T.DeleteConfirm, item.Title, item.Entry.StartedAt.ToString("dd MMM HH:mm", T.Culture)), AppInfo.Name,
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        Store.Delete(item.Entry.Path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", Store.Directory) { UseShellExecute = true });

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
