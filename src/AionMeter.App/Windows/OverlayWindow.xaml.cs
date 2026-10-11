using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AionMeter.App.Controls;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.Core;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Storage;

namespace AionMeter.App.Windows;

public partial class OverlayWindow : Window
{
    private readonly MeterService _meter;
    private readonly AppSettings _settings;
    private readonly OverlayViewModel _vm = new();
    private Guid? _segment; // in-memory segment; null = live
    private SavedFight? _saved; // a fight loaded from history (takes precedence over _segment)
    private nint _hwnd;
    private readonly DragAnywhere _drag;

    private sealed record SavedFight(HistoryEntry Entry, FightRecord Record, RecordFightView View, string Title);

    /// <summary>The render tool's fight clock for animation frames (Unix ms); null = real time.</summary>
    internal static Func<long>? ClockOverride { get; set; }

    /// <summary>True for a popped-out copy: it has a close X, never writes the placement settings and is not auto-hidden.</summary>
    public bool IsSecondary { get; }

    public OverlayWindow(MeterService meter, bool secondary = false)
    {
        _meter = meter;
        _settings = meter.Settings;
        IsSecondary = secondary;
        var opacity = _settings.BackgroundOpacity; // the slider's coercion during InitializeComponent would overwrite it
        InitializeComponent();
        _settings.BackgroundOpacity = opacity;
        DataContext = _vm;

        Left = _settings.OverlayLeft;
        Top = _settings.OverlayTop;
        Width = Math.Max(MinWidth, _settings.OverlayWidth);
        Height = Math.Max(MinHeight, _settings.OverlayHeight);
        EnsureOnScreen();

        if (secondary) { CloseWindowButton.Visibility = Visibility.Visible; RightPanel.Margin = new Thickness(0, 0, 26, 0); Left += 40; Top += 40; }
        OpacitySlider.Value = _settings.BackgroundOpacity;
        FrameBackground.Opacity = _settings.BackgroundOpacity;
        ApplyLock();
        ApplyRowSize(_settings.RowSize);
        UpdateModeLabel();
        ApplyHotkeyTips();
        LayoutUpdated += (_, _) => FitNames();
        _meter.Updates.Changed += ShowUpdateBanner;
        ShowUpdateBanner();

        // Drag the card by any part of it (unless locked); a click without movement still opens a breakdown.
        _drag = DragAnywhere.Attach(this, canDrag: () => !_settings.Locked && !(_atlasView?.MapHover ?? false), dropped: SavePlacement);

        // The card stays clean: its buttons only appear while the mouse is over it, in place of the title and the state
        // label (the timer stays).
        MouseEnter += (_, _) =>
        {
            TitleText.Visibility = Visibility.Collapsed;
            LabelPanel.Visibility = Visibility.Collapsed;
            Toolbar.Visibility = Visibility.Visible;
        };
        MouseLeave += (_, _) =>
        {
            Toolbar.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
            LabelPanel.Visibility = Visibility.Visible;
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Clicking the overlay must never pull keyboard focus away from the game window.
            NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            ApplyClickThrough();
            FitToMonitor();
        };
    }

    public nint Handle => _hwnd;

