using System.Runtime.CompilerServices;
using Avalonia;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

[Flags]
public enum DataChange : byte { None = 0, Characters = 1, Prices = 2, Market = 4 }

/// <summary>
/// A2M-48 phase 3c: the daemon rewrites characters/*/{inventory,character}.json, account-warehouse.json and the price files every ≤ 5 s;
/// the viewer engine has no change event for another process's writes. One 2 s mtime poll per shell (stat only, no reads) while at least
/// one view is shown, raising <see cref="Changed"/> with what moved. Also the character the tabs show (Items, Craft): the engine's
/// <see cref="MeterEngine.CharacterKey"/> once picked, else the daemon's bound character (`status`, state/summary.json without a daemon).
/// </summary>
public sealed class DataWatch
{
    private static readonly ConditionalWeakTable<AppShell, DataWatch> PerShell = new();
    public static DataWatch Of(AppShell shell) => PerShell.GetValue(shell, s => new DataWatch(s));

    private readonly AppShell _shell;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _users;
    private long _chars, _prices, _market;
    private string _summaryBound = "";
    private DateTime _summaryStamp;

    public event Action<DataChange>? Changed;
    /// <summary>The viewed character moved (a pick in any selector) or the bound one changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>The character the live packets belong to ("" when unknown): the daemon's `status`, else state/summary.json.</summary>
    public string Bound { get; private set; } = "";

    private DataWatch(AppShell shell)
    {
        _shell = shell;
        _timer.Tick += (_, _) => Poll();
        shell.Daemon.Status += st =>
        {
            if (st.Character == Bound || st.Character.Length == 0) return;
            Bound = st.Character;
            SelectionChanged?.Invoke();
        };
        shell.Daemon.ConnectionChanged += on => { if (on) shell.Daemon.Send("status"); };
        if (shell.Daemon.Connected) shell.Daemon.Send("status");
        ReadSummary();
        (_chars, _prices, _market) = (CharStamp(), PriceStamp(), MarketStamp());
    }

    /// <summary>The character the tabs show: the pick, else the bound one.</summary>
    public string Viewed => _shell.Engine?.CharacterKey is { Length: > 0 } k ? k : Bound;

    public void Select(string key)
    {
        if (_shell.Engine is not { } e || key == Viewed) return;
        e.SelectCharacter(key);
        SelectionChanged?.Invoke();
    }

    /// <summary>A view became visible: poll while anyone is (Hidden → <see cref="Release"/>).</summary>
    public void Acquire()
    {
        if (_users++ == 0) { Poll(); _timer.Start(); }
    }

    public void Release()
    {
        if (_users > 0 && --_users == 0) _timer.Stop();
    }

    private void Poll()
    {
        var change = DataChange.None;
        var c = CharStamp();
        if (c != _chars) { _chars = c; change |= DataChange.Characters; }
        var p = PriceStamp();
        if (p != _prices) { _prices = p; change |= DataChange.Prices; }
        var m = MarketStamp();
        if (m != _market) { _market = m; change |= DataChange.Market; }
        if (!_shell.Daemon.Connected && ReadSummary()) SelectionChanged?.Invoke();
        if (change != DataChange.None) Changed?.Invoke(change);
    }

    private static long Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }   // missing file: 1601-01-01, still a stable value
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static long CharStamp()
    {
        var root = AppSettings.AppDataDir;
        long h = Stamp(Path.Combine(root, "account-warehouse.json"));
        var dir = Path.Combine(root, "characters");
        try
        {
            if (!Directory.Exists(dir)) return h;
            foreach (var d in Directory.EnumerateDirectories(dir))
                h = h * 31 + Stamp(Path.Combine(d, "inventory.json")) * 7 + Stamp(Path.Combine(d, "character.json")) * 3 + Stamp(Path.Combine(d, "profile.txt")) + d.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return h;
    }

    private static long PriceStamp()
    {
        var st = Path.Combine(AppSettings.AppDataDir, "state");
        return Stamp(Path.Combine(st, "price-watch.json")) * 31 + Stamp(Path.Combine(st, "price-history.json"));
    }

    private static long MarketStamp() => Stamp(MeterEngine.MarketPath);

    /// <summary>characterKey of state/summary.json (the daemon writes it with every state save); true when it changed.</summary>
    private bool ReadSummary()
    {
        var path = Path.Combine(AppSettings.AppDataDir, "state", "summary.json");
        try
        {
            if (!File.Exists(path)) return false;
            var w = File.GetLastWriteTimeUtc(path);
            if (w == _summaryStamp) return false;
            _summaryStamp = w;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var key = doc.RootElement.TryGetProperty("characterKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
            if (key == _summaryBound) return false;
            _summaryBound = key;
            if (key.Length == 0 || key == Bound) return false;
            Bound = key;
            return true;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private static string Label(string key) { var i = key.IndexOf('-'); return i > 0 ? key[(i + 1)..] : key; }

    /// <summary>
    /// The WPF card's character selector (A2M-28) for a view header: every characters/ folder, the bound one marked "(current)", amber while
    /// viewing someone else. A pick moves the shared view (<see cref="Select"/>), every view that uses it follows.
    /// </summary>
    public ComboBox Picker()
    {
        var box = new ComboBox { MinWidth = 130, FontSize = 12, Height = 28, Margin = new Thickness(0, 0, 6, 0), MaxDropDownHeight = 360 };
        var syncing = false;
        var shownAs = "";
        void Fill()
        {
            var keys = _shell.Engine?.CharacterKeys() ?? new List<string>();
            var bound = Bound;
            if (bound.Length > 0 && !keys.Contains(bound)) keys.Add(bound);
            var viewed = Viewed;
            // refilling an open drop-down closes it: only when something changed
            var sig = bound + "|" + viewed + "|" + string.Join(",", keys);
            if (sig == shownAs) return;
            shownAs = sig;
            syncing = true;
            try
            {
                box.Items.Clear();
                var sel = -1;
                for (var i = 0; i < keys.Count; i++)
                {
                    var k = keys[i];
                    box.Items.Add(new ComboBoxItem { Content = k == bound ? Label(k) + UiText.Current.CharCurrentSuffix : Label(k), Tag = k });
                    if (k == viewed) sel = i;
                }
                box.SelectedIndex = sel;
                box.IsVisible = keys.Count > 0;
                var other = viewed.Length > 0 && viewed != bound && bound.Length > 0;
                box.Foreground = other ? Visuals.Amber : Visuals.Text;
                box.BorderBrush = other ? Visuals.Amber : Visuals.Line;
                ToolTip.SetTip(box, other ? string.Format(UiText.Current.CharViewingTip, Label(bound), Label(viewed)) : UiText.Current.CharSelectTip);
            }
            finally { syncing = false; }
        }
        box.SelectionChanged += (_, _) => { if (!syncing && box.SelectedItem is ComboBoxItem { Tag: string k }) Select(k); };
        box.DropDownOpened += (_, _) => { if (!syncing) Fill(); };
        SelectionChanged += Fill;
        _shell.WhenEngine(_ => Fill());
        Fill();
        return box;
    }
}
