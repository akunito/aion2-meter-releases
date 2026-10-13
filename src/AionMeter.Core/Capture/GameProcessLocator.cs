using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AionMeter.Tests")]

namespace AionMeter.Core.Capture;

public readonly record struct GameConnection(IPAddress LocalIp, ushort LocalPort, IPAddress RemoteIp, ushort RemotePort);

/// <summary>Finds the running AION 2 client and the TCP connections it owns (via the Windows TCP table).</summary>
public static class GameProcessLocator
{
    public static readonly string[] ProcessNames = ["AION2", "Aion2", "AION2-Win64-Shipping", "Aion2-Win64-Shipping"];

    public static int[] FindGameProcessIds()
    {
        if (OperatingSystem.IsLinux()) return LinuxGameProcessIds();
        var ids = new HashSet<int>();
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                ids.Add(p.Id);
                p.Dispose();
            }
        }
        return ids.ToArray();
    }

    /// <summary>Established IPv4 connections of the given processes, excluding plain web traffic (80/443).</summary>
    public static List<GameConnection> FindConnections(IReadOnlyCollection<int> pids)
    {
        var result = new List<GameConnection>();
        if (pids.Count == 0) return result;
        if (OperatingSystem.IsLinux()) { LinuxConnections(pids, result); return result; }

        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return result;
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++, rowPtr += rowSize)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                if (row.State != MIB_TCP_STATE_ESTAB || !pids.Contains((int)row.OwningPid)) continue;
                var remotePort = Port(row.RemotePort);
                if (remotePort is 80 or 443) continue;
                result.Add(new GameConnection(new IPAddress(row.LocalAddr), Port(row.LocalPort), new IPAddress(row.RemoteAddr), remotePort));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    // Linux (AION 2 under Proton/Wine, NixOS 2026-10-13): the game is a Wine process whose /proc comm is the 15-char truncation
    // "AION2-Win64-Shi", so Process.GetProcessesByName never matches; the cmdline ends with the .exe path instead. Its sockets sit in
    // the host stack (ss showed 192.168.8.97:38042 → 193.202.112.96:13328 owned by the game thread and wineserver), so the connections
    // come from /proc/net/tcp rows whose inode one of the game's /proc/<pid>/fd links points at. Needs the same user (or root).
    private static int[] LinuxGameProcessIds()
    {
        var ids = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid)) continue;
            string cmd;
            try { cmd = File.ReadAllText(Path.Combine(dir, "cmdline")); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            var end = cmd.IndexOf('\0');
            var exe = end < 0 ? cmd : cmd[..end];
            var file = exe.Replace('\\', '/');
            file = file[(file.LastIndexOf('/') + 1)..];
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
            foreach (var n in ProcessNames) if (string.Equals(file, n, StringComparison.OrdinalIgnoreCase)) { ids.Add(pid); break; }
        }
        return ids.ToArray();
    }

    private static void LinuxConnections(IReadOnlyCollection<int> pids, List<GameConnection> result)
    {
        var inodes = new HashSet<long>();
        foreach (var pid in pids)
        {
            string[] fds;
            try { fds = Directory.GetFiles($"/proc/{pid}/fd"); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            foreach (var fd in fds)
            {
                string? target;
                try { target = new FileInfo(fd).LinkTarget; } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                // "socket:[12345]"
                if (target is { Length: > 9 } && target.StartsWith("socket:[", StringComparison.Ordinal) && long.TryParse(target.AsSpan(8, target.Length - 9), out var ino)) inodes.Add(ino);
            }
        }
        if (inodes.Count == 0) return;
        string[] lines;
        try { lines = File.ReadAllLines("/proc/net/tcp"); } catch (IOException) { return; }
        for (var i = 1; i < lines.Length; i++)
        {
            // sl local_address rem_address st tx_queue:rx_queue tr:tm->when retrnsmt uid timeout inode
            var f = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 10 || f[3] != "01" || !long.TryParse(f[9], out var inode) || !inodes.Contains(inode)) continue;
            if (!TryHexEndpoint(f[1], out var localIp, out var localPort) || !TryHexEndpoint(f[2], out var remoteIp, out var remotePort)) continue;
            if (remotePort is 80 or 443) continue;
            result.Add(new GameConnection(localIp, localPort, remoteIp, remotePort));
        }
    }

    /// <summary>ESTAB rows of a /proc/net/tcp dump whose remote port is <paramref name="port"/>, whoever owns them (the A2M-48 fallback
    /// for a game the pid locator cannot see).</summary>
    internal static List<GameConnection> FlowsToPort(string procNetTcp, int port)
    {
        var result = new List<GameConnection>();
        var lines = procNetTcp.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var f = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || f[3] != "01") continue;
            if (!TryHexEndpoint(f[2], out var remoteIp, out var remotePort) || remotePort != port || !TryHexEndpoint(f[1], out var localIp, out var localPort)) continue;
            result.Add(new GameConnection(localIp, localPort, remoteIp, remotePort));
        }
        return result;
    }

    /// <summary>/proc/net/tcp "0A0808C0:9496" = little-endian IPv4 hex + port hex.</summary>
    internal static bool TryHexEndpoint(string s, out IPAddress ip, out ushort port)
    {
        ip = IPAddress.None; port = 0;
        var colon = s.IndexOf(':');
        if (colon != 8 || s.Length < 13) return false;
        if (!uint.TryParse(s.AsSpan(0, 8), System.Globalization.NumberStyles.HexNumber, null, out var raw) ||
            !ushort.TryParse(s.AsSpan(colon + 1), System.Globalization.NumberStyles.HexNumber, null, out port)) return false;
        ip = new IPAddress(raw);   // the kernel writes the address in host byte order, which on x86 is the byte-reversed dotted form — IPAddress(long) takes network order bytes as stored
        return true;
    }

    private static ushort Port(uint networkOrder) => (ushort)((networkOrder & 0xFF) << 8 | (networkOrder >> 8) & 0xFF);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint MIB_TCP_STATE_ESTAB = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
