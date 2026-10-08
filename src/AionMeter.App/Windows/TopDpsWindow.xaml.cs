using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.Core;
using AionMeter.Core.Events;
using AionMeter.Core.Ranking;

namespace AionMeter.App.Windows;

/// <summary>The ten best DPS per class the meter has seen, each expandable into skill shares, opener, repeated sequences and cadence.</summary>
public partial class TopDpsWindow : Window
{
    private static UiText T => UiText.Current;

    private readonly MeterService _meter;
    private readonly ObservableCollection<ClassTab> _tabs = new();
    private readonly ObservableCollection<Row> _rows = new();
    private GameClass _class;

    public sealed class ClassTab(GameClass c, string label, bool selected)
    {
        public GameClass Class { get; } = c;
        public string Label { get; } = label;
        public bool Selected { get; set; } = selected;
    }

    public sealed class Row(TopEntry e, int rank) : ObservableObject
    {
        private bool _expanded;
        public TopEntry Entry { get; } = e;
        public string Rank { get; } = rank.ToString();
        public string Name { get; } = e.Name;
        public string Meta { get; } = $"{e.Title}{(string.IsNullOrEmpty(e.Zone) ? "" : " · " + e.Zone)} · {e.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Format.Compact(e.Damage)} dmg";
        public string Dps { get; } = Format.Compact(e.Dps) + "/s";
        public string Duration { get; } = TimeSpan.FromMilliseconds(e.CombatMs).ToString(@"m\:ss");
        public Brush NameBrush { get; } = e.IsSelf ? (Brush)Application.Current.FindResource("Gold") : (Brush)Application.Current.FindResource("Text");
        public Brush FrameBrush { get; } = e.IsSelf ? (Brush)Application.Current.FindResource("GoldDim") : Brushes.Transparent;
        public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) Raise(nameof(DetailVisibility)); } }
        public Visibility DetailVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;
        public string SkillsText { get; set; } = "";
        public string OpeningText { get; set; } = "";
        public string RepeatedText { get; set; } = "";
        public string CadenceText { get; set; } = "";
    }

    public TopDpsWindow(MeterService meter)
    {
        _meter = meter;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        ClassTabs.ItemsSource = _tabs;
        List.ItemsSource = _rows;
        _class = meter.Tracker.SelfClass is var sc && sc != GameClass.Unknown ? sc : GameClass.Unknown;
        Loaded += (_, _) => Reload();
        _meter.Top.Changed += OnChanged;
        Closed += (_, _) => _meter.Top.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Reload);

    private void Reload()
    {
        var classes = _meter.Top.Classes;
        if (_class == GameClass.Unknown || !classes.Contains(_class)) _class = classes.Count > 0 ? classes[0] : GameClass.Unknown;
        _tabs.Clear();
        foreach (var c in classes) _tabs.Add(new ClassTab(c, $"{T.ClassName(c)} ({_meter.Top.For(c).Count})", c == _class));
        CountText.Text = _meter.Top.Count > 0 ? string.Format(T.TopCount, _meter.Top.Count) : "";
        FillRows();
    }

    private void FillRows()
    {
        var expanded = _rows.Where(r => r.Expanded).Select(r => r.Entry.Name).ToHashSet();
        _rows.Clear();
        var rank = 0;
        foreach (var e in _meter.Top.For(_class))
        {
            var row = new Row(e, ++rank);
            if (expanded.Contains(e.Name)) Expand(row);
            _rows.Add(row);
        }
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Expand(Row row)
    {
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
        var path = Path.Combine(_meter.History.Directory, row.Entry.Fight);
        var record = _meter.History.Load(path);
        if (record is null) return;
        var who = record.Details.FirstOrDefault(d => d.Name == row.Entry.Name && d.Class == row.Entry.Class);
        if (who is null) return;
        AppHost.Current.ShowBreakdown(new RecordFightView(record), who.ActorId);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
