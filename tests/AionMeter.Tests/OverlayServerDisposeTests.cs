using System.Net.Sockets;
using AionMeter.Service.Overlay;

namespace AionMeter.Tests;

/// <summary>A2M-50: OverlayServer.Dispose with clients still connected. Client.Close re-entered the server's lock and removed itself from the
/// list Dispose was enumerating ("Collection was modified", DaemonSmokeTests.Socket_ServesOverlaySnapshotsAndCommands 2026-10-13).</summary>
public class OverlayServerDisposeTests
{
    [Fact]
    public async Task Dispose_WithConnectedClients_ClosesThemAll()
    {
        for (var round = 0; round < 100; round++)
        {
            var path = Path.Combine(Path.GetTempPath(), "am-d" + Guid.NewGuid().ToString("N")[..8] + ".sock");
            var server = new OverlayServer(path);
            var clients = new Socket[8];
            try
            {
                for (var i = 0; i < clients.Length; i++)
                {
                    clients[i] = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await clients[i].ConnectAsync(new UnixDomainSocketEndPoint(path));
                }
                for (var i = 0; i < 200 && server.ClientCount < clients.Length; i++) await Task.Delay(5);
                Assert.Equal(clients.Length, server.ClientCount);
                server.Broadcast("{\"type\":\"ping\"}\n"u8.ToArray());

                // one more client connecting while Dispose runs: it must be closed too, never left in the list
                var late = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var connecting = late.ConnectAsync(new UnixDomainSocketEndPoint(path));
                server.Dispose();
                server.Dispose();
                Assert.Equal(0, server.ClientCount);
                try { await connecting; } catch (SocketException) { }
                late.Dispose();

                // every client sees the server side go away (EOF or reset), none hangs
                var buf = new byte[256];
                foreach (var c in clients)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { while (await c.ReceiveAsync(buf, SocketFlags.None, cts.Token) > 0) { } }
                    catch (SocketException) { }
                }
                Assert.False(File.Exists(path));
            }
            finally
            {
                server.Dispose();
                foreach (var c in clients) c?.Dispose();
            }
        }
    }
}
