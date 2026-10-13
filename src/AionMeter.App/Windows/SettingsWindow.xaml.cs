using System.Windows;
using AionMeter.App.Services;
using AionMeter.App.Views;

namespace AionMeter.App.Windows;

/// <summary>Stand-alone host of <see cref="SettingsView"/> (tray with the overlay hidden, the tab's pop-out); the overlay shows the same view as a tab (A2M-46).</summary>
public partial class SettingsWindow : Window
{
    public SettingsView View { get; }

    /// <param name="view">The overlay tab's view on pop-out (unsaved edits kept); null = a fresh one.</param>
    public SettingsWindow(MeterService meter, SettingsView? view = null)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = view ?? new SettingsView(meter, hosted: false);
        View.Hosted = false;
        View.CloseRequested += Close;
        Closed += (_, _) => View.Discard();
        Host.Content = View;
    }
}
