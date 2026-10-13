using System.Diagnostics;
using AionMeter.Core.Atlas;
using AionMeter.Overlay.Shell;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// The atlas painter (one DrawingArea): the map from a mip pyramid into a cached device-pixel "base" surface (re-rendered only when the map,
/// zoom, pan or size changes), then the markers, zones, labels, player and pet card into a frame surface that is blitted to GTK in one paint.
/// GTK4 records a DrawingArea's cairo calls and rasterises them later in the renderer, so timing the draw func alone would not see the map
/// resampling; rasterising here makes the logged frame time the real cost. No allocation per frame once the caches are warm.
/// Frame time measured 2026-10-13 (Release, nested Sway 1.11 headless at output scale 1.5, card 560×540 at 100 %, Verteron, 2734 spots,
/// AIONMETER_UI_DEBUG=1): Follow zoom 0.7–1.3 ms; Fit with pets 6–9 ms (4668 spawn dots); zoomed out (0.245) 7–8 ms with pets, 4 ms without;
/// zoom/pan frames that re-render the base 9–14 ms (base 2.3–3.7 ms). Avalonia measured 1.8–12 ms.
/// </summary>
internal sealed partial class AtlasView
{
    private const uint NoMapBg = 0x0B0F18, SelfRgb = 0xF2C96B, LiveRgb = 0x5EE07A, GoneRgb = 0x8A909C, BossUpRgb = 0xE04A4A;
    private const uint Ink = 0xC0000000, SeededDim = 0x609AA0B0, PetInk = 0xA010141C;
    private const double DimAlpha = 0.45;
    // soft, so the markers never compete with the nodes: red = not owned / level 0, orange = 1, yellow = 2; level 3 draws nothing
    private static readonly uint[] PetLevel = [0x8CE0473F, 0x8CF08A24, 0x8CF2C25A];
    private static readonly string[] Digits = ["0", "1", "2", "3"], DigitsQ = ["0?", "1?", "2?", "3?"];
    private static readonly double[] ZoneDash = [6, 3], NoDash = [];
    private const int FontLabel = 0, FontZone = 1, FontDigit = 2, FontPet = 3, FontLetter = 4, FontPetTitle = 5, FontPetBody = 6;
    private static readonly bool Debug = Environment.GetEnvironmentVariable("AIONMETER_UI_DEBUG") is not null;

    private MapImage? _map;
    private uint _edge = NoMapBg;
    private bool _disposed;
    private Gdk.Surface? _gdkSurface;
    private Cairo.ImageSurface? _frame, _base;
    private Cairo.Context? _fcr, _bcr;
    private int _frameW, _frameH;
    private BaseKey _baseKey;
    private int _frames;
    private int _iconPx;   // a 22 px marker icon in device pixels this frame

    private readonly AtlasTrail _trail = new();
    private IReadOnlyList<BossTimer> _timers = [];
    private readonly LabelSpacing _labels = new();
    private readonly List<(double X, double Y, int Index)> _petMarkers = new();
    private readonly List<(double X, double Y, string Name)> _petLabels = new();
    private readonly List<KindInfo> _kinds = new();
    private readonly Dictionary<string, int> _kindIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(NodeSpot Spot, int Kind)> _marks = new();
    private readonly List<ZoneDraw> _zones = new();
    private readonly Dictionary<(int Kind, int Cx, int Cy), int> _cellIndex = new();
    private readonly List<Cell> _cells = new();
    private readonly Dictionary<string, IconEntry?> _iconByName = new(), _iconByKey = new();
    private readonly Dictionary<string, Gdk.Texture?> _textures = new();
    private readonly Dictionary<(string, int), TextEntry> _text = new();
    private readonly Dictionary<(int, long), string> _strings = new();
    private Pango.FontDescription[]? _fonts;
    private Pango.Layout? _petTitle, _petBody;
    private bool _petCard;
    private double _petX, _petY, _petW, _petH, _petTitleH;

    private sealed class KindInfo(string name, IconEntry? icon)
    {
        public readonly string Name = name;
        public readonly IconEntry? Icon = icon;
        public bool Used;
    }

    private sealed record ZoneDraw(float[] Hull, float CenterX, float CenterY, int Rank, uint Rgb, string Label);

    private struct Cell { public int Kind; public double Px, Py; public int N, Alive; }

    private sealed record TextEntry(Pango.Layout Layout, int W, int H);

    private readonly record struct BaseKey(MapImage? Map, uint Edge, double Scale, double Tx, double Ty, int W, int H, double Ds, object? Pets);

    /// <summary>
    /// A kind's catalogue icon at 48 px as cairo patterns (normal and pre-dimmed to 45 %), its dominant colour, and copies resampled once to the
    /// marker's device size. Measured 2026-10-13 (nested Sway, scale 1.5, Verteron zoomed out without clustering, ~2700 spots): drawing the 48 px
    /// pattern with GOOD per marker cost 87–97 ms a frame (pixman rebuilds the downscale kernel for every paint); the device-size copies are drawn
    /// with FAST (nearest) at ~1:1, snapped to device pixels.
    /// </summary>
    private sealed class IconEntry(Cairo.ImageSurface surface, Cairo.ImageSurface dim, int w, int h, uint colour) : IDisposable
    {
        public readonly Cairo.SurfacePattern Pattern = Pat(surface, Cairo.Filter.Good), Dim = Pat(dim, Cairo.Filter.Good);
        public readonly int W = w, H = h;
        public readonly uint Colour = colour;
        private Cairo.ImageSurface? _sized, _sizedDim;
        private Cairo.SurfacePattern? _sp, _spDim;
        private int _px;
        private static Cairo.SurfacePattern Pat(Cairo.ImageSurface s, Cairo.Filter f) => new(s) { Filter = f };

