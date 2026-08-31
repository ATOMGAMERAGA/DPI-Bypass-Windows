using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>One established TCP connection, both ends, as the stack reports it.</summary>
/// <remarks>
/// The local end matters because IP Helper's extended statistics are keyed on the whole
/// four-tuple: measuring a running game's round trip without opening a new handshake
/// needs to name the exact connection, not just the server.
/// </remarks>
public sealed record ProcessTcpConnection(IPEndPoint Local, IPEndPoint Remote);

/// <summary>The remote endpoints one running program currently has open.</summary>
public sealed record ProcessEndpointSet
{
    public bool ProcessFound { get; init; }

    /// <summary>Established TCP connections, both ends.</summary>
    public IReadOnlyList<ProcessTcpConnection> TcpConnections { get; init; } = [];

    public IReadOnlyList<IPEndPoint> TcpRemoteEndpoints => [.. TcpConnections.Select(connection => connection.Remote)];

    /// <summary>
    /// Whether the process holds UDP sockets.
    /// </summary>
    /// <remarks>
    /// Only whether, never where: Windows' UDP table carries the local address and port
    /// and nothing else, because a UDP socket has no remote peer to report. A game that
    /// speaks only UDP therefore cannot have its server discovered this way, and saying
    /// so is the only honest answer.
    /// </remarks>
    public bool HasUdpSockets { get; init; }
}

public interface IProcessEndpointProvider
{
    ProcessEndpointSet ForProcess(string processName);

    /// <summary>Names of running processes that currently hold a remote connection.</summary>
    IReadOnlyList<string> ConnectedProcesses();
}

/// <summary>
/// Reads the per-process connection tables through the IP Helper API.
/// </summary>
/// <remarks>
/// <para>
/// <c>GetExtendedTcpTable</c> with <c>TCP_TABLE_OWNER_PID_CONNECTIONS</c> is the
/// documented way to ask which process owns which connection. It reports the real
/// remote address and port, which is what makes measuring the endpoint a game is
/// actually talking to possible at all.
/// </para>
/// <para>
/// Nothing here starts, stops, or changes a connection, and no process path is read or
/// stored: the caller supplies an image name, the table is filtered by process id, and
/// only addresses come back.
/// </para>
/// </remarks>
public sealed class WindowsProcessEndpointProvider : IProcessEndpointProvider
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidConnections = 4;
    private const int UdpTableOwnerPid = 1;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpStateEstablished = 5;

    private readonly Action<string>? _log;

    public WindowsProcessEndpointProvider(Action<string>? log = null) => _log = log;

    public ProcessEndpointSet ForProcess(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        var pids = ProcessIds(processName);
        if (pids.Count == 0)
        {
            return new ProcessEndpointSet { ProcessFound = false };
        }

        return new ProcessEndpointSet
        {
            ProcessFound = true,
            TcpConnections =
            [
                .. ReadTcpTable()
                    .Where(row => pids.Contains(row.Pid))
                    .Select(row => new ProcessTcpConnection(row.Local, row.Remote)),
            ],
            HasUdpSockets = ReadUdpOwners().Any(pids.Contains),
        };
    }

    public IReadOnlyList<string> ConnectedProcesses()
    {
        var owners = ReadTcpTable().Select(row => row.Pid).ToHashSet();
        if (owners.Count == 0)
        {
            return [];
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in SafeProcesses())
        {
            try
            {
                if (owners.Contains((uint)process.Id))
                {
                    names.Add(process.ProcessName);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // Exited between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        return [.. names];
    }

    private static HashSet<uint> ProcessIds(string processName)
    {
        var trimmed = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        var pids = new HashSet<uint>();

        try
        {
            foreach (var process in Process.GetProcessesByName(trimmed))
            {
                try
                {
                    pids.Add((uint)process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            return pids;
        }

        return pids;
    }

    private static IEnumerable<Process> SafeProcesses()
    {
        try
        {
            return Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    private IReadOnlyList<(uint Pid, IPEndPoint Local, IPEndPoint Remote)> ReadTcpTable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var rows = new List<(uint, IPEndPoint, IPEndPoint)>();

        try
        {
            foreach (var row in ReadTable<MibTcpRowOwnerPid>(GetExtendedTcpTable, TcpTableOwnerPidConnections))
            {
                if (row.State != TcpStateEstablished || row.RemoteAddress == 0)
                {
                    continue;
                }

                var address = new IPAddress(BitConverter.GetBytes(row.RemoteAddress));
                if (IsUninteresting(address))
                {
                    continue;
                }

                var local = new IPAddress(BitConverter.GetBytes(row.LocalAddress));
                rows.Add((
                    row.OwningPid,
                    new IPEndPoint(local, NetworkPort(row.LocalPort)),
                    new IPEndPoint(address, NetworkPort(row.RemotePort))));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            _log?.Invoke($"latency.target: TCP bağlantı tablosu okunamadı ({ex.Message}).");
        }

        return rows;
    }

    private IReadOnlyList<uint> ReadUdpOwners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return [.. ReadTable<MibUdpRowOwnerPid>(GetExtendedUdpTable, UdpTableOwnerPid).Select(row => row.OwningPid)];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            _log?.Invoke($"latency.target: UDP soket tablosu okunamadı ({ex.Message}).");
            return [];
        }
    }

    /// <summary>Loopback, link-local and unspecified addresses say nothing about a route.</summary>
    private static bool IsUninteresting(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static int NetworkPort(uint port)
    {
        // The table stores the port in network byte order inside a 32-bit field.
        var bytes = BitConverter.GetBytes(port);
        return (bytes[0] << 8) | bytes[1];
    }

    private delegate uint TableReader(
        nint table,
        ref int size,
        bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    private static List<T> ReadTable<T>(TableReader reader, int tableClass)
        where T : struct
    {
        var size = 0;
        var status = reader(nint.Zero, ref size, false, AfInet, tableClass, 0);

        if (status != ErrorInsufficientBuffer || size <= 0)
        {
            return [];
        }

        // The table can grow between the sizing call and the read, which the API
        // reports by asking for more room again. A couple of attempts is plenty; a
        // machine opening sockets faster than that has no stable answer to give.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                status = reader(buffer, ref size, false, AfInet, tableClass, 0);
                if (status == ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (status != 0)
                {
                    return [];
                }

                var count = Marshal.ReadInt32(buffer);
                var rows = new List<T>(Math.Max(0, count));
                var stride = Marshal.SizeOf<T>();

                for (var index = 0; index < count; index++)
                {
                    rows.Add(Marshal.PtrToStructure<T>(buffer + 4 + (index * stride)));
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddress;
        public uint LocalPort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint tcpTable,
        ref int size,
        bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        nint udpTable,
        ref int size,
        bool order,
        int addressFamily,
        int tableClass,
        uint reserved);
}
