using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// An extra layer-shell surface beside the card (A2M-50: dialogs, help): OVERLAY layer on the card's output, no anchors = centred by the
/// compositor, keyboard ON_DEMAND (a click gives it the keyboard; the game keeps it until then), the card's dialog frame (gold-edged dark card,
/// class k-dialog under k-root) with a serif title and an X; Esc closes. Sway paints no floating window over a fullscreen view, so a normal
/// GTK window would sit under the game. Window mode (the default since A2M-50's window pass): a transient toplevel of the card instead, the
/// patched Sway's above_fullscreen rule keeps it over the game. Size in unscaled px (× the card's scale); CSS restyles it with the card's sheet.
/// </summary>
internal abstract class LayerWindow
{
    private readonly Gtk.Window _win;
    private readonly Gtk.Box _inner;
    private readonly Gtk.Label _title;
    private Gtk.Widget? _content;
    private bool _closed;
    protected readonly AppShell Shell;
    /// <summary>Widgets between the title and the X (the help's language switch).</summary>
    protected readonly Gtk.Box HeadRight;

    public event Action? Closed;

    protected LayerWindow(AppShell shell, string ns, string title, double width, double height)
    {
        Shell = shell;
        var win = _win = Gtk.Window.New();
        win.Application = shell.Host.App;
        win.Title = title;
        win.Decorated = false;
        win.AddCssClass("aionmeter");
        if (shell.Host.Layer)
        {
            var w = win.Handle.DangerousGetHandle();
            LayerShell.InitForWindow(w);
            LayerShell.SetNamespace(w, ns);
            LayerShell.SetLayer(w, LayerShell.Layer.Overlay);
            LayerShell.SetKeyboardMode(w, LayerShell.KeyboardMode.OnDemand);
            LayerShell.SetExclusiveZone(w, 0);
            if (shell.Host.Monitor != IntPtr.Zero) LayerShell.SetMonitor(w, shell.Host.Monitor);
        }
        else
        {
            // window mode: a transient toplevel of the card, so Sway floats it, centres it and stacks it above the card (the card's
            // for_window rules match the same app_id: sticky and above_fullscreen too)
            win.TransientFor = shell.Host.Window;
            win.Resizable = false;
        }

        var frame = UiKit.VBox();
        frame.AddCssClass("k-root");
        frame.AddCssClass("k-dialog");
        frame.SetSizeRequest(UiKit.Px(width), UiKit.Px(height));
        _inner = UiKit.VBox();
        _inner.AddCssClass("k-dinner");
        _inner.Vexpand = true;
        frame.Append(_inner);
        var head = UiKit.HBox();
        head.AddCssClass("k-dhead");
        _title = Gtk.Label.New(title);
        _title.AddCssClass("k-dtitle");
        _title.Xalign = 0;
        _title.Hexpand = true;
        _title.Ellipsize = Pango.EllipsizeMode.End;
        head.Append(_title);
        HeadRight = UiKit.HBox();
        HeadRight.Valign = Gtk.Align.Center;
        head.Append(HeadRight);
        var close = UiKit.IconButton(Icons.Close, UiText.Current.TipClose, Close);
        close.MarginStart = UiKit.Px(8);
        head.Append(close);
        _inner.Append(head);
        win.Child = frame;

        var keys = Gtk.EventControllerKey.New();
        keys.OnKeyPressed += (_, e) =>
        {
            if (e.Keyval != 0xff1b) return false;   // GDK_KEY_Escape
            Close();
            return true;
        };
        win.AddController(keys);
        win.OnCloseRequest += (_, _) => { Close(); return true; };
    }

    protected string Title { get => _title.GetText(); set { _title.SetText(value); _win.Title = value; } }

    protected void SetContent(Gtk.Widget content)
    {
        if (_content is not null) _inner.Remove(_content);
        _content = content;
        content.Vexpand = true;
        _inner.Append(content);
    }

    public bool IsOpen => !_closed && _win.GetVisible();

    public void Show()
    {
        _win.Present();
        Opened();
    }

    /// <summary>Window mode: brings an open dialog to the front (layer surfaces cannot be raised).</summary>
    public void Raise()
    {
        if (!Shell.Host.Layer && IsOpen) _win.Present();
    }

    /// <summary>After the first Present: focus an entry. Measured (nested Sway 1.11, 2026-10-13): a newly mapped ON_DEMAND surface does not get the
    /// keyboard — keys kept going to the focused app — until it is clicked; Esc then closed it and the app had the keyboard again.</summary>
    protected virtual void Opened() { }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        OnClosed();
        _win.Destroy();
        Closed?.Invoke();
    }

    protected virtual void OnClosed() { }
}
