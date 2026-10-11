using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AionMeter.App.Services;
using AionMeter.Core.Updates;

namespace AionMeter.App.Windows;

/// <summary>"?" on every tab: the page for that tab from data\help\&lt;tab&gt;.&lt;lang&gt;.md, EN by default, ES on the dropdown (A2M-24, saved as HelpLanguage).</summary>
public partial class HelpWindow : Window
{
    private readonly AppSettings _settings;
    private string _tab = "meter";
    private bool _loading;

    public HelpWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        _loading = true;
        foreach (ComboBoxItem it in LangBox.Items) if ((string)it.Tag == settings.HelpLanguage) LangBox.SelectedItem = it;
        if (LangBox.SelectedItem is null) LangBox.SelectedIndex = 0;
        _loading = false;
    }

    public void ShowTab(string tab)
    {
        _tab = tab;
        var t = UiText.Current;
        TitleText.Text = t.HelpTitle + " · " + tab switch { "atlas" => t.AtlasTitle, "top" => t.TopTitle, "prices" => t.PricesTitle, "craft" => t.CraftTitle, "build" => t.BuildTitle, "settings" => t.SettingsTitle, _ => "DPS" };
        Render();
        if (!IsVisible) Show(); else Activate();
    }

    private void Render()
    {
        var lang = LangBox.SelectedItem is ComboBoxItem { Tag: string l } ? l : "en";
        var dir = Path.Combine(MeterService.DataDirectory, "help");
        var path = Path.Combine(dir, $"{_tab}.{lang}.md");
        if (!File.Exists(path)) path = Path.Combine(dir, $"{_tab}.en.md");
        var gold = (Brush)FindResource("Gold");
        Body.Inlines.Clear();
        if (!File.Exists(path)) { Body.Inlines.Add(new Run(UiText.Current.HelpMissing)); return; }
        var first = true;
        foreach (var (text, heading) in ReleaseNotes.Lines(File.ReadAllText(path), lang))
        {
            if (!first) Body.Inlines.Add(new LineBreak());
            first = false;
            if (heading) Body.Inlines.Add(new Run(text) { FontWeight = FontWeights.SemiBold, Foreground = gold, FontSize = 14 });
            else Body.Inlines.Add(new Run(text));
        }
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.HelpLanguage = LangBox.SelectedItem is ComboBoxItem { Tag: string l } ? l : "en";
        _settings.Save();
        Render();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
