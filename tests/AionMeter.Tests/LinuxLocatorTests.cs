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
}
