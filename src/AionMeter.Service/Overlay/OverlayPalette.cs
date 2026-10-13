using AionMeter.Core.Events;

namespace AionMeter.Service.Overlay;

/// <summary>Class colours (0xRRGGBB) shared by the WPF overlay (ClassVisuals) and the Linux UI.</summary>
public static class OverlayPalette
{
    /// <summary>Sampled from the in-game class emblems, brightened to read well as fills (names, tile frames, thin bars).</summary>
    public static uint ClassColor(GameClass c) => c switch
    {
        GameClass.Gladiator => 0x5FC0D6,
        GameClass.Templar => 0x6E91EE,
        GameClass.Ranger => 0x4DBE90,
        GameClass.Assassin => 0x6FC85F,
        GameClass.Sorcerer => 0x9B66EC,
        GameClass.Elementalist => 0xCF55D0,
        GameClass.Cleric => 0xE8BE52,
        GameClass.Chanter => 0xE59B4A,
        GameClass.Brawler => 0xDE3A41,
        _ => 0x94A3B8,
    };

    /// <summary>Saturated fills for the full-height damage bars (the emblem hues, pushed so a whole row reads at a glance).</summary>
    public static uint BarColor(GameClass c) => c switch
    {
        GameClass.Gladiator => 0x1FA2C8,
        GameClass.Templar => 0x3B7CE6,
        GameClass.Ranger => 0x1CAE7E,
        GameClass.Assassin => 0x46B84E,
        GameClass.Sorcerer => 0x864CE0,
        GameClass.Elementalist => 0xC23FC4,
        GameClass.Cleric => 0xD8A22A,
        GameClass.Chanter => 0xEA7D2C,
        GameClass.Brawler => 0xD6333E,
        _ => 0x5E6B84,
    };
}
