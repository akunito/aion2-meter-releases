using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionMeter.Overlay;

/// <summary>~/.local/share/AionMeter/overlay.json: the layer-shell overlay's place, size and switches (ui.json: the UI scale; settings.json: the daemon's).</summary>
public sealed class OverlaySettings
{
    /// <summary>Output connector ("DP-1"); empty = the compositor's choice (the focused output).</summary>
    public string Monitor { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<Anchor>))]
    public Anchor Anchor { get; set; } = Anchor.TopRight;
    /// <summary>Distance from the anchored left/right edge, logical px.</summary>
    public int MarginH { get; set; } = 24;
    /// <summary>Distance from the anchored top/bottom edge, logical px.</summary>
    public int MarginV { get; set; } = 140;
    /// <summary>Card size in unscaled px (the window is Width × Scale).</summary>
    public int Width { get; set; } = 560;
    public int Height { get; set; } = 540;
    /// <summary>Not saved here: the shared "UI scale" in ui.json (or --scale for one run).</summary>
    [JsonIgnore] public double Scale { get; set; } = 1;
    public bool ClickThrough { get; set; }
    // A2M-50 window mode (the default; the fields above are the --layer card's): the toplevel's rect in Sway's layout coordinates as get_tree
    // reports it, and the output it was on. WinW 0 = never placed: Sway's own placement.
    public string WinOutput { get; set; } = "";
    public int WinX { get; set; }
    public int WinY { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    [JsonIgnore] public WinGeom Win { get => new(WinOutput, WinX, WinY, WinW, WinH); set => (WinOutput, WinX, WinY, WinW, WinH) = (value.Output, value.X, value.Y, value.W, value.H); }
    public double Opacity { get; set; } = 0.92;

    public const int MinWidth = 340, MinHeight = 220;

    [JsonIgnore] public string Path { get; private set; } = "";

    public static OverlaySettings Load(string path)
    {
        OverlaySettings? s = null;
        try { if (File.Exists(path)) s = JsonSerializer.Deserialize(File.ReadAllText(path), OverlaySettingsJson.Default.OverlaySettings); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        s ??= new OverlaySettings();
        s.Path = path;
        s.Width = Math.Max(MinWidth, s.Width);
        s.Height = Math.Max(MinHeight, s.Height);
        s.Opacity = double.IsFinite(s.Opacity) ? Math.Clamp(s.Opacity, 0.2, 1) : 0.92;
        return s;
    }

    public void Save()
    {
        if (Path.Length == 0) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, OverlaySettingsJson.Default.OverlaySettings));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(OverlaySettings))]
internal sealed partial class OverlaySettingsJson : JsonSerializerContext;
