using System.ComponentModel;
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
/// <para>
/// The structure below is passed to the API by size, so its layout is part of the
/// contract rather than a convenience. It is checked against
/// <see cref="PathRodSize"/> and <see cref="SmoothedRttOffset"/> by the tests, because a
/// struct that is short by even one field makes every call fail with
/// <c>ERROR_INSUFFICIENT_BUFFER</c> - silently, if the failure is treated as "no data".
/// </para>
/// </remarks>
public static partial class TcpEStats
{
    private const int NoError = 0;
    private const int TcpConnectionEstatsPath = 3;
    private const int MibTcpStateEstablished = 5;

    /// <summary>
    /// The documented size of <c>TCP_ESTATS_PATH_ROD_v0</c>: forty <c>ULONG</c> fields.
    /// </summary>
    /// <remarks>
    /// Named rather than derived so a test can assert the managed struct against the SDK
    /// number instead of against itself. A build where these disagree cannot read a
    /// single sample, so it is worth failing loudly at test time.
    /// </remarks>
    public const int PathRodSize = 160;

    /// <summary>Byte offset of <c>SmoothedRtt</c> inside that structure.</summary>
    public const int SmoothedRttOffset = 108;

    /// <summary>Byte offset of <c>CountRtt</c>, the counter that says a sample is new.</summary>
    public const int CountRttOffset = 128;

    /// <summary><c>TCP_ESTATS_PATH_RW_v0</c> is a single <c>BOOLEAN</c>.</summary>
    public const int PathRwSize = 1;

    /// <summary>One reading of the stack's own estimate for one connection.</summary>
    /// <remarks>
    /// <see cref="CountRtt"/> and <see cref="SumRtt"/> are carried alongside the smoothed
    /// estimate because they answer a question the estimate cannot: whether the stack has
    /// taken a new measurement since the last read. <c>SmoothedRtt</c> is a filtered value
    /// that can repeat across genuinely new samples and can also sit unchanged for many
    /// reads, so treating "the number moved" as "a packet came back" both invents samples
    /// and loses them.
    /// </remarks>
    public sealed record PathSample(
        double SmoothedRttMs,
        double SampleRttMs,
        double RttVarianceMs,
        uint CountRtt,
        uint SumRtt)
    {
        /// <summary>Whether the stack has actually produced an estimate yet.</summary>
        public bool IsUsable => SmoothedRttMs is > 0 and < 60_000;

        /// <summary>The stack's own mean of the raw per-packet RTTs, when it has one.</summary>
        /// <remarks>
        /// Deliberately not mixed into the smoothed series. <c>SumRtt</c>/<c>CountRtt</c>
        /// is the arithmetic mean of the raw samples; <c>SmoothedRtt</c> is Jacobson's
        /// filtered estimator. They answer different questions and their distributions
        /// are not the same, so the report shows whichever it says it is showing.
        /// </remarks>
        public double? MeanRawRttMs => CountRtt > 0 ? SumRtt / (double)CountRtt : null;
    }

    /// <summary>Why a read produced nothing, so the report can say which it was.</summary>
    public enum PathReadStatus
    {
        /// <summary>A usable sample came back.</summary>
        Ok = 0,

        /// <summary>This build or this OS cannot use extended statistics at all.</summary>
        Unsupported = 1,

        /// <summary>The API refused; <see cref="PathRead.NativeError"/> says how.</summary>
        CallFailed = 2,

        /// <summary>The call succeeded but the stack reports collection as off.</summary>
        CollectionDisabled = 3,

        /// <summary>Collection is on and the stack has not measured this connection yet.</summary>
        NoEstimateYet = 4,
    }

    /// <summary>The outcome of one read, with the native error when there was one.</summary>
    /// <remarks>
    /// The error code is carried rather than swallowed because the two failures that
    /// actually happen here are distinguishable and mean different things to whoever is
    /// reading a support log: <c>ERROR_INSUFFICIENT_BUFFER</c> (122) is this application
    /// getting the structure wrong, and <c>ERROR_ACCESS_DENIED</c> (5) is the process not
    /// being elevated.
    /// </remarks>
    public readonly record struct PathRead(PathReadStatus Status, PathSample? Sample, uint NativeError = 0)
    {
        public static readonly PathRead Unsupported = new(PathReadStatus.Unsupported, null);

        public bool HasSample => Sample is not null;

        /// <summary>A short phrase naming the reason, for the diagnostics report.</summary>
        public string Describe() => Status switch
        {
            PathReadStatus.Ok => "TCP EStats örneği okundu",
            PathReadStatus.Unsupported => "bu sistemde TCP EStats kullanılamıyor",
            PathReadStatus.CollectionDisabled => "TCP EStats toplama açık değil",
            PathReadStatus.NoEstimateYet => "TCP yığını bu bağlantı için henüz RTT ölçmedi",
            _ => $"TCP EStats çağrısı başarısız (Windows hata kodu {NativeError}: {ErrorText(NativeError)})",
        };

        private static string ErrorText(uint error)
        {
            try
            {
                return new Win32Exception((int)error).Message;
            }
            catch (Exception)
            {
                return "açıklama alınamadı";
            }
        }
    }

