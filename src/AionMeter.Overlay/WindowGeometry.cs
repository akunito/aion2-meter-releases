using System.Text.Json;

namespace AionMeter.Overlay;

/// <summary>The card's own container in Sway's tree: con id, output, rect in layout coordinates, state.</summary>
public readonly record struct SwayNode(long Id, string Output, int X, int Y, int W, int H, bool Floating, bool Sticky, bool Focused);

/// <summary>An active output in layout coordinates (get_outputs).</summary>
public readonly record struct SwayOutput(string Name, int X, int Y, int W, int H);

/// <summary>The window mode's saved geometry (overlay.json Win*), layout px; W = 0: nothing saved yet.</summary>
public readonly record struct WinGeom(string Output, int X, int Y, int W, int H)
{
    public bool Saved => W > 0 && H > 0;
}

/// <summary>
/// A2M-50 window mode: the card is a plain xdg toplevel (app_id aionmeter) and Sway places it. A Wayland client cannot set its own position,
/// so the card finds its container in get_tree, saves the rect into overlay.json, and restores it with commands after every map. Pure, tested
/// without a display or a compositor (WindowModeTests).
/// </summary>
public static class WindowGeometry
{
    /// <summary>The view of process <paramref name="pid"/> titled <paramref name="title"/> (the card; its dialogs share the pid and app_id, not
    /// the title), or null. Views in the scratchpad (output "__i3") are not placed anywhere: null too.</summary>
    public static SwayNode? FindOwn(ReadOnlyMemory<byte> tree, int pid, string title)
    {
        try
        {
            using var doc = JsonDocument.Parse(tree);
            return Find(doc.RootElement, pid, title, "", false);
        }
        catch (JsonException) { return null; }
    }

    private static SwayNode? Find(JsonElement n, int pid, string title, string output, bool floating)
    {
        if (n.TryGetProperty("type", out var t) && t.ValueEquals("output") && n.TryGetProperty("name", out var on)) output = on.GetString() ?? "";
        if (n.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() == pid
            && n.TryGetProperty("name", out var name) && name.ValueEquals(title))
        {
            if (output == "__i3") return null;
            var r = n.GetProperty("rect");
            var fl = floating || (n.TryGetProperty("type", out var ty) && ty.ValueEquals("floating_con"));
            return new SwayNode(n.GetProperty("id").GetInt64(), output, r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
                r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), fl,
                n.TryGetProperty("sticky", out var st) && st.ValueKind == JsonValueKind.True,
                n.TryGetProperty("focused", out var fo) && fo.ValueKind == JsonValueKind.True);
        }
        foreach (var (key, fl) in (ReadOnlySpan<(string, bool)>)[("nodes", floating), ("floating_nodes", true)])
        {
            if (!n.TryGetProperty(key, out var kids) || kids.ValueKind != JsonValueKind.Array) continue;
            foreach (var k in kids.EnumerateArray())
                if (Find(k, pid, title, output, fl) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>The active outputs of a get_outputs reply.</summary>
    public static SwayOutput[] Outputs(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var list = new List<SwayOutput>();
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False) continue;
                if (!o.TryGetProperty("rect", out var r)) continue;
                list.Add(new SwayOutput(o.GetProperty("name").GetString() ?? "", r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
                    r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32()));
            }
            return list.ToArray();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    /// <summary>Layout coordinates: `move position` without `absolute` is relative to the workspace (sway(5)).</summary>
    public static string Move(long id, int x, int y) => $"[con_id={id}] {Pos(x, y)}";

    private static string Pos(int x, int y) => $"move absolute position {x} px {y} px";

    public static string Resize(long id, int w, int h) => $"[con_id={id}] resize set width {w} px height {h} px";

    /// <summary>A resize that keeps the top-left corner where it is: `resize set` on a floating view keeps its centre (measured: a tab's
    /// 479 → 560 growth moved the card 40 px left), the layer card grew from its corner.</summary>
    public static string ResizeAt(long id, int x, int y, int w, int h) => $"{Resize(id, w, h)}, {Pos(x, y)}";

    /// <summary>
    /// The commands that put a freshly mapped card back where it was saved: size always; the position when the saved rect's centre is on an
    /// active output, else clamped onto the saved output when it is connected (layout changed), else none (that output is gone: Sway's own
    /// placement keeps the card visible). Null when nothing is saved.
    /// </summary>
    public static string? Restore(long id, WinGeom g, ReadOnlySpan<SwayOutput> outputs)
    {
        if (!g.Saved) return null;
        var resize = Resize(id, g.W, g.H);
        double cx = g.X + g.W / 2.0, cy = g.Y + g.H / 2.0;
        foreach (var o in outputs)
            if (cx >= o.X && cx < o.X + o.W && cy >= o.Y && cy < o.Y + o.H) return resize + ", " + Pos(g.X, g.Y);
        foreach (var o in outputs)
        {
            if (o.Name != g.Output) continue;
            var x = Math.Clamp(g.X, o.X, Math.Max(o.X, o.X + o.W - g.W));
            var y = Math.Clamp(g.Y, o.Y, Math.Max(o.Y, o.Y + o.H - g.H));
            return resize + ", " + Pos(x, y);
        }
        return resize;
    }

    /// <summary>
    /// What to save of a rect Sway reports. On the DPS rows: all of it. On a tab the window may have been grown to the tab minimum
    /// (<paramref name="minW"/>×<paramref name="minH"/> px) from the saved DPS size by the size request, not by the user: that axis keeps the
    /// saved DPS size (the card returns to it on DPS, as the layer card does); any other size there is the user's and is saved.
    /// </summary>
    public static (int W, int H) SizeToSave(bool meter, int savedW, int savedH, int w, int h, int minW, int minH)
    {
        if (meter || savedW <= 0 || savedH <= 0) return (w, h);
        return (w == Math.Max(savedW, minW) ? savedW : w, h == Math.Max(savedH, minH) ? savedH : h);
    }

    /// <summary>GdkSurfaceEdge for the edges <see cref="Placement.EdgeAt"/> picked (-1 left/top, 1 right/bottom): NORTH_WEST 0 … SOUTH_EAST 7.</summary>
    public static int SurfaceEdge(int ex, int ey) => (ex, ey) switch
    {
        (-1, -1) => 0, (0, -1) => 1, (1, -1) => 2,
        (-1, 0) => 3, (1, 0) => 4,
        (-1, 1) => 5, (0, 1) => 6,
        _ => 7,
    };
}
