using System.Runtime.CompilerServices;
using System.Text.Json;
using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

[Flags]
internal enum DataChange : byte { None = 0, Characters = 1, Prices = 2, Market = 4 }

/// <summary>
/// A2M-48 phase 3c (GTK port A2M-50, same logic as the Avalonia DataWatch): the daemon rewrites characters/*/{inventory,character}.json, account-warehouse.json and the price files every ≤ 5 s;
/// the viewer engine has no change event for another process's writes. One 2 s mtime poll per shell (stat only, no reads) while at least
/// one view is shown, raising <see cref="Changed"/> with what moved. Also the character the tabs show (Items, Craft): the engine's
/// <see cref="MeterEngine.CharacterKey"/> once picked, else the daemon's bound character (`status`, state/summary.json without a daemon).
/// </summary>
internal sealed class DataWatch
{
    private static readonly ConditionalWeakTable<AppShell, DataWatch> PerShell = new();
    public static DataWatch Of(AppShell shell) => PerShell.GetValue(shell, s => new DataWatch(s));

    private readonly AppShell _shell;
    private readonly UiTimer _timer = new(TimeSpan.FromSeconds(2));
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
        _timer.Tick += Poll;
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
    /// viewing someone else. A pick moves the shared view (<see cref="Select"/>), every view that uses it follows. Unsubscribe with the returned
    /// action on the view's Detach (HostedView.OnDetach).
    /// </summary>
    public (Gtk.DropDown Box, Action Unsubscribe) Picker()
    {
        var box = UiKit.Combo([], -1, 130);
        box.MarginEnd = UiKit.Px(6);
        var syncing = false;
        var shownAs = "";
        var keys = new List<string>();
        void Fill()
        {
            var now = _shell.Engine?.CharacterKeys() ?? new List<string>();
            var bound = Bound;
            if (bound.Length > 0 && !now.Contains(bound)) now.Add(bound);
            var viewed = Viewed;
            // refilling an open drop-down closes it: only when something changed
            var sig = bound + "|" + viewed + "|" + string.Join(",", now);
            if (sig == shownAs) return;
            shownAs = sig;
            syncing = true;
            try
            {
                keys = now;
                var labels = new string[now.Count];
                var sel = -1;
                for (var i = 0; i < now.Count; i++)
                {
                    labels[i] = now[i] == bound ? Label(now[i]) + UiText.Current.CharCurrentSuffix : Label(now[i]);
                    if (now[i] == viewed) sel = i;
                }
                UiKit.SetItems(box, labels, sel);
                box.Visible = now.Count > 0;
                var other = viewed.Length > 0 && viewed != bound && bound.Length > 0;
                UiKit.SetFg(box, other ? Fg.Amber : Fg.Text);
                box.SetTooltipText(other ? string.Format(UiText.Current.CharViewingTip, Label(bound), Label(viewed)) : UiText.Current.CharSelectTip);
            }
            finally { syncing = false; }
        }
        UiKit.OnSelected(box, () =>
        {
            var i = UiKit.Selected(box);
            if (!syncing && i >= 0 && i < keys.Count) Select(keys[i]);
        });
        SelectionChanged += Fill;
        _shell.WhenEngine(_ => Fill());
        Fill();
        return (box, () => SelectionChanged -= Fill);
    }
}
