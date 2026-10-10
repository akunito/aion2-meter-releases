using System.Windows;
using AionMeter.App.Services;
using AionMeter.App.Views;

namespace AionMeter.App.Windows;

/// <summary>Stand-alone host of <see cref="PriceWatchView"/>; the overlay shows the same view as a tab (2026-10-10).</summary>
public partial class PriceWatchWindow : Window
{
    public PriceWatchView View { get; }

    public PriceWatchWindow(MeterService meter)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = new PriceWatchView(meter);
        View.CloseRequested += Close;
        Host.Content = View;
    }
}
