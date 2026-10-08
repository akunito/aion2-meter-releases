using System.Windows;
using System.Windows.Controls;
using AionMeter.App.Services;

namespace AionMeter.App.Windows;

/// <summary>Asked once per unknown object code when the player starts gathering it; the answer names every node of that kind.</summary>
public partial class NameNodeWindow : Window
{
    private readonly MeterService _meter;
    private readonly int _code;

    public NameNodeWindow(MeterService meter, int code)
    {
        _meter = meter;
        _code = code;
        InitializeComponent();
        Question.Text = string.Format(UiText.Current.NameNodeQuestion, code);
        Loaded += (_, _) => { NameBox.Focus(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var category = CategoryBox.SelectedItem is ComboBoxItem item ? item.Content?.ToString() ?? "" : CategoryBox.Text;
        _meter.NameObject(_code, NameBox.Text, category ?? "");
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();
}
