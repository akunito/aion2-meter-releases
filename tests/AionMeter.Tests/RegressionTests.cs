using AionMeter.Core.Game;
using AionMeter.Core.Capture;
using AionMeter.Core.Character;
using AionMeter.Core.Events;
using AionMeter.Core.Inventory;
using AionMeter.Core.Market;

namespace AionMeter.Tests;

/// <summary>
/// Whole-capture regression (A2M-7): a real login of Akunita on 2026-10-09 (20:17–20:31, Verteron, bag/warehouse windows, a broker
/// settlement at 20:23) replayed through the parser and every store. When a game update changes a packet, these numbers move first.
/// Needs libpcap in the test container (build.sh installs it).
/// </summary>
public class RegressionTests
{
    private static string Pcap => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pcaps", "login-20261009.pcap");

    [Fact]
    public void Login2026_10_09_DecodesEveryStore()
    {
        Assert.True(File.Exists(Pcap), Pcap);
        var inventory = new InventoryStore(null);
        var character = new CharacterState(null);
        var broker = new BrokerStore(null);
        var counts = new Dictionary<string, int>();
        var replay = new PcapReplaySource(GameData.Empty, Pcap, realtime: false);
        replay.EventDecoded += e =>
        {
            counts[e.GetType().Name] = counts.GetValueOrDefault(e.GetType().Name) + 1;
            inventory.OnEvent(e); character.OnEvent(e); broker.OnEvent(e);
        };
        replay.RunToEnd();

        // inventory: login list + refreshes; Kina is a bag stack and the settlement added 9,733 + 72,900 to it
        Assert.InRange(inventory.All().Count, 135, 150);
        Assert.Equal(20, inventory.Items(Containers.Equipped).Count);
        Assert.InRange(inventory.Items(Containers.Bag).Count, 36, 40);
        Assert.InRange(inventory.Items(Containers.AccountWarehouse).Count, 41, 45);
        Assert.InRange(inventory.Items(Containers.CharacterWarehouse).Count, 36, 40);
        Assert.InRange(inventory.CountOf(InventoryStore.Kinah), 2_169_808, 2_172_000);
        Assert.Equal(7_000, inventory.CountOf(InventoryStore.KinahBound));
        Assert.Equal(127_350, inventory.CountOf(930100003));                  // Abyss Points
        Assert.DoesNotContain(inventory.All(), s => s.Container == 0 || s.Uid == 0);

        // login lists
        Assert.Equal(1097, character.EquippedPet);
        Assert.InRange(character.Pets.Count, 120, 130);
        Assert.InRange(character.Titles.Count, 180, 190);
        Assert.Equal(1512, character.Achievements.Count);

        // movement and identity keep flowing
        Assert.True(counts.GetValueOrDefault("PositionEvent") > 100, $"positions {counts.GetValueOrDefault("PositionEvent")}");
        Assert.True(counts.GetValueOrDefault("InventoryEvent") >= 3, $"inventory events {counts.GetValueOrDefault("InventoryEvent")}");
        Assert.True(counts.GetValueOrDefault("SelfIdentifiedEvent") >= 1, string.Join(", ", counts.Select(kv => kv.Key + "=" + kv.Value)));
    }
}
