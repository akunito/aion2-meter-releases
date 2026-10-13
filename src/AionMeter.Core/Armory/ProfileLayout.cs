using System.Globalization;
using System.Text;
using AionMeter.Core.GameDb;

namespace AionMeter.Core.Armory;

public enum GearZone : byte { Armour, Accessory, Weapon, Arcana, Other }

/// <summary>Equipment arranged like the in-game character screen (questlog's armory): armour left, accessories right in pairs, weapons in the centre.</summary>
public sealed record GearLayout(EquipItem?[] Armour, EquipItem?[] Accessories, EquipItem?[] Weapons, List<EquipItem> Arcana, List<EquipItem> Other)
{
    public bool HasGear
    {
        get
        {
            foreach (var x in Armour) if (x is not null) return true;
            foreach (var x in Accessories) if (x is not null) return true;
            foreach (var x in Weapons) if (x is not null) return true;
            return Other.Count > 0;
        }
    }
}

public sealed record SkillGroup(string Key, List<SkillLine> Skills);

public sealed record BoardBar(string Name, int Open, int Total)
{
    public double Fraction => Total > 0 ? (double)Open / Total : 0;
}

/// <summary>A2M-44: Core side of the card's Profile / Skills / Daevanion tabs — slot mapping, badge texts, groupings, Daevanion sums.</summary>
public static class ProfileLayout
{
    // slotPosName values of the official equipment list (2026-10-13, Teinmeir and Akunita)
    public static readonly string[] ArmourSlots = ["Helmet", "Shoulder", "Torso", "Pants", "Gloves", "Boots", "Cape"];
    /// <summary>Laid out two per row: necklace + amulet, earrings, rings, bracelets, runes, belt.</summary>
    public static readonly string[] AccessorySlots = ["Necklace", "Amulet", "Earring1", "Earring2", "Ring1", "Ring2", "Bracelet1", "Bracelet2", "Rune1", "Rune2", "Belt"];
    public static readonly string[] WeaponSlots = ["MainHand", "SubHand"];
    public static readonly string[] PrimaryStats = ["STR", "DEX", "INT", "CON", "AGI", "WIS"];

    public static (GearZone Zone, int Index) Place(string slot)
    {
        var i = Array.IndexOf(ArmourSlots, slot);
        if (i >= 0) return (GearZone.Armour, i);
        i = Array.IndexOf(AccessorySlots, slot);
        if (i >= 0) return (GearZone.Accessory, i);
        i = Array.IndexOf(WeaponSlots, slot);
        if (i >= 0) return (GearZone.Weapon, i);
        if (slot.StartsWith("Arcana", StringComparison.Ordinal)) return (GearZone.Arcana, SlotNumber(slot));
        return (GearZone.Other, 0);
    }

    public static GearLayout Arrange(IReadOnlyList<EquipItem> items)
    {
        var layout = new GearLayout(new EquipItem?[ArmourSlots.Length], new EquipItem?[AccessorySlots.Length], new EquipItem?[WeaponSlots.Length], new(), new());
        foreach (var it in items)
        {
            var (zone, ix) = Place(it.Slot);
            switch (zone)
            {
                case GearZone.Armour: layout.Armour[ix] = it; break;
                case GearZone.Accessory: layout.Accessories[ix] = it; break;
                case GearZone.Weapon: layout.Weapons[ix] = it; break;
                case GearZone.Arcana: layout.Arcana.Add(it); break;
                default: layout.Other.Add(it); break;
            }
        }
        layout.Arcana.Sort(static (a, b) => SlotNumber(a.Slot).CompareTo(SlotNumber(b.Slot)));
        return layout;
    }

    private static int SlotNumber(string slot)
    {
        var n = 0;
        foreach (var c in slot) if (char.IsAsciiDigit(c)) n = n * 10 + (c - '0');
        return n;
    }

    public static string? EnchantText(int enchant) => enchant > 0 ? "+" + enchant.ToString(CultureInfo.InvariantCulture) : null;
    public static string? ExceedText(int exceed) => exceed > 0 ? "x" + exceed.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>The API's grade words ("Legend", "Unique", "Special"…) as the meter's grades.</summary>
    public static Grade GradeOf(string? apiGrade) => apiGrade switch
    {
        "Common" => Grade.Common,
        "Rare" => Grade.Rare,
        "Legend" or "Legendary" => Grade.Legendary,
        "Unique" => Grade.Unique,
        "Epic" => Grade.Epic,
        "Special" => Grade.Special,
        _ => Grade.Unknown,
    };