        /// <summary>The icon resampled to px×px device pixels (normal, dimmed), rebuilt only when the size changes (UI scale, output scale).</summary>
        public (Cairo.SurfacePattern Normal, Cairo.SurfacePattern Dim) At(int px)
        {
            if (px != _px || _sp is null || _spDim is null)
            {
                DropSized();
                _sized = Render(Pattern, px, 1);
                _sizedDim = Render(Pattern, px, DimAlpha);
                _sp = Pat(_sized, Cairo.Filter.Fast);
                _spDim = Pat(_sizedDim, Cairo.Filter.Fast);
                _px = px;
            }
            return (_sp, _spDim);
        }

        private Cairo.ImageSurface Render(Cairo.SurfacePattern src, int px, double alpha)
        {
            var s = new Cairo.ImageSurface(Cairo.Format.Argb32, px, px);
            using (var c = new Cairo.Context(s))
            {
                c.Scale(px / (double)W, px / (double)H);
                c.SetSource(src);
                c.PaintWithAlpha(alpha);
            }
            s.Flush();
            return s;
        }

        private void DropSized()
        {
            _sp?.Dispose(); _spDim?.Dispose(); _sized?.Dispose(); _sizedDim?.Dispose();
            _sp = _spDim = null; _sized = _sizedDim = null;
        }

        public void Dispose() { DropSized(); Pattern.Dispose(); Dim.Dispose(); surface.Dispose(); dim.Dispose(); }
    }

    /// <summary>
    /// The zone map decoded once at native size (4096², the WPF measurement: 3000 upscaled read soft, 2026-10-11) as cairo RGB24 plus halvings down
    /// to ≤ 256 px (+33 % memory), so every zoom samples a level within 2× of the screen with bilinear.
    /// </summary>
    private sealed class MapImage : IDisposable
    {
        private readonly Cairo.ImageSurface[] _levels;
        public readonly Cairo.SurfacePattern[] Patterns;
        public readonly int[] LevelW, LevelH;
        public readonly uint Edge;
        public int Width => LevelW[0];
        public int Height => LevelH[0];
        public int Levels => _levels.Length;

        private MapImage(List<(Cairo.ImageSurface S, int W, int H)> levels, uint edge)
        {
            _levels = new Cairo.ImageSurface[levels.Count];
            Patterns = new Cairo.SurfacePattern[levels.Count];
            LevelW = new int[levels.Count];
            LevelH = new int[levels.Count];
            for (var i = 0; i < levels.Count; i++)
            {
                (_levels[i], LevelW[i], LevelH[i]) = levels[i];
                // Pad: the bilinear edge of the last row/column never fades into the edge colour
                Patterns[i] = new Cairo.SurfacePattern(_levels[i]) { Filter = Cairo.Filter.Bilinear, Extend = Cairo.Extend.Pad };
            }
            Edge = edge;
        }

        /// <summary>Thread pool: JPEG → RGB24 + pyramid; null (logged) when the file cannot be decoded.</summary>
        public static unsafe MapImage? Decode(string path)
        {
            GdkPixbuf.Pixbuf? pb;
            try { pb = GdkPixbuf.Pixbuf.NewFromFile(path); }
            catch (GLib.GException ex) { Log.Error($"Atlas background {path}", ex); return null; }
            if (pb is null) return null;
            var levels = new List<(Cairo.ImageSurface, int, int)>(6);
            uint edge;
            int lw, lh;
            using (pb)
            {
                int w = pb.GetWidth(), h = pb.GetHeight(), stride = pb.GetRowstride(), ch = pb.GetNChannels();
                var src = new ReadOnlySpan<byte>((void*)pb.ReadPixels(), stride * (h - 1) + w * ch);
                edge = AtlasMath.EdgeColour(src, w, h, stride, ch) ?? NoMapBg;
                var s0 = new Cairo.ImageSurface(Cairo.Format.Rgb24, w, h);
                s0.Flush();
                AtlasMath.PackRgb24(src, w, h, stride, ch, s0.GetData(), s0.Stride);
                s0.MarkDirty();
                levels.Add((s0, w, h));
                (lw, lh) = (w, h);
            }
            while (lw > 256 && lh > 256)
            {
                var prev = levels[^1].Item1;
                var next = new Cairo.ImageSurface(Cairo.Format.Rgb24, lw / 2, lh / 2);
                next.Flush();
                AtlasMath.Half(prev.GetData(), lw, lh, prev.Stride, next.GetData(), next.Stride);
                next.MarkDirty();
                lw /= 2; lh /= 2;
                levels.Add((next, lw, lh));
            }
            return new MapImage(levels, edge);
        }

        public void Dispose()
        {
            foreach (var p in Patterns) p.Dispose();
            foreach (var s in _levels) s.Dispose();
        }
    }

    // ------------------------------------------------------------ per refresh (≤ 2 Hz): kinds, icons, zones

