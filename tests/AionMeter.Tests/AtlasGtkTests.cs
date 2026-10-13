using AionMeter.Core.Atlas;
using AionMeter.Overlay.Shell;
using AionMeter.Overlay.Views;

namespace AionMeter.Tests;

/// <summary>A2M-50: the GTK atlas tab's maths and pixel work (AtlasMath, AtlasTrail, AtlasCatalog), no display.</summary>
public class AtlasGtkTests
{
    private static MapCalibration Calibration(string json)
    {
        var dir = Directory.CreateTempSubdirectory("atlas-gtk-");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "77"));
        File.WriteAllText(Path.Combine(dir.FullName, "77", "calibration.json"), json);
        return MapCalibration.Load(dir.FullName, 77)!;
    }

    // world → px: x' = 0.01·x + 2000, y' = -0.01·y + 2000 (north up: the shipped maps flip y), 4000² calibration
    private static readonly string CalJson = """
        { "name": "Testland", "background_size": [4000, 4000], "world_to_px": { "ax": [0.01, 0, 2000], "ay": [0, -0.01, 2000] },
          "bosses": [ { "code": 2100001, "name": "Boss", "pixel": [100, 200] } ] }
        """;

    [Fact]
    public void ZoomAt_KeepsThePointUnderThePointerAndClamps()
    {
        var (s, tx, ty) = AtlasMath.ZoomAt(1, 10, 20, 200, 100, AtlasMath.WheelStep);
        Assert.Equal(1.15, s, 9);
        // the image point under (200,100) before = after
        Assert.Equal((200 - 10) / 1.0, (200 - tx) / s, 9);
        Assert.Equal((100 - 20) / 1.0, (100 - ty) / s, 9);
        Assert.Equal(AtlasMath.MaxScale, AtlasMath.ZoomAt(11.9, 0, 0, 0, 0, 2).Scale);
        Assert.Equal(AtlasMath.MinScale, AtlasMath.ZoomAt(0.09, 0, 0, 0, 0, 0.5).Scale);
        // at the limit a zoom changes nothing, not even the pan
        var still = AtlasMath.ZoomAt(AtlasMath.MaxScale, 5, 6, 300, 300, 1.15);
        Assert.Equal((AtlasMath.MaxScale, 5.0, 6.0), still);
    }

    [Fact]
    public void ZoomTo_SliderZoomsAroundTheCentre()
    {
        var (s, tx, ty) = AtlasMath.ZoomTo(0.5, -100, -50, 400, 300, Math.Log(1));
        Assert.Equal(1, s, 9);
        // the image point at the centre (200,150) stays there
        Assert.Equal((200 - -100) / 0.5, (200 - tx) / s, 9);
        Assert.Equal((150 - -50) / 0.5, (150 - ty) / s, 9);
        Assert.Equal(AtlasMath.MaxScale, AtlasMath.ZoomTo(1, 0, 0, 10, 10, 50).Scale);
    }

    [Fact]
    public void Fit_CentresTheWholeImage()
    {
        var (s, tx, ty) = AtlasMath.Fit(4096, 4096, 512, 384);
        Assert.Equal(384.0 / 4096, s, 9);
        Assert.Equal((512 - 4096 * s) / 2, tx, 9);
        Assert.Equal(0, ty, 9);
        // no image yet: 1000², a tiny view counts as 100²
        Assert.Equal(0.1, AtlasMath.Fit(0, 0, 10, 10).Scale, 9);
        Assert.Equal(AtlasMath.MinScale, AtlasMath.Fit(100_000, 100_000, 100, 100).Scale);
    }

    [Fact]
    public void Centre_PutsThePixelInTheMiddle()
    {
        var (tx, ty) = AtlasMath.Centre(1000, 500, 2.5, 516, 380);
        Assert.Equal(516 / 2.0, 1000 * 2.5 + tx, 9);
        Assert.Equal(380 / 2.0, 500 * 2.5 + ty, 9);
    }

    [Fact]
    public void ScreenToWorld_InvertsTheProjection()
    {
        var cal = Calibration(CalJson);
        double k = 0.75, tx = -40, ty = 13;
        foreach (var (wx, wy) in new (float, float)[] { (0, 0), (-36729, -34461), (12345, 67890) })
        {
            var (px, py) = cal.ToPixel(wx, wy);
            var (bx, by) = AtlasMath.ScreenToWorld(cal, px * k + tx, py * k + ty, tx, ty, k);
            Assert.Equal(wx, bx, 0.5);
            Assert.Equal(wy, by, 0.5);
        }
        // 14 px at k = 0.75 → 18.67 calibration px → 1867 cm at 0.01 px/cm
        Assert.Equal(1866.7f, AtlasMath.PickCm(cal, k), 0.1);
    }

    [Fact]
    public void Nearest_PicksTheClosestMarkerInsideTheRadius()
    {
        var markers = new List<(double X, double Y, int Index)> { (10, 10, 0), (20, 10, 1), (100, 100, 2) };
        Assert.Equal(1, AtlasMath.Nearest(markers, 18, 11));
        Assert.Equal(0, AtlasMath.Nearest(markers, 12, 12));
        Assert.Equal(-1, AtlasMath.Nearest(markers, 60, 60));
        Assert.Equal(-1, AtlasMath.Nearest(markers, 100 + 14, 100));   // exactly the radius is outside
        Assert.Equal(2, AtlasMath.Nearest(markers, 100 + 13.9, 100));
        Assert.Equal(-1, AtlasMath.Nearest([], 0, 0));
    }

    [Fact]
    public void InView_HasAMarkerMargin()
    {
        Assert.True(AtlasMath.InView(-29, -29, 100, 100));
        Assert.False(AtlasMath.InView(-30, 50, 100, 100));
        Assert.True(AtlasMath.InView(129, 129, 100, 100));
        Assert.False(AtlasMath.InView(50, 130, 100, 100));
    }

    [Fact]
    public void Countdown_TextAndItsCacheKeyChangeTogether()
    {
        const long now = 1_000_000_000;
        Assert.Equal("soon", AtlasMath.Countdown(now, now));
        Assert.Equal("soon", AtlasMath.Countdown(now - 5, now));
        Assert.Equal("0:59", AtlasMath.Countdown(now + 59_999, now));
        Assert.Equal("1:00", AtlasMath.Countdown(now + 60_000, now));
        Assert.Equal("59:59", AtlasMath.Countdown(now + 3_599_999, now));
        Assert.Equal("1h00", AtlasMath.Countdown(now + 3_600_000, now));
        Assert.Equal("2h05", AtlasMath.Countdown(now + (2 * 3600 + 5 * 60 + 30) * 1000L, now));
        // the key decides when a cached text is reused: same key ⇒ same text, for every ms over two minutes
        var seen = new Dictionary<long, string>();
        for (long ms = -2000; ms < 125_000; ms += 7)
        {
            var key = AtlasMath.CountdownKey(now + ms, now);
            var text = AtlasMath.Countdown(now + ms, now);
            if (seen.TryGetValue(key, out var prev)) Assert.Equal(prev, text);
            else seen[key] = text;
        }
    }

    [Fact]
    public void PyramidLevel_StaysWithinTwiceTheScreen()
    {
        Assert.Equal(0, AtlasMath.PyramidLevel(15, 5));     // magnified (Follow zoom): full resolution
        Assert.Equal(0, AtlasMath.PyramidLevel(1, 5));
        Assert.Equal(0, AtlasMath.PyramidLevel(0.6, 5));
        Assert.Equal(1, AtlasMath.PyramidLevel(0.5, 5));
        Assert.Equal(1, AtlasMath.PyramidLevel(0.26, 5));
        Assert.Equal(2, AtlasMath.PyramidLevel(0.14, 5));   // Fit of a 4096² map in ~560 device px: the 1024² level
        Assert.Equal(4, AtlasMath.PyramidLevel(0.001, 5));  // capped at the smallest level
        Assert.Equal(0, AtlasMath.PyramidLevel(0.1, 1));
        for (var s = 0.01; s < 2; s += 0.013)
        {
            var lvl = AtlasMath.PyramidLevel(s, 8);
            var magnification = s * (1 << lvl);
            if (s < 1) Assert.InRange(magnification, 0.5 - 1e-9, 1 + 1e-9);
        }
    }

    [Fact]
    public void Wedge_PointsAlongTheHeading()
    {
        Assert.True(AtlasMath.Wedge(100, 100, 200, 100, out var tip, out var l, out var r));
        Assert.Equal((119.0, 100.0), tip);
        Assert.Equal((109.0, 106.0), l);
        Assert.Equal((109.0, 94.0), r);
        Assert.True(AtlasMath.Wedge(0, 0, 0, -3, out tip, out _, out _));
        Assert.Equal(0, tip.X, 9);
        Assert.Equal(-19, tip.Y, 9);
        Assert.False(AtlasMath.Wedge(5, 5, 5, 5, out _, out _, out _));
    }

    [Fact]
    public void PetCard_StaysInsideTheView()
    {
        Assert.Equal((114.0, 90.0), AtlasMath.PetCardAt(100, 100, 516, 380, 360, 120));
        Assert.Equal((516.0 - 360, 380.0 - 120), AtlasMath.PetCardAt(500, 370, 516, 380, 360, 120));
        Assert.Equal((4.0, 4.0), AtlasMath.PetCardAt(-50, 5, 516, 380, 360, 120));
    }

    [Fact]
    public void ToggleKind_KeepsAtMostTwo()
    {
        var on = new List<string>();
        AtlasMath.ToggleKind(on, "aria");
        AtlasMath.ToggleKind(on, "ruby");
        AtlasMath.ToggleKind(on, "odyle");
        Assert.Equal(["ruby", "odyle"], on);
        AtlasMath.ToggleKind(on, "ruby");
        Assert.Equal(["odyle"], on);
        Assert.Equal("blue crystal", AtlasMath.KindKey("  Blue Crystal "));
        Assert.Equal(-1, AtlasMath.Cell(-0.1));
        Assert.Equal(1, AtlasMath.Cell(28));
    }

    [Fact]
    public void Trail_KeepsTenSecondsOfMovement()
    {
        var t = new AtlasTrail();
        t.Update(0, 0, 0);
        t.Update(100, 30, 30);         // under 50 cm: no point
        Assert.Equal(1, t.Count);
        t.Update(200, 60, 0);
        t.Update(5000, 60, 100);
        Assert.Equal(3, t.Count);
        Assert.Equal(0.75, t.Alpha(2, 5000), 9);
        Assert.Equal(0.1, t.Alpha(0, 10_000), 9);
        t.Update(10_150, 60, 100);     // standing still: the first point (0 ms) is gone, the 200 ms one is 9.95 s old
        Assert.Equal(2, t.Count);
        Assert.Equal(200, t[0].Ms);
        t.Update(30_000, 60, 100);
        Assert.Equal(0, t.Count);
        t.Update(30_001, 60, 100);
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void DominantColour_WeighsSaturatedOpaquePixels()
    {
        // 2×2 RGBA: two opaque saturated reds, one opaque black outline, one transparent green
        byte[] px = [255, 0, 0, 255, 255, 0, 0, 255, 0, 0, 0, 255, 0, 255, 0, 0];
        var c = AtlasMath.DominantColour(px, 2, 2, 8, 4)!.Value;
        var r = (c >> 16) & 0xFF; var g = (c >> 8) & 0xFF; var b = c & 0xFF;
        Assert.True(r > 200, $"{c:X6}");
        Assert.True(g < 80 && g == b, $"{c:X6}");   // lifted 25 % towards white, never green
        Assert.Null(AtlasMath.DominantColour(new byte[16], 2, 2, 8, 4));   // all transparent
        // RGB without alpha: a flat grey stays that grey, lifted
        byte[] grey = [100, 100, 100, 100, 100, 100];
        Assert.Equal(0x8A8A8Au, AtlasMath.DominantColour(grey, 2, 1, 6, 3));
    }

    [Fact]
    public void EdgeColour_AveragesTheBorderPatches()
    {
        const int w = 100, h = 80, stride = w * 3 + 4;   // padded rows like a pixbuf
        var px = new byte[stride * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = y * stride + x * 3;
                var centre = x >= 32 && x < 68 && y >= 32 && y < 48;   // nothing the patches read
                (px[o], px[o + 1], px[o + 2]) = centre ? ((byte)0, (byte)0, (byte)0) : ((byte)0xDA, (byte)0xD1, (byte)0xBC);
            }
        Assert.Equal(0xDAD1BCu, AtlasMath.EdgeColour(px, w, h, stride, 3));
        Assert.Null(AtlasMath.EdgeColour(px, 60, 60, stride, 3));
    }

    [Fact]
    public void PackAndHalf_ProduceCairoPixels()
    {
        // RGB pixbuf → xRGB (B,G,R,255)
        byte[] rgb = [10, 20, 30, 40, 50, 60, 0, 0];   // one row of 2 pixels, stride 8
        var dst = new byte[8];
        AtlasMath.PackRgb24(rgb, 2, 1, 8, 3, dst, 8);
        Assert.Equal(new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, dst);

        // straight RGBA → premultiplied BGRA, and the 45 % dim copy
        byte[] rgba = [200, 100, 50, 128];
        var p = new byte[4];
        AtlasMath.PackArgb32(rgba, 1, 1, 4, 4, p, 4);
        Assert.Equal(new byte[] { 25, 50, 100, 128 }, p);
        AtlasMath.PackArgb32(rgba, 1, 1, 4, 4, p, 4, 0.45);
        Assert.Equal(new byte[] { 11, 23, 45, 58 }, p);

        // 2×2 box average, odd last column dropped
        var src = new byte[3 * 4 * 2];
        byte[][] pix = [[0, 0, 0, 255], [4, 8, 12, 255], [9, 9, 9, 9], [8, 4, 0, 255], [0, 0, 0, 255], [9, 9, 9, 9]];
        for (var i = 0; i < 6; i++) Array.Copy(pix[i], 0, src, i * 4, 4);
        var half = new byte[4];
        AtlasMath.Half(src, 3, 2, 12, half, 4);
        Assert.Equal(new byte[] { 3, 3, 3, 255 }, half);
    }

    [Fact]
    public void Catalog_ReadsKindsInFileOrderAndMarksTheCurrentOne()
    {
        var dir = Directory.CreateTempSubdirectory("atlas-cat-");
        var path = Path.Combine(dir.FullName, "gather-catalog.json");
        File.WriteAllText(path, """
            { "categories": [
                { "key": "mineral", "en": "Ore", "nodes": [ { "key": "orichalcum", "en": "Orichalcum" }, { "key": "ruby" } ] },
                { "key": "plant", "nodes": [ { "key": "aria", "en": "Aria" } ] } ] }
            """);
        var list = AtlasCatalog.Load(path);
        Assert.Equal(3, list.Count);
        Assert.Equal(new CatalogNode("mineral", "Ore", "orichalcum", "Orichalcum"), list[0]);
        Assert.Equal("ruby", list[1].Name);                 // no English name: the key
        Assert.Equal("plant", list[2].CategoryName);        // no English category: the key
        Assert.True(AtlasCatalog.IsCurrent(" orichalcum ", "Orichalcum"));
        Assert.False(AtlasCatalog.IsCurrent(null, "Orichalcum"));
        Assert.False(AtlasCatalog.IsCurrent("Ruby", "Orichalcum"));
        File.WriteAllText(path, "{ \"nope\": 1 }");
        Assert.Empty(AtlasCatalog.Load(path));
        Assert.Empty(AtlasCatalog.Load(Path.Combine(dir.FullName, "missing.json")));
    }

    [Fact]
    public void ShippedCatalog_EveryKindHasAnIcon()
    {
        // data/atlas is copied into upstream/ by build.sh (private assets, gitignored): skip quietly where it is absent
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "atlas", "gather-catalog.json"))) dir = dir.Parent;
        if (dir is null) return;
        var atlas = Path.Combine(dir.FullName, "data", "atlas");
        var list = AtlasCatalog.Load(Path.Combine(atlas, "gather-catalog.json"));
        Assert.True(list.Count >= 17, list.Count.ToString());   // 17 Essence Extraction kinds on 2026-10-08, 19 entries by 2026-10-13
        foreach (var n in list.Where(n => !File.Exists(Path.Combine(atlas, "icons", AtlasMath.KindKey(n.Key) + ".png"))))
            Assert.Fail("no icon for " + n.Key);
    }

    [Fact]
    public void Registry_HasTheAtlas()
    {
        Assert.True(ViewRegistry.Has("atlas"));
    }
}
