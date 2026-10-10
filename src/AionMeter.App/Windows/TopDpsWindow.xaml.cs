using System.Windows;
using AionMeter.App.Services;
using AionMeter.App.Views;

namespace AionMeter.App.Windows;

/// <summary>Stand-alone host of <see cref="TopDpsView"/>; the overlay shows the same view as a tab (2026-10-10).</summary>
public partial class TopDpsWindow : Window
{
    public TopDpsView View { get; }

    public TopDpsWindow(MeterService meter)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = new TopDpsView(meter);
        View.CloseRequested += Close;
        Host.Content = View;
    }
}