    /// <summary>Resolves each spot's kind and icon once per refresh and the toggled kinds' dense areas, so a frame only projects and draws.</summary>
    private void PrepareSpots(MeterEngine e)
    {
        foreach (var k in _kinds) k.Used = false;
        _marks.Clear();
        foreach (var s in _spots)
        {
            var idx = -1;
            if (KindOf(e, s) is { } name)
            {
                if (!_kindIndex.TryGetValue(name, out idx)) { idx = _kinds.Count; _kinds.Add(new KindInfo(name, Icon(name))); _kindIndex[name] = idx; }
                _kinds[idx].Used = true;
            }
            _marks.Add((s, idx));
        }

        // the 5 densest areas of each toggled kind (A2M-3)
        _zones.Clear();
        var mine = new List<NodeSpot>();
        foreach (var key in Shell.Settings.AtlasZoneKinds)
        {
            mine.Clear();
            foreach (var s in _spots)
                if (KindOf(e, s) is { } n && string.Equals(n.Trim(), key, StringComparison.OrdinalIgnoreCase)) mine.Add(s);
            if (mine.Count == 0) continue;
            var rgb = Icon(key)?.Colour ?? SelfRgb;
            var rank = 0;
            foreach (var z in NodeZones.Compute(mine))
            {
                rank++;
                var hull = new float[z.Hull.Count * 2];
                for (var i = 0; i < z.Hull.Count; i++) { hull[2 * i] = z.Hull[i].X; hull[2 * i + 1] = z.Hull[i].Y; }
                _zones.Add(new ZoneDraw(hull, z.CenterX, z.CenterY, rank, rgb, $"#{rank} · {z.Count}"));
            }
        }
    }

    /// <summary>The catalogue icon of a kind (data/atlas/icons/&lt;name&gt;.png, English name lower-cased), cached; null when there is none.</summary>
    private IconEntry? Icon(string name)
    {
        if (_iconByName.TryGetValue(name, out var hit)) return hit;
        var key = AtlasMath.KindKey(name);
        if (!_iconByKey.TryGetValue(key, out hit)) _iconByKey[key] = hit = LoadIcon(key);
        _iconByName[name] = hit;
        return hit;
    }

    private static unsafe IconEntry? LoadIcon(string key)
    {
        var path = Path.Combine(MeterEngine.DataDirectory, "atlas", "icons", key + ".png");
        if (!File.Exists(path)) return null;
        GdkPixbuf.Pixbuf? pb;
        try { pb = GdkPixbuf.Pixbuf.NewFromFile(path); }
        catch (GLib.GException) { return null; }
        if (pb is null) return null;
        var scaled = pb.GetWidth() != 48 ? pb.ScaleSimple(48, Math.Max(1, pb.GetHeight() * 48 / pb.GetWidth()), GdkPixbuf.InterpType.Hyper) : null;
        var src = scaled ?? pb;
        try
        {
            int w = src.GetWidth(), h = src.GetHeight(), stride = src.GetRowstride(), ch = src.GetNChannels();
            var px = new ReadOnlySpan<byte>((void*)src.ReadPixels(), stride * (h - 1) + w * ch);
            var colour = AtlasMath.DominantColour(px, w, h, stride, ch) ?? SelfRgb;
            var normal = new Cairo.ImageSurface(Cairo.Format.Argb32, w, h);
            var dim = new Cairo.ImageSurface(Cairo.Format.Argb32, w, h);
            normal.Flush(); dim.Flush();
            AtlasMath.PackArgb32(px, w, h, stride, ch, normal.GetData(), normal.Stride);
            AtlasMath.PackArgb32(px, w, h, stride, ch, dim.GetData(), dim.Stride, DimAlpha);
            normal.MarkDirty(); dim.MarkDirty();
            return new IconEntry(normal, dim, w, h, colour);
        }
        finally { scaled?.Dispose(); pb.Dispose(); }
    }

    /// <summary>The icon as a GdkTexture for the naming chips.</summary>
    private Gdk.Texture? Texture(string key)
    {
        if (_textures.TryGetValue(key, out var t)) return t;
        var path = Path.Combine(MeterEngine.DataDirectory, "atlas", "icons", AtlasMath.KindKey(key) + ".png");
        try { t = File.Exists(path) ? Gdk.Texture.NewFromFilename(path) : null; }
        catch (GLib.GException) { t = null; }
        return _textures[key] = t;
    }

    /// <summary>The icon in a size×size box at (x, y): pre-dimmed pattern at 45 %, a clipped paint for any other opacity.</summary>
    private static void DrawIcon(Cairo.Context cr, IconEntry icon, double x, double y, double size, double alpha = 1, int px = 0)
    {
        cr.Save();
        cr.Translate(x, y);
        if (px > 0 && (alpha >= 1 || Math.Abs(alpha - DimAlpha) < 1e-6))
        {
            var (normal, dim) = icon.At(px);
            cr.Scale(size / px, size / px);
            cr.SetSource(alpha >= 1 ? normal : dim);
            cr.Rectangle(0, 0, px, px);
            cr.Fill();
            cr.Restore();
            return;
        }
        cr.Scale(size / icon.W, size / icon.H);
        if (alpha >= 1 || Math.Abs(alpha - DimAlpha) < 1e-6)
        {
            cr.SetSource(alpha >= 1 ? icon.Pattern : icon.Dim);
            cr.Rectangle(0, 0, icon.W, icon.H);
            cr.Fill();
        }
        else
        {
            cr.Rectangle(0, 0, icon.W, icon.H);
            cr.Clip();
            cr.SetSource(icon.Pattern);
            cr.PaintWithAlpha(alpha);
        }
        cr.Restore();
    }

