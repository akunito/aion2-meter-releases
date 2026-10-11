namespace AionMeter.Core.Character;

/// <summary>What a pick in the character selector asks the meter to do.</summary>
public enum CharacterPick
{
    /// <summary>Already viewing it.</summary>
    None,
    /// <summary>Only the view moves; the live stores stay with the bound character.</summary>
    View,
    /// <summary>Game closed: the live stores move to the pick too (bound = viewed).</summary>
    Rebind,
}

/// <summary>
/// A2M-28 view-while-playing: <see cref="Bound"/> is the character the live packets belong to, <see cref="Viewed"/> the one the tabs
/// show. A login (bound changes) always pulls the view to the logged-in character, whatever was viewed; a pick while the game runs
/// moves only the view; a pick with the game closed rebinds, except back to the bound character (nothing to rebind). Not thread-safe:
/// the meter calls it under its own lock.
/// </summary>
public sealed class CharacterSelection
{
    public string Bound { get; private set; } = "";
    public string Viewed { get; private set; } = "";
    public bool IsViewingOther => Viewed.Length > 0 && Viewed != Bound;

    /// <summary>The bound character came from a pick with the game closed, not from the game: the next identity rebinds even if it is the same one (the legacy mirrors were left off).</summary>
    public bool BoundByHand { get; private set; }

    /// <summary>The live identity says <paramref name="key"/>: true when the live stores must (re)bind and the view jumps to it. False for a
    /// repeat of the bound identity (identity packets repeat while playing; they must not yank the view back).</summary>
    public bool Login(string key)
    {
        if (key.Length == 0 || (key == Bound && !BoundByHand)) return false;
        Bound = key;
        Viewed = key;
        BoundByHand = false;
        return true;
    }

    public CharacterPick Pick(string key, bool gameRunning)
    {
        if (key.Length == 0 || key == Viewed) return CharacterPick.None;
        Viewed = key;
        if (key == Bound || gameRunning) return CharacterPick.View;
        Bound = key;
        BoundByHand = true;
        return CharacterPick.Rebind;
    }
}
