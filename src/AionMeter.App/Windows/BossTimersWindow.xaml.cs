using System.Windows;
using AionMeter.App.Services;
using AionMeter.App.Views;

namespace AionMeter.App.Windows;

/// <summary>Stand-alone host of <see cref="BossTimersView"/> (tray, hotkey); the overlay shows the same view as a tab (A2M-39).</summary>
public partial class BossTimersWindow : Window
{
    public BossTimersView View { get; }

    public BossTimersWindow(MeterService meter)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = new BossTimersView(meter);
        View.CloseRequested += Close;
        Host.Content = View;
    }
}