    // ------------------------------------------------------------ text

    private Pango.FontDescription[] Fonts => _fonts ??=
    [
        Font(11, Pango.Weight.Normal), Font(12, Pango.Weight.Bold), Font(10, Pango.Weight.Bold), Font(10.5, Pango.Weight.Normal),
        Font(12, Pango.Weight.Normal), Font(13.5, Pango.Weight.Semibold), Font(11.5, Pango.Weight.Normal),
    ];

    private static Pango.FontDescription Font(double px, Pango.Weight weight)
    {
        var d = Pango.FontDescription.FromString("Inter, Noto Sans, DejaVu Sans");
        d.SetAbsoluteSize(px * 1024);   // PANGO_SCALE
        d.SetWeight(weight);
        return d;
    }

    /// <summary>A laid-out text (the widget's Pango context), cached by text and font: a frame re-measures nothing it drew before.</summary>
    private TextEntry TextOf(string s, int font)
    {
        if (_text.TryGetValue((s, font), out var t)) return t;
        if (_text.Count > 3000) ClearText();   // countdown labels change every second
        var layout = _area.CreatePangoLayout(s);
        layout.SetFontDescription(Fonts[font]);
        layout.GetPixelSize(out var w, out var h);
        return _text[(s, font)] = new TextEntry(layout, w, h);
    }

    private void ClearText()
    {
        foreach (var t in _text.Values) t.Layout.Dispose();
        _text.Clear();
    }

    /// <summary>A label string built once per distinct value (kind &lt; 0: generic texts; kind = boss NPC code: its label).</summary>
    private string Str(int kind, long key, Func<long, string> make)
    {
        if (_strings.TryGetValue((kind, key), out var s)) return s;
        return Put(kind, key, make(key));
    }

    private string Put(int kind, long key, string s)
    {
        if (_strings.Count > 4000) _strings.Clear();
        return _strings[(kind, key)] = s;
    }

    private string CountdownText(long atMs, long nowMs)
    {
        var key = AtlasMath.CountdownKey(atMs, nowMs);
        return _strings.TryGetValue((-1, key), out var s) ? s : Put(-1, key, AtlasMath.Countdown(atMs, nowMs));
    }

    private string BossLabel(int code, string name, long atMs, long nowMs)
    {
        var key = AtlasMath.CountdownKey(atMs, nowMs);
        return _strings.TryGetValue((code, key), out var s) ? s : Put(code, key, name + " · " + AtlasMath.Countdown(atMs, nowMs));
    }

    // ------------------------------------------------------------ pet card (drawn on the map: it takes no input)

    private void SetPetCard(string title, string markup, double x, double y)
    {
        const double inner = 340;   // WPF MaxWidth 360 with 10 px padding
        _petTitle ??= _area.CreatePangoLayout("");
        _petBody ??= _area.CreatePangoLayout("");
        _petTitle.SetFontDescription(Fonts[FontPetTitle]);
        _petBody.SetFontDescription(Fonts[FontPetBody]);
        foreach (var l in (Pango.Layout[])[_petTitle, _petBody]) { l.SetWidth((int)(inner * 1024)); l.SetWrap(Pango.WrapMode.WordChar); }
        _petTitle.SetText(title, -1);
        _petBody.SetMarkup(markup, -1);
        _petTitle.GetPixelSize(out var tw, out var th);
        _petBody.GetPixelSize(out var bw, out var bh);
        _petTitleH = th;
        _petW = Math.Min(inner, Math.Max(tw, bw)) + 20;
        _petH = th + 4 + bh + 14;
        var (vw, vh) = Viewport;
        (_petX, _petY) = AtlasMath.PetCardAt(x, y, vw, vh, _petW, _petH);
        _petCard = true;
        _area.QueueDraw();
    }

    private void HidePetCard()
    {
        if (!_petCard) return;
        _petCard = false;
        _area.QueueDraw();
    }

    private void PaintPetCard(Cairo.Context cr)
    {
        if (!_petCard || _petTitle is null || _petBody is null) return;
        Paint.RoundRect(cr, _petX + 0.5, _petY + 0.5, _petW - 1, _petH - 1, 8);
        Paint.Argb(cr, 0xE6121826);
        cr.FillPreserve();
        Paint.Argb(cr, 0x55FFFFFF);
        cr.LineWidth = 1;
        cr.Stroke();
        Paint.Rgb(cr, 0xFFFFFF);
        cr.MoveTo(_petX + 10, _petY + 7);
        PangoCairo.Functions.ShowLayout(cr, _petTitle);
        Paint.Rgb(cr, Palette.TextDim);
        cr.MoveTo(_petX + 10, _petY + 7 + _petTitleH + 4);
        PangoCairo.Functions.ShowLayout(cr, _petBody);
    }

    // ------------------------------------------------------------ frame

    private void InvalidateBase() => _baseKey = default;

