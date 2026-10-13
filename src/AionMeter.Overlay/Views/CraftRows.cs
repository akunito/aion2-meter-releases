using System.Globalization;
using System.Text;
using AionMeter.Core.GameDb;
using AionMeter.Service;

namespace AionMeter.Overlay.Views;

/// <summary>Colour of a row's right-hand figure: craftable / almost (Avalonia Ok, Near) and a losing recipe in profit mode (Bad).</summary>
internal enum CraftTone : byte { Ok, Near, Bad }

/// <summary>One line of the Craft list. <see cref="OkFrame"/> = the green hairline (craftable now; in profit mode also profitable).</summary>
internal sealed record CraftRow(int Output, string Name, Grade Grade, string Sub, string Times, CraftTone Tone, bool OkFrame, bool Fav);

/// <summary>The engine lookups the rows need (the view passes MeterEngine's, tests pass dictionaries).</summary>
internal readonly record struct CraftNames(Func<int, string> Name, Func<int, string> Label, Func<int, (string? En, string? Es)> Both, Func<int, string?> Source, Func<int, Grade> Grade);

/// <summary>
/// A2M-50: the Craft tab's list logic without widgets (the Avalonia CraftView's Reload / ReloadProfit / PickTwins, same rules), so the GTK
/// view only binds rows and the rules are testable without a display.
/// </summary>
internal static class CraftRows
{
    public static readonly string[] ScopeKeys = ["now", "near", "all"];
    public static readonly string[] ProfKeys = ["", "blacksmithing", "tailoring", "jewelcrafting", "alchemy", "cooking"];
    public const int ProfitMax = 150;

    public static int ScopeIndex(string? key)
    {
        var i = Array.IndexOf(ScopeKeys, key);
        return i < 0 ? 0 : i;
    }

    public static string ScopeKey(int index) => index is > 0 and < 3 ? ScopeKeys[index] : "now";

    /// <summary>Typing searches the whole book (Diego 2026-10-11): the scope only applies while the box is empty.</summary>
    public static string EffectiveScope(ItemQuery q, string scope) => q.IsEmpty ? scope : "all";

    // aion2hub lists 41 recipe pairs with two output ids for one name and identical materials (faction/version twins, e.g. 510716007/510716019):
    // one row per name + materials + profession + level, so the list never shows the same craft twice (Diego, 2026-10-11)
    // 602 of the 1,684 hub recipes share their name with another one: the Elyos/Asmodian item pair (same materials, or the base item
    // of the other faction) and the two routes of the top gear (level 90 from the previous tier vs level 0 from a token). One row per
    // name and profession (Diego 2026-10-11, "están duplicadas otra vez"): the variant the character can craft, else the one missing least.
    // grade in the key: same-named outputs of different grades ("Artisan's Refining Stone" rare/legendary in EN) are different recipes, not variants
    private static (string, string, int) TwinKey(Recipe r, string name, Func<int, Grade> grade) => (name, r.Profession, (int)grade(r.Output));

    /// <summary>The kept output per twin group and its group size; <paramref name="names"/> receives the display name of every option (index = opts index).</summary>
    public static (HashSet<int> Keep, Dictionary<int, int> Variants) PickTwins(List<CraftOption> opts, Func<int, string> nm, Func<int, Grade> grade, string[]? names = null, List<int>? favs = null)
    {
        var best = new Dictionary<(string, string, int), CraftOption>(opts.Count);
        var count = new Dictionary<(string, string, int), int>(opts.Count);
        for (var i = 0; i < opts.Count; i++)
        {
            var o = opts[i];
            var name = nm(o.Recipe.Output);
            if (names is not null) names[i] = name;
            var key = TwinKey(o.Recipe, name, grade);
            count[key] = count.TryGetValue(key, out var c) ? c + 1 : 1;
            if (!best.TryGetValue(key, out var b) || Better(o, b, favs)) best[key] = o;
        }
        var keep = new HashSet<int>(best.Count);
        var variants = new Dictionary<int, int>(best.Count);
        foreach (var (key, o) in best) { keep.Add(o.Recipe.Output); variants[o.Recipe.Output] = count[key]; }
        return (keep, variants);
    }

