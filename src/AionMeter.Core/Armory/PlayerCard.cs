using System.Globalization;
using AionMeter.Core.Events;
using AionMeter.Core.Leaderboard;

namespace AionMeter.Core.Armory;

/// <summary>The five tiles of a fight (PLACE / DMG / DPS / CONTRIB / TIME) for one player.</summary>
public sealed record FightStats(int Place, int PlayerCount, long Damage, double Dps, double Share, long CombatMs);

public enum CardTab : byte { Dps, Accuracy, Rotation, Defense, Profile, Skills, Daevanion }

public enum CardTile : byte { CombatPower, ItemLevel, Rank, Seen }

/// <summary>
/// A2M-44: what the unified player card (BreakdownView) shows for one character: identity, the fight's five tiles when it came from a
/// fight, the official profile when one is known, and the ranking extras of a Top DPS row (rank, CP, item level, last seen).
/// </summary>
public sealed record PlayerCard(string Name, GameClass Class, string? Server, int ServerId)
{
    public int Level { get; init; }
    public string? Legion { get; init; }
    public string? Title { get; init; }
    public FightStats? Fight { get; init; }
    public CharacterProfile? Profile { get; init; }
    public long CombatPower { get; init; }
    public int ItemLevel { get; init; }
    public int Rank { get; init; }
    /// <summary>Where the rank counts: "EU", a server name.</summary>
    public string? RankScope { get; init; }
    public DateTimeOffset? Seen { get; init; }

    public long Cp => CombatPower > 0 ? CombatPower : Profile?.CombatPower ?? 0;
    public int Il => ItemLevel > 0 ? ItemLevel : Profile?.ItemLevel ?? 0;

    /// <summary>The same character: name (case-insensitive) and server, so a late profile answer lands on the card still showing it.</summary>
    public bool SameCharacter(PlayerCard other) =>
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) && (ServerId == 0 || other.ServerId == 0 || ServerId == other.ServerId);

    /// <summary>The profile laid over the identity: level, legion and title come from it when the card had none.</summary>
    public PlayerCard With(CharacterProfile p) => this with
    {
        Profile = p,
        Level = p.Level > 0 ? p.Level : Level,
        Legion = p.Legion ?? Legion,
        Title = p.Title ?? Title,
        Class = Class != GameClass.Unknown ? Class : LeaderboardRow.ClassOf(p.ClassName),
        Server = Server ?? (p.Ref.ServerName.Length > 0 ? p.Ref.ServerName : null),
        ServerId = ServerId != 0 ? ServerId : p.Ref.ServerId,
        Seen = Seen ?? p.FetchedAt,
    };

    public static PlayerCard Of(CharacterProfile p) =>
        new PlayerCard(p.Ref.Name, LeaderboardRow.ClassOf(p.ClassName), p.Ref.ServerName.Length > 0 ? p.Ref.ServerName : null, p.Ref.ServerId).With(p);

    public static PlayerCard Of(LeaderboardRow b) => new(b.Name, b.Class, b.ServerName, 0)
    {
        Level = b.Level, CombatPower = b.CombatPower, ItemLevel = b.ItemLevel, Rank = b.Rank, RankScope = b.Region.ToUpperInvariant(), Seen = b.LastInfoAt,
    };
}

public static class CardLayout
{
    private static readonly CardTab[] FightTabs = [CardTab.Dps, CardTab.Accuracy, CardTab.Rotation, CardTab.Defense];

    /// <summary>
    /// The tabs the card shows, in order: the four fight tabs only with a fight, Profile / Skills / Daevanion only when the profile has
    /// something for them (Diego: "cuando no haya datos de ciertos tabs, esos tabs no los mostramos").
    /// </summary>
    public static List<CardTab> Tabs(bool hasFight, CharacterProfile? p)
    {
        var list = new List<CardTab>(7);
        if (hasFight) list.AddRange(FightTabs);
        if (p is null) return list;
        if (p.Equipment.Count > 0 || p.Stats.Count > 0 || p.CombatPower > 0) list.Add(CardTab.Profile);
        if (p.Skills.Count > 0) list.Add(CardTab.Skills);
        if (p.Boards.Count > 0 || p.DaevanionEffects.Count > 0) list.Add(CardTab.Daevanion);
        return list;
    }

    /// <summary>The header tiles of a card without a fight, in order; a tile without a value is left out.</summary>
    public static List<(CardTile Tile, string Value)> Tiles(PlayerCard c, DateTimeOffset now)
    {
        var list = new List<(CardTile, string)>(4);
        if (c.Cp > 0) list.Add((CardTile.CombatPower, Format.Grouped(c.Cp)));
        if (c.Il > 0) list.Add((CardTile.ItemLevel, Format.Grouped(c.Il)));
        if (c.Rank > 0) list.Add((CardTile.Rank, c.RankScope is { Length: > 0 } s ? $"#{c.Rank} {s}" : $"#{c.Rank}"));
        if (c.Seen is { } t) list.Add((CardTile.Seen, Ago(t, now)));
        return list;
    }

    /// <summary>"now", "12 min", "5 h", "3 d" (time since <paramref name="t"/>).</summary>
    public static string Ago(DateTimeOffset t, DateTimeOffset now)
    {
        var d = now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return ((int)d.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        if (d.TotalDays < 1) return ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + " h";
        return ((int)d.TotalDays).ToString(CultureInfo.InvariantCulture) + " d";
    }

    /// <summary>"Templar · Lv. 45 · Vaizel · Pandamonium" — the parts that are known, in that order.</summary>
    public static string Meta(string className, PlayerCard c)
    {
        var parts = new List<string>(4) { className };
        if (c.Level > 0) parts.Add("Lv. " + c.Level.ToString(CultureInfo.InvariantCulture));
        if (c.Server is { Length: > 0 } s) parts.Add(s);
        if (c.Legion is { Length: > 0 } l) parts.Add(l);
        return string.Join(" · ", parts);
    }
}
