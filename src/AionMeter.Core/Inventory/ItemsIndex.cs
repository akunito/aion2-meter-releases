using AionMeter.Core.GameDb;

namespace AionMeter.Core.Inventory;

/// <summary>Items tab filter (A2M-29): by id range, as the approved mock grouped them.</summary>
public enum ItemGroup : byte { Equipment, Materials, Consumables }

/// <summary>Where a stack lives, as the Items tab names it: the equipped set includes the cosmetic container 4.</summary>
public enum Place : byte { Bag, CharacterWarehouse, AccountWarehouse, Equipped }

public enum ItemSortMode : byte { Game, Grade, Name, Value }

/// <summary>One item in one place: <paramref name="Who"/> = character key ("" for the shared account warehouse).</summary>
public readonly record struct Holding(string Who, Place Place, long Count, int Stacks);

public static class ItemKinds
{
    /// <summary>1xx–3xx million = gear (weapons 11/15, armour 21/25, accessories 31); 60x–66x million = crafting, gathering and conversion
    /// materials (aion2hub categories CraftResource / GatherResource / ConversionResource live on 61, 63, 65, 66); the rest = consumables & misc.</summary>
    public static ItemGroup Group(int item) => item < 400_000_000 ? ItemGroup.Equipment : item is >= 600_000_000 and < 670_000_000 ? ItemGroup.Materials : ItemGroup.Consumables;

    public static Place? PlaceOf(byte container) => container switch
    {
        Containers.Bag => Place.Bag,
        Containers.CharacterWarehouse => Place.CharacterWarehouse,
        Containers.AccountWarehouse => Place.AccountWarehouse,
        Containers.Equipped or Containers.Cosmetic => Place.Equipped,
        _ => null,
    };

    /// <summary>
    /// For files written before A2M-32 (no container times): a bag with many holes below its last used slot and without the progression
    /// currencies did not come from a login list (Akunita 2026-10-11: 8 stacks in slots 1, 9–15, no Abyss Points or Enhance Stone, after the
    /// old ≥ 8-stack replace rule). Holes alone are normal: a deposit leaves them (Arielka after the warehouse at 13:36: 22 stacks in 0–45,
    /// Enhance Stone still there). A bag whose login list was seen (ContainerSeen.FullMs) is complete by construction.
    /// </summary>
    public static bool BagLooksIncomplete(IReadOnlyList<ItemStack> bag)
    {
        if (bag.Count == 0) return false;
        var max = 0;
        foreach (var s in bag)
        {
            if (s.Item is InventoryStore.AbyssPoints or InventoryStore.EnhanceStone) return false;
            if (s.Slot > max) max = s.Slot;
        }
        var holes = max + 1 - bag.Count;
        return holes >= 5 && holes * 4 > max + 1;
    }

    public static void Sort(List<ItemStack> rows, ItemSortMode mode, Func<int, string> name, Func<int, Grade> grade, Func<int, long?> price)
    {
        switch (mode)
        {
            case ItemSortMode.Grade:
                rows.Sort((a, b) => { var c = ItemGrades.Rank(grade(a.Item)).CompareTo(ItemGrades.Rank(grade(b.Item))); return c != 0 ? c : string.Compare(name(a.Item), name(b.Item), StringComparison.CurrentCultureIgnoreCase); });
                break;
            case ItemSortMode.Name:
                rows.Sort((a, b) => { var c = string.Compare(name(a.Item), name(b.Item), StringComparison.CurrentCultureIgnoreCase); return c != 0 ? c : a.Slot.CompareTo(b.Slot); });
                break;
            case ItemSortMode.Value:
                rows.Sort((a, b) => { var c = ((price(b.Item) ?? 0) * b.Count).CompareTo((price(a.Item) ?? 0) * a.Count); return c != 0 ? c : a.Slot.CompareTo(b.Slot); });
                break;
            default:
                rows.Sort(static (a, b) => a.Container != b.Container ? a.Container.CompareTo(b.Container) : a.Slot.CompareTo(b.Slot));
                break;
        }
    }
}

