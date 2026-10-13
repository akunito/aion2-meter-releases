using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;

namespace AionMeter.App.Views;

/// <summary>Boss respawn timers; a tab of the overlay card and the content of the stand-alone <see cref="Windows.BossTimersWindow"/> (A2M-39).</summary>
public partial class BossTimersView : UserControl
{
    private static UiText T => UiText.Current;
    private static readonly int[] Presets = [0, 15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440];
    private static readonly int[] AlertChoices = [0, 1, 2, 5, 10];

    private readonly MeterService _meter;
    private readonly ObservableCollection<TimerRow> _rows = new();
    private readonly DispatcherTimer _clock;
    private int _server;              // whose timers are shown
    private bool _followCurrent = true; // until a server is picked by hand, follow the character's

    /// <summary>One boss in the list; refreshed in place every second.</summary>
    public sealed class TimerRow(int npcCode) : ObservableObject
    {
        private string _name = "", _meta = "", _status = "", _statusDetail = "", _respawnTip = "", _respawnLabel = "", _sourceTip = "";
        private bool _watch;
        private Brush _statusBrush = Brushes.White, _frameBrush = Brushes.Transparent;
        private double _statusSize = 19;
        private ImageSource? _portrait;

        public int NpcCode { get; } = npcCode;
        public int Respawn { get; set; }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Meta { get => _meta; set => Set(ref _meta, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
        public string RespawnTip { get => _respawnTip; set => Set(ref _respawnTip, value); }
        public string RespawnLabel { get => _respawnLabel; set => Set(ref _respawnLabel, value); }
        /// <summary>Where the time comes from: the in-game list (and when it was read) or the meter.</summary>
        public string SourceTip { get => _sourceTip; set => Set(ref _sourceTip, value); }
        public bool Watch { get => _watch; set => Set(ref _watch, value); }
        public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
        public Brush FrameBrush { get => _frameBrush; set => Set(ref _frameBrush, value); }
        /// <summary>Countdowns are big; words ("SHOULD BE UP") a little smaller so they fit.</summary>
        public double StatusSize { get => _statusSize; set => Set(ref _statusSize, value); }
        public ImageSource? Portrait { get => _portrait; set => Set(ref _portrait, value); }
    }

    /// <summary>Under 700 px (the overlay tab) each row's actions go on a second line.</summary>
    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(BossTimersView), new PropertyMetadata(false));
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    private volatile bool _shown;

    public BossTimersView(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        List.ItemsSource = _rows;
        UpdateAlertButton();

        // A hidden tab (another tab in front, overlay hidden) neither ticks nor reloads; it reloads once when shown again.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Reload();
        Loaded += (_, _) =>
        {
            _meter.Timers.Changed += OnTimersChanged;
            if (!IsVisible) return;
            Reload();
            _clock.Start();
        };
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            _meter.Timers.Changed -= OnTimersChanged;
        };
        IsVisibleChanged += (_, e) =>
        {
            _shown = e.NewValue is true;
            if (!_shown) { _clock.Stop(); return; }
            UpdateAlertButton();   // the tab and the window share the setting
            Reload();
            if (IsLoaded) _clock.Start();
        };
        SizeChanged += (_, e) => { if (e.WidthChanged) Compact = e.NewSize.Width < 700; };
    }

    // Changed comes from the capture thread: IsVisible cannot be read there, _shown can
    private void OnTimersChanged() { if (_shown) Dispatcher.BeginInvoke(Reload); }

    /// <summary>Fills the view without it being shown (offscreen render tool).</summary>
    public void ReloadNow()
    {
        if (ActualWidth > 0) Compact = ActualWidth < 700;
        Reload();
    }