    // A2M-50: identical twins (Truth Orb 110660023/110660024) tie on all three, and RecipeBook.Evaluate's List.Sort is unstable, so the
    // first one met changed with the profession filter: a starred twin vanished under "All" (measured in the nested sway). On a tie the
    // favourite wins, then the lower id (Avalonia still keeps whichever comes first).
    private static bool Better(CraftOption a, CraftOption b, List<int>? favs)
    {
        if (a.Times != b.Times) return a.Times > b.Times;
        if (a.Missing.Length != b.Missing.Length) return a.Missing.Length < b.Missing.Length;
        if (a.Recipe.Level != b.Recipe.Level) return a.Recipe.Level < b.Recipe.Level;
        if (favs is { Count: > 0 })
        {
            bool fa = favs.Contains(a.Recipe.Output), fb = favs.Contains(b.Recipe.Output);
            if (fa != fb) return fa;
        }
        return a.Recipe.Output < b.Recipe.Output;
    }

    private static bool Match(ItemQuery q, Grade grade, string name, string en, string es, string recipe) =>
        q.Matches(grade, ItemNames.Normalize(name), ItemNames.Normalize(en), ItemNames.Normalize(es), ItemNames.Normalize(recipe));

    /// <summary>
    /// Book mode: <paramref name="opts"/> = RecipeBook.Evaluate (book order). Favourites first, then the rest, each in book order (WPF
    /// OrderByDescending is stable); the scope filters, favourites bypass it; a query searches every scope.
    /// </summary>
    public static List<CraftRow> Book(List<CraftOption> opts, CraftNames n, ItemQuery q, string scope, List<int> favs, UiText t, out int craftable)
    {
        var names = new string[opts.Count];
        var (keep, variants) = PickTwins(opts, n.Name, n.Grade, names, favs);
        scope = EffectiveScope(q, scope);
        var rows = new List<CraftRow>(keep.Count);
        var sb = new StringBuilder(256);
        craftable = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < opts.Count; i++)
            {
                var o = opts[i];
                var r = o.Recipe;
                var fav = favs.Contains(r.Output);
                if (fav != (pass == 0) || !keep.Contains(r.Output)) continue;
                if (!fav && scope == "now" && o.Times == 0) continue;
                if (!fav && scope == "near" && o.Times == 0 && o.Missing.Length > 2) continue;
                var name = names[i];
                var grade = n.Grade(r.Output);
                if (!q.IsEmpty)
                {
                    var (en, es) = n.Both(r.Output);
                    if (!Match(q, grade, name, en ?? "", es ?? "", r.Name)) continue;
                }
                sb.Clear();
                sb.Append(r.Profession).Append(' ').Append(r.Level).Append(" · ").Append(r.Grade);
                var v = variants[r.Output];
                if (v > 1) sb.Append(" · +").Append(v - 1).Append(' ').Append(t.CraftVariant);
                sb.Append(" · ");
                for (var m = 0; m < r.Materials.Length; m++)
                {
                    if (m > 0) sb.Append(", ");
                    sb.Append(n.Label(r.Materials[m].Item)).Append(" ×").Append(r.Materials[m].Qty);
                }
                if (o.Times > 0)
                {
                    craftable++;
                    rows.Add(new CraftRow(r.Output, name, grade, sb.ToString(), "×" + o.Times.ToString(CultureInfo.InvariantCulture), CraftTone.Ok, true, fav));
                    continue;
                }
                sb.Append("  —  ").Append(t.CraftMissing).Append(": ");
                for (var m = 0; m < o.Missing.Length; m++)
                {
                    var x = o.Missing[m];
                    if (m > 0) sb.Append(", ");
                    sb.Append(n.Label(x.Item)).Append(' ').Append(x.Have).Append('/').Append(x.Need);
                    if (n.Source(x.Item) is { } src) sb.Append(" (").Append(src).Append(')');
                }
                rows.Add(new CraftRow(r.Output, name, grade, sb.ToString(), "", CraftTone.Near, false, fav));
            }
        return rows;
    }

    /// <summary>Profit mode: <paramref name="all"/> = RecipeBook.Profit (most profitable first); favourites first, one row per twin group, at most <see cref="ProfitMax"/>.</summary>
    public static List<CraftRow> Profit(List<CraftProfit> all, CraftNames n, Func<int, long?> price, ItemQuery q, List<int> favs, UiText t)
    {
        var rows = new List<CraftRow>(120);
        var shown = 0;
        var seen = new HashSet<(string, string, int)>();
        var sb = new StringBuilder(256);
        for (var pass = 0; pass < 2 && shown <= ProfitMax; pass++)
            for (var i = 0; i < all.Count; i++)
            {
                var p = all[i];
                var r = p.Recipe;
                var fav = favs.Contains(r.Output);
                if (fav != (pass == 0)) continue;
                var name = n.Name(r.Output);
                if (!seen.Add(TwinKey(r, name, n.Grade))) continue;
                var grade = n.Grade(r.Output);
                if (!q.IsEmpty)
                {
                    var (en, es) = n.Both(r.Output);
                    if (!Match(q, grade, name, en ?? "", es ?? "", r.Name)) continue;
                }
                if (++shown > ProfitMax) break;
                sb.Clear();
                sb.Append(r.Profession).Append(' ').Append(r.Level).Append(" · ").Append(r.Grade).Append(" · ").Append(t.CraftSells).Append(' ').Append(ItemFormat.Kinah(p.OutUnit));
                if (r.Yield > 1) sb.Append(" ×").Append(r.Yield);
                sb.Append(" → ").Append(ItemFormat.Kinah(p.Value)).Append(' ').Append(t.CraftAfterFee).Append(" · ").Append(t.CraftCost).Append(' ').Append(ItemFormat.Kinah(p.Cost));
                if (p.UnpricedMaterials > 0) sb.Append(" (").Append(p.UnpricedMaterials).Append(' ').Append(t.CraftUnpriced).Append(')');
                if (p.Times > 0) sb.Append(" · ×").Append(p.Times).Append(' ').Append(t.CraftNow);
                sb.Append('\n');
                for (var m = 0; m < r.Materials.Length; m++)
                {
                    var (item, qty) = r.Materials[m];
                    if (m > 0) sb.Append(", ");
                    sb.Append(n.Label(item)).Append(" ×").Append(qty);
                    if (price(item) is { } mp) sb.Append(" @").Append(ItemFormat.Kinah(mp));
                    else sb.Append(" @?");
                }
                var good = p.Profit > 0;
                var times = (good ? "+" : "") + ItemFormat.Kinah(p.Profit) +
                            (p.Cost > 0 ? " (" + ((double)p.Profit / p.Cost * 100).ToString("+0;-0", CultureInfo.InvariantCulture) + "%)" : "");
                rows.Add(new CraftRow(r.Output, name, grade, sb.ToString(), times, good ? CraftTone.Ok : CraftTone.Bad, good && p.Times > 0, fav));
            }
        return rows;
    }

    /// <summary>The newest aion2exchange-*.json in <paramref name="dir"/> (the browser script's export in Downloads); null when none.</summary>
    public static string? NewestExport(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        string? path = null;
        var newest = DateTime.MinValue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "aion2exchange-*.json"))
            {
                var w = File.GetLastWriteTimeUtc(f);
                if (w > newest) { newest = w; path = f; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>
    /// A GtkFileDialog error that means "the user closed it" (GTK_DIALOG_ERROR_DISMISSED / CANCELLED; the portal's cancel maps to DISMISSED):
    /// GirCore's GException carries no code, only the message, which GTK translates — <paramref name="translated"/> are the gtk40 catalogue's.
    /// Any other error = no usable picker, the caller falls back to the newest export in Downloads.
    /// </summary>
    public static bool IsDismissal(string message, params string[] translated)
    {
        if (message.Contains("Dismissed", StringComparison.Ordinal) || message.Contains("Cancelled", StringComparison.Ordinal)) return true;
        for (var i = 0; i < translated.Length; i++) if (translated[i].Length > 0 && message.Contains(translated[i], StringComparison.Ordinal)) return true;
        return false;
    }
}
