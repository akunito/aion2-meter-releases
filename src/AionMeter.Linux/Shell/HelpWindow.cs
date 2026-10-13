using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using AionMeter.Core.Updates;
using AionMeter.Service;

namespace AionMeter.Linux.Shell;

/// <summary>
/// "?" on every tab (WPF HelpWindow, A2M-24): data/help/&lt;tab&gt;.&lt;lang&gt;.md through the same parser as the release notes
/// (ReleaseNotes.Lines: headings in gold, list items and paragraphs as lines), EN by default, ES on the switch (saved as HelpLanguage).
/// </summary>
public sealed class HelpWindow : CardWindow
{
    private readonly AppShell _shell;
    private readonly TextBlock _title;
    private readonly SelectableTextBlock _body;
    private readonly ComboBox _lang;
    private string _tab = "meter";
    private bool _loading;

    public HelpWindow(AppShell shell) : base(shell, "help", 620, 560)
    {
        _shell = shell;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new Grid { Margin = new Thickness(12, 6, 4, 10), ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        header.Children.Add(_title = new TextBlock { FontFamily = Visuals.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Visuals.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        _loading = true;
        _lang = UiKit.Combo(["English", "Español"], shell.Settings.HelpLanguage == "es" ? 1 : 0, 110);
        _loading = false;
        _lang.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            var lang = _lang.SelectedIndex == 1 ? "es" : "en";
            if (_shell.Settings.HelpLanguage == lang) { Render(); return; }
            _shell.RefreshSettings();
            _shell.Settings.HelpLanguage = lang;
            _shell.SaveSettings();
            Render();
        };
        header.Place(_lang, 0, 1);
        var close = UiKit.IconButton(Visuals.IconClose, UiText.Current.TipClose, Close);
        close.Margin = new Thickness(8, 0, 0, 0);
        header.Place(close, 0, 2);
        grid.Children.Add(header);
        _body = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Visuals.Text, LineHeight = 20, Margin = new Thickness(0, 0, 8, 12) };
        grid.Place(UiKit.Scroll(_body, new Thickness(14, 0, 10, 0)), 1, 0);
        Body = grid;
    }

    public void ShowTab(string tab)
    {
        _tab = tab == "fight" ? "meter" : tab;
        var t = UiText.Current;
        _title.Text = t.HelpTitle + " · " + _tab switch
        {
            "atlas" => t.AtlasTitle, "top" => t.TopTitle, "prices" => t.PricesTitle, "craft" => t.CraftTitle, "build" => t.BuildTitle,
            "items" => t.ItemsTitle, "timers" => t.TimersTitle, "settings" => t.SettingsTitle, _ => "DPS",
        };
        Render();
        if (!IsVisible) Show(); else Activate();
    }

    private void Render()
    {
        var lang = _lang.SelectedIndex == 1 ? "es" : "en";
        var dir = Path.Combine(MeterEngine.DataDirectory, "help");
        var path = Path.Combine(dir, $"{_tab}.{lang}.md");
        if (!File.Exists(path)) path = Path.Combine(dir, $"{_tab}.en.md");
        var inlines = new InlineCollection();
        if (!File.Exists(path)) { inlines.Add(new Run(UiText.Current.HelpMissing)); _body.Inlines = inlines; return; }
        var first = true;
        foreach (var (text, heading) in ReleaseNotes.Lines(File.ReadAllText(path), lang))
        {
            if (!first) inlines.Add(new LineBreak());
            first = false;
            if (heading) inlines.Add(new Run(text) { FontWeight = FontWeight.SemiBold, Foreground = Visuals.Gold, FontSize = 15 });
            else inlines.Add(new Run(text));
        }
        _body.Inlines = inlines;
    }
}
