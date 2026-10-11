using System.Text.Json;
using AionMeter.Core.Capture;
using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.GameDb;
using AionMeter.Core.Inventory;
using AionMeter.Core.Protocol;

namespace AionMeter.Tests;

/// <summary>A2M-29/32: the stricter container rule, container times, the shared account warehouse and the Items tab's helpers.</summary>
public class ItemsTabTests
{
    private static byte[] Fixture(string name) => Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inventory", name)).Trim());

    private static List<GameEvent> Parse(ushort op, byte[] body)
    {
        var events = new List<GameEvent>();
        new PacketParser(GameData.Empty, events.Add) { TimeMs = 1_000 }.Handle(op, body);
        return events;
    }

    private static ItemStack S(uint uid, int item, int count, byte container, byte slot) => new(uid, item, count, container, slot, 0);

    /// <summary>The three real inventory.json files of 2026-10-11 (tools/items-sample.py) as InventoryFile, keyed by character.</summary>
    internal static List<(string Key, string Cls, InventoryFile File)> Sample()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "items", "sample-items.json")));
        var list = new List<(string, string, InventoryFile)>();
        foreach (var c in doc.RootElement.GetProperty("characters").EnumerateArray())
        {
            var stacks = new List<ItemStack>();
            foreach (var r in c.GetProperty("stacks").EnumerateArray())
                stacks.Add(new ItemStack(r[0].GetUInt32(), r[1].GetInt32(), r[2].GetInt32(), (byte)r[3].GetInt32(), (byte)r[4].GetInt32(), r[5].GetInt32()));
            list.Add((c.GetProperty("key").GetString()!, c.GetProperty("cls").GetString()!, new InventoryFile(stacks, new(), c.GetProperty("writtenUtc").GetDateTime().ToUniversalTime())));
        }
        return list;
    }

    [Fact]
    public void RefreshHeaderFlagMarksWholeContainers()
    {
        var wh = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Fixture("warehouse-1b56.hex"))));
        Assert.True(wh.Whole);                                                           // 26 01 01
        var g = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Fixture("gather-1b56.hex"))));
        Assert.False(g.Whole);                                                           // 01 01 00
        var login = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.Inventory, Fixture("full-1156.hex"))));
        Assert.DoesNotContain(login.Stacks, s => s.Count == 0);                          // count-0 records only from 1b56
    }

    [Fact]
    public void DepositOfTwelveStacksMovesThemAndWipesNothing()
    {
        // the real 18 01 00 packet of 2026-10-10 22:32 (daily capture), reduced to 2 of its 12 pairs: warehouse slot filled + bag slot emptied
        var hex = "180100" + Rec(741144, 930100031, 512550, 2, 59) + Rec(964635, 930100031, 0, 1, 0) + Rec(1489156, 530116006, 59, 2, 60) + Rec(701045, 530116006, 0, 1, 9);
        var ev = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Convert.FromHexString(hex))));
        Assert.False(ev.Whole);
        Assert.Equal(4, ev.Stacks.Count);
        var store = new InventoryStore(null);
        var login = new List<ItemStack>();
        for (byte i = 0; i < 20; i++) login.Add(S(900_000u + i, 610_000_001 + i, 5, Containers.Bag, i));
        login[0] = S(964635, 930100031, 512550, Containers.Bag, 0);
        login[9] = S(701045, 530116006, 59, Containers.Bag, 9);
        for (byte i = 0; i < 30; i++) login.Add(S(800_000u + i, 620_000_001 + i, 1, Containers.AccountWarehouse, i));
        store.OnEvent(new InventoryEvent(10, true, login));
        store.OnEvent(ev with { TimeMs = 20 });
        Assert.Equal(18, store.Items(Containers.Bag).Count);
        Assert.Equal(32, store.Items(Containers.AccountWarehouse).Count);                 // filled, not replaced
        Assert.Equal(512550, store.Items(Containers.AccountWarehouse).Single(s => s.Slot == 59).Count);
        Assert.DoesNotContain(store.Items(Containers.Bag), s => s.Slot is 0 or 9);
    }

    private static string Rec(uint uid, int item, int count, byte container, byte slot)
    {
        var b = new byte[22 + 17];
        BitConverter.GetBytes(uid).CopyTo(b, 0);
        BitConverter.GetBytes(item).CopyTo(b, 8);
        BitConverter.GetBytes(count).CopyTo(b, 12);
        b[20] = container; b[21] = slot;
        return Convert.ToHexString(b);
    }

    [Fact]
    public void EightSingleSlotUpdatesInARowDoNotWipeTheBag()
    {
        var store = new InventoryStore(null);
        var login = new List<ItemStack>();
        for (byte i = 0; i < 31; i++) login.Add(S(1000u + i, 610_000_001 + i, 10, Containers.Bag, i));
        store.OnEvent(new InventoryEvent(1, true, login));
        // eight potions used in one burst: one event of 8 slots, then eight events of one slot
        store.OnEvent(new InventoryEvent(2, false, Enumerable.Range(9, 8).Select(i => S(1000u + (uint)i, 610_000_001 + i, 9, Containers.Bag, (byte)i)).ToList()));
        for (var i = 0; i < 8; i++) store.OnEvent(new InventoryEvent(3 + i, false, [S(1000u + (uint)i, 610_000_001 + i, 8, Containers.Bag, (byte)i)]));
        var bag = store.Items(Containers.Bag);
        Assert.Equal(31, bag.Count);
        Assert.Equal(Enumerable.Range(0, 31), bag.Select(s => (int)s.Slot));
        Assert.False(ItemKinds.BagLooksIncomplete(bag));
        Assert.Equal(new ContainerSeen(10, 1), store.Seen(Containers.Bag));
    }

    [Fact]
    public void LoginListWithoutWarehousesKeepsThemAndReplacesTheBag()
    {
        var store = new InventoryStore(null);
        store.OnEvent(new InventoryEvent(1, true, [S(1, 610_000_001, 1, Containers.Bag, 0), S(2, 610_000_002, 1, Containers.Bag, 1), S(3, 620_000_001, 4, Containers.AccountWarehouse, 0), S(4, 620_000_002, 4, Containers.CharacterWarehouse, 0)]));
        store.OnEvent(new InventoryEvent(100_000, true, [S(11, 610_000_003, 1, Containers.Bag, 0)]));    // next login: bag only
        Assert.Single(store.Items(Containers.Bag));
        Assert.Single(store.Items(Containers.AccountWarehouse));
        Assert.Single(store.Items(Containers.CharacterWarehouse));
        Assert.Equal(1, store.Seen(Containers.AccountWarehouse).FullMs);
        Assert.Equal(100_000, store.Seen(Containers.Bag).FullMs);
        // a login list split over two packets of one burst: the second does not wipe what the first brought
        store.OnEvent(new InventoryEvent(200_000, true, [S(21, 610_000_004, 1, Containers.Bag, 0), S(22, 610_000_005, 1, Containers.Bag, 1)]));
        store.OnEvent(new InventoryEvent(200_400, true, [S(23, 610_000_006, 1, Containers.Bag, 2), S(24, 620_000_009, 1, Containers.AccountWarehouse, 5)]));
        Assert.Equal(3, store.Items(Containers.Bag).Count);
        Assert.Single(store.Items(Containers.AccountWarehouse));                          // carried in the list: replaced
    }

    [Fact]
    public void LoginReplayLeavesAContiguousBagWithItsCurrencies()
    {
        var pcap = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pcaps", "login-20261009.pcap");
        var store = new InventoryStore(null);
        var updates = 0;
        var replay = new PcapReplaySource(GameData.Empty, pcap, realtime: false);
        replay.EventDecoded += e => { if (e is InventoryEvent { Full: false } u) { updates++; Assert.False(u.Whole); } store.OnEvent(e); };
        replay.RunToEnd();
        var bag = store.Items(Containers.Bag);
        Assert.True(updates >= 15, $"{updates} single-slot updates");
        Assert.Equal(Enumerable.Range(0, bag.Count), bag.Select(s => (int)s.Slot));       // slots 0..37, no holes
        Assert.InRange(bag.Count, 36, 40);
        Assert.Contains(bag, s => s.Item == InventoryStore.AbyssPoints);
        Assert.Contains(bag, s => s.Item == InventoryStore.EnhanceStone);
        Assert.True(store.Seen(Containers.Bag).FullMs > 0);
        Assert.Empty(store.Items(Containers.Cosmetic));                                   // the cosmetic used at 18:32:55 left with a count-0 record
    }

    [Fact]
    public void ContainerTimesSurviveSaveAndOldReadersStillParseTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "inv-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "inventory.json");
        var store = new InventoryStore(path);
        store.OnEvent(new InventoryEvent(1_000, true, [S(7, InventoryStore.Kinah, 100, Containers.Bag, 0)]));
        store.Bind(path);
        store.OnEvent(new InventoryEvent(2_000, false, [S(8, 620_000_001, 3, Containers.CharacterWarehouse, 4)]));
        store.Save();
        var f = InventoryStore.ReadFile(path)!;
        Assert.Equal(new ContainerSeen(1_000, 1_000), f.SeenOf(Containers.Bag));
        Assert.Equal(new ContainerSeen(2_000, 0), f.SeenOf(Containers.CharacterWarehouse));
        Assert.Equal(2, f.Stacks.Count);
        // what builds before A2M-32 do: a plain list of stacks; the times element becomes a uid-0 stack they drop
        var old = JsonSerializer.Deserialize<List<ItemStack>>(File.ReadAllText(path))!;
        Assert.Equal(3, old.Count);
        Assert.Equal(2, old.Count(s => s.Uid != 0));
        // and a file of the old shape loads
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { S(9, InventoryStore.Kinah, 55, Containers.Bag, 0) }));
        var again = new InventoryStore(path);
        Assert.Equal(55, again.CountOf(InventoryStore.Kinah));
        Assert.Equal(default, again.Seen(Containers.Bag));
        Directory.Delete(root, true);
    }

    [Fact]
    public void AccountWarehouseMergesBySlotNewestWins()
    {
        var acct = new AccountWarehouse(null);
        var full = Enumerable.Range(0, 10).Select(i => S(100u + (uint)i, 620_000_001 + i, 5, Containers.AccountWarehouse, (byte)i)).ToList();
        Assert.True(acct.Absorb("1303", "1303-Akunita", 1_000, full, new Dictionary<byte, long>()));
        // Irin deposits into slot 10 and changes slot 3 later
        Assert.True(acct.Absorb("1303", "1303-Irin", 0, [S(1, 630_000_001, 7, 2, 3), S(2, 630_000_002, 1, 2, 10)], new Dictionary<byte, long> { [3] = 2_000, [10] = 2_100 }));
        // Akunita's stale copy of slot 3 (her session stamp is older) must not win back
        Assert.False(acct.Absorb("1303", "1303-Akunita", 1_000, full, new Dictionary<byte, long> { [3] = 1_500 }));
        var v = acct.View("1303")!;
        Assert.Equal(11, v.Slots.Count);
        var s3 = v.Slots.Single(s => s.Slot == 3);
        Assert.Equal((630_000_001, 7, "1303-Irin", 2_000L), (s3.Item, s3.Count, s3.By, s3.AtMs));
        Assert.Equal(("1303-Akunita", 1_000L), (v.FullBy, v.FullMs));
        var newer = Assert.Single(v.NewerThanFull());
        Assert.Equal(("1303-Irin", 2_100L, 2), newer);
        Assert.Equal(("1303-Irin", 2_100L), v.LastUpdate());
        // Arielka empties slot 10: gone, and an older copy cannot bring it back
        acct.Absorb("1303", "1303-Arielka", 0, [], new Dictionary<byte, long> { [10] = 3_000 });
        acct.Absorb("1303", "1303-Irin", 0, [S(2, 630_000_002, 1, 2, 10)], new Dictionary<byte, long> { [10] = 2_100 });
        Assert.DoesNotContain(acct.View("1303")!.Slots, s => s.Slot == 10);
        // a newer full list replaces every older slot (slot 3 included), keeps nothing absent from it
        acct.Absorb("1303", "1303-Arielka", 4_000, full.Take(5).ToList(), new Dictionary<byte, long>());
        v = acct.View("1303")!;
        Assert.Equal(5, v.Slots.Count);
        Assert.All(v.Slots, s => Assert.Equal(("1303-Arielka", 4_000L), (s.By, s.AtMs)));
        Assert.Empty(v.NewerThanFull());
        Assert.Null(acct.View("2305"));
    }

    [Fact]
    public void AccountWarehouseFollowsTheLiveStoreAndPersists()
    {
        var store = new InventoryStore(null);
        var list = Enumerable.Range(0, 12).Select(i => S(500u + (uint)i, 620_000_001 + i, 2, Containers.AccountWarehouse, (byte)i)).ToList();
        store.OnEvent(new InventoryEvent(10_000, false, list, Whole: true));             // warehouse window opened
        store.OnEvent(new InventoryEvent(11_000, false, [S(500, 620_000_001, 9, Containers.AccountWarehouse, 0)]));
        var path = Path.Combine(Path.GetTempPath(), "acct-" + Guid.NewGuid().ToString("N") + ".json");
        var acct = new AccountWarehouse(path);
        var seen = store.Seen(Containers.AccountWarehouse);
        Assert.Equal(new ContainerSeen(11_000, 10_000), seen);
        acct.Absorb("1303", "1303-Irin", seen.FullMs, store.Items(Containers.AccountWarehouse), store.SlotStamps(Containers.AccountWarehouse));
        acct.Save();
        var back = new AccountWarehouse(path).View("1303")!;
        Assert.Equal(12, back.Slots.Count);
        Assert.Equal((9, 11_000L), (back.Slots[0].Count, back.Slots[0].AtMs));
        Assert.Equal(("1303-Irin", 10_000L), (back.FullBy, back.FullMs));
        File.Delete(path);
    }

    [Fact]
    public void SeedFromTheRealFilesTakesAkunitasFullListAndArielkasNewerSlots()
    {
        var acct = new AccountWarehouse(null);
        acct.Seed("1303", Sample().Select(c => (c.Key, c.File)));
        var v = acct.View("1303")!;
        Assert.Equal("1303-Akunita", v.FullBy);                                           // 67 stacks at 12:04, newer than Irin's 65 at 02:20
        Assert.Equal(67, v.Slots.Count);
        var newer = Assert.Single(v.NewerThanFull());
        Assert.Equal("1303-Arielka", newer.By);
        Assert.Equal(10, newer.Slots);
    }

    [Fact]
    public void AcrossCharactersCountsEveryPlaceButEquipped()
    {
        var idx = new ItemsIndex();
        var acct = new AccountWarehouse(null);
        var sample = Sample();
        acct.Seed("1303", sample.Select(c => (c.Key, c.File)));
        foreach (var (key, _, f) in sample)
        {
            idx.Add(key, Place.Bag, f.Items(Containers.Bag));
            idx.Add(key, Place.CharacterWarehouse, f.Items(Containers.CharacterWarehouse));
            idx.Add(key, Place.Equipped, f.Items(Containers.Equipped));
        }
        idx.Add("", Place.AccountWarehouse, acct.View("1303")!.Slots.Select(s => new ItemStack(0, s.Item, s.Count, Containers.AccountWarehouse, s.Slot, s.Enchant)).ToList());
        // Odyle (610610013): bags of Irin and Arielka + the account warehouse
        Assert.True(idx.Places(610610013) >= 3);
        Assert.Equal(idx.Of(610610013).Where(h => h.Place != Place.Equipped).Sum(h => h.Count), idx.Total(610610013));
        // 310900001 is equipped twice on every character and in Akunita's warehouse once: only the warehouse counts
        Assert.Contains(idx.Of(310900001), h => h.Place == Place.Equipped);
        Assert.Equal(1, idx.Total(310900001));
        var split = idx.Split();
        Assert.NotEmpty(split);
        Assert.All(split, id => Assert.True(idx.Places(id) >= 3 && !InventoryStore.IsCurrency(id)));
        Assert.True(idx.Places(split[0]) >= idx.Places(split[^1]));
        var m = idx.Matching(id => id == 610610013 || id == InventoryStore.Kinah);
        Assert.Equal([610610013], m);
        Assert.False(ItemKinds.BagLooksIncomplete(sample.Single(c => c.Key == "1303-Irin").File.Items(Containers.Bag)));
        Assert.True(ItemKinds.BagLooksIncomplete(sample.Single(c => c.Key == "1303-Akunita").File.Items(Containers.Bag)));   // 8 stacks in 1, 9–15
        Assert.False(ItemKinds.BagLooksIncomplete(sample.Single(c => c.Key == "1303-Arielka").File.Items(Containers.Bag)));    // 22 stacks in 0–45 after a deposit, Enhance Stone there
    }

    [Fact]
    public void GroupsSortAndGrades()
    {
        Assert.Equal(ItemGroup.Equipment, ItemKinds.Group(110230047));
        Assert.Equal(ItemGroup.Equipment, ItemKinds.Group(310900001));
        Assert.Equal(ItemGroup.Materials, ItemKinds.Group(610530008));
        Assert.Equal(ItemGroup.Materials, ItemKinds.Group(631900001));
        Assert.Equal(ItemGroup.Consumables, ItemKinds.Group(531110002));
        Assert.Equal(ItemGroup.Consumables, ItemKinds.Group(930100031));
        Assert.Equal(Place.Equipped, ItemKinds.PlaceOf(Containers.Cosmetic));
        Assert.Null(ItemKinds.PlaceOf(Containers.Unknown17));

        var grades = ItemGrades.Parse("""{"1":"common b","2":"rare","3":"legendary","4":"unique b","5":"epic","6":"special","7":"bogus"}""");
        Assert.Equal(Grade.Rare, grades.Of(2));
        Assert.True(grades.Bound(4));
        Assert.False(grades.Bound(2));
        Assert.Equal(Grade.Unknown, grades.Of(7));
        Assert.Equal(Grade.Unknown, grades.Of(99));

        var rows = new List<ItemStack> { S(1, 300_000_001, 1, 1, 2), S(2, 300_000_002, 5, 1, 0), S(3, 300_000_003, 2, 1, 1) };
        var names = new Dictionary<int, string> { [300_000_001] = "Bravo", [300_000_002] = "Charlie", [300_000_003] = "alpha" };
        var gr = new Dictionary<int, Grade> { [300_000_001] = Grade.Common, [300_000_002] = Grade.Epic, [300_000_003] = Grade.Unique };
        var price = new Dictionary<int, long> { [300_000_001] = 1_000, [300_000_002] = 10 };
        ItemKinds.Sort(rows, ItemSortMode.Game, id => names[id], id => gr[id], id => price.TryGetValue(id, out var p) ? p : null);
        Assert.Equal([0, 1, 2], rows.Select(r => (int)r.Slot));
        ItemKinds.Sort(rows, ItemSortMode.Name, id => names[id], id => gr[id], id => price.TryGetValue(id, out var p) ? p : null);
        Assert.Equal(["alpha", "Bravo", "Charlie"], rows.Select(r => names[r.Item]));
        ItemKinds.Sort(rows, ItemSortMode.Grade, id => names[id], id => gr[id], id => price.TryGetValue(id, out var p) ? p : null);
        Assert.Equal([Grade.Epic, Grade.Unique, Grade.Common], rows.Select(r => gr[r.Item]));
        ItemKinds.Sort(rows, ItemSortMode.Value, id => names[id], id => gr[id], id => price.TryGetValue(id, out var p) ? p : null);
        Assert.Equal([300_000_001, 300_000_002, 300_000_003], rows.Select(r => r.Item));   // 1,000 > 50 > unpriced
    }

    [Fact]
    public void CraftHintPicksTheRecipesClosestToCompletion()
    {
        var path = Path.Combine(Path.GetTempPath(), "recipes-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            [{"o":110240013,"n":"Artisan's Orichalcum Longsword","p":"blacksmithing","l":30,"g":"legendary","y":1,"m":[[110240012,1],[610530008,5],[610620015,8],[610610015,4]]},
             {"o":110240014,"n":"Artisan's Orichalcum Longsword","p":"blacksmithing","l":30,"g":"legendary","y":1,"m":[[110240012,1],[610530008,5],[610620015,8],[610610015,4]]},
             {"o":610710001,"n":"Orichalcum Ingot","p":"blacksmithing","l":1,"g":"common","y":1,"m":[[610620015,3]]},
             {"o":210140015,"n":"Artisan's Orichalcum Breastplate","p":"tailoring","l":30,"g":"legendary","y":1,"m":[[210140014,1],[610530008,3],[610620015,5],[610610015,3]]}]
            """);
        var book = RecipeBook.Load(path); File.Delete(path);
        Assert.Equal(4, book.UsedIn(610620015).Length);
        Assert.Empty(book.UsedIn(999_999_999));
        var have = new Dictionary<int, long> { [610620015] = 6, [610530008] = 5, [110240012] = 1 };
        var hints = CraftHints.For(610620015, book, id => have.GetValueOrDefault(id));
        Assert.Equal(2, hints.Count);
        Assert.Equal("Orichalcum Ingot", hints[0].Recipe.Name);                          // 6 ≥ 3: complete
        Assert.Empty(hints[0].Missing);
        Assert.Equal((3, 6L), (hints[0].Need, hints[0].Have));
        Assert.Equal("Artisan's Orichalcum Longsword", hints[1].Recipe.Name);            // twins counted once; 3 of 4 materials partly there
        Assert.Equal([(610620015, 6L, 8), (610610015, 0L, 4)], hints[1].Missing);
    }
}