/// <summary>Every place each item is held, over the characters of a server and their shared warehouse ("Across characters").
/// Equipped pieces are listed but never counted in totals.</summary>
public sealed class ItemsIndex
{
    private readonly Dictionary<int, List<Holding>> _by = new(512);
    private readonly Dictionary<int, long> _total = new(512);

    public void Add(string who, Place place, IReadOnlyList<ItemStack> stacks)
    {
        foreach (var s in stacks)
        {
            if (s.Count <= 0) continue;
            if (!_by.TryGetValue(s.Item, out var l)) _by[s.Item] = l = new List<Holding>(3);
            var i = l.FindIndex(h => h.Who == who && h.Place == place);
            if (i >= 0) l[i] = l[i] with { Count = l[i].Count + s.Count, Stacks = l[i].Stacks + 1 };
            else l.Add(new Holding(who, place, s.Count, 1));
            if (place != Place.Equipped) _total[s.Item] = _total.GetValueOrDefault(s.Item) + s.Count;
        }
    }

    public IReadOnlyList<Holding> Of(int item) => _by.TryGetValue(item, out var l) ? l : [];

    public long Total(int item) => _total.GetValueOrDefault(item);

    public IEnumerable<int> Items => _by.Keys;

    public int Places(int item)
    {
        if (!_by.TryGetValue(item, out var l)) return 0;
        var n = 0;
        foreach (var h in l) if (h.Place != Place.Equipped) n++;
        return n;
    }

    /// <summary>Items spread over at least <paramref name="minPlaces"/> storage places (worth consolidating), most places first, then largest total. Currencies excluded.</summary>
    public List<int> Split(int minPlaces = 3)
    {
        var l = new List<int>();
        foreach (var id in _by.Keys) if (!InventoryStore.IsCurrency(id) && Places(id) >= minPlaces) l.Add(id);
        l.Sort((a, b) => { var c = Places(b).CompareTo(Places(a)); return c != 0 ? c : Total(b).CompareTo(Total(a)); });
        return l;
    }

    /// <summary>Held items the predicate accepts, largest total first. Currencies excluded.</summary>
    public List<int> Matching(Func<int, bool> match)
    {
        var l = new List<int>();
        foreach (var id in _by.Keys) if (!InventoryStore.IsCurrency(id) && match(id)) l.Add(id);
        l.Sort((a, b) => { var c = Total(b).CompareTo(Total(a)); return c != 0 ? c : a.CompareTo(b); });
        return l;
    }
}

/// <summary>"Used in X · needs N · you have M · missing …" for one material against what the whole account holds.</summary>
public sealed record CraftHint(Recipe Recipe, int Need, long Have, (int Item, long Have, int Need)[] Missing);

public static class CraftHints
{
    /// <summary>The <paramref name="max"/> recipes using <paramref name="item"/> that the account is closest to completing (share of every
    /// material covered), one per recipe name and profession (faction twins share both).</summary>
    public static List<CraftHint> For(int item, RecipeBook book, Func<int, long> total, int max = 2)
    {
        var used = book.UsedIn(item);
        var scored = new List<(double Score, Recipe R)>(used.Length);
        var seen = new HashSet<string>(used.Length);
        foreach (var r in used)
        {
            if (!seen.Add(r.Name + "|" + r.Profession)) continue;
            double sum = 0;
            foreach (var (m, q) in r.Materials) sum += q <= 0 ? 1 : Math.Min(1.0, total(m) / (double)q);
            scored.Add((r.Materials.Length == 0 ? 0 : sum / r.Materials.Length, r));
        }
        scored.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : b.R.Level.CompareTo(a.R.Level));
        var result = new List<CraftHint>(Math.Min(max, scored.Count));
        var missing = new List<(int, long, int)>(4);
        for (var i = 0; i < scored.Count && result.Count < max; i++)
        {
            var r = scored[i].R; var need = 0; missing.Clear();
            foreach (var (m, q) in r.Materials)
            {
                if (m == item) need += q;
                var have = total(m);
                if (have < q) missing.Add((m, have, q));
            }
            result.Add(new CraftHint(r, need, total(item), missing.ToArray()));
        }
        return result;
    }
}