    /// <summary>Called by the app's UI timer (≈5 Hz).</summary>
    public void Refresh()
    {
        _vm.ShowGear = _settings.ShowGear;
        if (TabPrices.IsChecked != true) TabPrices.Foreground = _meter.Prices.AnyHit ? PriceHitBrush : PricePillBrush;   // green tab = something is at or under its maximum
        // Looking at an old fight when a new pull starts: jump back to live so the fight is never missed.
        if ((_saved is not null || _segment is not null) &&
            _meter.Tracker.Segments() is [{ IsActive: true } running, ..] && running.StartedAt > _selectedAt)
        {
            _saved = null;
            _segment = null;
        }

        if (_saved is { } saved)
        {
            _vm.Apply(saved.Record.Summary with { Title = saved.Title, Zone = _meter.DisplayZone(saved.Record.Summary.Zone) },
                _settings.MaxRows, _settings.BarsRelativeToTop);
            _vm.SetSegment("saved", saved.Entry.StartedAt.ToString("dd.MM HH:mm"));
        }
        else
        {
            var now = ClockOverride?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var snap = _meter.Tracker.Snapshot(_segment, now);
            if (snap is null && _segment is not null)
            {
                // The selected segment fell out of the in-memory list.
                _segment = null;
                snap = _meter.Tracker.Snapshot(null, now);
            }
            _vm.Apply(snap, _settings.MaxRows, _settings.BarsRelativeToTop);
            _vm.SetSegment(_segment is null ? "live" : "session", snap?.StartedAt.ToString("HH:mm"));
        }
        if (!_settings.ShowBossPanel) _vm.HasBoss = false;
        if (Environment.TickCount64 - _timersLabelAt >= 1_000) UpdateTimersLabel();

        var status = _meter.DemoRunning ? new CaptureStatus(CaptureState.Capturing, "Demo fight running") : _meter.CaptureStatus;
        // Names are only sent on a loading screen: players already around when the meter started show as #id.
        var unnamed = _vm.Rows.Any(r => r.Name.Contains('#')); // "Templar #10388": the server has not sent this name yet
        _vm.Status = status.State != CaptureState.Capturing ? status.Message
            : _vm.MostDamageUnsent && _vm.HasData ? UiText.Current.DamageUnsentTip // the footer says it, its tooltip says why
            : unnamed ? "Names appear after the next loading screen"
            : status.Message;
        // Footer reads like the card's caption; capture problems take its place so they are never missed.
        FooterText.Text = status.State is CaptureState.Capturing && _vm.HasData
            ? (unnamed ? _vm.Footer + " · " + UiText.Current.NamesLater : _vm.Footer)
            : _vm.Status;
        // A2M-38: the last kill's Abyss Logs line for 60 s, under the status line (meter tab only)
        var rank = _meter.LastAbyssLogs is { } al && Environment.TickCount64 - al.At < 60_000 && FooterStatusLine.Visibility == Visibility.Visible ? al.Text : null;
        if (rank is not null && !ReferenceEquals(AbyssLogsLine.Text, rank)) AbyssLogsLine.Text = rank;
        AbyssLogsLine.Visibility = rank is null ? Visibility.Collapsed : Visibility.Visible;
        _vm.StatusBrush = status.State switch
        {
            CaptureState.Capturing => (Brush)FindResource("Green"),
            CaptureState.Error => (Brush)FindResource("Red"),
            CaptureState.WaitingForGame or CaptureState.Starting => (Brush)FindResource("Amber"),
            _ => (Brush)FindResource("TextMute"),
        };
    }

    public void OnLanguageChanged()
    {
        _vm.LanguageChanged();
        ShowUpdateBanner();
        ApplyHotkeyTips();
        Refresh();
    }

    private Version? _bannerClosedFor; // the banner's ✕: hidden until a newer version comes out or the next start

