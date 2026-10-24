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
    private static string Pcap => PcapFixture.Login;

    [PcapFact]
    public void Login2026_10_09_DecodesEveryStore()
    {
        Assert.True(File.Exists(Pcap), Pcap);
        var inventory = new InventoryStore(null);
        var character = new CharacterState(null);
        var shop = new ShopStore(null);
        var counts = new Dictionary<string, int>();
        var replay = new PcapReplaySource(GameData.Empty, Pcap, realtime: false);
        replay.EventDecoded += e =>
        {
            counts[e.GetType().Name] = counts.GetValueOrDefault(e.GetType().Name) + 1;
            inventory.OnEvent(e); character.OnEvent(e); shop.OnEvent(e);
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
        Assert.Equal(2327, character.Achievements.Count);                    // A2M-15 (was 1,512 before the full 83 56 read)
        Assert.Equal(23, character.AchievementGroups.Count);
        Assert.InRange(counts.GetValueOrDefault("AchievementUpdateEvent"), 60, 67);   // 84 56 live counters (67 packets in the export)
        Assert.Equal(73, character.Tickets.Count);                            // 0b 61 at login + 4 deltas of keys 10/12
        Assert.Equal(30, character.Tickets[60_000_001].Base);                 // Odyle 30 (+110), docs/27

        // A2M-35 build lists (docs/27): the same values as the official profile of that evening
        var cap = character.Captured;
        Assert.Equal(67, cap.Skills.Count);
        var levels = cap.LevelsByBase();
        Assert.Equal(14, levels[12240000]);                                   // Judgment lv14 (+4)
        Assert.Equal(13, levels[12010000]);                                   // Vicious Strike lv13 (+3)
        Assert.Equal(16, cap.Equipped.Count);
        Assert.Equal(4, cap.Equipped.Count(e => e.IsStigma));
        Assert.Equal(15, cap.Bar.Where(k => k.Page == 1).SelectMany(k => k.Skills).Distinct().Count());
        Assert.Equal([50, 52, 47, 44, 1], cap.Boards.Select(b => b.Nodes.Length));
        Assert.Equal((25, 46), (cap.Attributes[1], cap.Attributes[2]));       // Might, Dexterity
        Assert.Equal(1, counts.GetValueOrDefault("SkillListEvent"));
        Assert.Equal(1, counts.GetValueOrDefault("DaevanionBoardsEvent"));

        // movement and identity keep flowing
        Assert.True(counts.GetValueOrDefault("PositionEvent") > 100, $"positions {counts.GetValueOrDefault("PositionEvent")}");
        Assert.True(counts.GetValueOrDefault("InventoryEvent") >= 3, $"inventory events {counts.GetValueOrDefault("InventoryEvent")}");
        Assert.True(counts.GetValueOrDefault("SelfIdentifiedEvent") >= 1, string.Join(", ", counts.Select(kv => kv.Key + "=" + kv.Value)));
    }
}
