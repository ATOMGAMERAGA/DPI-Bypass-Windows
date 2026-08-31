using System.Net;
using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>
/// Reads the TCP stack's own round-trip estimate for a connection that already exists.
/// </summary>
/// <remarks>
/// <para>
/// This is the honest way to measure a game's TCP round trip. Opening a fresh handshake
/// every few hundred milliseconds measures connection setup, not the session, and a
/// stream of SYNs at somebody's game server is exactly the shape of traffic their
/// anti-abuse rules exist to stop. IP Helper's extended statistics instead report what
/// the stack has already measured on the connection the game is using:
/// <c>TcpConnectionEstatsPath</c> yields <c>SmoothedRtt</c>, <c>SampleRtt</c> and
/// <c>RttVar</c> in milliseconds.
/// </para>
/// <para>
/// Two documented constraints shape the API here. Collection has to be turned on per
/// connection first with <c>SetPerTcpConnectionEStats</c>, which "can only be called by a
/// user logged on as a member of the Administrators group"; and the caller "should check
/// the EnableCollection field in the returned Rw struct, and if it is not TRUE, then the
/// caller should ignore the data" - so a failure to enable never turns into a plausible
/// looking number. IPv4 only: the IPv6 pair of functions takes a different row type, and
/// the probe path this feeds is IPv4 throughout.
/// </para>
/// </remarks>
public static partial class TcpEStats
{
    private const int NoError = 0;
    private const int TcpConnectionEstatsPath = 3;
    private const int MibTcpStateEstablished = 5;

    /// <summary>One reading of the stack's own estimate for one connection.</summary>
    public sealed record PathSample(double SmoothedRttMs, double SampleRttMs, double RttVarianceMs)
    {
        /// <summary>Whether the stack has actually produced an estimate yet.</summary>
        public bool IsUsable => SmoothedRttMs is > 0 and < 60_000;
    }

    /// <summary>Whether this build can use extended statistics at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Turns on path statistics for one established IPv4 connection.
    /// </summary>
    /// <returns>False when the stack refused, which is never treated as a measurement.</returns>
    public static bool TryEnable(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (!IsSupported)
        {
            return false;
        }

        var row = BuildRow(local, remote);
        var rw = new TcpEstatsPathRwV0 { EnableCollection = 1 };
        var size = Marshal.SizeOf<TcpEstatsPathRwV0>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(rw, buffer, fDeleteOld: false);
            return SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, buffer, 0, (uint)size, 0) == NoError;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Reads the current estimate, or null when there is not a trustworthy one.
    /// </summary>
    /// <remarks>
    /// Null covers every case where the answer would be a guess: the call failed, the
    /// connection is gone, or collection is not actually on - which the documentation is
    /// explicit about, because the dynamic buffer holds undefined data when it is off.
    /// </remarks>
    public static PathSample? TryRead(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (!IsSupported)
        {
            return null;
        }

        var row = BuildRow(local, remote);
        var rwSize = Marshal.SizeOf<TcpEstatsPathRwV0>();
        var rodSize = Marshal.SizeOf<TcpEstatsPathRodV0>();
        var rwBuffer = Marshal.AllocHGlobal(rwSize);
        var rodBuffer = Marshal.AllocHGlobal(rodSize);

        try
        {
            var status = GetPerTcpConnectionEStats(
                ref row,
                TcpConnectionEstatsPath,
                rwBuffer,
                0,
                (uint)rwSize,
                nint.Zero,
                0,
                0,
                rodBuffer,
                0,
                (uint)rodSize);

            if (status != NoError)
            {
                return null;
            }

            var rw = Marshal.PtrToStructure<TcpEstatsPathRwV0>(rwBuffer);
            if (rw.EnableCollection == 0)
            {
                return null;
            }

            var rod = Marshal.PtrToStructure<TcpEstatsPathRodV0>(rodBuffer);
            var sample = new PathSample(rod.SmoothedRtt, rod.SampleRtt, rod.RttVar);
            return sample.IsUsable ? sample : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(rwBuffer);
            Marshal.FreeHGlobal(rodBuffer);
        }
    }

    private static MibTcpRow BuildRow(IPEndPoint local, IPEndPoint remote) => new()
    {
        State = MibTcpStateEstablished,
        LocalAddr = ToNetworkOrder(local.Address),
        LocalPort = ToPortBytes(local.Port),
        RemoteAddr = ToNetworkOrder(remote.Address),
        RemotePort = ToPortBytes(remote.Port),
    };

    private static uint ToNetworkOrder(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out var written) && written == 4
            ? BitConverter.ToUInt32(bytes)
            : 0;
    }

    /// <summary>MIB_TCPROW stores ports in network order inside the low 16 bits.</summary>
    private static uint ToPortBytes(int port)
        => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpEstatsPathRwV0
    {
        [MarshalAs(UnmanagedType.U1)]
        public byte EnableCollection;
    }

    /// <summary>
    /// TCP_ESTATS_PATH_ROD_v0. Only the three RTT fields are read, but the whole layout
    /// has to be declared because the API is given the structure size.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TcpEstatsPathRodV0
    {
        public uint FastRetran;
        public uint Timeouts;
        public uint SubsequentTimeouts;
        public uint CurTimeoutCount;
        public uint AbruptTimeouts;
        public uint PktsRetrans;
        public uint BytesRetrans;
        public uint DupAcksIn;
        public uint SacksRcvd;
        public uint SackBlocksRcvd;
        public uint CongSignals;
        public uint PreCongSumCwnd;
        public uint PreCongSumRtt;
        public uint PostCongSumRtt;
        public uint PostCongCountRtt;
        public uint EcnSignals;
        public uint EceRcvd;
        public uint SendStall;
        public uint QuenchRcvd;
        public uint RetranThresh;
        public uint SndDupAckEpisodes;
        public uint SumBytesReordered;
        public uint NonRecovDa;
        public uint NonRecovDaEpisodes;
        public uint AckAfterFr;
        public uint DsackDups;
        public uint SampleRtt;
        public uint SmoothedRtt;
        public uint RttVar;
        public uint MaxRtt;
        public uint MinRtt;
        public uint SumRtt;
        public uint CountRtt;
        public uint CurRto;
        public uint MaxRto;
        public uint MinRto;
        public uint Mss;
        public uint SpuriousRtoDetections;
    }

    [LibraryImport("iphlpapi.dll", EntryPoint = "SetPerTcpConnectionEStats")]
    private static partial uint SetPerTcpConnectionEStats(
        ref MibTcpRow row,
        int estatsType,
        nint rw,
        uint rwVersion,
        uint rwSize,
        uint offset);

    [LibraryImport("iphlpapi.dll", EntryPoint = "GetPerTcpConnectionEStats")]
    private static partial uint GetPerTcpConnectionEStats(
        ref MibTcpRow row,
        int estatsType,
        nint rw,
        uint rwVersion,
        uint rwSize,
        nint ros,
        uint rosVersion,
        uint rosSize,
        nint rod,
        uint rodVersion,
        uint rodSize);
}
