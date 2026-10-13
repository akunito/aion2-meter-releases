using System.IO;
using AionMeter.Core.Capture;
using AionMeter.Core.Combat;
using AionMeter.Core.Game;
using AionMeter.Core.GameDb;
using AionMeter.Core.Storage;

namespace AionMeter.App.Services;

/// <summary>The engine plus what only the WPF app has: skill/item icons and boss portraits as ImageSource, the player card's deps and the updater (A2M-48).</summary>
public sealed class MeterService : MeterEngine
{
    public MeterService(AppSettings settings, Func<GameData, IEventSource>? captureFactory, CaptureAvailability? availability = null)
        : base(settings, captureFactory, availability)
    {
        Icons = new SkillIcons(DataDirectory, settings.DownloadIcons);
        Portraits = new BossPortraits(DataDirectory, settings.DownloadIcons);
        Updates = new Updater(settings);
        ItemIconCache = new IconCache(ItemIcons.Load(Path.Combine(DataDirectory, "gamedb", "item-icons.json"))) { Enabled = settings.DownloadIcons, ExtraUrl = Names.ExtraIconUrl };
    }

    public SkillIcons Icons { get; }
    public BossPortraits Portraits { get; }
    /// <summary>New versions on GitHub; checks only run once the app calls <see cref="Updater.Start"/>.</summary>
    public Updater Updates { get; }
    /// <summary>Item icons on demand for the Craft and Prices rows (A2M-26).</summary>
    public IconCache ItemIconCache { get; }

    private Views.ProfileDeps? _cardDeps;
    /// <summary>A2M-44: what the player card needs for its profile tabs (item/skill icons, skill catalogue, profiles known without the network).</summary>
    public Views.ProfileDeps CardDeps
    {
        get => _cardDeps ??= new Views.ProfileDeps(ItemIconCache, Skills, () => Settings.SkillLanguage, KnownProfile);
        set => _cardDeps = value;   // render tool: sample profile and icon folder
    }

    /// <summary>Portrait of the fight's boss, or of its main target when there was none (null until downloaded).</summary>
    public System.Windows.Media.ImageSource? PortraitOf(EncounterSnapshot s) => Portraits.Of(s);

    public System.Windows.Media.ImageSource? PortraitOf(HistoryEntry e) => Portraits.Of(e.BossCode, e.TargetCode);

    protected override void OnSettingsApplied()
    {
        Icons.Enabled = Settings.DownloadIcons;
        Portraits.Enabled = Settings.DownloadIcons;
        ItemIconCache.Enabled = Settings.DownloadIcons;   // the same "download icons" switch covers item icons
    }

    public override void Dispose()
    {
        base.Dispose();
        Updates.Dispose();
    }
}
