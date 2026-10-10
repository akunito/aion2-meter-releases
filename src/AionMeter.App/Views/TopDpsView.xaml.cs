using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Net.Http;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.Core;
using AionMeter.Core.Armory;
using AionMeter.Core.Events;
using AionMeter.Core.Leaderboard;
using AionMeter.Core.Ranking;

namespace AionMeter.App.Views;

/// <summary>The ten best DPS per class the meter has seen, each expandable into skill shares, opener, repeated sequences and cadence.</summary>
public partial class TopDpsView : UserControl
{
    private static UiText T => UiText.Current;

    private readonly MeterService _meter;
    private readonly ObservableCollection<ClassTab> _tabs = new();
    private readonly ObservableCollection<Row> _rows = new();
    private GameClass _class;
    private int _scope;             // 0 = own fights, 1 = own server, 2 = region
    private int _boardLoad;         // bumps on every scope/class change so a late answer is dropped

    public sealed class ClassTab(GameClass c, string label, bool selected)
    {
        public GameClass Class { get; } = c;
        public string Label { get; } = label;
        public bool Selected { get; set; } = selected;
    }

    /// <summary>A ranked player: either one of our fights (Entry, with rotation) or a combat-power ranking row (Board).</summary>
    public sealed class Row : ObservableObject
    {
        private bool _expanded;
        private string _profileText = "";

        public Row(TopEntry e, int rank)
        {
            Entry = e;
            Rank = rank.ToString();
            Name = e.Name;
            ServerName = "";
            Meta = $"{e.Title}{(string.IsNullOrEmpty(e.Zone) ? "" : " · " + e.Zone)} · {e.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Format.Compact(e.Damage)} dmg";
            Dps = Format.Compact(e.Dps) + "/s";
            Duration = TimeSpan.FromMilliseconds(e.CombatMs).ToString(@"m\:ss");
            var self = e.IsSelf;
            NameBrush = self ? (Brush)Application.Current.FindResource("Gold") : (Brush)Application.Current.FindResource("Text");
            FrameBrush = self ? (Brush)Application.Current.FindResource("GoldDim") : Brushes.Transparent;
        }

        public Row(LeaderboardRow b, int rank, bool self)
        {
            Board = b;
            Rank = rank.ToString();
            Name = b.Name;
            ServerName = b.ServerName;
            Meta = $"{b.ServerName} · lv{b.Level} · #{b.Rank} in {b.Region.ToUpperInvariant()}{(b.LastInfoAt is { } t ? " · seen " + t.ToLocalTime().ToString("MM-dd HH:mm") : "")}";
            Dps = Format.Compact(b.CombatPower) + " CP";
            Duration = "item level " + b.ItemLevel;
            NameBrush = self ? (Brush)Application.Current.FindResource("Gold") : (Brush)Application.Current.FindResource("Text");
            FrameBrush = self ? (Brush)Application.Current.FindResource("GoldDim") : Brushes.Transparent;
        }

