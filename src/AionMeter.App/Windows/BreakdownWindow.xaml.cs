using System.Windows;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.App.Views;
using AionMeter.Core.Game;

namespace AionMeter.App.Windows;

/// <summary>Stand-alone host of <see cref="BreakdownView"/> (pop-out, --open-last, overlay hidden); the overlay shows the same view as its "fight" tab (A2M-43).</summary>
public partial class BreakdownWindow : Window
{
    public BreakdownView View { get; }

    public BreakdownWindow(IFightView view, uint actorId, GameData data, SkillIcons icons, BossPortraits? portraits = null, Func<Guid, string?>? fightNote = null)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = new BreakdownView(data, icons, portraits, fightNote, hosted: false);
        View.CloseRequested += Close;
        View.MinimizeRequested += () => WindowState = WindowState.Minimized;
        View.TitleChanged += t => Title = t;
        View.SetFight(view, actorId);
        Host.Content = View;
    }

    public bool Shows(IFightView view) => View.Shows(view);

    public void ShowTab(string name) => View.ShowTab(name);

    public void Select(uint actorId) => View.Select(actorId);
}
