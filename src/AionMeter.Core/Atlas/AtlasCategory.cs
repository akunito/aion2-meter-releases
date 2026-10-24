namespace AionMeter.Core.Atlas;

public static class AtlasCategory
{
    /// <summary>Category of an object the player said is not a gathering node (a door, a chest…): never drawn on the map, never asked again.</summary>
    public const string Other = "other";

    public static bool IsOther(string category) => string.Equals(category, Other, StringComparison.OrdinalIgnoreCase);
}
