using AionMeter.Core.Events;
using AionMeter.Core.Game;
using AionMeter.Core.Protocol;

namespace AionMeter.Tests;

/// <summary>A2M-35: the two character windows Diego opened on 2026-10-11 (captures/inspect-20261011-2044b.pcap, 20:48:05 and 20:49:19).</summary>
public class InspectTests
{
    private static byte[] Fixture(string name) => Convert.FromHexString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inspect", name)).Trim());

    private static InspectEvent Inspect(byte[] body)
    {
        var events = new List<GameEvent>();
        new PacketParser(GameData.Empty, events.Add) { TimeMs = 1_000 }.Handle(Opcodes.Inspect, body);
        return Assert.Single(events.OfType<InspectEvent>());
    }

    private static string Gear(InspectEvent e) => string.Join(" ", e.Items.Select(i => $"{i.Slot}:{i.Item}+{i.Enchant}"));

    [Fact]
    public void Ifigenia_window_reads_header_gear_arcana_and_combat_power()
    {
        var e = Inspect(Fixture("ifigenia-5036.hex"));
        Assert.Equal("Ifigenia", e.Name);
        Assert.Equal(GameClass.Sorcerer, e.Class);   // wire 26, same as her 45 36 record in the capture
        Assert.Equal(45, e.Level);
        Assert.Equal(2197, e.GearScore);
        Assert.Equal(1303, e.ServerId);
        Assert.Equal("PhalanxGR", e.Legion);
        Assert.Equal(553, e.LegionId);              // the id her 45 36 record carries before the legion name
        Assert.Equal(89_565, e.CombatPower);
        Assert.Equal(25, e.Items.Count);            // 20 worn pieces + 5 arcana
        Assert.Equal(
            "23:310900001+4 4:210430038+5 17:215230001+0 16:310440017+5 8:210630052+10 24:310900001+4 13:310330052+10 10:310130050+10 " +
            "14:310330052+10 44:810460001+0 22:311030001+3 11:310230052+10 12:310230052+10 2:115030052+9 5:210130052+10 19:210730052+10 " +
            "1:110530050+14 3:210330052+10 15:310430046+11 45:810560001+0 42:810240002+0 6:210230053+10 7:210530053+10 43:810350001+0 41:810150001+0",
            Gear(e));
        // arcana slot 40+n holds the 810n xxxx card: the slot byte is the wire's own, not inferred from the item kind
        Assert.All(e.Items.Where(i => i.Slot > 40), i => Assert.Equal(i.Slot - 40, i.Item / 100_000 % 10));
    }

    [Fact]
    public void Aetalia_window_reads_header_and_gear_without_stones_or_arcana()
    {
        var e = Inspect(Fixture("aetalia-5036.hex"));
        Assert.Equal("Aetalia", e.Name);
        Assert.Equal(GameClass.Cleric, e.Class);
        Assert.Equal(41, e.Level);
        Assert.Equal(754, e.GearScore);
        Assert.Equal("Chopper", e.Legion);
        Assert.Equal(655, e.LegionId);
        Assert.Equal(35_004, e.CombatPower);
        Assert.Equal(
            "1:110740030+0 7:210540030+0 13:310350028+0 8:210640030+0 14:310360005+0 2:115040023+0 3:210340030+0 15:310450011+0 " +
            "16:310440015+0 4:210440024+0 11:310240024+0 12:310240024+0 19:210740024+0 10:310140024+0 5:210140031+0 6:210240031+0 " +
            "22:311040001+5 17:215250001+10",
            Gear(e));
    }

    [Fact]
    public void Truncated_or_foreign_bodies_emit_nothing_or_drop_only_the_tail()
    {
        var body = Fixture("aetalia-5036.hex");
        var events = new List<GameEvent>();
        var parser = new PacketParser(GameData.Empty, events.Add);
        for (var len = 0; len < body.Length; len += 37) parser.Handle(Opcodes.Inspect, body.AsSpan(0, len));
        Assert.All(events.OfType<InspectEvent>(), e => Assert.Equal("Aetalia", e.Name));
        // cut inside the gear: what is left is a prefix of the full list and the combat power is gone
        var cut = Inspect(body[..1000]);
        Assert.Equal(0, cut.CombatPower);
        Assert.Equal(Gear(Inspect(body))[..Gear(cut).Length], Gear(cut));
    }
}
