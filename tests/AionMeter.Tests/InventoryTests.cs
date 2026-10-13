using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Inventory;
using AionMeter.Core.Protocol;

namespace AionMeter.Tests;

/// <summary>Real packets of Akunita's relog on 2026-10-08 (EU): the login list, the warehouse refresh, one gather result.</summary>
public class InventoryTests
{
    private static byte[] Fixture(string name) => Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inventory", name)).Trim());

    private static List<GameEvent> Parse(ushort op, byte[] body)
    {
        var events = new List<GameEvent>();
        var parser = new PacketParser(GameData.Empty, events.Add) { TimeMs = 1_000 };
        parser.Handle(op, body);
        return events;
    }

    [Fact]
    public void Login_list_gives_every_container_and_the_equipped_set_matches_the_character_page()
    {
        var inv = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.Inventory, Fixture("full-1156.hex"))));
        Assert.True(inv.Full);
        Assert.Equal(188, inv.Stacks.Count);
        var equipped = inv.Stacks.Where(s => s.Container == Containers.Equipped).ToList();
        Assert.Equal(20, equipped.Count);
        var sword = equipped.Single(s => s.Slot == 1);
        Assert.Equal((110230047, 10), (sword.Item, sword.Enchant));
        Assert.Equal(5, equipped.Single(s => s.Item == 210130038).Enchant); // Bakarma Breastplate +5
        var bag = inv.Stacks.Where(s => s.Container == Containers.Bag).ToList();
        Assert.Equal(70, bag.Count);
        Assert.Equal(49146, bag.Single(s => s.Item == 930100031).Count); // Kina
        Assert.Equal(54, bag.Single(s => s.Item == 610680047).Count);    // Conide (the Inina node's drop)
        Assert.Equal(56, inv.Stacks.Count(s => s.Container == Containers.AccountWarehouse));
        Assert.Equal(38, inv.Stacks.Count(s => s.Container == Containers.CharacterWarehouse));
    }

    /// <summary>Real packet of 2026-10-13 10:40:19: one crafted Orichalcum Longsword, header 01 00 01. Byte 3 is not the whole-list flag: this must patch slot 39, not wipe the bag.</summary>
    [Fact]
    public void A_crafted_item_patches_its_slot_and_never_wipes_the_bag()
    {
        var store = new InventoryStore(null);
        store.OnEvent(Parse(Opcodes.Inventory, Fixture("full-1156.hex"))[0]);
        var kina = store.CountOf(InventoryStore.Kinah);
        var bag = store.Items(Containers.Bag).Count;
        var crafted = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Fixture("crafted-1b56.hex"))));
        Assert.False(crafted.Whole);
        var st = Assert.Single(crafted.Stacks);
        Assert.Equal((110260023, 1, (byte)1, (byte)39), (st.Item, st.Count, st.Container, st.Slot));
        store.OnEvent(crafted);
        Assert.InRange(store.Items(Containers.Bag).Count, bag, bag + 1);
        Assert.Equal(kina, store.CountOf(InventoryStore.Kinah));
        // and even a packet that does carry the window flag may not wipe the bag unless it brings the currencies
        store.OnEvent(new InventoryEvent(2_000, false, crafted.Stacks, Whole: true));
        Assert.InRange(store.Items(Containers.Bag).Count, bag, bag + 1);
        Assert.Equal(kina, store.CountOf(InventoryStore.Kinah));
    }

    /// <summary>Real packet of 2026-10-13 12:17:22: Akunita deposited all her Kina; the stack keeps its uid and moves bag slot 0 → account warehouse slot 64 (one record, no bag record). A packet that names both purses under one uid keeps both.</summary>
    [Fact]
    public void Kina_deposit_moves_the_purse_to_the_warehouse_and_a_packet_naming_both_purses_keeps_both()
    {
        var store = new InventoryStore(null);
        store.OnEvent(Parse(Opcodes.Inventory, Fixture("full-1156.hex"))[0]);
        var dep = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Fixture("kina-deposit-1b56.hex"))));
        var st = Assert.Single(dep.Stacks);
        Assert.Equal((930100031, 2_092_580, (byte)2, (byte)64, 150424u), (st.Item, st.Count, st.Container, st.Slot, st.Uid));
        // the fixture's login bag has its own Kina uid; give the move the same uid as that purse to reproduce the real sequence
        var bagKina = store.Items(Containers.Bag).Single(x => x.Item == InventoryStore.Kinah);
        store.OnEvent(new InventoryEvent(2_000, false, [st with { Uid = bagKina.Uid }]));
        Assert.DoesNotContain(store.Items(Containers.Bag), x => x.Item == InventoryStore.Kinah);
        Assert.Equal(2_092_580, store.Items(Containers.AccountWarehouse).Single(x => x.Slot == 64).Count);
        // partial deposit shape (assumed until captured): both purses in one packet under one uid → both stay
        store.OnEvent(new InventoryEvent(3_000, false, [bagKina with { Count = 1_000 }, st with { Uid = bagKina.Uid, Count = 2_091_580 }]));
        Assert.Equal(1_000, store.Items(Containers.Bag).Single(x => x.Item == InventoryStore.Kinah).Count);
        Assert.Equal(2_091_580, store.Items(Containers.AccountWarehouse).Single(x => x.Slot == 64).Count);
    }

    [Fact]
    public void Store_replaces_a_container_on_refresh_and_patches_one_slot_on_a_gather()
    {
        var store = new InventoryStore(null);
        var login = Parse(Opcodes.Inventory, Fixture("full-1156.hex"))[0];
        store.OnEvent(login);
        Assert.Equal(38, store.Items(Containers.CharacterWarehouse).Count);
        var wh = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Fixture("warehouse-1b56.hex"))));
        Assert.False(wh.Full);
        store.OnEvent(wh);
        Assert.Equal(37, store.Items(Containers.CharacterWarehouse).Count); // the re-sent list has one slot fewer
        var before = store.CountOf(610680047);
        var g = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Fixture("gather-1b56.hex"))));
        Assert.Single(g.Stacks);
        Assert.Equal((610680047, 110, (byte)1, (byte)43), (g.Stacks[0].Item, g.Stacks[0].Count, g.Stacks[0].Container, g.Stacks[0].Slot));
        store.OnEvent(g);
        Assert.Equal(before - 54 + 110, store.CountOf(610680047));
        Assert.Equal(20, store.Items(Containers.Equipped).Count);
    }

    [Fact]
    public void MovedAndEmptiedStacksLeaveNoCopies()
    {
        var store = new InventoryStore(null);
        store.OnEvent(new InventoryEvent(1, true, new[] { new ItemStack(7, 610511013, 3, Containers.Bag, 4, 0), new ItemStack(8, 110130002, 1, Containers.Bag, 5, 0) }));
        store.OnEvent(new InventoryEvent(2, false, new[] { new ItemStack(7, 610511013, 3, Containers.CharacterWarehouse, 1, 0) }));   // moved to the warehouse
        Assert.Equal(3, store.CountOf(610511013));
        Assert.DoesNotContain(store.Items(Containers.Bag), st => st.Uid == 7);
        Assert.Single(store.Items(Containers.CharacterWarehouse));
        store.OnEvent(new InventoryEvent(3, false, new[] { new ItemStack(8, 110130002, 0, Containers.Bag, 5, 0) }));          // sold: count 0
        Assert.Equal(0, store.CountOf(110130002));
        Assert.Empty(store.Items(Containers.Bag));
    }

    [Fact]
    public void WithdrawalStampsTheVacatedWarehouseSlotSoTheSharedMergeDropsIt()
    {
        // 2026-10-11 15:08: withdrawing from the account warehouse sends only the bag slot (same uid); the shared warehouse ignores
        // unstamped slots, so the item stayed listed there until this stamp existed.
        var store = new InventoryStore(null);
        store.OnEvent(new InventoryEvent(1, true, new[] { new ItemStack(1535962, 531110003, 16, Containers.AccountWarehouse, 67, 0) }));
        store.OnEvent(new InventoryEvent(5_000, false, new[] { new ItemStack(1535962, 531110003, 16, Containers.Bag, 8, 0) }));
        Assert.Empty(store.Items(Containers.AccountWarehouse));
        Assert.Equal(5_000, store.SlotStamps(Containers.AccountWarehouse)[67]);
        var acct = new AccountWarehouse(null);
        acct.Absorb("1303", "1303-Akunita", 1, new[] { new ItemStack(1535962, 531110003, 16, Containers.AccountWarehouse, 67, 0) }, new Dictionary<byte, long>());
        Assert.True(acct.Absorb("1303", "1303-Akunita", 1, store.Items(Containers.AccountWarehouse), store.SlotStamps(Containers.AccountWarehouse)));
        Assert.DoesNotContain(acct.View("1303")!.Slots, sl => sl.Slot == 67 && sl.Count > 0);
        // a count-0 record (sold / used) stamps its slot the same way
        store.OnEvent(new InventoryEvent(6_000, false, new[] { new ItemStack(1535962, 531110003, 0, Containers.Bag, 8, 0) }));
        Assert.Equal(6_000, store.SlotStamps(Containers.Bag)[8]);
    }

    [Fact]
    public void KinahIsABagStackAndJunkIsDropped()
    {
        var store = new InventoryStore(null);
        store.OnEvent(new InventoryEvent(1, true, new[]
        {
            new ItemStack(1241087, InventoryStore.Kinah, 2_087_175, Containers.Bag, 0, 0),
            new ItemStack(0, 812236803, 1, 0, 0, 0),                       // uid 0, container 0
            new ItemStack(239427328, 899461376, 542, 0, 15, 0),             // 0x359C0000
        }));
        Assert.Equal(2_087_175, store.CountOf(InventoryStore.Kinah));
        Assert.Single(store.All());
        store.OnEvent(new InventoryEvent(2, false, new[] { new ItemStack(1241087, InventoryStore.Kinah, 2_169_808, Containers.Bag, 0, 0) }));
        Assert.Equal(2_169_808, store.CountOf(InventoryStore.Kinah));
    }

    [Fact]
    public void BindCarriesTheLoginListIntoTheNewCharacterFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "inv-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "a", "inventory.json"); var b = Path.Combine(root, "b", "inventory.json"); var mirror = Path.Combine(root, "inventory.json");
        var store = new InventoryStore(a);
        store.OnEvent(new InventoryEvent(1, true, new[] { new ItemStack(7, InventoryStore.Kinah, 100, Containers.Bag, 0, 0) }));
        store.Save();
        Assert.False(File.Exists(a));           // a login list waits for its character
        store.Bind(a);                          // identity of the same character: released and written
        Assert.Contains("\"Count\":100", File.ReadAllText(a));
        // identity arrives right after the login list: the list moves with it, file a keeps what it had — even if a periodic Save ran in between
        store.OnEvent(new InventoryEvent(2, true, new[] { new ItemStack(8, InventoryStore.Kinah, 555, Containers.Bag, 0, 0) }));
        store.Save();
        Assert.Contains("\"Count\":100", File.ReadAllText(a));
        store.Bind(b, mirror);
        store.Save();
        Assert.Equal(555, store.CountOf(InventoryStore.Kinah));
        Assert.True(File.Exists(b) && File.Exists(mirror));
        Assert.Contains("\"Count\":555", File.ReadAllText(b));
        Assert.Contains("\"Count\":100", File.ReadAllText(a));
        // a plain switch (no fresh login list) loads the other file
        var again = new InventoryStore(b);
        again.Bind(a);
        Assert.Equal(100, again.CountOf(InventoryStore.Kinah));
        Directory.Delete(root, true);
    }
}
