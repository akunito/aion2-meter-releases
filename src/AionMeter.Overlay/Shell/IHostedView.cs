using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// A tab of the layer-shell card (A2M-50, the Avalonia IHostedView on GTK): the card shows <see cref="View"/> in place of the boss bar and the
/// DPS rows, the `?` opens data/help/&lt;<see cref="HelpKey"/>&gt;.&lt;lang&gt;.md. Register a factory in <see cref="ViewRegistry"/> (one line) to put
/// a view in the tab strip. Views are recreated when the card is rebuilt (UI scale): whatever must survive lives in the shell or in files.
/// </summary>
internal interface IHostedView
{
    /// <summary>Registry key: "settings", "fight", "atlas", "top", "prices", "craft", "build", "items", "timers".</summary>
    string Key { get; }
    Gtk.Widget View { get; }
    /// <summary>data/help/&lt;HelpKey&gt;.&lt;lang&gt;.md ("meter" for the fight breakdown).</summary>
    string HelpKey { get; }
    /// <summary>In the card: a Back chip raises <see cref="CloseRequested"/> (Esc too, handled by the card); outside it an X does.</summary>
    bool Hosted { get; set; }
    /// <summary>Brought to front: start timers, refresh from the engine / socket.</summary>
    void Shown();
    /// <summary>Another tab in front: stop timers. A hidden view keeps its state and is reused while the card lives.</summary>
    void Hidden();
    /// <summary>The language changed (Settings save): rebuild the texts.</summary>
    void LanguageChanged();
    /// <summary>The card drops this view (card rebuilt on a UI-scale change, or the process ends): unsubscribe from the shell, stop timers.</summary>
    void Detach();
    event Action? CloseRequested;
}

/// <summary>
/// Base for hosted views: the WPF view frame (Bg0 card with a GoldDim hairline, class k-view), a header with Back chip, serif title, `?` and
/// the view's own chips on the right (<see cref="HeaderRight"/>), the body (<see cref="SetBody"/>) and an optional footer bar (<see cref="SetFooter"/>).
/// Shell event subscriptions go through <see cref="Listen"/> so <see cref="Detach"/> undoes them when the card is rebuilt.
/// </summary>
internal abstract class HostedView : IHostedView
{
    protected readonly AppShell Shell;
    private readonly Gtk.Box _root;
    private readonly Gtk.Button _back, _close;
    private readonly Gtk.Label _title;
    private Gtk.Widget? _body;
    private Gtk.Widget? _footer;
    private readonly List<Action> _cleanup = new();
    private bool _shown;
    protected readonly Gtk.Box HeaderRight;

    protected HostedView(AppShell shell, string key, string title, string? helpKey = null)
    {
        Shell = shell;
        Key = key;
        HelpKey = helpKey ?? key;
        _root = UiKit.VBox();
        _root.AddCssClass("k-root");
        _root.AddCssClass("k-view");
        _root.Vexpand = true;
        _root.Hexpand = true;
        var header = UiKit.HBox();
        header.AddCssClass("k-head");
        _back = UiKit.Chip(UiText.Current.Back, () => CloseRequested?.Invoke(), null, Icons.Back);
        _back.MarginEnd = UiKit.Px(10);
        header.Append(_back);
        _title = Gtk.Label.New(title);
        _title.AddCssClass("k-title");
        _title.Ellipsize = Pango.EllipsizeMode.End;
        _title.Valign = Gtk.Align.Center;
        header.Append(_title);
        var help = UiKit.IconButton(Icons.Help, UiText.Current.TipHelp, () => Shell.ShowHelp(HelpKey));
        help.MarginStart = UiKit.Px(8);
        header.Append(help);
        header.Append(UiKit.Spacer());
        HeaderRight = UiKit.HBox();
        HeaderRight.Valign = Gtk.Align.Center;
        header.Append(HeaderRight);
        _close = UiKit.IconButton(Icons.Close, UiText.Current.Cancel, () => CloseRequested?.Invoke());
        _close.MarginStart = UiKit.Px(6);
        _close.Visible = false;
        header.Append(_close);
        _root.Append(header);
    }

    public string Key { get; }
    public Gtk.Widget View => _root;
    public string HelpKey { get; protected set; }
    public event Action? CloseRequested;
    protected bool IsShown => _shown;

    public bool Hosted
    {
        get => _back.Visible;
        set { _back.Visible = value; _close.Visible = !value; }
    }

    protected string Title { get => _title.GetText(); set => _title.SetText(value); }

    protected void SetBody(Gtk.Widget body)
    {
        if (_body is not null) _root.Remove(_body);
        _body = body;
        body.Vexpand = true;
        if (_footer is not null) _root.InsertChildAfter(body, _root.GetFirstChild());
        else _root.Append(body);
    }

    protected void SetFooter(Gtk.Widget? footer)
    {
        if (_footer is not null) _root.Remove(_footer);
        _footer = null;
        if (footer is null) return;
        var bar = UiKit.HBox();
        bar.AddCssClass("k-foot");
        footer.Hexpand = true;
        bar.Append(footer);
        _footer = bar;
        _root.Append(bar);
    }

    protected void RaiseClose() => CloseRequested?.Invoke();

    /// <summary>Subscribes now and unsubscribes on <see cref="Detach"/>: <c>Listen(() =&gt; Shell.Daemon.Overlay += OnOverlay, () =&gt; Shell.Daemon.Overlay -= OnOverlay)</c>.</summary>
    protected void Listen(Action subscribe, Action unsubscribe)
    {
        subscribe();
        _cleanup.Add(unsubscribe);
    }

    /// <summary>Runs on <see cref="Detach"/> (stop a timer that runs even while hidden, dispose a resource).</summary>
    protected void OnDetach(Action cleanup) => _cleanup.Add(cleanup);

    void IHostedView.Shown() { _shown = true; Shown(); }
    void IHostedView.Hidden() { _shown = false; Hidden(); }

    public virtual void Shown() { }
    public virtual void Hidden() { }

    public virtual void LanguageChanged()
    {
        UiKit.SetChipText(_back, UiText.Current.Back);
    }

    public void Detach()
    {
        if (_shown) { _shown = false; Hidden(); }
        foreach (var a in _cleanup) a();
        _cleanup.Clear();
    }
}
