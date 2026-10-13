using AionMeter.Service;

namespace AionMeter.Overlay.Shell;

/// <summary>
/// "?" on every tab (WPF HelpWindow, A2M-24) as a layer surface (A2M-50): data/help/&lt;tab&gt;.&lt;lang&gt;.md as <see cref="HelpPage"/> blocks —
/// headings in gold, paragraphs selectable — EN by default, ES on the switch (saved as HelpLanguage).
/// </summary>
internal sealed class HelpWindow : LayerWindow
{
    private readonly Gtk.Box _body;
    private readonly Gtk.DropDown _lang;
    private readonly Gtk.ScrolledWindow _scroll;
    private string _tab = "meter";
    private bool _loading;

    public HelpWindow(AppShell shell) : base(shell, "aionmeter-help", UiText.Current.HelpTitle, 620, 560)
    {
        _loading = true;
        _lang = UiKit.Combo(["English", "Español"], shell.Settings.HelpLanguage == "es" ? 1 : 0, 110);
        _loading = false;
        UiKit.OnSelected(_lang, () =>
        {
            if (_loading) return;
            var lang = UiKit.Selected(_lang) == 1 ? "es" : "en";
            if (Shell.Settings.HelpLanguage != lang)
            {
                Shell.RefreshSettings();
                Shell.Settings.HelpLanguage = lang;
                Shell.SaveSettings();
            }
            Render();
        });
        HeadRight.Append(_lang);
        _body = UiKit.VBox();
        _body.MarginEnd = UiKit.Px(8);
        _body.MarginBottom = UiKit.Px(12);
        _scroll = UiKit.Scroll(_body, gutters: false);
        _scroll.MarginStart = UiKit.Px(14);
        _scroll.MarginEnd = UiKit.Px(6);
        SetContent(_scroll);
        Closed += () => shell.HelpClosed(this);
    }

    public void ShowTab(string tab)
    {
        _tab = HelpPage.Tab(tab);
        Title = HelpPage.Title(_tab, UiText.Current);
        Render();
        if (!IsOpen) Show();
    }

    private void Render()
    {
        var lang = UiKit.Selected(_lang) == 1 ? "es" : "en";
        var path = HelpPage.PathFor(MeterEngine.DataDirectory, _tab, lang);
        string? md = null;
        try { if (path is not null) md = File.ReadAllText(path); } catch (IOException) { }
        while (_body.GetFirstChild() is { } c) _body.Remove(c);
        foreach (var b in HelpPage.Blocks(md, lang))
        {
            var l = b.Heading ? UiKit.Text(b.Text, 15, Fg.Gold, 600) : UiKit.Text(b.Text, 13);
            if (b.Heading) l.MarginTop = UiKit.Px(10);
            l.Selectable = !b.Heading;
            l.MarginBottom = UiKit.Px(4);
            _body.Append(l);
        }
        _scroll.GetVadjustment()?.SetValue(0);
    }
}
