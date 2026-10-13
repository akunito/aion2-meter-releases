using System.Windows;
using AionMeter.App.Services;
using AionMeter.App.ViewModels;
using AionMeter.App.Views;
using AionMeter.Core.Armory;
using AionMeter.Core.Game;

namespace AionMeter.App.Windows;

/// <summary>Stand-alone host of <see cref="BreakdownView"/> (pop-out, --open-last, overlay hidden); the overlay shows the same view as its "fight" tab (A2M-43).</summary>
public partial class BreakdownWindow : Window
{
    public BreakdownView View { get; }

    public BreakdownWindow(IFightView view, uint actorId, GameData data, SkillIcons icons, BossPortraits? portraits = null, Func<Guid, string?>? fightNote = null, ProfileDeps? deps = null)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = Wire(new BreakdownView(data, icons, portraits, fightNote, hosted: false, deps));
        View.SetFight(view, actorId);
        Host.Content = View;
    }

    /// <summary>A2M-44: the player card without a fight (Top DPS row, overlay hidden or popped out).</summary>
    public BreakdownWindow(PlayerCard card, string tab, string? status, GameData data, SkillIcons icons, ProfileDeps deps)
    {
        InitializeComponent();
        Controls.DragAnywhere.Attach(this);
        View = Wire(new BreakdownView(data, icons, null, null, hosted: false, deps));
        View.ShowCard(card, tab, status);
        Host.Content = View;
    }

    private BreakdownView Wire(BreakdownView v)
    {
        v.CloseRequested += Close;
        v.MinimizeRequested += () => WindowState = WindowState.Minimized;
        v.TitleChanged += t => Title = t;
        return v;
    }

    public bool Shows(IFightView view) => View.Shows(view);

    public void ShowTab(string name) => View.ShowTab(name);

    public void Select(uint actorId) => View.Select(actorId);
}