    private void Draw(Gtk.DrawingArea area, Cairo.Context cr, int w, int h)
    {
        if (_disposed || w < 1 || h < 1) return;
        var started = Stopwatch.GetTimestamp();
        _gdkSurface ??= area.GetNative()?.GetSurface();
        var ds = _gdkSurface?.GetScale() ?? 1;
        if (ds <= 0) ds = 1;
        int pw = (int)Math.Ceiling(w * ds), ph = (int)Math.Ceiling(h * ds);
        if (_frame is null || pw != _frameW || ph != _frameH) Allocate(pw, ph);
        var key = new BaseKey(_map, _edge, _scale, _tx, _ty, pw, ph, ds, _pets.Active && _cal is not null && Shell.Engine is not null ? _petLayer : null);
        var rebased = key != _baseKey;
        if (rebased) { RenderBase(ds, w / _u, h / _u, key.Pets is not null); _baseKey = key; }
        var baseTicks = Stopwatch.GetTimestamp() - started;

        var f = _fcr!;
        f.Save();
        f.SetSourceSurface(_base!, 0, 0);
        f.Paint();
        f.Scale(ds * _u, ds * _u);
        _iconPx = Math.Max(1, (int)Math.Round(22 * ds * _u));
        PaintOverlays(f, w / _u, h / _u);
        PaintPetCard(f);
        f.Restore();
        _frame!.Flush();

        cr.Save();
        cr.Scale(1 / ds, 1 / ds);
        cr.SetSourceSurface(_frame, 0, 0);
        cr.Paint();
        cr.Restore();

        if (++_frames % 120 == 1 || Debug)
        {
            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            var baseMs = baseTicks * 1000.0 / Stopwatch.Frequency;
            if (Debug) Console.Error.WriteLine($"atlas frame {ms:0.00} ms (base {(rebased ? $"{baseMs:0.00} ms" : "cached")}), scale {_scale:0.000}, {pw}x{ph} px, {_spots.Count} spots, labels {_labels.Count}, pet dots {_petDots.Count}");
            Log.Info($"UI atlas frame: {ms:0.0} ms (base {(rebased ? $"{baseMs:0.0} ms" : "cached")}), scale {_scale:0.000} viewport {w}x{h} at {ds:0.##}, {_spots.Count} spots, {_petLayer?.Count ?? 0} pet templates, labels {_labels.Count}, map {_mapShown}, self {(_pos is { } p ? $"({p.X:0},{p.Y:0}) exact {p.Exact} age {p.AgeS}s heading {p.Heading:0}" : "none")}");
        }
    }

    private void Allocate(int pw, int ph)
    {
        _fcr?.Dispose(); _bcr?.Dispose(); _frame?.Dispose(); _base?.Dispose();
        _frame = new Cairo.ImageSurface(Cairo.Format.Rgb24, pw, ph);
        _base = new Cairo.ImageSurface(Cairo.Format.Rgb24, pw, ph);
        _fcr = new Cairo.Context(_frame);
        _bcr = new Cairo.Context(_base);
        _frameW = pw; _frameH = ph;
        _baseKey = default;
    }

    /// <summary>The edge colour and the map at the pyramid level for this zoom, in device pixels.</summary>
    /// <summary>The edge colour, the map at the pyramid level for this zoom and the pet spawn dots (static until zoom/pan/pets change), in device pixels.</summary>
    private void RenderBase(double ds, double w, double h, bool pets)
    {
        var c = _bcr!;
        c.Save();
        Paint.Rgb(c, _edge);
        c.Paint();
        if (_map is { } m)
        {
            var s = ds * _u * _scale;   // image px → device px
            var lvl = AtlasMath.PyramidLevel(s, m.Levels);
            var ls = s * m.Width / m.LevelW[lvl];
            c.Translate(_tx * ds * _u, _ty * ds * _u);
            c.Scale(ls, ls);
            c.SetSource(m.Patterns[lvl]);
            c.Rectangle(0, 0, m.LevelW[lvl], m.LevelH[lvl]);
            c.Fill();
        }
        c.Restore();
        if (pets) { CollectPets(_f * _scale, w, h); BlitPetDots(c, ds * _u); }
        _base!.Flush();
    }

    private (double X, double Y) Screen(float wx, float wy, double k)
    {
        var (px, py) = _cal!.ToPixel(wx, wy);
        return (px * k + _tx, py * k + _ty);
    }

