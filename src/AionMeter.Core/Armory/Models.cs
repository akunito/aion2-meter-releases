namespace AionMeter.Core.Armory;

/// <summary>A character as the official search returns it (<c>api-search.plaync.com</c>). PcId is the class id (Templar = 10).</summary>
public sealed record CharacterRef(string CharacterId, string Name, int PcId, int Level, int ServerId, string ServerName, string Region);

/// <summary>One line of the stat panel: primary (STR…WIS), deity (Justice [Nezekan] …) or ItemLevel; Effects = the "+x%" texts.</summary>
public sealed record StatLine(string Type, string Name, string Value, IReadOnlyList<string> Effects);

public sealed record BoardSummary(int Id, string Name, int Open, int Total);

public sealed record EquipItem(int SlotPos, string Slot, string Name, string Grade, int Enchant, int Exceed, long Id, string? Icon);

/// <summary>Category is Active, Passive or Dp (stigma-like); Id is the skill code the meter sees in casts.</summary>
public sealed record SkillLine(string Category, string Name, int Level, bool Equipped, bool Acquired, int Id);

public sealed record DaevanionNode(int BoardId, int Row, int Col, int NodeId, bool Open, string Grade, string Type, string Name, IReadOnlyList<string> Effects);

/// <summary>Everything the official API exposes about one character, flattened; cached on disk with <see cref="FetchedAt"/>.</summary>
public sealed record CharacterProfile(
    CharacterRef Ref,
    string ClassName,
    int Level,
    long CombatPower,
    string? Title,
    string? Legion,
    string? Race,
    IReadOnlyList<StatLine> Stats,
    int ItemLevel,
    IReadOnlyList<BoardSummary> Boards,
    IReadOnlyList<EquipItem> Equipment,
    string? Pet,
    string? Wing,
    IReadOnlyList<SkillLine> Skills,
    IReadOnlyList<DaevanionNode> Daevanion,
    IReadOnlyList<string> DaevanionEffects,
    DateTimeOffset FetchedAt)
{
    public IEnumerable<SkillLine> EquippedDp => Skills.Where(s => s.Category == "Dp" && s.Equipped);
    public StatLine? Stat(string type) => Stats.FirstOrDefault(s => s.Type == type);
}
