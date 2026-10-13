using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AionMeter.Linux.Shell;
using AionMeter.Service;

namespace AionMeter.Linux.Views;

/// <summary>
/// Boss respawn timers (WPF BossTimersView, A2M-39) as the card's "timers" tab and its pop-out. The list is the viewer engine's BossTimers,
/// re-read from boss-timers.json by mtime (the daemon writes it: game lists, kills, alerts) and at once on `timers-changed`. The buttons
/// (Killed, bell, respawn interval, remove) are `timer` commands to the daemon, which owns the file; without a daemon they are disabled.
/// The alert lead time is a setting (settings.json, UI-owned). The last alert shows as a line for 60 s (no sound on Linux yet).
/// </summary>
public sealed class BossTimersView : HostedView
{
    private static UiText T => UiText.Current;
    private static readonly int[] Presets = [0, 15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440];
    private static readonly int[] AlertChoices = [0, 1, 2, 5, 10];
    private const string PortraitBase = "https://metabot.gg/web/aion2/npcs/";
    private const double CompactBelow = 700;

    private readonly StackPanel _list;
    private readonly TextBlock _empty, _hint, _announce;
    private readonly Border _serverChip, _alertChip, _filterAll, _filterWatched;
    private readonly DispatcherTimer _clock;
    private readonly Dictionary<int, Row> _rows = new();
    private readonly List<int> _order = new();
    private Dictionary<int, string>? _portraitFiles;
    private int _server;
    private bool _followCurrent = true, _watchedOnly, _compact, _shown;

    private sealed class Row
    {
        public required Border Frame;
        public required Grid Grid;
        public required Border Portrait;
        public required TextBlock Name, Meta, Status, Detail, RespawnLabel;
        public required StackPanel Actions, StatusBox;
        public required Avalonia.Controls.Shapes.Path Bell;
        public required Border BellBox, KilledChip, RespawnChip, RemoveButton;
        public int Respawn;
        public bool Watch, PortraitSet;
    }

