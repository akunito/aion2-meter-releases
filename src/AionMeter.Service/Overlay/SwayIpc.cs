using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace AionMeter.Service.Overlay;

/// <summary>
/// Sway's IPC (i3-ipc framing on $SWAYSOCK) for the GTK card's window mode (A2M-50: its own place and size, which a Wayland client cannot
/// set). No-op outside Sway.
/// </summary>
public static class SwayIpc
{
    public const uint RunCommand = 0, Subscribe = 2, GetOutputs = 3, GetTree = 4;
    /// <summary>Event types have the high bit set; `window` is event 3.</summary>
    public const uint WindowEvent = 0x80000003;
    private const int HeaderSize = 14;

    public static string? SocketPath => Environment.GetEnvironmentVariable("SWAYSOCK") is { Length: > 0 } s && File.Exists(s) ? s : null;

    /// <summary>One message: "i3-ipc", payload length and type (native byte order, little-endian on every target), UTF-8 payload.</summary>
    public static byte[] Frame(uint type, string payload)
    {
        var n = Encoding.UTF8.GetByteCount(payload);
        var b = new byte[HeaderSize + n];
        "i3-ipc"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(6), n);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10), type);
        Encoding.UTF8.GetBytes(payload, b.AsSpan(HeaderSize));
        return b;
    }

    /// <summary>Parses a 14-byte header; false when the magic is wrong.</summary>
    public static bool TryHeader(ReadOnlySpan<byte> h, out int length, out uint type)
    {
        length = 0;
        type = 0;
        if (h.Length < HeaderSize || !h[..6].SequenceEqual("i3-ipc"u8)) return false;
        length = BinaryPrimitives.ReadInt32LittleEndian(h[6..]);
        type = BinaryPrimitives.ReadUInt32LittleEndian(h[10..]);
        return length >= 0;
    }

    /// <summary>A connection to the IPC socket; one caller thread at a time. Payloads are returned in a reused buffer, valid until the next call.</summary>
    public sealed class Connection : IDisposable
    {
        private readonly Socket _s;
        private readonly byte[] _head = new byte[HeaderSize];
        private byte[] _buf = new byte[64 * 1024];

        private Connection(Socket s) => _s = s;

        public static Connection? Open(string? path = null)
        {
            path ??= SocketPath;
            if (path is null) return null;
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { s.Connect(new UnixDomainSocketEndPoint(path)); return new Connection(s); }
            catch (SocketException) { s.Dispose(); return null; }
        }

        public Socket Socket => _s;

        /// <summary>Sends a request and returns its reply payload; null when the socket failed.</summary>
        public ReadOnlyMemory<byte>? Request(uint type, string payload = "")
        {
            try
            {
                _s.Send(Frame(type, payload));
                // a subscribed connection interleaves events with replies: skip them
                while (Read() is { } m) if (m.Type == type) return m.Payload;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            return null;
        }

        /// <summary>The next message (reply or event); null at end of stream.</summary>
        public (uint Type, ReadOnlyMemory<byte> Payload)? Read()
        {
            if (!Fill(_head, HeaderSize) || !TryHeader(_head, out var n, out var type)) return null;
            if (_buf.Length < n) _buf = new byte[Math.Max(n, 2 * _buf.Length)];
            return Fill(_buf, n) ? (type, _buf.AsMemory(0, n)) : null;
        }

        private bool Fill(byte[] b, int n)
        {
            for (var got = 0; got < n;)
            {
                var r = _s.Receive(b, got, n - got, SocketFlags.None);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        public void Dispose()
        {
            try { _s.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            _s.Dispose();
        }
    }
}
