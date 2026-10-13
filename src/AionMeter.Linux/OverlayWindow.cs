using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AionMeter.Core;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Linux.Shell;
using AionMeter.Linux.Views;
using AionMeter.Service;
using AionMeter.Service.Overlay;

namespace AionMeter.Linux;

/// <summary>
/// The overlay card (WPF OverlayWindow.xaml): header with the tab strip, fight title, time limit, state label and clock; boss HP bar; the
/// ranking; update banner; status line and the timer/event pills. The DPS rows come from the daemon's `overlay` snapshots (OverlayBuilder),
/// nothing is computed here. A2M-48 phase 3b: the tab host — a registered view (<see cref="ViewRegistry"/>) replaces the boss bar and the
/// rows, Back / Esc / the pill again return, `?` opens the tab's help, the pop-out button moves the tab into its own window.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly AppShell _shell;
    private readonly DaemonClient _client;
    private readonly UiSettings _ui;
    private readonly ContentControl _tabHost;
    private readonly Dictionary<string, (Border Pill, Avalonia.Controls.Shapes.Path Icon)> _tabPills = new();
    private readonly Dictionary<string, IHostedView> _views = new();
    private readonly Grid _statusLine;
    private readonly Border _party, _popOut;
    private readonly TextBlock _fightsLabel;
    private string _tab = "meter", _beforeSettings = "meter";
    private readonly Border _frame, _frameBg;
    private readonly TextBlock _title, _label, _clock, _limitText, _footer, _abyss, _bossHp, _bossPercent, _empty, _timersText, _bannerText, _modeChip;
    private readonly Path _limitIcon;
    private readonly Ellipse _labelDot, _statusDot;
    private readonly StackPanel _labelPanel, _limitPanel, _toolbar, _rows;
    private readonly Grid _boss, _labels, _banner;
    private readonly ScaleTransform _bossScale = new(1, 1);
    private readonly WrapPanel _pills;
    private readonly Border _clickToggle;
    private readonly List<RowView> _rowViews = new();
    private readonly List<(Border Pill, TextBlock Text, PillTone Tone)> _pillViews = new();
    private readonly DispatcherTimer _saveTimer;
    private OverlayModel? _last;
    private bool _connected;

    public OverlayWindow(AppShell shell)
    {
        _shell = shell;
        _client = shell.Daemon;
        _ui = shell.Ui;
        var client = _client;
        var ui = _ui;
        Title = AppInfo.Name;
        Width = Math.Max(340, ui.Width);
        Height = Math.Max(220, ui.Height);
        MinWidth = 340;
        MinHeight = 220;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = true;
        FontFamily = Visuals.Ui;
        if (!double.IsNaN(ui.Left) && !double.IsNaN(ui.Top)) { WindowStartupLocation = WindowStartupLocation.Manual; Position = new PixelPoint((int)ui.Left, (int)ui.Top); }

        var root = new Grid { Margin = new Thickness(10) };
        // Card
        root.Children.Add(_frame = new Border
        {
            CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), BorderBrush = Visuals.CardFrame,
            BoxShadow = BoxShadows.Parse("0 0 18 0 #99000000"),
        });
        root.Children.Add(_frameBg = new Border { CornerRadius = new CornerRadius(18), Margin = new Thickness(1), Background = new SolidColorBrush(Color.FromUInt32(0xFF0D111A), ui.Opacity), IsHitTestVisible = false });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(18), Background = Visuals.CardGloss, IsHitTestVisible = false });
        root.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(70, 0), Background = Visuals.GoldTopLine, IsHitTestVisible = false });

        var body = new Grid { Margin = new Thickness(18, 10, 18, 10), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        root.Children.Add(body);

        // ---------------------------------------------------------------- header: tabs · title (toolbar on hover) · limit, state, clock
        var header = new Grid { Height = 26, Background = Brushes.Transparent, ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        body.Children.Add(header);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        // A pill per registered view; the others show dimmed until their view is ported (ViewRegistry.Factories).
        foreach (var spec in ViewRegistry.Strip) tabs.Children.Add(TabPill(spec.Key, spec.Icon));
        // the breakdown and Settings get a pill only while they are in front (WPF TabFight / TabSettings)
        tabs.Children.Add(TabPill("fight", Visuals.IconFight));
        tabs.Children.Add(TabPill("settings", Visuals.IconGear));
        _tabPills["fight"].Pill.IsVisible = false;
        _tabPills["settings"].Pill.IsVisible = false;
        var gear = UiKit.IconButton(Visuals.IconGear, UiText.Current.SettingsTitle, () => SelectTab(_tab == "settings" ? _beforeSettings : "settings"));
        gear.Margin = new Thickness(2, 0, 0, 0);
        tabs.Children.Add(gear);
        _popOut = UiKit.IconButton(Visuals.IconPopOut, UiText.Current.PopOut, PopOut);
        _popOut.Margin = new Thickness(2, 0, 0, 0);
        _popOut.IsVisible = false;
        tabs.Children.Add(_popOut);
        var help = UiKit.IconButton(Visuals.IconHelp, UiText.Current.TipHelp, () => _shell.ShowHelp(_tab is "meter" or "fight" ? "meter" : _tab));
        help.Margin = new Thickness(2, 0, 0, 0);
        tabs.Children.Add(help);
        header.Children.Add(tabs);

        _title = new TextBlock
        {
            FontFamily = Visuals.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Cream, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Effect = Visuals.TextShadow(), Background = Brushes.Transparent,
        };
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);

        _toolbar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        Grid.SetColumn(_toolbar, 1);
        _modeChip = new TextBlock { FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 11, Foreground = Visuals.Gold, VerticalAlignment = VerticalAlignment.Center };
        _toolbar.Children.Add(Chip(_modeChip, "Boss only / everything (what the meter counts)", () =>
            _client.Send("set-measure", "mode", _last?.Mode == TargetMode.BossOnly ? "all" : "boss")));
        _toolbar.Children.Add(ToolIcon(Visuals.IconReset, "Restart the meter (closes and saves the fight so far)", () => _client.Send("reset")));
        _clickToggle = ToolIcon(Visuals.IconPointer, "Click-through: the mouse goes to the game (SIGUSR1 / the Sway hotkey turns it back off)", () => SetClickThrough(true));
        _toolbar.Children.Add(_clickToggle);
        _toolbar.Children.Add(ToolIcon(Visuals.IconClose, "Close the overlay (the daemon keeps running)", Close));
        header.Children.Add(_toolbar);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Effect = Visuals.TextShadow() };
        Grid.SetColumn(right, 2);
        _limitPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), IsVisible = false, Background = Brushes.Transparent };
        _limitPanel.Children.Add(_limitIcon = Visuals.Icon(Visuals.IconStopwatch, Visuals.LimitAmber, 11, 1.6));
        _limitIcon.Margin = new Thickness(0, 1, 4, 0);
        _limitIcon.VerticalAlignment = VerticalAlignment.Center;
        _limitPanel.Children.Add(_limitText = new TextBlock { FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = Visuals.LimitAmber, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(_limitPanel);
        _labelPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        _labelPanel.Children.Add(_labelDot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), Fill = Visuals.Dim });
        _labelPanel.Children.Add(_label = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Dim, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(_labelPanel);
        right.Children.Add(_clock = new TextBlock { FontFamily = Visuals.Number, FontWeight = FontWeight.Bold, FontSize = 17, Foreground = Visuals.Cream, VerticalAlignment = VerticalAlignment.Center, Text = "0:00" });
        header.Children.Add(right);

        // ---------------------------------------------------------------- boss HP: a slim bar
        _boss = new Grid { Height = 20, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
        Grid.SetRow(_boss, 2);
        _boss.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Visuals.Solid(0xFF230C0F), BorderBrush = Visuals.Solid(0xFF5E1F24), BorderThickness = new Thickness(1) });
        _boss.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Visuals.HpFill, Margin = new Thickness(1), RenderTransform = _bossScale, RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative) });
        _boss.Children.Add(new Border { CornerRadius = new CornerRadius(6, 6, 0, 0), Height = 9, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 1, 1, 0), Background = Visuals.Solid(0x14FFFFFF), IsHitTestVisible = false });
        var bossText = new Grid { Margin = new Thickness(10, 0), Effect = new DropShadowDirectionEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.9, Color = Colors.Black } };
        bossText.Children.Add(_bossHp = new TextBlock { FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        bossText.Children.Add(_bossPercent = new TextBlock { FontFamily = Visuals.Number, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right });
        _boss.Children.Add(bossText);
        body.Children.Add(_boss);

        // ---------------------------------------------------------------- party (labels and rows share their value column widths)
        var party = _party = new Border { Margin = new Thickness(-10, 10, -10, 0), BorderBrush = Visuals.GoldDim, BorderThickness = new Thickness(1), Padding = new Thickness(18, 6, 18, 6) };
        Grid.SetRow(party, 3);
        var partyGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetIsSharedSizeScope(partyGrid, true);
        _labels = new Grid { Margin = new Thickness(8, 0, 14, 5), Effect = Visuals.TextShadow(), IsVisible = false };
        AddColumns(_labels);
        var t = UiText.Current;
        _labels.Children.Add(ColLabel("#", 0, center: true));
        var player = ColLabel(t.ColPlayer, 1);
        Grid.SetColumnSpan(player, 2);
        _labels.Children.Add(player);
        _labels.Children.Add(_gsLabel = ColLabel(t.ColGs, 3, right: true));
        _labels.Children.Add(_cpLabel = ColLabel(t.ColCp, 4, right: true));
        _labels.Children.Add(ColLabel(t.ColDps, 7, right: true));
        _labels.Children.Add(ColLabel(t.ColDamage, 8, right: true));
        _labels.Children.Add(ColLabel(t.ColShare, 9, right: true));
        partyGrid.Children.Add(_labels);
        _rows = new StackPanel();
        var scroll = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(10, 4, 7, 0), Margin = new Thickness(-10, -4, -7, 0) };
        Grid.SetRow(scroll, 1);
        partyGrid.Children.Add(scroll);
        _empty = new TextBlock { FontSize = 12, Foreground = Visuals.TextMute, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Text = t.EmptyParty };
        Grid.SetRowSpan(_empty, 2);
        partyGrid.Children.Add(_empty);
        party.Child = partyGrid;
        body.Children.Add(party);
        // the tab host: over the boss bar and the rows, between the header and the footer (WPF TabHost)
        _tabHost = new ContentControl { IsVisible = false, Margin = new Thickness(-8, 10, -8, 0) };
        Grid.SetRow(_tabHost, 2);
        Grid.SetRowSpan(_tabHost, 2);
        body.Children.Add(_tabHost);

        // ---------------------------------------------------------------- footer
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(footer, 4);
        _banner = new Grid { Margin = new Thickness(0, 0, 0, 8), IsVisible = false };
        var bannerBox = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), BorderThickness = new Thickness(1), BorderBrush = Visuals.Solid(0x803DD68C), Background = Visuals.Solid(0x2A3DD68C) };
        var bannerRow = new DockPanel();
        var dl = Visuals.Icon(Visuals.IconDownload, Visuals.Solid(0xFFD2FAE6), 12);
        dl.Margin = new Thickness(0, 1, 8, 0);
        bannerRow.Children.Add(dl);
        bannerRow.Children.Add(_bannerText = new TextBlock { Foreground = Visuals.Solid(0xFFD2FAE6), FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        bannerBox.Child = bannerRow;
        _banner.Children.Add(bannerBox);
        footer.Children.Add(_banner);

        var statusLine = _statusLine = new Grid { Margin = new Thickness(0, 0, 12, 4), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        status.Children.Add(_statusDot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 7, 0), Fill = Visuals.Amber });
        status.Children.Add(_footer = new TextBlock { FontSize = 12, Foreground = Visuals.FooterText, TextTrimming = TextTrimming.CharacterEllipsis, Effect = Visuals.TextShadow() });
        statusLine.Children.Add(status);
        var fights = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Effect = Visuals.TextShadow(), VerticalAlignment = VerticalAlignment.Center };
        fights.Children.Add(_fightsLabel = new TextBlock { Text = t.Fights, FontSize = 12, Foreground = Visuals.FooterLink, VerticalAlignment = VerticalAlignment.Center });
        var chev = Visuals.Icon(Visuals.IconChevron, Visuals.FooterLink, 9, 1.6);
        chev.Margin = new Thickness(5, 1, 0, 0);
        chev.VerticalAlignment = VerticalAlignment.Center;
        fights.Children.Add(chev);
        fights.PointerPressed += (_, e) => { e.Handled = true; _fightsAnchor = fights; _client.Send("fights", "max", 40); };
        Grid.SetColumn(fights, 1);
        statusLine.Children.Add(fights);
        footer.Children.Add(statusLine);
        footer.Children.Add(_abyss = new TextBlock { FontSize = 12, Foreground = Visuals.GoldBright, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(13, 0, 12, 4), Effect = Visuals.TextShadow(), IsVisible = false });

        _pills = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        var timers = new Border
        {
            CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 11, 3), BorderThickness = new Thickness(1), BorderBrush = Visuals.Solid(0xFFFFE3A6),
            Background = Visuals.TimersPill, BoxShadow = BoxShadows.Parse("0 0 8 0 #8CE9A93C"), Margin = new Thickness(0, 2, 12, 2),
        };
        var timersRow = new StackPanel { Orientation = Orientation.Horizontal };
        var clockIcon = Visuals.Icon(Visuals.IconClock, Visuals.Solid(0xFF1A140A), 12, 1.6);
        clockIcon.Margin = new Thickness(0, 1, 6, 0);
        clockIcon.VerticalAlignment = VerticalAlignment.Center;
        timersRow.Children.Add(clockIcon);
        timersRow.Children.Add(_timersText = new TextBlock { FontFamily = Visuals.Number, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Solid(0xFF1A140A), VerticalAlignment = VerticalAlignment.Center });
        timers.Child = timersRow;
        _pills.Children.Add(timers);
        footer.Children.Add(_pills);
        body.Children.Add(footer);

        // ---------------------------------------------------------------- resize grip
        var grip = new Border { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 5, 5), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
        grip.Child = new Path { Data = Visuals.IconGrip, Stroke = Visuals.GoldDim, StrokeThickness = 1.3 };
        grip.PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { e.Handled = true; BeginResizeDrag(WindowEdge.SouthEast, e); } };
        root.Children.Add(grip);
        Content = root;

        // Drag the card by any part of it.
        PointerPressed += (_, e) => { if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        // The card stays clean: its buttons only appear while the mouse is over it, in place of the title and the state label (the timer stays).
        PointerEntered += (_, _) => { _title.IsVisible = false; _labelPanel.IsVisible = false; _toolbar.IsVisible = _connected; };
        PointerExited += (_, _) => { _toolbar.IsVisible = false; _title.IsVisible = true; _labelPanel.IsVisible = true; };

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SavePlacement(); };
        PositionChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
        Opened += (_, _) =>
        {
            if (_ui.ClickThrough) SetClickThrough(true);
            // Sway floats an Xwayland window at the size it was mapped with, which is not the one asked for (777×669 for 490×560,
            // 2026-10-13): ask again once mapped; floating Xwayland windows may resize and place themselves.
            Dispatcher.UIThread.Post(() =>
            {
                var (w, h) = (Width, Height);
                Width = w + 1;
                Height = h + 1;
                Width = w;
                Height = h;
                if (!double.IsNaN(_ui.Left) && !double.IsNaN(_ui.Top)) Position = new PixelPoint((int)_ui.Left, (int)_ui.Top);
                Console.Error.WriteLine($"overlay window {ClientSize.Width:0}x{ClientSize.Height:0} DIP at {Position}, scale {RenderScaling}, screen {Screens.ScreenFromWindow(this)?.Bounds}");
            }, DispatcherPriority.Background);
        };

        client.Overlay += Apply;
        client.ConnectionChanged += OnConnection;
        client.Fights += ShowFightMenu;
        OnConnection(false);
        shell.SelectTabHandler = SelectTab;
        shell.OpenFightHandler = OpenFight;
        shell.LanguageChanged += ApplyLanguage;
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled || _tab == "meter") return;
            e.Handled = true;
            SelectTab(_tab == "settings" ? _beforeSettings : "meter");
        };
    }

    // ------------------------------------------------------------ tab host (A2M-48 phase 3b, WPF SelectTab)

    public string Tab => _tab;

    /// <summary>Shows a tab inside the card ("meter" = the DPS rows); the card keeps its size whatever the tab (Diego, 2026-10-10).</summary>
    public void SelectTab(string tab)
    {
        if (tab != "meter" && !ViewRegistry.Has(tab)) return;
        if (tab == "fight" && !_views.ContainsKey("fight")) tab = "meter";   // reached from a row / the fight list only
        if (tab == _tab) { SyncPills(); return; }
        if (tab == "settings") _beforeSettings = _tab;
        if (_views.TryGetValue(_tab, out var old)) old.Hidden();
        _tab = tab;
        var meter = tab == "meter";
        _statusLine.IsVisible = meter;   // the DPS status and the fight links belong to the DPS tab
        _boss.IsVisible = meter && _last?.Fight.Boss is not null;
        _party.IsVisible = meter;
        if (meter) { _tabHost.Content = null; _tabHost.IsVisible = false; }
        else
        {
            var v = View(tab);
            _tabHost.Content = v.View;
            _tabHost.IsVisible = true;
            v.Shown();
        }
        _popOut.IsVisible = !meter;
        SyncPills();
    }

    private IHostedView View(string key)
    {
        if (_views.TryGetValue(key, out var v)) return v;
        v = ViewRegistry.Create(key, _shell)!;
        v.Hosted = true;
        v.CloseRequested += () => SelectTab(key == "settings" ? _beforeSettings : "meter");
        _views[key] = v;
        return v;
    }

    /// <summary>A2M-43: the fight's breakdown as this card's "fight" tab (a row click, the fight list).</summary>
    private void OpenFight(string fightId, uint actor)
    {
        var v = (FightView)View("fight");
        v.SetFight(fightId, actor);
        if (_tab == "fight") v.Shown();
        else SelectTab("fight");
    }

    /// <summary>The tab in front moves into its own window (a copy, or the view itself for Settings); the card goes back.</summary>
    public void PopOut()
    {
        if (!_views.TryGetValue(_tab, out var v)) return;
        var key = _tab;
        var popped = v.PopOut();
        if (ReferenceEquals(popped, v)) { v.Hidden(); _tabHost.Content = null; _views.Remove(key); }
        SelectTab(key == "settings" ? _beforeSettings : "meter");
        new PopOutWindow(_shell, popped, Math.Max(900, Bounds.Width), Math.Max(640, Bounds.Height)).Show();
    }

    private void SyncPills()
    {
        foreach (var (key, (pill, icon)) in _tabPills)
        {
            var on = key == _tab;
            var usable = key == "meter" || ViewRegistry.Has(key);
            pill.Background = on ? Visuals.Gold : Visuals.Solid(0x99111726);
            pill.Opacity = on || usable ? 1 : 0.4;
            icon.Stroke = on ? Visuals.Solid(0xFF1A1407) : Visuals.Solid(0xFFF3D58F);
        }
        _tabPills["fight"].Pill.IsVisible = _tab == "fight";
        _tabPills["settings"].Pill.IsVisible = _tab == "settings";
    }

    private void ApplyLanguage()
    {
        var t = UiText.Current;
        _fightsLabel.Text = t.Fights;
        _empty.Text = t.EmptyParty;
        foreach (var (key, (pill, _)) in _tabPills) ToolTip.SetTip(pill, PillTip(key));
        foreach (var v in _views.Values) v.LanguageChanged();
        if (_labels.Children is [_, TextBlock player, TextBlock gs, TextBlock cp, TextBlock dps, TextBlock dmg, TextBlock share])
        {
            player.Text = t.ColPlayer; gs.Text = t.ColGs; cp.Text = t.ColCp; dps.Text = t.ColDps; dmg.Text = t.ColDamage; share.Text = t.ColShare;
        }
    }

    private static string PillTip(string key)
    {
        var t = UiText.Current;
        var tip = key switch { "fight" => t.FightTab, "settings" => t.SettingsTitle, _ => Array.Find(ViewRegistry.Strip, s => s.Key == key)?.Tip(t) ?? key };
        return key == "meter" || ViewRegistry.Has(key) ? tip : string.Format(t.LinuxNotYet, tip);
    }

    private Control? _fightsAnchor;
    private readonly TextBlock _gsLabel, _cpLabel;

    /// <summary>[#][emblem][name][GS][CP][*][rule][DPS][damage][share]: the value columns share their widths between the labels and every row.</summary>
    internal static void AddColumns(Grid g)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition(30, GridUnitType.Pixel));
        g.ColumnDefinitions.Add(new ColumnDefinition(32, GridUnitType.Pixel));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Name" });
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Gs" });
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Cp" });
        g.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        g.ColumnDefinitions.Add(new ColumnDefinition(12, GridUnitType.Pixel));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Dps" });
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Damage" });
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "Share" });
    }

    private static TextBlock ColLabel(string text, int column, bool right = false, bool center = false)
    {
        var tb = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Visuals.ColHeader, VerticalAlignment = VerticalAlignment.Center };
        if (right) { tb.TextAlignment = TextAlignment.Right; tb.HorizontalAlignment = HorizontalAlignment.Right; tb.Margin = new Thickness(10, 0, 0, 0); }
        if (center) { tb.Width = 24; tb.TextAlignment = TextAlignment.Center; tb.HorizontalAlignment = HorizontalAlignment.Left; }
        Grid.SetColumn(tb, column);
        return tb;
    }

    /// <summary>WPF TabPill: icon toggle, the selected one filled gold; clicking the selected one goes back to the DPS rows.</summary>
    private Border TabPill(string key, Geometry icon)
    {
        var path = Visuals.Icon(icon, Visuals.Solid(0xFFF3D58F), 13, 1.5);
        var b = new Border
        {
            Width = 30, Height = 22, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = Visuals.Gold, Background = Visuals.Solid(0x99111726), Child = path, Cursor = new Cursor(StandardCursorType.Hand),
        };
        _tabPills[key] = (b, path);
        b.PointerEntered += (_, _) => { if (key != _tab) b.BorderBrush = Visuals.Solid(0xFFFFE3A6); };
        b.PointerExited += (_, _) => b.BorderBrush = Visuals.Gold;
        b.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            SelectTab(key != _tab ? key : key == "settings" ? _beforeSettings : "meter");
        };
        ToolTip.SetTip(b, PillTip(key));
        if (key == "meter") b.Background = Visuals.Gold;
        return b;
    }

    private Border Chip(TextBlock content, string tip, Action click)
    {
        var b = new Border
        {
            Height = 22, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 5, 0), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            BorderBrush = Visuals.GoldDim, Background = Visuals.Solid(0x1AC9A45C), Cursor = new Cursor(StandardCursorType.Hand), Child = content,
        };
        b.PointerEntered += (_, _) => { b.BorderBrush = Visuals.Gold; content.Foreground = Visuals.GoldBright; };
        b.PointerExited += (_, _) => { b.BorderBrush = Visuals.GoldDim; content.Foreground = Visuals.Gold; };
        b.PointerPressed += (_, e) => { e.Handled = true; click(); };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private Border ToolIcon(Geometry icon, string tip, Action click)
    {
        var path = Visuals.Icon(icon, Visuals.TextDim, 12, 1.5);
        var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = path };
        b.PointerEntered += (_, _) => { b.Background = Visuals.Solid(0x14FFFFFF); path.Stroke = Visuals.GoldBright; };
        b.PointerExited += (_, _) => { b.Background = Brushes.Transparent; path.Stroke = Visuals.TextDim; };
        b.PointerPressed += (_, e) => { e.Handled = true; click(); };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private void OnConnection(bool on)
    {
        _connected = on;
        if (on) return;
        // No daemon: the card says so instead of showing a stale fight.
        _last = null;
        _title.Text = "No daemon";
        SetLabel("OFFLINE", null, Visuals.Dim, false);
        _clock.Text = "—";
        _limitPanel.IsVisible = false;
        _boss.IsVisible = false;
        _labels.IsVisible = false;
        _empty.IsVisible = true;
        _empty.Text = "aionmeter-daemon is not running.\nWaiting for " + _client.Path;
        foreach (var r in _rowViews) r.IsVisible = false;
        _statusDot.Fill = Visuals.Amber;
        _footer.Text = "Connecting to the daemon…";
        _abyss.IsVisible = false;
        _banner.IsVisible = false;
        _timersText.Text = UiText.Current.TimersButton;
        for (var i = 0; i < _pillViews.Count; i++) _pillViews[i].Pill.IsVisible = false;
    }

    private void SetLabel(string label, string? when, IBrush brush, bool live)
    {
        _label.Text = when is null ? Visuals.Spaced(label) : Visuals.Spaced(label) + "   " + when;
        _label.Foreground = brush;
        _labelDot.Fill = brush;
        // A pulse driven by the 5 Hz snapshots (1.6 s cycle): an Avalonia animation would redraw the window at the monitor's refresh rate
        // for as long as a fight runs, next to the game.
        _labelDot.Opacity = live ? 0.25 + 0.75 * Math.Abs(Math.Cos(Environment.TickCount64 / 1600.0 * Math.PI)) : 1;
    }

    private void Apply(OverlayModel m)
    {
        _last = m;
        var f = m.Fight;
        _title.Text = f.Title.Length > 0 ? f.Title : f.Detail;
        ToolTip.SetTip(_title, f.TitleTip);
        SetLabel(m.HeaderLabel, m.HeaderWhen, m.View switch { OverlayView.Live => Visuals.LiveRed, OverlayView.Saved or OverlayView.Session => Visuals.SavedGold, _ => Visuals.Dim },
            m.View == OverlayView.Live);
        _clock.Text = f.Clock;
        _modeChip.Text = m.ModeLabel;
        if (m.Limit is { } limit)
        {
            _limitPanel.IsVisible = true;
            _limitText.Text = limit.Text;
            var lb = limit.Urgent ? Visuals.LiveRed : Visuals.LimitAmber;
            _limitText.Foreground = lb;
            _limitIcon.Stroke = lb;
            ToolTip.SetTip(_limitPanel, limit.Tip);
        }
        else _limitPanel.IsVisible = false;

        if (f.Boss is { } boss)
        {
            _boss.IsVisible = _tab == "meter";
            _bossScale.ScaleX = boss.Fraction;
            _bossHp.Text = boss.HpText;
            _bossPercent.Text = boss.Percent;
        }
        else _boss.IsVisible = false;

        _labels.IsVisible = f.HasData;
        _gsLabel.IsVisible = f.HasGear;
        _cpLabel.IsVisible = f.HasGear;
        _empty.IsVisible = !f.HasData;
        _empty.Text = UiText.Current.EmptyParty;
        var rows = f.Rows;
        while (_rowViews.Count < rows.Length)
        {
            var rv = new RowView();
            rv.Clicked += actor => OpenFight(_last?.FightId ?? "live", actor);
            _rowViews.Add(rv);
            _rows.Children.Add(rv);
        }
        for (var i = 0; i < _rowViews.Count; i++)
        {
            var rv = _rowViews[i];
            if (i >= rows.Length) { rv.IsVisible = false; continue; }
            rv.IsVisible = true;
            rv.Update(rows[i], f.HasGear);
        }
        FitNames();

        _statusDot.Fill = m.StatusTone switch { StatusTone.Good => Visuals.Green, StatusTone.Error => Visuals.Red, StatusTone.Warn => Visuals.Amber, _ => Visuals.TextMute };
        _footer.Text = m.FooterText;
        ToolTip.SetTip(_footer, m.Status);
        _abyss.IsVisible = m.AbyssLogs is not null;
        if (m.AbyssLogs is { } al) _abyss.Text = al;
        _banner.IsVisible = m.UpdateBanner is not null;
        if (m.UpdateBanner is { } ub) _bannerText.Text = ub;

        _timersText.Text = m.TimersLabel;
        var pills = m.Pills;
        for (var i = 0; i < pills.Length; i++)
        {
            if (i >= _pillViews.Count)
            {
                var text = new TextBlock { FontFamily = Visuals.Number, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Solid(0xFFF4F1E8) };
                var pill = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 11, 3), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 6, 2), Child = text };
                _pills.Children.Add(pill);
                _pillViews.Add((pill, text, (PillTone)255));
            }
            var (b, tb, tone) = _pillViews[i];
            b.IsVisible = true;
            tb.Text = pills[i].Text;
            if (tone != pills[i].Tone)
            {
                (b.Background, b.BorderBrush) = Visuals.Pill(pills[i].Tone);
                _pillViews[i] = (b, tb, pills[i].Tone);
            }
        }
        for (var i = pills.Length; i < _pillViews.Count; i++) _pillViews[i].Pill.IsVisible = false;
    }

    /// <summary>
    /// The name column is as wide as the longest name, which may take all the room the other columns leave but no more, so the fight's
    /// numbers always show (a long name is cut instead). Uses the previous layout's column widths: it settles on the next snapshot.
    /// </summary>
    private void FitNames()
    {
        var cols = _labels.ColumnDefinitions;
        if (_labels.Bounds.Width <= 0) return;
        double others = 0;
        for (var i = 0; i < cols.Count; i++)
            if (i is not (2 or 5)) others += cols[i].ActualWidth;
        var max = Math.Max(30, Math.Floor(_labels.Bounds.Width - others - 16));   // 4 name margin + 12 for the scrollbar
        if (Math.Abs(max - _nameMax) < 1) return;
        _nameMax = max;
        foreach (var r in _rowViews) r.SetNameMax(max);
    }

    private double _nameMax = 220;

    private void ShowFightMenu(OverlayFightItem[] items)
    {
        if (_fightsAnchor is null) return;
        var t = UiText.Current;
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        var live = new MenuItem { Header = $"● {t.Live}" };
        live.Click += (_, _) => _client.Send("show-fight", "id", "live");
        menu.Items.Add(live);
        var shown = _last?.FightId;
        foreach (var it in items)
        {
            var mark = it.Active ? "●" : it.Reason switch { EncounterEndReason.Kill => "✔", EncounterEndReason.Wipe => "✖", _ => "•" };
            var dps = it.SelfDps > 0 ? $"  ·  {Format.Compact(it.SelfDps)}/s{(it.Place > 0 ? $" #{it.Place}" : "")}" : it.PartyDps > 0 ? $"  ·  {t.Party} {Format.Compact(it.PartyDps)}/s" : "";
            var item = new MenuItem { Header = $"{(it.Id == shown ? "▸ " : "")}{mark} {it.StartedAt.ToLocalTime():HH:mm}  {it.Title}  ·  {Format.Clock(it.CombatMs)}{dps}" };
            var id = it.Id;
            item.Click += (_, _) => _client.Send("show-fight", "id", id);
            menu.Items.Add(item);
        }
        if (items.Length == 0) menu.Items.Add(new MenuItem { Header = t.NoFights, IsEnabled = false });
        menu.ShowAt(_fightsAnchor);
        _fightsAnchor = null;   // a later `fights` reply (asked by the breakdown) is not ours
    }

    public void ToggleClickThrough() => SetClickThrough(!_ui.ClickThrough);

    private void SetClickThrough(bool on)
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (!X11Input.SetClickThrough(handle, on) && on) return;
        _ui.ClickThrough = on;
        _ui.Save();
        _frame.BorderBrush = on ? Visuals.Line : Visuals.CardFrame;
        if (on) { _toolbar.IsVisible = false; _title.IsVisible = true; _labelPanel.IsVisible = true; }
    }

    private void SavePlacement()
    {
        _ui.Left = Position.X;
        _ui.Top = Position.Y;
        _ui.Width = Bounds.Width;
        _ui.Height = Bounds.Height;
        _ui.Save();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty && _saveTimer is not null)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
            if (Environment.GetEnvironmentVariable("AIONMETER_UI_DEBUG") is not null) Console.Error.WriteLine($"client size {ClientSize}, scale {RenderScaling}");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePlacement();
        base.OnClosed(e);
    }
}