    public BossTimersView(AppShell shell) : base(shell, "timers", T.TimersTitle)
    {
        _serverChip = UiKit.Chip("…", ShowServerMenu, T.TipServer);
        _serverChip.Margin = new Thickness(0, 0, 4, 0);
        HeaderRight.Children.Add(_serverChip);

        var top = new StackPanel { Margin = new Thickness(18, 0, 18, 8) };
        var filterRow = new DockPanel { Margin = new Thickness(-4, 0, 0, 6) };
        var alert = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        alert.Children.Add(new TextBlock { Text = T.AlertLabel, Foreground = Visuals.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        alert.Children.Add(_alertChip = UiKit.Chip("…", ShowAlertMenu));
        DockPanel.SetDock(alert, Dock.Right);
        filterRow.Children.Add(alert);
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        filters.Children.Add(_filterAll = TabToggle(T.FilterAll, () => SetFilter(false)));
        filters.Children.Add(_filterWatched = TabToggle(T.FilterWatched, () => SetFilter(true)));
        filterRow.Children.Add(filters);
        top.Children.Add(filterRow);
        top.Children.Add(_announce = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Amber, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), IsVisible = false });
        top.Children.Add(_hint = new TextBlock { Text = T.TimersHint, FontSize = 11.5, Foreground = Visuals.TextMute, TextWrapping = TextWrapping.Wrap });

        _list = new StackPanel();
        var listHost = new Grid();
        listHost.Children.Add(_list);
        listHost.Children.Add(_empty = new TextBlock { Text = T.TimersEmpty, TextWrapping = TextWrapping.Wrap, Foreground = Visuals.TextMute, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(30, 40, 30, 0), MaxWidth = 380, IsVisible = false });
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Place(top, 0, 0);
        body.Place(UiKit.Scroll(listHost, new Thickness(14, 0, 14, 12)), 1, 0);
        SetBody(body);
        SyncFilter();

        // a hidden tab neither ticks nor reloads; it reloads once when shown again
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Reload();
        shell.Daemon.TimersChanged += () => { if (_shown) Reload(); };
        shell.Daemon.BossAlert += _ => { if (_shown) Reload(); };
        shell.Daemon.ConnectionChanged += _ => { if (_shown) Reload(); };
        shell.EngineReady += () => { if (_shown) Reload(); };
        shell.Icons.Loaded += () => { foreach (var r in _rows.Values) if (!r.PortraitSet) r.PortraitSet = SetPortrait(r, r.Frame.Tag is int code ? code : 0); };
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) ApplyCompact(e.NewSize.Width < CompactBelow); };
    }

    public override void Shown()
    {
        _shown = true;
        UpdateAlertChip();   // the tab and the pop-out share the setting
        Reload();
        _clock.Start();
    }

    public override void Hidden()
    {
        _shown = false;
        _clock.Stop();
    }

    public override void LanguageChanged()
    {
        base.LanguageChanged();
        Title = T.TimersTitle;
        _hint.Text = T.TimersHint;
        _empty.Text = T.TimersEmpty;
        foreach (var r in _rows.Values) ChipText(r.KilledChip, T.BtnKilled);
        Reload();
    }

    // ------------------------------------------------------------ list

    private void Reload()
    {
        if (Shell.Engine is not { } e) { _empty.Text = T.FightLoading; _empty.IsVisible = true; return; }
        var timers = e.Timers;
        timers.ReloadIfChanged();
        var now = DateTimeOffset.Now;
        if (_followCurrent) _server = timers.CurrentServer;
        ChipText(_serverChip, ServerLabel(e, _server) + "  ▾");
        var list = new List<BossTimer>();
        foreach (var t in timers.ForServer(_server))
            if (t.NpcCode > 0 && (!_watchedOnly || t.Watch)) list.Add(t);   // slots whose boss is not named yet stay out of sight until it is
        list.Sort((a, b) =>
        {
            int Group(BossTimer t) => t.AliveNow || t.NextSpawn <= now ? 0 : t.NextSpawn is not null ? 1 : 2;
            var c = Group(a).CompareTo(Group(b));
            if (c != 0) return c;
            c = (a.NextSpawn ?? DateTimeOffset.MaxValue).CompareTo(b.NextSpawn ?? DateTimeOffset.MaxValue);
            return c != 0 ? c : (b.LastKill ?? DateTimeOffset.MinValue).CompareTo(a.LastKill ?? DateTimeOffset.MinValue);
        });

        var same = list.Count == _order.Count;
        for (var i = 0; same && i < list.Count; i++) same = list[i].NpcCode == _order[i];
        if (!same)
        {
            _order.Clear();
            _list.Children.Clear();
            var keep = new HashSet<int>();
            foreach (var t in list)
            {
                if (!_rows.TryGetValue(t.NpcCode, out var row)) _rows[t.NpcCode] = row = NewRow(t.NpcCode);
                keep.Add(t.NpcCode);
                _order.Add(t.NpcCode);
                _list.Children.Add(row.Frame);
            }
            foreach (var code in _rows.Keys.ToList()) if (!keep.Contains(code)) _rows.Remove(code);
        }
        var connected = Shell.Daemon.Connected;
        foreach (var t in list) Fill(e, _rows[t.NpcCode], t, now, connected);
        _empty.Text = T.TimersEmpty;
        _empty.IsVisible = list.Count == 0;
        var an = Shell.LastOverlay?.Announcement;
        _announce.IsVisible = an is not null;
        if (an is not null) _announce.Text = an;
    }

    private Row NewRow(int code)
    {
        var name = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        var meta = new TextBlock { FontSize = 11.5, Foreground = Visuals.TextMute, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var who = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(name);
        who.Children.Add(meta);
        var status = new TextBlock { FontFamily = Visuals.Number, FontSize = 19, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        var detail = new TextBlock { FontSize = 11, Foreground = Visuals.TextDim, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        var statusBox = new StackPanel { Width = 150, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        statusBox.Children.Add(status);
        statusBox.Children.Add(detail);

        var respawnLabel = new TextBlock { FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 11.5, Foreground = Visuals.Gold, VerticalAlignment = VerticalAlignment.Center };
        Row? self = null;
        var respawn = UiKit.Chip("", () => ShowRespawnMenu(self!, code), T.TipRespawn, Visuals.IconReset);
        if (respawn.Child is StackPanel rp) { rp.Children.RemoveAt(rp.Children.Count - 1); rp.Children.Add(respawnLabel); rp.Children.Add(new TextBlock { Text = " ▾", FontSize = 10, Foreground = Visuals.Gold, VerticalAlignment = VerticalAlignment.Center }); }
        respawn.MinWidth = 96;
        respawn.Margin = new Thickness(0, 0, 8, 0);
        var bell = Visuals.Icon(BellIcon, Visuals.TextDim, 13, 1.5);
        var bellBox = new Border { Width = 26, Height = 24, CornerRadius = new CornerRadius(6), Child = bell, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        UiKit.Tip(bellBox, T.TipWatch);
        bellBox.PointerPressed += (_, e) => { e.Handled = true; Command(self!.Watch ? "unwatch" : "watch", code); };
        var killed = UiKit.Chip(T.BtnKilled, () => Command("killed", code), T.TipKilled);
        killed.Margin = new Thickness(0, 0, 4, 0);
        var remove = UiKit.IconButton(Visuals.IconClose, T.TipRemoveTimer, () => Command("remove", code));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(respawn);
        actions.Children.Add(bellBox);
        actions.Children.Add(killed);
        actions.Children.Add(remove);

        var portrait = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(23), ClipToBounds = true, BorderThickness = new Thickness(1), BorderBrush = Visuals.GoldDim, Background = Visuals.Bg2, VerticalAlignment = VerticalAlignment.Center };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        g.Place(portrait, 0, 0);
        Grid.SetRowSpan(portrait, 2);
        g.Place(who, 0, 1);
        g.Place(statusBox, 0, 2);
        g.Place(actions, 0, 3);
        var frame = new Border { Background = Visuals.Solid(0x0DFFFFFF), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 7), Child = g, Tag = code };
        self = new Row
        {
            Frame = frame, Grid = g, Portrait = portrait, Name = name, Meta = meta, Status = status, Detail = detail, RespawnLabel = respawnLabel, Actions = actions,
            StatusBox = statusBox, Bell = bell, BellBox = bellBox, KilledChip = killed, RespawnChip = respawn, RemoveButton = remove,
        };
        Place(self, _compact);
        self.PortraitSet = SetPortrait(self, code);
        return self;
    }

    /// <summary>Narrow (the 560 px card tab): the actions drop to a second line under name and status (WPF Compact under 700 px).</summary>
    private static void Place(Row r, bool compact)
    {
        Grid.SetRow(r.Actions, compact ? 1 : 0);
        Grid.SetColumn(r.Actions, compact ? 1 : 3);
        Grid.SetColumnSpan(r.Actions, compact ? 2 : 1);
        r.Actions.Margin = compact ? new Thickness(12, 6, 0, 0) : new Thickness(0);
        r.Actions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void ApplyCompact(bool compact)
    {
        if (compact == _compact) return;
        _compact = compact;
        foreach (var r in _rows.Values) Place(r, compact);
    }

    private void Fill(MeterEngine e, Row row, BossTimer t, DateTimeOffset now, bool connected)
    {
        row.Name.Text = e.Data.NpcName(t.NpcCode);
        row.Watch = t.Watch;
        row.Bell.Stroke = t.Watch ? Visuals.GoldBright : Visuals.TextDim;
        row.BellBox.Background = t.Watch ? Visuals.Solid(0x26C9A45C) : Brushes.Transparent;
        UiKit.Tip(row.StatusBox, t.FromGame && t.ListedAt is { } listed ? string.Format(T.SourceGame, When(listed, now)) : T.SourceMeter);
        var zone = e.Timers.MapName(t.MapId, T.Code)
                   ?? (t.SlotId != 0 ? string.Format(T.MapNumber, t.MapId) : t.MapId != 0 ? e.Data.MapName(t.MapId) : t.Zone);
        if (zone == "Open world") zone = T.OpenWorld;
        var death = t.LastKill is { } k ? string.Format(T.TimerKilled, When(k, now))
            : t.LastSeenDead is { } d ? string.Format(T.TimerFoundDead, When(d, now)) : null;
        row.Meta.Text = string.IsNullOrEmpty(zone) ? death ?? "" : death is null ? zone : zone + " · " + death;
        row.Respawn = t.RespawnMinutes;
        row.RespawnLabel.Text = t.RespawnMinutes > 0 ? (t.RespawnLearned ? "≈ " : "") + T.Duration(t.RespawnMinutes) : t.FromGame ? T.RespawnFromGame : T.RespawnUnknown;
        UiKit.Tip(row.RespawnChip, t.RespawnLearned ? T.TimerLearned : T.TipRespawn);
        row.Actions.Opacity = connected ? 1 : 0.4;
        row.Actions.IsHitTestVisible = connected;
        ToolTip.SetTip(row.Actions, connected ? null : "aionmeter-daemon is not running");

        row.Status.FontSize = 19;
        if (t.AliveNow)
        {
            row.Status.FontSize = 16;
            row.Status.Text = T.TimerAlive;
            row.Detail.Text = t.AliveSince is { } since ? string.Format(T.TimerAliveSince, When(since, now)) : "";
            row.Status.Foreground = Visuals.Green;
            row.Frame.BorderBrush = Visuals.GoldDim;
        }
        else if (t.NextSpawn is { } next && next > now)
        {
            var left = next - now;
            row.Status.Text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
            row.Detail.Text = string.Format(T.TimerAt, When(next, now));
            row.Status.Foreground = left.TotalMinutes <= Math.Max(1, Shell.Settings.BossAlertMinutes) ? Visuals.Amber : Visuals.GoldBright;
            row.Frame.BorderBrush = Brushes.Transparent;
        }
        else if (t.NextSpawn is { } due)
        {
            row.Status.FontSize = 14;
            row.Status.Text = T.TimerDue;
            row.Detail.Text = string.Format(T.TimerSince, When(due, now));
            row.Status.Foreground = Visuals.Green;
            row.Frame.BorderBrush = Visuals.GoldDim;
        }
        else
        {
            row.Status.Text = "—";
            row.Detail.Text = t.FromGame && t.ListedTime is null ? T.TimerNoTime : T.TimerNeedsInterval;
            row.Status.Foreground = Visuals.TextMute;
            row.Frame.BorderBrush = Brushes.Transparent;
        }
    }

    /// <summary>MetaBot.GG portrait (game art © NCSOFT) through the icon cache: field bosses by NPC code, dungeon bosses by data/boss_portraits.json.</summary>
    private bool SetPortrait(Row r, int code)
    {
        if (code <= 0) return true;
        _portraitFiles ??= LoadPortraitFiles();
        var name = _portraitFiles.TryGetValue(code, out var file) ? file : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Shell.Icons.Url("portrait:" + name.Replace('/', '_'), PortraitBase + name + ".webp", 64) is not { } bmp)
        {
            if (r.Portrait.Child is null)
                r.Portrait.Child = new Avalonia.Controls.Shapes.Path { Data = Visuals.IconClock, Stroke = Visuals.GoldDim, StrokeThickness = 1.4, Width = 18, Height = 18, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return false;
        }
        r.Portrait.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        return true;
    }

    private static Dictionary<int, string> LoadPortraitFiles()
    {
        var map = new Dictionary<int, string>();
        var path = Path.Combine(MeterEngine.DataDirectory, "boss_portraits.json");
        try
        {
            if (!File.Exists(path)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("portraits", out var p))
                foreach (var kv in p.EnumerateObject())
                    if (int.TryParse(kv.Name, out var code) && kv.Value.GetString() is { Length: > 0 } f) map[code] = f;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Log.Info($"boss_portraits.json ignored: {ex.Message}"); }
        return map;
    }

    /// <summary>"14:05", or "05.10 14:05" when not today.</summary>
    private static string When(DateTimeOffset t, DateTimeOffset now) =>
        t.LocalDateTime.Date == now.LocalDateTime.Date ? t.ToLocalTime().ToString("HH:mm") : t.ToLocalTime().ToString("dd.MM HH:mm");

    // ------------------------------------------------------------ actions

    private void Command(string action, int code, int value = 0)
    {
        if (!Shell.Daemon.Connected) return;
        Shell.Daemon.Timer(action, _server, code, value);   // the daemon writes boss-timers.json and answers `timers-changed`
    }

    private void ShowRespawnMenu(Row row, int code)
    {
        var minutes = Presets.Contains(row.Respawn) ? Presets : Presets.Append(row.Respawn).Order().ToArray();
        Choices(row.RespawnChip, minutes, row.Respawn, m => m == 0 ? T.RespawnUnknown : T.Duration(m), m => Command("respawn", code, m));
    }

    private void ShowAlertMenu() =>
        Choices(_alertChip, AlertChoices, Shell.Settings.BossAlertMinutes, m => m == 0 ? T.AlertOff : T.Duration(m), m =>
        {
            Shell.RefreshSettings();
            Shell.Settings.BossAlertMinutes = m;
            Shell.SaveSettings();   // the daemon announces with it (reload-settings)
            UpdateAlertChip();
        });

    private void UpdateAlertChip()
    {
        var m = Shell.Settings.BossAlertMinutes;
        ChipText(_alertChip, (m == 0 ? T.AlertOff : T.Duration(m)) + "  ▾");
    }

    /// <summary>Other servers' timers (another character's): pick whose to show; the current one is marked.</summary>
    private void ShowServerMenu()
    {
        if (Shell.Engine is not { } e) return;
        var current = e.Timers.CurrentServer;
        var servers = e.Timers.Servers();
        if (servers.Count == 0) servers = [current];
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var s in servers)
        {
            var item = new MenuItem { Header = (s == _server ? "▸ " : "") + ServerLabel(e, s) + (s == current ? "  · " + T.ServerYours : "") };
            var chosen = s;
            item.Click += (_, _) => { _server = chosen; _followCurrent = chosen == current; Reload(); };
            menu.Items.Add(item);
        }
        menu.ShowAt(_serverChip);
    }

    private static string ServerLabel(MeterEngine e, int server) =>
        server == 0 ? T.ServerUnknown : $"{e.Data.ServerName(server)} {Core.Game.GameData.RegionOf(server)}".Trim();

    private static void Choices(Control anchor, IEnumerable<int> values, int current, Func<int, string> label, Action<int> pick)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var v in values)
        {
            var item = new MenuItem { Header = (v == current ? "▸ " : "   ") + label(v) };
            var chosen = v;
            item.Click += (_, _) => pick(chosen);
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private void SetFilter(bool watched)
    {
        if (watched == _watchedOnly) return;
        _watchedOnly = watched;
        SyncFilter();
        Reload();
    }

    private void SyncFilter()
    {
        Paint(_filterAll, !_watchedOnly);
        Paint(_filterWatched, _watchedOnly);
        static void Paint(Border b, bool on)
        {
            b.BorderBrush = on ? Visuals.Gold : Brushes.Transparent;
            if (b.Child is TextBlock t) { t.Foreground = on ? Visuals.GoldBright : Visuals.TextDim; t.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal; }
        }
    }

    /// <summary>A UiKit chip's label (the last TextBlock of its row).</summary>
    internal static void ChipText(Border chip, string text)
    {
        if (chip.Child is StackPanel { Children.Count: > 0 } row && row.Children[^1] is TextBlock label) label.Text = text;
    }

    /// <summary>WPF TabToggle: text with a gold underline when selected.</summary>
    internal static Border TabToggle(string text, Action click)
    {
        var b = new Border
        {
            Padding = new Thickness(10, 4, 10, 6), Margin = new Thickness(0, 0, 2, 0), BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = Visuals.TextDim },
        };
        b.PointerPressed += (_, e) => { if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return; e.Handled = true; click(); };
        return b;
    }

    private static readonly Geometry BellIcon = Geometry.Parse("M4 11 L4 7 C4 4.5 5.8 2.8 8 2.8 C10.2 2.8 12 4.5 12 7 L12 11 L13.5 12.5 L2.5 12.5 Z M6.5 14.2 L9.5 14.2");
}