    /// <summary>The outcome of turning collection on, with the native error when it failed.</summary>
    public readonly record struct EnableResult(bool Enabled, uint NativeError = 0)
    {
        public string? Failure => Enabled
            ? null
            : $"TCP EStats toplaması açılamadı (Windows hata kodu {NativeError}). "
                + "Bu genelde yönetici hakkı olmadığında görülür.";
    }

    /// <summary>Whether this build can use extended statistics at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Turns on path statistics for one established IPv4 connection.
    /// </summary>
    /// <returns>False when the stack refused, which is never treated as a measurement.</returns>
    public static bool TryEnable(IPEndPoint local, IPEndPoint remote) => Enable(local, remote).Enabled;

    /// <summary>The same, with the native error kept for the diagnostics report.</summary>
    public static EnableResult Enable(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (!IsSupported)
        {
            return new EnableResult(false);
        }

        var row = BuildRow(local, remote);
        var rw = new TcpEstatsPathRwV0 { EnableCollection = 1 };
        var size = Marshal.SizeOf<TcpEstatsPathRwV0>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(rw, buffer, fDeleteOld: false);
            var status = SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsPath, buffer, 0, (uint)size, 0);
            return new EnableResult(status == NoError, status);
        }
        catch (DllNotFoundException)
        {
            return new EnableResult(false);
        }
        catch (EntryPointNotFoundException)
        {
            return new EnableResult(false);
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
    public static PathSample? TryRead(IPEndPoint local, IPEndPoint remote) => Read(local, remote).Sample;

    /// <summary>The same read, with the reason it produced nothing when it did.</summary>
    public static PathRead Read(IPEndPoint local, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);

        if (!IsSupported)
        {
            return PathRead.Unsupported;
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
                return new PathRead(PathReadStatus.CallFailed, null, status);
            }

            var rw = Marshal.PtrToStructure<TcpEstatsPathRwV0>(rwBuffer);
            if (rw.EnableCollection == 0)
            {
                return new PathRead(PathReadStatus.CollectionDisabled, null);
            }

            var rod = Marshal.PtrToStructure<TcpEstatsPathRodV0>(rodBuffer);
            var sample = new PathSample(rod.SmoothedRtt, rod.SampleRtt, rod.RttVar, rod.CountRtt, rod.SumRtt);
            return sample.IsUsable
                ? new PathRead(PathReadStatus.Ok, sample)
                : new PathRead(PathReadStatus.NoEstimateYet, null);
        }
        catch (DllNotFoundException)
        {
            return PathRead.Unsupported;
        }
        catch (EntryPointNotFoundException)
        {
            return PathRead.Unsupported;
        }
        finally
        {
            Marshal.FreeHGlobal(rwBuffer);
            Marshal.FreeHGlobal(rodBuffer);
        }
    }

    /// <summary>The marshalled size of the read-only dynamic structure, for the tests.</summary>
    internal static int MarshalledPathRodSize => Marshal.SizeOf<TcpEstatsPathRodV0>();

    /// <summary>The marshalled size of the read-write structure, for the tests.</summary>
    internal static int MarshalledPathRwSize => Marshal.SizeOf<TcpEstatsPathRwV0>();

    /// <summary>The offset of one field of the dynamic structure, for the tests.</summary>
    internal static int PathRodOffsetOf(string field) => (int)Marshal.OffsetOf<TcpEstatsPathRodV0>(field);

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
    /// TCP_ESTATS_PATH_ROD_v0, in the SDK's own field order.
    /// </summary>
    /// <remarks>
    /// Only the RTT fields are read, but every one of the forty <c>ULONG</c>s has to be
    /// declared because the API is given the structure size and validates it. An earlier
    /// version of this declaration collapsed <c>CurMss</c>, <c>MaxMss</c> and
    /// <c>MinMss</c> into a single <c>Mss</c>, which made the struct 152 bytes instead of
    /// 160 - so every call returned <c>ERROR_INSUFFICIENT_BUFFER</c> and the probe fell
    /// through to timing TCP handshakes without ever saying why.
    /// </remarks>
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
        public uint CurMss;
        public uint MaxMss;
        public uint MinMss;
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
