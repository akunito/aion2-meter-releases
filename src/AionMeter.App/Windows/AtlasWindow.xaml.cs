using System.Windows;
using AionMeter.App.Services;
using AionMeter.App.Views;

namespace AionMeter.App.Windows;

/// <summary>Stand-alone host of <see cref="AtlasView"/>; the overlay shows the same view as a tab (2026-10-10).</summary>
public partial class AtlasWindow : Window
{
    public AtlasView View { get; }

    public AtlasWindow(MeterService meter)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = new AtlasView(meter);
        View.CloseRequested += Close;
        Host.Content = View;
        var s = meter.Settings;
        if (s.AtlasLeft is { } left && s.AtlasTop is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left; Top = top; Width = Math.Max(500, s.AtlasWidth); Height = Math.Max(400, s.AtlasHeight);
        }
        Closed += (_, _) => { if (WindowState == WindowState.Normal) { s.AtlasLeft = Left; s.AtlasTop = Top; s.AtlasWidth = Width; s.AtlasHeight = Height; s.Save(); } };
    }
}
