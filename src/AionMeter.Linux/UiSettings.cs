using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionMeter.Linux;

/// <summary>~/.local/share/AionMeter/ui.json: the Linux overlay's own place and switches (settings.json stays the daemon's).</summary>
public sealed class UiSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 560;   // WPF: 470 by default, but the tab strip pushes the title into an ellipsis below ~560
    public double Height { get; set; } = 540;
    public bool ClickThrough { get; set; }
    public double Opacity { get; set; } = 0.92;

    [JsonIgnore] public string Path { get; private set; } = "";

    public static UiSettings Load(string path)
    {
        UiSettings? s = null;
        try { if (File.Exists(path)) s = JsonSerializer.Deserialize(File.ReadAllText(path), UiJson.Default.UiSettings); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        s ??= new UiSettings();
        s.Path = path;
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, UiJson.Default.UiSettings));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class UiJson : JsonSerializerContext;
