using System.Text.Json;
using System.Text.Json.Serialization;

namespace AionMeter.Linux;

/// <summary>~/.local/share/AionMeter/ui.json: the Linux overlay's own place and switches (settings.json stays the daemon's).</summary>
public sealed class UiSettings
{
    /// <summary>A2M-48 UX: the output (RandR / Sway connector, "DP-2") the card was on; <see cref="Left"/>/<see cref="Top"/> are relative to its
    /// top-left. Empty = an old absolute place: ignored (xwayland_zero_scaling multiplied the X11 space by 1.5 and put DP-2's old place on DP-1).</summary>
    public string Output { get; set; } = "";
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 560;   // WPF: 470 by default, but the tab strip pushes the title into an ellipsis below ~560
    public double Height { get; set; } = 540;
    public bool ClickThrough { get; set; }
    public double Opacity { get; set; } = 0.92;
    /// <summary>Stand-alone windows (pop-outs "popout-&lt;tab&gt;", "help"): [left, top, width, height], left/top relative to <see cref="WindowOutputs"/>.</summary>
    public Dictionary<string, double[]> Windows { get; set; } = new();
    public Dictionary<string, string> WindowOutputs { get; set; } = new();
    /// <summary>The tools window opens on this tab (the DPS card is the layer-shell overlay's job): UiTabs.StartTab.</summary>
    public string LastTab { get; set; } = "";
    /// <summary>Zoom of the Avalonia windows and the layer-shell card (80–200 %, UiScale.Clamp); the card reads it from this file.</summary>
    public double UiScale { get; set; } = 1;

    /// <summary>Raised on the caller's thread when <see cref="Save"/> or <see cref="ReloadScaleIfChanged"/> found a UiScale written by the card.</summary>
    public event Action? ScaleChangedOnDisk;
    private DateTime _stamp;

    [JsonIgnore] public string Path { get; private set; } = "";

    public static UiSettings Load(string path)
    {
        UiSettings? s = null;
        try { if (File.Exists(path)) s = JsonSerializer.Deserialize(File.ReadAllText(path), UiJson.Default.UiSettings); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        s ??= new UiSettings();
        s.Path = path;
        s.UiScale = AionMeter.Service.Overlay.UiScale.Clamp(s.UiScale);
        // pre-2026-10-13 places were absolute X11 coordinates at Avalonia scale 1: dropped once (see Output)
        if (s.Output.Length == 0 && !double.IsNaN(s.Left)) { s.Left = s.Top = double.NaN; s.Width = 560; s.Height = 540; }
        foreach (var key in s.Windows.Keys.ToArray())
            if (!s.WindowOutputs.ContainsKey(key)) s.Windows.Remove(key);
        s._stamp = Stamp(path);
        return s;
    }

    private static DateTime Stamp(string path) { try { return File.GetLastWriteTimeUtc(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; } }

    /// <summary>The layer-shell card patches "UiScale" (wheel on its corner): take it before overwriting the file, and on the 2 s poll.</summary>
    public bool ReloadScaleIfChanged()
    {
        var st = Stamp(Path);
        if (st == _stamp || Path.Length == 0) return false;
        _stamp = st;
        var disk = AionMeter.Service.Overlay.UiScale.Read(Path);
        if (Math.Abs(disk - UiScale) < 0.001) return false;
        UiScale = disk;
        ScaleChangedOnDisk?.Invoke();
        return true;
    }

    public void Save()
    {
        ReloadScaleIfChanged();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, UiJson.Default.UiSettings));
            File.Move(tmp, Path, overwrite: true);
            _stamp = Stamp(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(UiSettings))]
internal sealed partial class UiJson : JsonSerializerContext;