    /// <summary>The green "Version 0.2.0 is out" banner above the footer, while an update is available (not skipped or closed).</summary>
    private void ShowUpdateBanner()
    {
        var available = _meter.Updates.Available;
        var show = available is not null && available.Version != _bannerClosedFor;
        UpdateBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            var ready = _meter.Updates.Downloaded?.Version == available!.Version; // downloaded: it installs itself soon
            UpdateBannerText.Text = string.Format(ready ? UiText.Current.UpdateBannerReady : UiText.Current.UpdateBanner, available.Version.ToString(3));
        }
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!_drag.JustDragged) AppHost.Current.ShowUpdate();
    }

    private void UpdateBannerClose_Click(object sender, RoutedEventArgs e)
    {
        _bannerClosedFor = _meter.Updates.Available?.Version;
        ShowUpdateBanner();
    }

    private void Language_Click(object sender, RoutedEventArgs e) => AppHost.Current.ToggleLanguage();

    /// <summary>Re-reads appearance settings changed in the Settings window.</summary>
    public void ApplyAppearance()
    {
        OpacitySlider.Value = _settings.BackgroundOpacity;
        FrameBackground.Opacity = _settings.BackgroundOpacity;
        ApplyRowSize(_settings.RowSize);
        UpdateModeLabel();
    }

    /// <summary>
    /// Row size in percent of the original. The height follows it exactly; text, emblems and the number columns shrink
    /// more gently (square root), so the smallest rows stay readable and the names get the room the numbers give up.
    /// </summary>
    public void ApplyRowSize(int percent)
    {
        var k = Math.Clamp(percent, AppSettings.MinRowSize, AppSettings.MaxRowSize) / 100.0;
        var f = Math.Sqrt(k);
        var height = Math.Round(42 * k);
        Resources["RowHeight"] = height;
        Resources["RowMargin"] = new Thickness(0, 0, 0, Math.Max(2, Math.Round(6 * k)));
        Resources["RowGlossHeight"] = Math.Round(19 * k);
        Resources["RowNameFont"] = Math.Round(16.5 * f, 1);
        Resources["RowGearFont"] = Math.Round(14 * f, 1);
        Resources["RowNumberFont"] = Math.Round(16 * f, 1);
        Resources["RowGlyphSize"] = Math.Min(Math.Round(30 * f), height - 2);
        Resources["RowRankSize"] = Math.Min(Math.Round(24 * f), height - 3);
        Resources["RowRankFont"] = Math.Round(13 * f, 1);
        Resources["ColHeaderFont"] = Math.Max(9, Math.Round(11 * f, 1));
        Resources["ColRankWidth"] = new GridLength(Math.Round(30 * f));
        Resources["ColGlyphWidth"] = new GridLength(Math.Round(32 * f));
        Resources["ColGap"] = new Thickness(Math.Round(10 * f), 0, 0, 0);
        Resources["ColRuleWidth"] = new GridLength(Math.Round(12 * f));
        Resources["RuleMargin"] = new Thickness(0, Math.Round(height * 0.2), 0, Math.Round(height * 0.2));
    }

    private const int NameColumn = 2, GsColumn = 3, CpColumn = 4, SpacerColumn = 5; // in the row and label grids
    private const double MinNameWidth = 60;
    private double _gearWidth; // GS + CP columns when last shown

    /// <summary>
    /// The name column is as wide as the longest name, which may take all the room the other columns leave but no more,
    /// so the fight's numbers always show (a long name is cut instead). A little is kept back for the rows' scrollbar.
    /// When even that leaves the names too little, the GS / CP columns step aside until the overlay is wide enough again.
    /// </summary>
    private void FitNames()
    {
        if (ColumnLabels.ActualWidth <= 0) return;
        var columns = ColumnLabels.ColumnDefinitions;
        double others = 0;
        for (var i = 0; i < columns.Count; i++)
            if (i is not (NameColumn or SpacerColumn)) others += columns[i].ActualWidth;
        var room = ColumnLabels.ActualWidth - others - 16; // 4 name margin + 12 for the scrollbar

        if (_vm.GearFits && _vm.HasGear && room < MinNameWidth)
        {
            _gearWidth = columns[GsColumn].ActualWidth + columns[CpColumn].ActualWidth;
            _vm.GearFits = false;
            Refresh();
        }
        else if (!_vm.GearFits && room - _gearWidth >= MinNameWidth + 20) // a margin, so it does not flip back and forth
        {
            _vm.GearFits = true;
            Refresh();
        }

        var max = Math.Max(30, Math.Floor(room));
        if (Resources["NameMaxWidth"] is double current && Math.Abs(current - max) < 1) return;
        Resources["NameMaxWidth"] = max;
    }

    /// <summary>Ctrl + mouse wheel over the rows: row size in 5 % steps, kept right away.</summary>
    private void Rows_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!NativeMethods.IsCtrlDown()) return;
        e.Handled = true;
        var size = Math.Clamp(_settings.RowSize + Math.Sign(e.Delta) * 5, AppSettings.MinRowSize, AppSettings.MaxRowSize);
        if (size == _settings.RowSize) return;
        _settings.RowSize = size;
        ApplyRowSize(size);
        _settings.Save();
    }

    public void SetClickThrough(bool on)
    {
        _settings.ClickThrough = on;
        _settings.Save();
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == 0) return;
        var through = _settings.ClickThrough && !TabActive;   // a tab needs the mouse
        if (through) NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT);
        else NativeMethods.SetExStyle(_hwnd, 0, NativeMethods.WS_EX_TRANSPARENT);
        Frame.BorderBrush = through ? (Brush)FindResource("Line") : (Brush)FindResource("CardFrame");
    }

    private void ApplyLock()
    {
        LockButton.IsChecked = _settings.Locked;
        LockGlyph.Text = _settings.Locked ? "" : "";
        ResizeGrip.Visibility = _settings.Locked ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateModeLabel() =>
        _vm.ModeLabel = _settings.TargetMode == TargetMode.BossOnly ? "BOSS" : "ALL";

    private void EnsureOnScreen()
    {
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        if (Left + 60 > vl + vw || Left + Width - 60 < vl) Left = vl + 80;
        if (Top + 40 > vt + vh || Top < vt) Top = vt + 160;
    }

    /// <summary>
    /// Keeps the whole card inside the work area of the monitor it sits on (a taller layout must not hang off the
    /// bottom edge). Runs once the window has a handle, when the monitor and its DPI are known.
    /// </summary>
    private void FitToMonitor()
    {
        if (_hwnd == 0) return;
        var area = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea; // physical pixels
        var dpi = VisualTreeHelper.GetDpi(this);
        var left = area.Left / dpi.DpiScaleX;
        var top = area.Top / dpi.DpiScaleY;
        var right = area.Right / dpi.DpiScaleX;
        var bottom = area.Bottom / dpi.DpiScaleY;
        if (Height > bottom - top) Height = Math.Max(MinHeight, bottom - top);
        Left = Math.Clamp(Left, left, Math.Max(left, right - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
        Log.Info($"Overlay placed at {Left:0},{Top:0} {Width:0}x{Height:0} DIP; monitor work area {left:0},{top:0}-{right:0},{bottom:0} DIP (scale {dpi.DpiScaleX})");
    }

    private void SavePlacement()
    {
        if (IsSecondary) return;
        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.OverlayWidth = Width;
        _settings.OverlayHeight = Height;
        _settings.Save();
    }

    // ------------------------------------------------------------ handlers

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void Resize_DragCompleted(object sender, DragCompletedEventArgs e) => SavePlacement();

    private void OpenBreakdown(uint actorId)
    {
        if (_saved is { } saved) AppHost.Current.ShowBreakdown(saved.View, actorId);
        else if (_vm.SegmentId is { } seg) AppHost.Current.ShowBreakdown(seg, actorId);
    }

    private EncounterSnapshot? ShownSummary() =>
        _saved is { } saved
            ? saved.Record.Summary with { Title = saved.Title }
            : _vm.SegmentId is { } seg ? _meter.Tracker.Snapshot(seg, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : null;

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (_drag.JustDragged) return; // the press moved the window, it was not a click
        if ((sender as FrameworkElement)?.DataContext is RowViewModel row) OpenBreakdown(row.ActorId);
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RowViewModel row) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        var open = new MenuItem { Header = UiText.Current.OpenBreakdown };
        open.Click += (_, _) => OpenBreakdown(row.ActorId);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        var player = row.ActorId == Combatant.UnknownSummonsId ? null : (uint?)row.ActorId;
        foreach (var item in ChatCopy.Items(CardBody, ShownSummary, player, row.Name)) menu.Items.Add(item);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>The toolbar's chat button: your result (when you took part), the party ranking or the Discord table.</summary>
    private void Chat_Click(object sender, RoutedEventArgs e)
    {
        if (ShownSummary() is null) return;
        var self = _vm.Rows.FirstOrDefault(r => r.IsSelf);
        ChatCopy.ShowMenu((FrameworkElement)sender, CardBody, ShownSummary, self?.ActorId, self?.Name);
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Rows.Count == 0) return;
        var target = _vm.Rows.FirstOrDefault(r => r.IsSelf) ?? _vm.Rows[0];
        OpenBreakdown(target.ActorId);
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        _settings.TargetMode = _settings.TargetMode == TargetMode.BossOnly ? TargetMode.All : TargetMode.BossOnly;
        _meter.ApplySettings();
        UpdateModeLabel();
    }

    // ------------------------------------------------------------ fight picker

    /// <summary>One selectable fight: a segment of this session or a fight saved on disk.</summary>
    private sealed record PickItem(
        string Title, DateTimeOffset Start, long CombatMs, EncounterEndReason Reason, bool Active,
        double SelfDps, int Place, double PartyDps, Guid? Segment, HistoryEntry? Saved, ImageSource? Portrait = null);

    private const int SavedInMenu = 40;

    /// <summary>Newest first: this session's segments, then saved fights not already in the session list.</summary>
    private List<PickItem> BuildFightList(int maxSaved)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<PickItem>();
        var sessionStarts = new HashSet<long>();
        foreach (var s in _meter.Tracker.Segments())
        {
            var snap = _meter.Tracker.Snapshot(s.Id, now);
            var selfIndex = snap?.Combatants.ToList().FindIndex(c => c.IsSelf) ?? -1;
            var self = selfIndex >= 0 ? snap!.Combatants[selfIndex] : null;
            list.Add(new PickItem(s.Title, s.StartedAt, s.CombatMs, s.Reason, s.IsActive,
                self?.Dps ?? 0, selfIndex + 1, s.TotalDamage / Math.Max(1.0, s.CombatMs / 1000.0), s.Id, null,
                snap is null ? null : _meter.PortraitOf(snap)));
            sessionStarts.Add(s.StartedAt.ToUnixTimeSeconds());
        }
        foreach (var h in _meter.History.List(maxSaved + sessionStarts.Count))
        {
            if (sessionStarts.Contains(h.StartedAt.ToUnixTimeSeconds())) continue;
            list.Add(new PickItem(_meter.DisplayTitle(h), h.StartedAt, h.CombatMs, h.Reason, false,
                h.SelfDps, h.SelfPlace, h.PartyDps, null, h, _meter.PortraitOf(h)));
            if (list.Count(i => i.Saved is not null) >= maxSaved) break;
        }
        return list;
    }

    private bool IsSelected(PickItem item) =>
        item.Saved is { } h ? _saved?.Entry.FileName == h.FileName : _saved is null && _segment == item.Segment;

    private void SelectLive()
    {
        _saved = null;
        _segment = null;
        Refresh();
    }

    private DateTimeOffset _selectedAt = DateTimeOffset.MinValue;

    private void Select(PickItem item)
    {
        _selectedAt = DateTimeOffset.Now;
        if (item.Saved is { } h)
        {
            if (_meter.History.Load(h.Path) is not { } record)
            {
                AppHost.Current.ShowHistory(); // file vanished or is unreadable: let the history window refresh
                return;
            }
            _saved = new SavedFight(h, record, new RecordFightView(record), _meter.DisplayTitle(h));
            _segment = null;
        }
        else
        {
            _saved = null;
            _segment = item.Segment;
        }
        Refresh();
    }

    private void Segments_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom, MinWidth = 380 };
        var live = new MenuItem
        {
            Header = PickHeader(UiText.Current.Live, UiText.Current.LiveHint, null, 0, EncounterEndReason.None, active: true, partyDps: 0, portrait: null),
            IsCheckable = true,
            IsChecked = _saved is null && _segment is null,
        };
        live.Click += (_, _) => SelectLive();
        menu.Items.Add(live);

        var items = BuildFightList(SavedInMenu);
        string? section = null;
        foreach (var item in items)
        {
            var group = item.Saved is null ? UiText.Current.SessionSection : DayLabel(item.Start);
            if (group != section)
            {
                section = group;
                menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem
                {
                    Header = new TextBlock { Text = group, Style = (Style)FindResource("SectionLabel") },
                    IsEnabled = false,
                });
            }
            var meta = $"{item.Start:HH:mm} · {Format.Clock(item.CombatMs)}";
            var row = new MenuItem
            {
                Header = PickHeader(item.Title, meta, item.SelfDps > 0 ? item.SelfDps : null, item.Place, item.Reason, item.Active, item.PartyDps, item.Portrait),
                IsCheckable = true,
                IsChecked = IsSelected(item),
            };
            var pick = item;
            row.Click += (_, _) => Select(pick);
            menu.Items.Add(row);
        }
        if (items.Count == 0)
            menu.Items.Add(new MenuItem { Header = UiText.Current.NoFights, IsEnabled = false });

        menu.Items.Add(new Separator());
        var total = _meter.History.List().Count;
        var history = new MenuItem { Header = string.Format(UiText.Current.AllSaved, total) };
        history.Click += (_, _) => AppHost.Current.ShowHistory();
        menu.Items.Add(history);
        menu.IsOpen = true;
    }

    private static string DayLabel(DateTimeOffset t)
    {
        var day = t.LocalDateTime.Date;
        if (day == DateTime.Today) return UiText.Current.SavedToday;
        if (day == DateTime.Today.AddDays(-1)) return UiText.Current.SavedYesterday;
        return string.Format(UiText.Current.SavedOn, day.ToString("dd MMM yyyy", UiText.Current.Culture).ToUpperInvariant());
    }

    /// <summary>Two-line menu row: boss portrait, result mark, title, time · length, and your DPS with your place badge.</summary>
    private FrameworkElement PickHeader(string title, string meta, double? selfDps, int place, EncounterEndReason reason, bool active, double partyDps,
        ImageSource? portrait)
    {
        var grid = new Grid { MinWidth = 360 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new BossPortrait { Source = portrait, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left });

        var (mark, markBrush) = active ? ("●", "Red") : reason switch
        {
            EncounterEndReason.Kill => ("✔", "Green"),
            EncounterEndReason.Wipe => ("✖", "Red"),
            _ => ("•", "TextMute"),
        };
        var markText = new TextBlock { Text = mark, Foreground = (Brush)FindResource(markBrush), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(markText, 1);
        grid.Children.Add(markText);

        var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, MaxWidth = 230,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (Brush)FindResource("Text"),
        });
        text.Children.Add(new TextBlock
        {
            Text = meta, FontSize = 10.5, FontFamily = (FontFamily)FindResource("NumberFont"), Foreground = (Brush)FindResource("TextMute"),
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (selfDps is { } dps)
        {
            right.Children.Add(new TextBlock
            {
                Text = Format.Compact(dps) + "/s", FontFamily = (FontFamily)FindResource("NumberFont"), FontSize = 12.5,
                FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("GoldBright"), VerticalAlignment = VerticalAlignment.Center,
            });
            if (place > 0)
            {
                var (bg, fg, border) = RankBrushes.For(place);
                right.Children.Add(new Border
                {
                    Width = 20, Height = 20, Margin = new Thickness(7, 0, 0, 0), CornerRadius = new CornerRadius(5),
                    Background = bg, BorderBrush = border, BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = place.ToString(), FontFamily = (FontFamily)FindResource("NumberFont"), FontSize = 11,
                        FontWeight = FontWeights.Bold, Foreground = fg,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }
        }
        else if (partyDps > 0)
        {
            right.Children.Add(new TextBlock
            {
                Text = UiText.Current.Party + " " + Format.Compact(partyDps) + "/s", FontFamily = (FontFamily)FindResource("NumberFont"),
                FontSize = 11.5, Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Mouse wheel over the title steps through fights: down = older, up = newer, past the newest = live.</summary>
    private void Title_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var items = BuildFightList(SavedInMenu);
        var index = items.FindIndex(IsSelected); // -1 = live
        if (_saved is null && _segment is null) index = -1;
        index += e.Delta < 0 ? 1 : -1;
        if (index < 0) SelectLive();
        else if (index < items.Count) Select(items[index]);
        e.Handled = true;
    }

    /// <summary>Called by the history window to show a saved fight in the overlay.</summary>
    public void ShowSaved(HistoryEntry entry)
    {
        Select(new PickItem(entry.Title, entry.StartedAt, entry.CombatMs, entry.Reason, false,
            entry.SelfDps, entry.SelfPlace, entry.PartyDps, null, entry));
    }

    private void Timers_Click(object sender, RoutedEventArgs e)
    {
        if (!_drag.JustDragged) AppHost.Current.ShowBossTimers();
    }

    /// <summary>"Shugo 39m" until the start, "Shugo ● 7m" while it runs; the same list every second, items replaced in place.</summary>
    private void UpdateEventLabels()
    {
        var now = DateTimeOffset.Now;
        var pills = _vm.EventLabels;
        var i = 0;
        foreach (var ev in _meter.Events)
        {
            var (start, end, running) = ev.Next(now, _meter.Settings.EventAnchors.TryGetValue(ev.Key, out var anchor) ? anchor : null, _meter.Settings.EventPeriods.TryGetValue(ev.Key, out var pm) ? pm : null);
            var left = running ? end - now : start - now;
            var span = left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}" : $"{Math.Max(0, (int)Math.Ceiling(left.TotalMinutes))}m";
            var text = running ? $"{ev.Name} ● {span}" : $"{ev.Name} {span}";
            if (i >= pills.Count || pills[i].Key != ev.Key)
            {
                var pill = new EventPill(ev.Key, ev.Name, _meter.Settings.AlertFor(ev.Key), _meter.Settings.Save, SyncEventNow, ResetEventSync) { CanSync = ev.EveryMinutes > 0 };
                if (i >= pills.Count) pills.Add(pill); else pills[i] = pill;
            }
            if (pills[i].Text != text) pills[i].Text = text;
            pills[i].Paint(left, running);
            i++;
        }
        while (pills.Count > i) pills.RemoveAt(pills.Count - 1);
    }

    /// <summary>The player saw the event begin: re-phase its cycle from this instant (and log it, to find the packet that announces it later).</summary>
    private void SyncEventNow(string key)
    {
        var now = DateTimeOffset.Now;
        // Two marks ≥ 30 min apart measure the real cycle: elapsed / rounded number of cycles. Within ±15 min of nominal it is kept
        // and used from then on, so a Shugo that really runs every 63 min stops drifting after the second mark.
        if (_meter.Settings.EventAnchors.TryGetValue(key, out var prev) && _meter.Events.FirstOrDefault(e => e.Key == key) is { EveryMinutes: > 0 } ev)
        {
            var elapsed = (now - prev).TotalMinutes;
            if (elapsed >= 30 && elapsed <= 24 * 60)
            {
                var cycles = Math.Max(1, Math.Round(elapsed / ev.EveryMinutes));
                var period = elapsed / cycles;
                if (Math.Abs(period - ev.EveryMinutes) <= 15) { _meter.Settings.EventPeriods[key] = period; Log.Info($"Event {key}: period measured {period:0.0} min from {cycles} cycle(s) between marks"); }
            }
        }
        _meter.Settings.EventAnchors[key] = now;
        _meter.Settings.Save();
        Log.Info($"Event {key} synced by hand: starts now ({now:HH:mm:ss})");
        UpdateEventLabels();
    }

    private void ResetEventSync(string key)
    {
        var removed = _meter.Settings.EventAnchors.Remove(key) | _meter.Settings.EventPeriods.Remove(key);
        if (removed) _meter.Settings.Save();
        UpdateEventLabels();
    }

    // Boss timer pill, right-click: the same two switches the event pills have (toast / sound), saved at once
    private void BossMenu_Opened(object sender, RoutedEventArgs e) { BossNotifyItem.IsChecked = _meter.Settings.BossAlertNotify; BossSoundItem.IsChecked = _meter.Settings.BossAlertSound; }
    private void BossNotify_Click(object sender, RoutedEventArgs e) { _meter.Settings.BossAlertNotify = BossNotifyItem.IsChecked; _meter.Settings.Save(); }
    private void BossSound_Click(object sender, RoutedEventArgs e) { _meter.Settings.BossAlertSound = BossSoundItem.IsChecked; _meter.Settings.Save(); }

    private void EventSync_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventPill p }) p.SyncNow();
    }

    private void EventResetSync_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: EventPill p }) p.ResetSync();
    }

    // ------------------------------------------------------------ tabs (2026-10-10): Atlas / Top / Prices live inside the card
    private string _tab = "meter";
    private Views.AtlasView? _atlasView;
    private Views.TopDpsView? _topView;
    private Views.PriceWatchView? _pricesView;
    private Views.CraftView? _craftView;
    private Views.BuildView? _buildView;
    private Views.ItemsView? _itemsView;

    public string Tab => _tab;
    public bool TabActive => _tab != "meter";
    public Views.AtlasView? AtlasView => _atlasView;
    /// <summary>The view of the tab in front (null on the meter tab); the render tool fills it with sample data.</summary>
    public System.Windows.Controls.UserControl? TabView => _tab == "meter" ? null : TabHost.Content as System.Windows.Controls.UserControl;

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (_drag.JustDragged) { SyncTabChecks(); return; }
        if (sender is ToggleButton { Tag: string tab }) SelectTab(tab == _tab ? "meter" : tab);
    }

    /// <summary>Shows one of the tabs inside the card; the card keeps its size whatever the tab (Diego, 2026-10-10).</summary>
    public void SelectTab(string tab)
    {
        if (tab == _tab) { SyncTabChecks(); return; }
        _tab = tab;
        Services.Telemetry.Count("tab." + tab);
        FooterStatusLine.Visibility = tab == "meter" ? Visibility.Visible : Visibility.Collapsed;   // DPS status + fight links belong to the meter tab only
        // the frame is for the DPS rows; the other tabs' cards draw their own border inside the same row
        PartyFrame.BorderThickness = tab == "meter" ? new Thickness(1) : new Thickness(0);
        PartyFrame.Padding = tab == "meter" ? new Thickness(18, 6, 18, 6) : new Thickness(0);
        PartyFrame.Margin = tab == "meter" ? new Thickness(-10, 10, -10, 0) : new Thickness(0, 10, 0, 0);
        if (tab == "meter") TabHost.Visibility = Visibility.Collapsed;
        else
        {
            TabHost.Content = tab switch
            {
                "atlas" => _atlasView ??= Wire(new Views.AtlasView(_meter), v => v.CloseRequested += () => SelectTab("meter")),
                "top" => _topView ??= Wire(new Views.TopDpsView(_meter), v => v.CloseRequested += () => SelectTab("meter")),
                "craft" => _craftView ??= Wire(new Views.CraftView(_meter), v => v.CloseRequested += () => SelectTab("meter")),
                "build" => _buildView ??= Wire(new Views.BuildView(_meter), v => v.CloseRequested += () => SelectTab("meter")),
                "items" => _itemsView ??= Wire(new Views.ItemsView(_meter), v => { v.CloseRequested += () => SelectTab("meter"); v.OpenInCraft += OpenInCraft; }),
                _ => _pricesView ??= Wire(new Views.PriceWatchView(_meter), v => v.CloseRequested += () => SelectTab("meter")),
            };
            TabHost.Visibility = Visibility.Visible;
        }
        SyncTabChecks();
        ApplyClickThrough();
    }

    private void Help_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowHelp(_tab);

    /// <summary>Items tab → "Open in Craft →": the Craft tab with the recipe's name in its search box.</summary>
    private void OpenInCraft(string text)
    {
        SelectTab("craft");
        _craftView?.SetSearch(text);
    }

    // ---- character selector (A2M-28, view-while-playing) ----
    private bool _charSync;
    private static string CharLabel(string key) { var i = key.IndexOf('-'); return i > 0 ? key[(i + 1)..] : key; }
    /// <summary>Fills the list (the bound character marked "(current)") and selects the viewed one; amber while viewing someone else.</summary>
    public void SyncCharBox() => FillCharBox(_meter.CharacterKeys(), _meter.BoundKey, _meter.CharacterKey);

    /// <summary>Render tool (--tab viewing|current): the selector with these characters, nothing read from disk.</summary>
    public void SampleCharacters(List<string> keys, string bound, string viewed) => FillCharBox(keys, bound, viewed);

    private void FillCharBox(List<string> keys, string bound, string viewed)
    {
        _charSync = true;
        try
        {
            if (bound.Length > 0 && !keys.Contains(bound)) keys.Add(bound);   // a first login before its folder exists
            var t = UiText.Current;
            var items = new List<ComboBoxItem>(keys.Count);
            ComboBoxItem? selected = null;
            foreach (var k in keys)
            {
                var it = new ComboBoxItem { Content = k == bound ? CharLabel(k) + t.CharCurrentSuffix : CharLabel(k), Tag = k };
                if (k == viewed) selected = it;
                items.Add(it);
            }
            CharBox.ItemsSource = items;
            CharBox.SelectedItem = selected;
            // Never disabled: a disabled ComboBox cannot raise DropDownOpened, so after the game closed it stayed locked until something
            // else re-synced it (Diego 2026-10-11). Every pick is accepted (SelectCharacter only moves the view while the game runs).
            CharBox.IsEnabled = keys.Count > 0;
            CharBox.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var other = viewed.Length > 0 && viewed != bound;
            if (other)
            {
                var amber = TryFindResource("Amber") as Brush ?? Brushes.Orange;
                CharBox.Foreground = amber; CharBox.BorderBrush = amber;
                CharBox.ToolTip = string.Format(t.CharViewingTip, bound.Length > 0 ? CharLabel(bound) : t.CharViewingNobody, CharLabel(viewed));
            }
            else
            {
                CharBox.ClearValue(ForegroundProperty); CharBox.ClearValue(BorderBrushProperty);
                CharBox.ToolTip = t.CharSelectTip;
            }
        }
        finally { _charSync = false; }
    }
    private void CharBox_Opened(object sender, EventArgs e) => SyncCharBox();
    private void CharBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_charSync || CharBox.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        _meter.SelectCharacter(key);
        SyncCharBox();   // labels, colour and tooltip for the new view (CharacterChanged re-syncs too; idempotent)
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (!_drag.JustDragged) AppHost.Current.PopOut(_tab, Width, Height);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private static T Wire<T>(T view, Action<T> wire) { wire(view); return view; }

    private void SyncTabChecks()
    {
        TabMeter.IsChecked = _tab == "meter"; TabAtlas.IsChecked = _tab == "atlas"; TabTop.IsChecked = _tab == "top"; TabPrices.IsChecked = _tab == "prices"; TabCraft.IsChecked = _tab == "craft"; TabBuild.IsChecked = _tab == "build"; TabItems.IsChecked = _tab == "items";
        if (TabPrices.IsChecked == true) TabPrices.Foreground = (Brush)FindResource("Text");
    }

    private static readonly System.Windows.Media.Brush PricePillBrush = Frozen(0xF3, 0xD5, 0x8F), PriceHitBrush = Frozen(0x8E, 0xE0, 0x8A);
    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b) { var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)); br.Freeze(); return br; }

    private long _timersLabelAt;

    /// <summary>The next respawn among watched bosses (or all, when none is watched): "Gartua 12:34".</summary>
    private void UpdateTimersLabel()
    {
        UpdateEventLabels();
        _timersLabelAt = Environment.TickCount64;
        var now = DateTimeOffset.Now;
        var timers = _meter.Timers.ForServer(_meter.Timers.CurrentServer).Where(t => t.NpcCode > 0).ToList();
        var pool = timers.Any(t => t.Watch) ? timers.Where(t => t.Watch) : timers;
        var next = pool.Where(t => !t.AliveNow && t.NextSpawn > now).MinBy(t => t.NextSpawn);
        if (next?.NextSpawn is not { } at)
        {
            _vm.TimersLabel = UiText.Current.TimersButton;
            return;
        }
        var name = next.NpcCode > 0 ? _meter.Data.NpcName(next.NpcCode) : string.Format(UiText.Current.UnknownSlotBoss, -next.NpcCode % 100);
        var shortName = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;
        var left = at - now;
        _vm.TimersLabel = $"{shortName} {(left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss"))}";
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => AppHost.Current.RestartMeter();

    /// <summary>Back to the live view (after a restart of the meter: empty until the next hit).</summary>
    public void ShowLive()
    {
        _segment = null;
        _saved = null;
        Refresh();
    }

    /// <summary>Tooltips that name a hotkey the user can change.</summary>
    public void ApplyHotkeyTips() =>
        RestartButton.ToolTip = string.Format(UiText.Current.TipRestart, _settings.HotkeyReset);

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        _settings.Locked = LockButton.IsChecked == true;
        _settings.Save();
        ApplyLock();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => AppHost.Current.ShowSettings();

    private void Hide_Click(object sender, RoutedEventArgs e) => AppHost.Current.SetOverlayVisible(false);

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FrameBackground is null) return;
        FrameBackground.Opacity = e.NewValue;
        _settings.BackgroundOpacity = Math.Round(e.NewValue, 2);
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        base.OnClosed(e);
    }
}
