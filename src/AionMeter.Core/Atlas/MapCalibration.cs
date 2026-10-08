using System.Text.Json;

namespace AionMeter.Core.Atlas;

/// <summary>A zone map image and its world → pixel affine (data/atlas/&lt;mapId&gt;/calibration.json, built by the atlas project).</summary>
public sealed class MapCalibration
{
    public int MapId { get; private init; }
    public string Name { get; private init; } = "";
    public string BackgroundPath { get; private init; } = "";
    public int Width { get; private init; }
    public int Height { get; private init; }
    private double _ax0, _ax1, _ax2, _ay0, _ay1, _ay2;
    public IReadOnlyList<(int Code, string Name, double Px, double Py)> Bosses { get; private init; } = [];

    public (double X, double Y) ToPixel(float x, float y) => (_ax0 * x + _ax1 * y + _ax2, _ay0 * x + _ay1 * y + _ay2);

    /// <summary>Pixels per world unit (cm), x axis.</summary>
    public double Scale => Math.Sqrt(_ax0 * _ax0 + _ay0 * _ay0);

    public static MapCalibration? Load(string atlasDirectory, int mapId)
    {
        try
        {
            var dir = Path.Combine(atlasDirectory, mapId.ToString());
            var path = Path.Combine(dir, "calibration.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var w2p = r.GetProperty("world_to_px");
            var ax = w2p.GetProperty("ax").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var ay = w2p.GetProperty("ay").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var size = r.TryGetProperty("background_size", out var bs) ? bs.EnumerateArray().Select(e => e.GetInt32()).ToArray() : [0, 0];
            var bosses = new List<(int, string, double, double)>();
            if (r.TryGetProperty("bosses", out var list))
                foreach (var b in list.EnumerateArray())
                {
                    var px = b.GetProperty("pixel").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    bosses.Add((b.GetProperty("code").GetInt32(), b.GetProperty("name").GetString() ?? "", px[0], px[1]));
                }
            return new MapCalibration
            {
                MapId = mapId,
                Name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                BackgroundPath = Path.Combine(dir, r.TryGetProperty("background", out var bg) ? bg.GetString() ?? "background.jpg" : "background.jpg"),
                Width = size[0], Height = size[1],
                _ax0 = ax[0], _ax1 = ax[1], _ax2 = ax[2], _ay0 = ay[0], _ay1 = ay[1], _ay2 = ay[2],
                Bosses = bosses,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