    private void PaintOverlays(Cairo.Context cr, double w, double h)
    {
        _labels.Clear();
        _petLabels.Clear();
        if (_baseKey.Pets is null) _petMarkers.Clear();
        if (_cal is null || Shell.Engine is not { } e) return;
        var k = _f * _scale;
        var cluster = k < AtlasMath.ClusterBelow;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // seeded nodes (never seen by this meter) dim under everything
        if (!cluster)
            foreach (var (s, kind) in _marks)
            {
                if (!s.Seeded) continue;
                var (x, y) = Screen(s.X, s.Y, k);
                if (!AtlasMath.InView(x, y, w, h)) continue;
                if (kind >= 0 && _kinds[kind].Icon is { } icon) DrawIcon(cr, icon, x - 11, y - 11, 22, DimAlpha, _iconPx);
                else Dot(cr, x, y, 3, SeededDim, DimAlpha);
            }
        if (_baseKey.Pets is not null) PaintPetBadges(cr, k);
        PaintZones(cr, k, w, h);

        // field bosses first: their labels win the spacing
        var bosses = _cal.Bosses;
        for (var bi = 0; bi < bosses.Count; bi++)
        {
            var (code, name, bx, by) = bosses[bi];
            BossTimer? timer = null;
            for (var i = 0; i < _timers.Count; i++) if (_timers[i].NpcCode == code) { timer = _timers[i]; break; }
            var x = bx * k + _tx; var y = by * k + _ty;
            if (!AtlasMath.InView(x, y, w, h)) continue;
            var up = timer?.AliveNow == true;
            var label = up ? name : timer?.NextSpawn is { } next
                ? BossLabel(code, name, next.ToUnixTimeMilliseconds(), now)
                : name;
            var rgb = up ? BossUpRgb : GoneRgb;
            Circle(cr, x, y, 7, rgb);
            // a cross in place of WPF's skull glyph
            Paint.Rgb(cr, 0xFFFFFF);
            cr.LineWidth = 1.4;
            cr.MoveTo(x - 3, y - 3); cr.LineTo(x + 3, y + 3);
            cr.MoveTo(x + 3, y - 3); cr.LineTo(x - 3, y + 3);
            cr.Stroke();
            LabelAt(cr, label, x, y, x + 9, rgb, false, k);
        }

        _cellIndex.Clear();
        _cells.Clear();
        foreach (var (s, kind) in _marks)
        {
            var (x, y) = Screen(s.X, s.Y, k);
            if (cluster && kind >= 0)
            {
                var ck = (kind, AtlasMath.Cell(x), AtlasMath.Cell(y));
                var alive = s.Alive && !s.Seeded ? 1 : 0;
                if (_cellIndex.TryGetValue(ck, out var ci))
                {
                    var c = _cells[ci];
                    c.Px += x; c.Py += y; c.N++; c.Alive += alive;
                    _cells[ci] = c;
                }
                else { _cellIndex[ck] = _cells.Count; _cells.Add(new Cell { Kind = kind, Px = x, Py = y, N = 1, Alive = alive }); }
                continue;
            }
            if (s.Seeded || !AtlasMath.InView(x, y, w, h)) continue;
            var icon = kind >= 0 ? _kinds[kind].Icon : null;
            var units = Math.Clamp(s.UnitsLeft, 0, 3);
            if (s.Alive && s.InView) Mark(cr, x, y, 5, LiveRgb, icon, Digits[units], LiveRgb, false, false, k);
            else if (s.Alive) Mark(cr, x, y, 4, LiveRgb, icon, DigitsQ[units], GoneRgb, false, false, k);
            else Mark(cr, x, y, 4, GoneRgb, icon, s.NextSpawnMs > 0 ? CountdownText(s.NextSpawnMs, now) : "", GoneRgb, true, false, k);
        }
        if (_cells.Count > 0)
        {
            // biggest clusters first: when labels collide, the larger count keeps its label
            _cells.Sort(ByCount);
            foreach (var c in _cells)
            {
                var x = c.Px / c.N; var y = c.Py / c.N;
                if (!AtlasMath.InView(x, y, w, h)) continue;
                var rgb = c.Alive > 0 ? LiveRgb : GoneRgb;
                Mark(cr, x, y, 5, rgb, _kinds[c.Kind].Icon, c.N > 1 ? Str(-2, c.N, n => "×" + n) : "", rgb, c.Alive == 0, true, k);
            }
        }

        PaintPetLabels(cr);
        PaintSelf(cr, k);
    }

    private static readonly Comparison<Cell> ByCount = (a, b) => b.N.CompareTo(a.N);

    // spawn dots as sprites: an antialiased Arc+Fill per dot cost ~17 ms a frame on Verteron zoomed out (measured 2026-10-13, nested Sway at
    // 1.5), and one fill with every dot as a subpath was no faster (cairo tessellates the overlapping circles)
    private Cairo.ImageSurface[]? _dotSprites;
    private int _dotPx;

    private Cairo.ImageSurface[] DotSprites(double dev)
    {
        var px = (int)Math.Ceiling(7 * dev) + 2;
        if (_dotSprites is not null && px == _dotPx) return _dotSprites;
        if (_dotSprites is not null) foreach (var o in _dotSprites) o.Dispose();
        _dotSprites = new Cairo.ImageSurface[PetLevel.Length];
        for (var i = 0; i < PetLevel.Length; i++)
        {
            var sf = new Cairo.ImageSurface(Cairo.Format.Argb32, px, px);
            using (var c = new Cairo.Context(sf))
            {
                c.Arc(px / 2.0, px / 2.0, 3.5 * dev, 0, 2 * Math.PI);
                Paint.Argb(c, PetLevel[i]);
                c.Fill();
            }
            sf.Flush();
            _dotSprites[i] = sf;
        }
        _dotPx = px;
        return _dotSprites;
    }

    private readonly List<(double X, double Y, int Level)> _petDots = new();
    private readonly List<(double X, double Y, int Index, int Level)> _petBadges = new();

    /// <summary>Spawn dots, right-click markers and level badges of the pet layer in view at factor k (screen px).</summary>
    private void CollectPets(double k, double w, double h)
    {
        _petDots.Clear();
        _petBadges.Clear();
        _petMarkers.Clear();
        if (_petLayer is null) return;
        for (var li = 0; li < _petLayer.Count; li++)
        {
            var level = Math.Clamp(_petLayer[li].Level, 0, 2);
            double sx = 0, sy = 0; var n = 0;
            foreach (var (x0, y0) in _petLayer[li].Template.Points)
            {
                var (vx, vy) = Screen(x0, y0, k);
                sx += vx; sy += vy; n++;
                if (!AtlasMath.InView(vx, vy, w, h)) continue;
                _petDots.Add((vx, vy, level));
                _petMarkers.Add((vx, vy, li));
            }
            if (n == 0) continue;
            var cx = sx / n; var cy = sy / n;
            if (AtlasMath.InView(cx, cy, w, h)) { _petBadges.Add((cx, cy, li, level)); _petMarkers.Add((cx, cy, li)); }
        }
    }

