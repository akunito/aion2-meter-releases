using System.Net;
using AionMeter.Core.Capture;

namespace AionMeter.Tests;

/// <summary>/proc/net/tcp endpoints as the Linux game locator reads them (AION 2 under Proton on NixOS, 2026-10-13).</summary>
public class LinuxLocatorTests
{
    [Theory]
    [InlineData("6108A8C0:94A6", "192.168.8.97", 38054)]
    [InlineData("0100007F:DFFF", "127.0.0.1", 57343)]
    [InlineData("6070CAC1:3410", "193.202.112.96", 13328)]
    public void Proc_net_tcp_endpoint_is_little_endian_ipv4_and_hex_port(string field, string ip, int port)
    {
        Assert.True(GameProcessLocator.TryHexEndpoint(field, out var addr, out var p));
        Assert.Equal(IPAddress.Parse(ip), addr);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0100007F")]
    [InlineData("ZZZZZZZZ:0050")]
    public void Malformed_endpoints_are_refused(string field) => Assert.False(GameProcessLocator.TryHexEndpoint(field, out _, out _));

    [Fact]
    public void Port_fallback_keeps_only_established_flows_to_the_game_port()
    {
        // header, the game flow (bond0 192.168.8.97 → 193.202.112.96:13328, ESTAB), a TIME_WAIT one to the same port, a listener,
        // an https flow and a loopback relay to 13328 (filtered out later by the adapter match, not by the parser)
        const string table =
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
            "   0: 6108A8C0:94A6 6070CAC1:3410 01 00000000:00000000 00:00000000 00000000  1000        0 412345 1 0000000000000000 20 4 30 10 -1\n" +
            "   1: 6108A8C0:94A8 6070CAC1:3410 06 00000000:00000000 03:00000F2A 00000000     0        0 0 3 0000000000000000\n" +
            "   2: 00000000:0016 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 9999 1 0000000000000000 100 0 0 10 0\n" +
            "   3: 6108A8C0:B3C2 22D8B98E:01BB 01 00000000:00000000 02:00000A3B 00000000  1000        0 55555 2 0000000000000000 21 4 30 10 -1\n" +
            "   4: 0100007F:DFFF 0100007F:3410 01 00000000:00000000 00:00000000 00000000  1000        0 66666 1 0000000000000000 20 4 30 10 -1\n";
        var flows = GameProcessLocator.FlowsToPort(table, 13328);
        Assert.Equal(2, flows.Count);
        Assert.Equal(new GameConnection(IPAddress.Parse("192.168.8.97"), 38054, IPAddress.Parse("193.202.112.96"), 13328), flows[0]);
        Assert.Equal(IPAddress.Loopback, flows[1].LocalIp);
        Assert.Empty(GameProcessLocator.FlowsToPort("header only\n", 13328));
        Assert.Empty(GameProcessLocator.FlowsToPort("", 13328));
    }
}
