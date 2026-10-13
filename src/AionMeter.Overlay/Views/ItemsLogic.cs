using AionMeter.Core.Inventory;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>
/// The display-free part of the GTK Items tab (A2M-50): the Show filter, the purse in a card head, "Also" / location strings, the order of
/// the location pills, the warehouse state chip, the column and flow layout maths the view applies to its widgets (GTK has no star columns
/// nor a wrap panel), initials of icon-less tiles. GtkItemsTests drives it without libgtk.
/// </summary>
internal static class ItemsLogic
{
    /// <summary>Tile and gap of the WPF ItemTile grid (unscaled px).</summary>
    public const int Tile = 48, TileGap = 6;

    /// <summary>Show pills: "all", "eq", "mat", "use".</summary>
    public static bool InGroup(int id, string group) => group switch
    {
        "eq" => ItemKinds.Group(id) == ItemGroup.Equipment,
        "mat" => ItemKinds.Group(id) == ItemGroup.Materials,
        "use" => ItemKinds.Group(id) == ItemGroup.Consumables,
        _ => true,
    };

    /// <summary>The stacks a card shows (no currencies: the footer sums them; Show filter applied) into <paramref name="into"/>; returns the
    /// Kina held in <paramref name="stacks"/> (named in the card head, Diego 2026-10-13).</summary>
    public static long Visible(List<ItemStack> stacks, string group, List<ItemStack> into)
    {
        into.Clear();
        long kina = 0;
        for (var i = 0; i < stacks.Count; i++)
        {
            var s = stacks[i];
            if (s.Item == InventoryStore.Kinah) kina += s.Count;
            if (!InventoryStore.IsCurrency(s.Item) && InGroup(s.Item, group)) into.Add(s);
        }
        return kina;
    }

    /// <summary>"28 stacks" + " · 2,213,167 Kina" when the card holds Kina (<paramref name="kinaFormat"/> = UiText.ItemsKinaHere).</summary>
    public static string HeadRight(string stacks, long kina, string kinaFormat) =>
        kina > 0 ? stacks + " · " + string.Format(kinaFormat, ItemFormat.N(kina)) : stacks;

    public enum WhState : byte { Never, Full, Partial }

    /// <summary>The character warehouse chip: container times exist since A2M-32; older files: 10+ stacks can only come from the window's full list.</summary>
    public static WhState Warehouse(int stacks, long seenMs, long fullMs) =>
        stacks == 0 && seenMs == 0 ? WhState.Never : fullMs > 0 || (seenMs == 0 && stacks >= 10) ? WhState.Full : WhState.Partial;

    /// <summary>Where else the item is ("Irin bag 4 · acct 191"), equipped pieces and the row's own place left out (mock "Also").</summary>
    public static string Elsewhere(IReadOnlyList<Holding> holds, string who, Place place, Func<Holding, string> label)
    {
        string? one = null;
        System.Text.StringBuilder? sb = null;
        for (var i = 0; i < holds.Count; i++)
        {
            var h = holds[i];
            if (h.Place == Place.Equipped || (h.Place == place && h.Who == who)) continue;
            var part = label(h) + " " + ItemFormat.N(h.Count);
            if (one is null) { one = part; continue; }
            sb ??= new System.Text.StringBuilder(one, 64);
            sb.Append(" · ").Append(part);
        }
        return sb?.ToString() ?? one ?? "";
    }

    /// <summary>Location pills of an "Across characters" card: the viewed character first, the account warehouse last, then by count.</summary>
    public static void SortHoldings(List<Holding> holds, string current) => holds.Sort((a, b) =>
    {
        var c = (b.Who == current).CompareTo(a.Who == current);
        if (c != 0) return c;
        c = (a.Place == Place.AccountWarehouse).CompareTo(b.Place == Place.AccountWarehouse);
        return c != 0 ? c : b.Count.CompareTo(a.Count);
    });

    /// <summary>"Ranger lv45  ·  combat power …" (profile.txt, first line) → "Ranger 45".</summary>
    public static string ClassOf(string firstLine)
    {
        var dot = firstLine.IndexOf("  ·  ", StringComparison.Ordinal);
        return (dot > 0 ? firstLine[..dot] : firstLine).Replace(" lv", " ", StringComparison.Ordinal).Trim();
    }