    /// <summary>The dots as a pre-drawn sprite per level, blitted in device pixels (<paramref name="dev"/> = device px per screen px).</summary>
    private void BlitPetDots(Cairo.Context c, double dev)
    {
        if (_petDots.Count == 0) return;
        var d = DotSprites(dev);
        foreach (var (x, y, l) in _petDots)
        {
            var px = Math.Round(x * dev - _dotPx / 2.0); var py = Math.Round(y * dev - _dotPx / 2.0);
            c.SetSourceSurface(d[l], px, py);
            c.Rectangle(px, py, _dotPx, _dotPx);
            c.Fill();
        }
    }

    /// <summary>The level badges over everything static; the dots are in the base layer, the hit markers come from the base render.</summary>
    private void PaintPetBadges(Cairo.Context cr, double k)
    {
        if (_petLayer is null) return;
        foreach (var (cx, cy, li, level) in _petBadges)
        {
            if (li >= _petLayer.Count) continue;
            cr.NewPath();
            cr.Arc(cx, cy, 9, 0, 2 * Math.PI);
            Paint.Argb(cr, PetLevel[level]);
            cr.FillPreserve();
            Paint.Argb(cr, PetInk);
            cr.LineWidth = 1;
            cr.Stroke();
            var digit = TextOf(Digits[Math.Clamp(_petLayer[li].Level, 0, 3)], FontDigit);
            Paint.Rgb(cr, 0xFFFFFF);
            cr.MoveTo(cx - digit.W / 2.0, cy - digit.H / 2.0);
            PangoCairo.Functions.ShowLayout(cr, digit.Layout);
            if (k >= AtlasMath.LabelsFrom) _petLabels.Add((cx, cy, _petLayer[li].Pet.Name));
        }
    }

    // pet names go through the label spacing after the bosses and nodes (WPF drew them all: at the Fit zoom of Verteron they overprinted
    // into an unreadable block, render of 2026-10-13); the badge with the level always draws
    private void PaintPetLabels(Cairo.Context cr)
    {
        foreach (var (cx, cy, name) in _petLabels)
        {
            if (!_labels.TryPlace(cx, cy, cx + 11, cy - 8, name.Length * 6 + 6, 15)) continue;
            var t = TextOf(name, FontPet);
            Paint.Argb(cr, PetInk);
            cr.Rectangle(cx + 11, cy - 8, t.W + 6, t.H);
            cr.Fill();
            Paint.Rgb(cr, 0xFFFFFF);
            cr.MoveTo(cx + 14, cy - 8);
            PangoCairo.Functions.ShowLayout(cr, t.Layout);
        }
        _petLabels.Clear();
    }

    /// <summary>The 5 densest areas of each toggled kind as padded hull outlines in the kind's icon colour (A2M-3), #1 solid, the rest dashed.</summary>
    private void PaintZones(Cairo.Context cr, double k, double w, double h)
    {
        foreach (var z in _zones)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            cr.NewPath();
            for (var i = 0; i < z.Hull.Length; i += 2)
            {
                var (x, y) = Screen(z.Hull[i], z.Hull[i + 1], k);
                if (i == 0) cr.MoveTo(x, y); else cr.LineTo(x, y);
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            cr.ClosePath();
            if (maxX < -100 || maxY < -100 || minX > w + 100 || minY > h + 100) { cr.NewPath(); continue; }
            var (cx, cy) = Screen(z.CenterX, z.CenterY, k);
            cr.LineJoin = Cairo.LineJoin.Round;
            // zoomed out, a 150 m area is a few pixels: a readable ring (min 48 px); a dark halo keeps it visible on light and dark terrain
            if (Math.Max(maxX - minX, maxY - minY) < 48)
            {
                cr.NewPath();
                cr.Arc(cx, cy, 27, 0, 2 * Math.PI);
                Paint.Argb(cr, Ink);
                cr.LineWidth = 6;
                cr.Stroke();
                cr.Arc(cx, cy, 24, 0, 2 * Math.PI);
            }
            else
            {
                Paint.Argb(cr, Ink);
                cr.LineWidth = 7;
                cr.StrokePreserve();
            }
            Paint.Rgb(cr, z.Rgb, 0x2A / 255.0);
            cr.FillPreserve();
            Paint.Rgb(cr, z.Rgb);
            cr.LineWidth = 3;
            if (z.Rank != 1) cr.SetDash(ZoneDash, 0);
            cr.Stroke();
            if (z.Rank != 1) cr.SetDash(NoDash, 0);
            var t = TextOf(z.Label, FontZone);
            Paint.Argb(cr, Ink);
            cr.Rectangle(cx - 16, cy - 9, t.W + 8, t.H + 2);
            cr.Fill();
            Paint.Rgb(cr, z.Rgb);
            cr.MoveTo(cx - 12, cy - 8);
            PangoCairo.Functions.ShowLayout(cr, t.Layout);
        }
        cr.LineJoin = Cairo.LineJoin.Miter;
    }