    private void Reload()
    {
        var now = DateTimeOffset.Now;
        if (_followCurrent) _server = _meter.Timers.CurrentServer;
        UpdateServerButton();
        var timers = _meter.Timers.ForServer(_server)
            .OrderBy(t => t.AliveNow || t.NextSpawn <= now ? 0 : t.NextSpawn is not null ? 1 : 2)
            .ThenBy(t => t.NextSpawn ?? DateTimeOffset.MaxValue)
            .ThenByDescending(t => t.LastKill ?? DateTimeOffset.MinValue)
            .Where(t => t.NpcCode > 0) // slots whose boss is not named yet stay out of sight until it is
            .Where(t => FilterWatched.IsChecked != true || t.Watch)
            .ToList();

        for (var i = _rows.Count - 1; i >= 0; i--)
            if (timers.All(t => t.NpcCode != _rows[i].NpcCode)) _rows.RemoveAt(i);
        for (var i = 0; i < timers.Count; i++)
        {
            var t = timers[i];
            var at = IndexOf(t.NpcCode);
            TimerRow row;
            if (at < 0)
            {
                row = NewRow(t.NpcCode);
                _rows.Insert(i, row);
            }
            else
            {
                row = _rows[at];
                if (at != i) _rows.Move(at, i);
            }
            Fill(row, t, now);
        }
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private int IndexOf(int code)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].NpcCode == code) return i;
        return -1;
    }

    private static TimerRow NewRow(int code) => new(code);

    private void Fill(TimerRow row, BossTimer t, DateTimeOffset now)
    {
        // A list slot whose boss is not known yet is keyed by -slot: "Boss #5".
        row.Name = t.NpcCode > 0 ? _meter.Data.NpcName(t.NpcCode) : string.Format(T.UnknownSlotBoss, -t.NpcCode % 100);
        row.Portrait = t.NpcCode > 0 ? _meter.Portraits.Get(t.NpcCode) : null;
        row.Watch = t.Watch;
        row.SourceTip = t.FromGame && t.ListedAt is { } listed ? string.Format(T.SourceGame, When(listed, now)) : T.SourceMeter;
        var zone = _meter.Timers.MapName(t.MapId, T.Code)
                   ?? (t.SlotId != 0 ? string.Format(T.MapNumber, t.MapId) : t.MapId != 0 ? _meter.Data.MapName(t.MapId) : t.Zone);
        if (zone == "Open world") zone = T.OpenWorld;
        var death = t.LastKill is { } k ? string.Format(T.TimerKilled, When(k, now))
            : t.LastSeenDead is { } d ? string.Format(T.TimerFoundDead, When(d, now)) : null;
        row.Meta = string.Join(" · ", new[] { zone, death }.Where(x => !string.IsNullOrEmpty(x)));
        row.Respawn = t.RespawnMinutes;
        row.RespawnLabel = t.RespawnMinutes > 0 ? (t.RespawnLearned ? "≈ " : "") + T.Duration(t.RespawnMinutes)
            : t.FromGame ? T.RespawnFromGame : T.RespawnUnknown;
        row.RespawnTip = t.RespawnLearned ? T.TimerLearned : T.TipRespawn;
        row.StatusSize = 19;

        var green = (Brush)FindResource("Green");
        if (t.AliveNow)
        {
            row.StatusSize = 16;
            row.Status = T.TimerAlive;
            row.StatusDetail = t.AliveSince is { } since ? string.Format(T.TimerAliveSince, When(since, now)) : "";
            row.StatusBrush = green;
            row.FrameBrush = (Brush)FindResource("GoldDim");
        }
        else if (t.NextSpawn is { } next && next > now)
        {
            var left = next - now;
            row.Status = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
            row.StatusDetail = string.Format(T.TimerAt, When(next, now));
            row.StatusBrush = left.TotalMinutes <= Math.Max(1, _meter.Settings.BossAlertMinutes)
                ? (Brush)FindResource("Amber")
                : (Brush)FindResource("GoldBright");
            row.FrameBrush = Brushes.Transparent;
        }
        else if (t.NextSpawn is { } due)
        {
            row.StatusSize = 14;
            row.Status = T.TimerDue;
            row.StatusDetail = string.Format(T.TimerSince, When(due, now));
            row.StatusBrush = green;
            row.FrameBrush = (Brush)FindResource("GoldDim");
        }
        else
        {
            row.Status = "—";
            row.StatusDetail = t.FromGame && t.ListedTime is null ? T.TimerNoTime : T.TimerNeedsInterval;
            row.StatusBrush = (Brush)FindResource("TextMute");
            row.FrameBrush = Brushes.Transparent;
        }
    }

    /// <summary>"14:05", or "05.10 14:05" when not today.</summary>
    private static string When(DateTimeOffset t, DateTimeOffset now) =>
        t.LocalDateTime.Date == now.LocalDateTime.Date ? t.ToString("HH:mm") : t.ToString("dd.MM HH:mm");

    private void Killed_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimerRow row) _meter.Timers.MarkKilled(_server, row.NpcCode);
    }

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimerRow row) _meter.Timers.SetWatch(_server, row.NpcCode, !row.Watch);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Reload();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TimerRow row) _meter.Timers.Remove(_server, row.NpcCode);
    }

    /// <summary>Respawn interval presets as a menu under the row's chip.</summary>
    private void Respawn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TimerRow row } chip) return;
        var minutes = Presets.Contains(row.Respawn) ? Presets : Presets.Append(row.Respawn).Order().ToArray();
        ShowChoices(chip, minutes, row.Respawn, m => m == 0 ? T.RespawnUnknown : T.Duration(m),
            m => _meter.Timers.SetRespawn(_server, row.NpcCode, m));
    }

    private void Alert_Click(object sender, RoutedEventArgs e) =>
        ShowChoices(AlertButton, AlertChoices, _meter.Settings.BossAlertMinutes, m => m == 0 ? T.AlertOff : T.Duration(m), m =>
        {
            _meter.Settings.BossAlertMinutes = m;
            _meter.Settings.Save();
            UpdateAlertButton();
        });

    /// <summary>"Markutan EU ▾": the server whose timers are shown.</summary>
    private void UpdateServerButton() => ServerButton.Content = ServerLabel(_server) + "  ▾";

    private string ServerLabel(int server)
    {
        if (server == 0) return T.ServerUnknown;
        var region = Core.Game.GameData.RegionOf(server);
        return $"{_meter.Data.ServerName(server)} {region}".Trim();
    }

    /// <summary>Other servers' timers (another character's): pick whose to show; the current one is marked.</summary>
    private void Server_Click(object sender, RoutedEventArgs e)
    {
        var current = _meter.Timers.CurrentServer;
        var servers = _meter.Timers.Servers();
        if (servers.Count == 0) servers = [current];
        var menu = new ContextMenu { PlacementTarget = ServerButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var s in servers)
        {
            var item = new MenuItem
            {
                Header = ServerLabel(s) + (s == current ? "  · " + T.ServerYours : ""),
                IsCheckable = true,
                IsChecked = s == _server,
            };
            var chosen = s;
            item.Click += (_, _) =>
            {
                _server = chosen;
                _followCurrent = chosen == current;
                Reload();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void UpdateAlertButton()
    {
        var m = _meter.Settings.BossAlertMinutes;
        AlertButton.Content = (m == 0 ? T.AlertOff : T.Duration(m)) + "  ▾";
    }

    private static void ShowChoices(FrameworkElement anchor, IEnumerable<int> values, int current, Func<int, string> label, Action<int> pick)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var v in values)
        {
            var item = new MenuItem { Header = label(v), IsCheckable = true, IsChecked = v == current };
            var chosen = v;
            item.Click += (_, _) => pick(chosen);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp("timers");

    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
