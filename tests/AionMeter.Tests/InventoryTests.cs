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
        Assert.Equal(56, inv.Stacks.Count(s => s.Container == Containers.Bag2));
        Assert.Equal(38, inv.Stacks.Count(s => s.Container == Containers.Warehouse));
    }

    [Fact]
    public void Store_replaces_a_container_on_refresh_and_patches_one_slot_on_a_gather()
    {
        var store = new InventoryStore(null);
        var login = Parse(Opcodes.Inventory, Fixture("full-1156.hex"))[0];
        store.OnEvent(login);
        Assert.Equal(38, store.Items(Containers.Warehouse).Count);
        var wh = Assert.IsType<InventoryEvent>(Assert.Single(Parse(Opcodes.InventoryUpdate, Fixture("warehouse-1b56.hex"))));
        Assert.False(wh.Full);
        store.OnEvent(wh);
        Assert.Equal(37, store.Items(Containers.Warehouse).Count); // the re-sent list has one slot fewer
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
        store.OnEvent(new InventoryEvent(2, false, new[] { new ItemStack(7, 610511013, 3, Containers.Warehouse, 1, 0) }));   // moved to the warehouse
        Assert.Equal(3, store.CountOf(610511013));
        Assert.DoesNotContain(store.Items(Containers.Bag), st => st.Uid == 7);
        Assert.Single(store.Items(Containers.Warehouse));
        store.OnEvent(new InventoryEvent(3, false, new[] { new ItemStack(8, 110130002, 0, Containers.Bag, 5, 0) }));          // sold: count 0
        Assert.Equal(0, store.CountOf(110130002));
        Assert.Empty(store.Items(Containers.Bag));
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