    /// <summary>The player: a gold dot (a fix) or a hollow ring (centroid, "~N"), the heading wedge, the 10 s trail, the age past 10 s.</summary>
    private void PaintSelf(Cairo.Context cr, double k)
    {
        if (SelfPoint is not { } me || _pos is not { } p) return;
        var now = Environment.TickCount64;
        _trail.Update(now, me.X, me.Y);
        cr.LineWidth = 2.5;
        cr.LineCap = Cairo.LineCap.Round;
        for (var i = 1; i < _trail.Count; i++)
        {
            var (ax, ay) = Screen(_trail[i - 1].X, _trail[i - 1].Y, k);
            var (bx, by) = Screen(_trail[i].X, _trail[i].Y, k);
            Paint.Rgb(cr, SelfRgb, _trail.Alpha(i, now));
            cr.MoveTo(ax, ay);
            cr.LineTo(bx, by);
            cr.Stroke();
        }
        cr.LineCap = Cairo.LineCap.Butt;
        var (x, y) = Screen(me.X, me.Y, k);
        if (p.Heading is { } hd)
        {
            // world direction → screen direction through the calibration (the map may be rotated or mirrored against world axes)
            var rad = hd * Math.PI / 180;
            var (hx, hy) = Screen(me.X + (float)(Math.Cos(rad) * 500), me.Y + (float)(Math.Sin(rad) * 500), k);
            if (AtlasMath.Wedge(x, y, hx, hy, out var tip, out var l, out var r))
            {
                cr.NewPath();
                cr.MoveTo(tip.X, tip.Y); cr.LineTo(l.X, l.Y); cr.LineTo(r.X, r.Y); cr.ClosePath();
                Paint.Rgb(cr, SelfRgb);
                cr.FillPreserve();
                Paint.Argb(cr, Ink);
                cr.LineWidth = 1.5;
                cr.Stroke();
            }
        }
        var ageS = p.AgeS < 0 ? -1 : p.AgeS + (now - _posAt) / 1000;
        if (p.Exact)
        {
            Circle(cr, x, y, 8, SelfRgb);
            if (ageS > 10) LabelAt(cr, Str(-3, ageS, a => a + "s"), x, y, x + 10, SelfRgb, true, k);
        }
        else
        {
            cr.NewPath();
            cr.Arc(x, y, 9, 0, 2 * Math.PI);
            Paint.Rgb(cr, SelfRgb);
            cr.LineWidth = 2.5;
            cr.Stroke();
            LabelAt(cr, Str(-4, p.Samples, n => "~" + n), x, y, x + 12, SelfRgb, true, k);
        }
    }

    /// <summary>A filled circle with the 1.5 px ink outline.</summary>
    private static void Circle(Cairo.Context cr, double x, double y, double r, uint rgb)
    {
        cr.NewPath();
        cr.Arc(x, y, r, 0, 2 * Math.PI);
        Paint.Rgb(cr, rgb);
        cr.FillPreserve();
        Paint.Argb(cr, Ink);
        cr.LineWidth = 1.5;
        cr.Stroke();
    }

    private static void Dot(Cairo.Context cr, double x, double y, double r, uint argb, double alpha)
    {
        cr.NewPath();
        cr.Arc(x, y, r, 0, 2 * Math.PI);
        cr.SetSourceRgba(((argb >> 16) & 0xFF) / 255.0, ((argb >> 8) & 0xFF) / 255.0, (argb & 0xFF) / 255.0, (argb >> 24) / 255.0 * alpha);
        cr.Fill();
    }

    /// <summary>A node: its icon (when known) or a dot, the label beside it when it has room (A2M-8 spacing).</summary>
    private void Mark(Cairo.Context cr, double x, double y, double r, uint fill, IconEntry? icon, string label, uint textRgb, bool dim, bool always, double k)
    {
        if (icon is null) Circle(cr, x, y, r, fill);
        else DrawIcon(cr, icon, x - 11, y - 11, 22, dim ? DimAlpha : 1, _iconPx);
        if (label.Length > 0) LabelAt(cr, label, x, y, x + (icon is null ? r + 2 : 12), textRgb, always, k);
    }

    // label box estimated, not measured (Inter 11 px averages ~6.5 px a character plus 3+3 padding): nothing is laid out for a refused label.
    // always = shown at any zoom (cluster counts, the player); spacing still applies.
    private void LabelAt(Cairo.Context cr, string label, double x, double y, double left, uint rgb, bool always, double k)
    {
        if (!always && k < AtlasMath.LabelsFrom) return;
        if (!_labels.TryPlace(x, y, left, y - 7, label.Length * 6.5 + 6, 15)) return;
        var t = TextOf(label, FontLabel);
        Paint.Argb(cr, Ink);
        cr.Rectangle(left, y - 7, t.W + 6, 15);
        cr.Fill();
        Paint.Rgb(cr, rgb);
        cr.MoveTo(left + 3, y - 7 + (15 - t.H) / 2.0);
        PangoCairo.Functions.ShowLayout(cr, t.Layout);
    }

    private void DisposeResources()
    {
        _disposed = true;
        _map?.Dispose(); _map = null;
        _fcr?.Dispose(); _bcr?.Dispose(); _frame?.Dispose(); _base?.Dispose();
        _fcr = _bcr = null; _frame = _base = null;
        foreach (var i in _iconByKey.Values) i?.Dispose();
        _iconByKey.Clear(); _iconByName.Clear(); _kinds.Clear(); _kindIndex.Clear();
        ClearText();
        _petTitle?.Dispose(); _petBody?.Dispose();
        if (_dotSprites is not null) foreach (var o in _dotSprites) o.Dispose();
        _dotSprites = null;
    }
}