    /// <summary>"Earring1" → "Earring 1", "MainHand" → "Main Hand".</summary>
    public static string SlotLabel(string slot)
    {
        var sb = new StringBuilder(slot.Length + 3);
        for (var i = 0; i < slot.Length; i++)
        {
            var c = slot[i];
            if (i > 0 && (char.IsUpper(c) || (char.IsAsciiDigit(c) && !char.IsAsciiDigit(slot[i - 1])))) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"Silver Blade Rotan lv2" (the parser's form) → ("Silver Blade Rotan", 2); level 0 when the text has none.</summary>
    public static (string Name, int Level) SplitPet(string pet)
    {
        var i = pet.LastIndexOf(" lv", StringComparison.Ordinal);
        return i > 0 && int.TryParse(pet.AsSpan(i + 3), NumberStyles.None, CultureInfo.InvariantCulture, out var lv) ? (pet[..i], lv) : (pet, 0);
    }

    /// <summary>"Dark Veil Wings +3" → ("Dark Veil Wings", 3).</summary>
    public static (string Name, int Enchant) SplitWing(string wing)
    {
        var i = wing.LastIndexOf(" +", StringComparison.Ordinal);
        return i > 0 && int.TryParse(wing.AsSpan(i + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? (wing[..i], e) : (wing, 0);
    }

    /// <summary>The six primary attributes in the game's order (those present).</summary>
    public static List<StatLine> Primary(CharacterProfile p)
    {
        var list = new List<StatLine>(6);
        foreach (var t in PrimaryStats)
            foreach (var s in p.Stats)
                if (s.Type == t) { list.Add(s); break; }
        return list;
    }

    /// <summary>The divine (deity) stats: everything that is neither primary nor the item level line.</summary>
    public static List<StatLine> Divine(CharacterProfile p)
    {
        var list = new List<StatLine>(10);
        foreach (var s in p.Stats)
            if (s.Type != "ItemLevel" && Array.IndexOf(PrimaryStats, s.Type) < 0) list.Add(s);
        return list;
    }

    /// <summary>"Justice [Nezekan]" → ("Justice", "Nezekan").</summary>
    public static (string Name, string? Deity) SplitDeity(string name)
    {
        var i = name.IndexOf(" [", StringComparison.Ordinal);
        return i > 0 && name.EndsWith(']') ? (name[..i], name[(i + 2)..^1]) : (name, null);
    }

    /// <summary>
    /// Skills as the card lists them: actives on the bar / learned, stigmas equipped / learned / not acquired, passives. Empty groups are left
    /// out. Keys: active, active-off, stigma, stigma-off, stigma-missing, passive.
    /// </summary>
    public static List<SkillGroup> Skills(CharacterProfile p)
    {
        var bar = new List<SkillLine>(); var learned = new List<SkillLine>();
        var stigma = new List<SkillLine>(); var stigmaOff = new List<SkillLine>(); var stigmaMissing = new List<SkillLine>();
        var passive = new List<SkillLine>();
        foreach (var s in p.Skills)
        {
            switch (s.Category)
            {
                case "Active": (s.Equipped ? bar : learned).Add(s); break;
                // the API marks some stigmas equipped but not acquired (Second Skin on Teinmeir): the slot holds it, so it counts as equipped
                case "Dp": (s.Equipped ? stigma : s.Acquired ? stigmaOff : stigmaMissing).Add(s); break;
                case "Passive": passive.Add(s); break;
            }
        }
        var groups = new List<SkillGroup>(6);
        Add("active", bar); Add("active-off", learned); Add("stigma", stigma); Add("stigma-off", stigmaOff); Add("stigma-missing", stigmaMissing); Add("passive", passive);
        return groups;

        void Add(string key, List<SkillLine> l) { if (l.Count > 0) groups.Add(new SkillGroup(key, l)); }
    }

    public static List<BoardBar> Boards(CharacterProfile p)
    {
        var list = new List<BoardBar>(p.Boards.Count);
        foreach (var b in p.Boards) if (b.Total > 0) list.Add(new BoardBar(b.Name, b.Open, b.Total));
        return list;
    }

    /// <summary>
    /// The server's per-board effect sums added up over the boards: "Vicious Strike +1" ×4 → "Vicious Strike +4", "Combat Speed +3%" + "+1.5%"
    /// → "+4.5%". Skill levels (names of the character's skills) and stats come back apart, each in order of first appearance.
    /// </summary>
    public static (List<string> SkillLevels, List<string> Stats) DaevanionTotals(CharacterProfile p)
    {
        var skillNames = new HashSet<string>(p.Skills.Count, StringComparer.Ordinal);
        foreach (var s in p.Skills) skillNames.Add(s.Name);
        var order = new List<string>();
        var sums = new Dictionary<string, (decimal Sum, bool Percent)>(StringComparer.Ordinal);
        var plain = new List<string>();
        foreach (var e in p.DaevanionEffects)
        {
            var i = e.LastIndexOf(" +", StringComparison.Ordinal);
            var num = i > 0 ? e.AsSpan(i + 2) : default;
            var pct = num.Length > 0 && num[^1] == '%';
            if (pct) num = num[..^1];
            if (i <= 0 || !decimal.TryParse(num, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
            {
                if (!plain.Contains(e)) plain.Add(e);
                continue;
            }
            var key = e[..i];
            if (sums.TryGetValue(key, out var cur)) sums[key] = (cur.Sum + v, cur.Percent || pct);
            else { sums[key] = (v, pct); order.Add(key); }
        }
        var skills = new List<string>(); var stats = new List<string>();
        foreach (var k in order)
        {
            var (sum, pct) = sums[k];
            var text = k + " +" + (pct ? sum.ToString("0.##", CultureInfo.InvariantCulture) + "%" : sum.ToString("#,0.##", CultureInfo.InvariantCulture));
            (skillNames.Contains(k) ? skills : stats).Add(text);
        }
        stats.AddRange(plain);
        return (skills, stats);
    }
}