    /// <summary>"3 h 05 min" / "42 min".</summary>
    public static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(0, t.Minutes)} min";

    /// <summary>First letters of the first two words (letters only), as the mock does for tiles without an icon.</summary>
    public static string InitialsOf(string name)
    {
        Span<char> buf = stackalloc char[2];
        var n = 0;
        var start = true;
        foreach (var ch in name)
        {
            if (char.IsLetter(ch)) { if (start && n < 2) buf[n++] = char.ToUpperInvariant(ch); start = false; }
            else if (ch == ' ') start = true;
        }
        return n == 0 ? "?" : new string(buf[..n]);
    }

    /// <summary>Tiles per row in <paramref name="width"/> px for cells of <paramref name="cell"/> px spaced by <paramref name="gap"/>.</summary>
    public static int TileColumns(int width, int cell, int gap) => Math.Max(1, (width + gap) / Math.Max(1, cell + gap));

    /// <summary>
    /// Avalonia column definitions in pixels: <paramref name="spec"/> &gt; 0 = fixed unscaled px (× <paramref name="scale"/>), &lt; 0 = star
    /// weight (-1.4 = "1.4*"), 0 = hidden. Stars share what the fixed columns leave of <paramref name="width"/>; edges are rounded so the
    /// widths add up to the total exactly.
    /// </summary>
    public static void ColumnWidths(ReadOnlySpan<double> spec, int width, double scale, Span<int> into)
    {
        double fixedSum = 0, stars = 0;
        for (var i = 0; i < spec.Length; i++)
        {
            if (spec[i] > 0) fixedSum += spec[i] * scale;
            else if (spec[i] < 0) stars -= spec[i];
        }
        var rest = Math.Max(0, width - fixedSum);
        double x = 0;
        var prev = 0;
        for (var i = 0; i < spec.Length; i++)
        {
            x += spec[i] > 0 ? spec[i] * scale : spec[i] < 0 && stars > 0 ? rest * -spec[i] / stars : 0;
            var edge = (int)Math.Round(x);
            into[i] = Math.Max(0, edge - prev);
            prev = edge;
        }
    }

    /// <summary>
    /// Wrap panel: children of natural widths <paramref name="widths"/> in lines no wider than <paramref name="width"/>, <paramref name="gap"/>
    /// between neighbours; writes each child's line index (a child wider than a line gets a line of its own). Returns the line count.
    /// </summary>
    public static int Flow(ReadOnlySpan<int> widths, int width, int gap, Span<int> line)
    {
        int x = 0, ln = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            var w = widths[i];
            if (i > 0 && x > 0 && x + gap + w > width) { ln++; x = 0; }
            x += (x > 0 ? gap : 0) + w;
            line[i] = ln;
        }
        return widths.Length == 0 ? 0 : ln + 1;
    }

    /// <summary>Footer columns: Character · Kina · Kina (Bound) · AP · Enhance · Odyle · Shugo · Pass · Event · Seal · Value · Seen (WPF widths).</summary>
    public static readonly double[] FootWeights = [-1.5, -1.25, -1.15, -1, -0.9, -0.95, -0.65, -0.6, -0.6, -0.8, -0.9, -0.8];
    public const int FootCols = 12, ColBound = 2, ColEvent = 8;

    /// <summary>Below 820 px Kina (Bound) and Event pass drop out of the footer (WPF RenderFoot).</summary>
    public static void FootSpec(bool narrow, Span<double> into)
    {
        for (var i = 0; i < FootCols; i++) into[i] = narrow && i is ColBound or ColEvent ? 0 : FootWeights[i];
    }

    /// <summary>List mode columns: tile · name · count · where · also · unit · value (WPF ListGrid "44,1.4*,70,*,1.2*,56,64").</summary>
    public static readonly double[] ListSpec = [44, -1.4, 70, -1, -1.2, 56, 64];
    /// <summary>Today rows "*,150,130" and Achievement rows "1.2*,150,1.4*".</summary>
    public static readonly double[] TicketSpec = [-1, 150, 130], AchSpec = [-1.2, 150, -1.4];
}