        public TopEntry? Entry { get; }
        public LeaderboardRow? Board { get; }
        public string Rank { get; }
        public string Name { get; }
        public string ServerName { get; }
        public string Meta { get; }
        public string Dps { get; }
        public string Duration { get; }
        public Brush NameBrush { get; }
        public Brush FrameBrush { get; }
        public Visibility RotationVisibility => Entry is null ? Visibility.Collapsed : Visibility.Visible;
        public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) Raise(nameof(DetailVisibility)); } }
        public Visibility DetailVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;
        public string SkillsText { get; set; } = "";
        public string OpeningText { get; set; } = "";
        public string RepeatedText { get; set; } = "";
        public string CadenceText { get; set; } = "";
        public string ProfileText { get => _profileText; set => Set(ref _profileText, value); }
    }

    public TopDpsView(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        ClassTabs.ItemsSource = _tabs;
        List.ItemsSource = _rows;
        _class = meter.Tracker.SelfClass is var sc && sc != GameClass.Unknown ? sc : GameClass.Unknown;
        Loaded += (_, _) => Reload();
        _meter.Top.Changed += OnChanged;
        Unloaded += (_, _) => _meter.Top.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Reload);

    private static readonly GameClass[] AllClasses =
        [GameClass.Gladiator, GameClass.Templar, GameClass.Ranger, GameClass.Assassin, GameClass.Sorcerer, GameClass.Elementalist, GameClass.Cleric, GameClass.Chanter];

    private void Reload()
    {
        var server = _meter.Data.ServerName(_meter.Tracker.SelfServerId);
        ScopeServer.Content = string.Format(T.TopScopeServer, server.Length > 0 ? server : "?");
        ScopeServer.IsEnabled = server.Length > 0;
        IReadOnlyList<GameClass> classes = _scope == 0 ? _meter.Top.Classes : AllClasses;
        if (_class == GameClass.Unknown || !classes.Contains(_class)) _class = classes.Count > 0 ? classes[0] : GameClass.Unknown;
        _tabs.Clear();
        foreach (var c in classes) _tabs.Add(new ClassTab(c, _scope == 0 ? $"{T.ClassName(c)} ({_meter.Top.For(c).Count})" : T.ClassName(c), c == _class));
        CountText.Text = _scope == 0 && _meter.Top.Count > 0 ? string.Format(T.TopCount, _meter.Top.Count) : "";
        HintText.Text = _scope == 0 ? T.TopHint : T.TopBoardHint;
        FillRows();
    }

    private void FillRows()
    {
        var expanded = _rows.Where(r => r.Expanded).Select(r => r.Name).ToHashSet();
        _rows.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        if (_scope != 0) { _ = FillBoardAsync(++_boardLoad, expanded); return; }
        var rank = 0;
        foreach (var e in _meter.Top.For(_class))
        {
            var row = new Row(e, ++rank);
            if (expanded.Contains(e.Name)) Expand(row);
            _rows.Add(row);
        }
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Own server or whole region: the ten strongest of the class by combat power, from today's ranking snapshot.</summary>
    private async Task FillBoardAsync(int load, HashSet<string> expanded)
    {
        var server = _scope == 1 ? _meter.Data.ServerName(_meter.Tracker.SelfServerId) : null;
        var cls = _class;
        CountText.Text = T.TopBoardLoading;
        LoadingBox.Visibility = Visibility.Visible;   // the first Server/Europe read walks several ranking pages (Diego: "tarda en cargar la primera vez")
        try
        {
            var rows = await _meter.Leaderboard.TopAsync("eu", cls, server);
            if (load != _boardLoad) return;
            LoadingBox.Visibility = Visibility.Collapsed;
            var me = _meter.Tracker.SelfName;
            var rank = 0;
            foreach (var b in rows)
            {
                var row = new Row(b, ++rank, me is not null && string.Equals(b.Name, me, StringComparison.OrdinalIgnoreCase) && b.ServerName == _meter.Data.ServerName(_meter.Tracker.SelfServerId));
                if (expanded.Contains(b.Name)) row.Expanded = true;
                _rows.Add(row);
            }
            CountText.Text = "";
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (load != _boardLoad) return;
            LoadingBox.Visibility = Visibility.Collapsed;
            Log.Error("Leaderboard read failed", ex);
            CountText.Text = string.Format(T.TopBoardFailed, ex.Message);
        }
    }

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string tag }) return;
        var scope = int.Parse(tag);
        if (scope == _scope) return;
        _scope = scope;
        _boardLoad++;
        Reload();
    }

    private void Expand(Row row)
    {
        if (row.Entry is null) { row.Expanded = true; return; }
        if (row.SkillsText.Length == 0)
        {
            var d = _meter.Data;
            var e = row.Entry;
            var sb = new StringBuilder();
            foreach (var s in e.Skills.OrderByDescending(s => s.Share).Take(12))
                sb.Append(sb.Length > 0 ? "   " : "").Append(s.Name).Append(' ').Append((s.Share * 100).ToString("0")).Append("% ×").Append(s.Hits);
            row.SkillsText = sb.ToString();
            row.OpeningText = string.Join(" → ", TopDpsStore.Opening(e, 16).Select(d.SkillName));
            sb.Clear();
            foreach (var (skills, count) in TopDpsStore.Frequent(e, 3, 5))
                sb.Append(sb.Length > 0 ? "\n" : "").Append(string.Join(" → ", skills.Select(d.SkillName))).Append("   ×").Append(count);
            row.RepeatedText = sb.Length > 0 ? sb.ToString() : "—";
            sb.Clear();
            foreach (var (skill, casts, perMin, gap) in TopDpsStore.Cadence(e).Take(10))
                sb.Append(sb.Length > 0 ? "   " : "").Append(d.SkillName(skill)).Append(' ').Append(perMin.ToString("0.0")).Append("/min");
            row.CadenceText = sb.ToString();
        }
        row.Expanded = true;
    }

    private void Class_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: GameClass c } && c != _class) { _class = c; FillRows(); }
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Row row })
        {
            if (row.Expanded) row.Expanded = false;
            else Expand(row);
        }
    }

    /// <summary>Opens the saved fight's breakdown for this player (the full skill table with crit/back rates).</summary>
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Row row }) return;
        if (row.Entry is null) return;
        var path = Path.Combine(_meter.History.Directory, row.Entry.Fight);
        var record = _meter.History.Load(path);
        if (record is null) return;
        var who = record.Details.FirstOrDefault(d => d.Name == row.Entry.Name && d.Class == row.Entry.Class);
        if (who is null) return;
        AppHost.Current.ShowBreakdown(new RecordFightView(record), who.ActorId);
    }

    /// <summary>Official character page for this player: stats, gear, Dp skills, Daevanion sums, skill levels of the rotation.</summary>
    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Row row }) return;
        var server = ServerOf(row);
        if (server == 0) { row.ProfileText = T.TopProfileMissing; return; }
        var cached = _meter.Armory.Cached(row.Name, server);
        if (cached is not null) row.ProfileText = ProfileFormatter.Text(cached, row.Entry, _meter.Data);
        row.ProfileText = cached is null ? T.TopProfileLoading : row.ProfileText;
        try
        {
            var p = await _meter.Armory.LookupAsync(row.Name, server);
            row.ProfileText = p is null ? T.TopProfileMissing : ProfileFormatter.Text(p, row.Entry, _meter.Data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Error("Armory lookup failed", ex);
            if (cached is null) row.ProfileText = string.Format(T.TopProfileFailed, ex.Message);
        }
    }

    private int ServerOf(Row row)
    {
        if (row.Entry is { } e) return e.ServerId != 0 ? e.ServerId : _meter.Tracker.SelfServerId;
        foreach (var (id, name) in _meter.Data.Servers)
            if (string.Equals(name, row.ServerName, StringComparison.OrdinalIgnoreCase)) return id;
        return 0;
    }

    public event Action? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
