using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>WinDivert 2.2 layers.</summary>
public enum WinDivertLayer
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4,
}

/// <summary>WinDivert 2.2 events.</summary>
public enum WinDivertEvent
{
    NetworkPacket = 0,
    FlowEstablished = 1,
    FlowDeleted = 2,
    SocketBind = 3,
    SocketConnect = 4,
    SocketListen = 5,
    SocketAccept = 6,
    SocketClose = 7,
    ReflectOpen = 8,
    ReflectClose = 9,
}

[Flags]
public enum WinDivertFlags : ulong
{
    None = 0,
    Sniff = 0x0001,
    Drop = 0x0002,
    RecvOnly = 0x0004,
    SendOnly = 0x0008,
    NoInstall = 0x0010,
    Fragments = 0x0020,
}

public enum WinDivertParam
{
    QueueLength = 0,
    QueueTime = 1,
    QueueSize = 2,
    VersionMajor = 3,
    VersionMinor = 4,
}

public enum WinDivertShutdown
{
    Recv = 1,
    Send = 2,
    Both = 3,
}

/// <summary>
/// WINDIVERT_ADDRESS (80 bytes). The bitfield word and the 64 byte payload union
/// are exposed through properties so callers never have to do the shifting.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct WinDivertAddress
{
    [FieldOffset(0)] public long Timestamp;

    /// <summary>Layer:8 | Event:8 | Sniffed:1 | Outbound:1 | Loopback:1 | Impostor:1 | IPv6:1 | IPChecksum:1 | TCPChecksum:1 | UDPChecksum:1 | Reserved:8</summary>
    [FieldOffset(8)] public uint Bits;

    [FieldOffset(12)] public uint Reserved;

    // --- WINDIVERT_DATA_NETWORK ---
    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;

    // --- WINDIVERT_DATA_SOCKET ---
    [FieldOffset(16)] public ulong Endpoint;
    [FieldOffset(24)] public ulong ParentEndpoint;
    [FieldOffset(32)] public uint ProcessId;
    [FieldOffset(36)] public uint LocalAddr0;
    [FieldOffset(40)] public uint LocalAddr1;
    [FieldOffset(44)] public uint LocalAddr2;
    [FieldOffset(48)] public uint LocalAddr3;
    [FieldOffset(52)] public uint RemoteAddr0;
    [FieldOffset(56)] public uint RemoteAddr1;
    [FieldOffset(60)] public uint RemoteAddr2;
    [FieldOffset(64)] public uint RemoteAddr3;
    [FieldOffset(68)] public ushort LocalPort;
    [FieldOffset(70)] public ushort RemotePort;
    [FieldOffset(72)] public byte Protocol;

    private const uint SniffedBit = 1u << 16;
    private const uint OutboundBit = 1u << 17;
    private const uint LoopbackBit = 1u << 18;
    private const uint ImpostorBit = 1u << 19;
    private const uint IPv6Bit = 1u << 20;
    private const uint IPChecksumBit = 1u << 21;
    private const uint TCPChecksumBit = 1u << 22;
    private const uint UDPChecksumBit = 1u << 23;

    public WinDivertLayer Layer
    {
        get => (WinDivertLayer)(Bits & 0xFF);
        set => Bits = (Bits & ~0xFFu) | ((uint)value & 0xFF);
    }

    public WinDivertEvent Event
    {
        get => (WinDivertEvent)((Bits >> 8) & 0xFF);
        set => Bits = (Bits & ~0xFF00u) | (((uint)value & 0xFF) << 8);
    }

    private bool GetBit(uint mask) => (Bits & mask) != 0;

    private void SetBit(uint mask, bool value)
    {
        if (value) Bits |= mask;
        else Bits &= ~mask;
    }

    public bool Sniffed { get => GetBit(SniffedBit); set => SetBit(SniffedBit, value); }

    public bool Outbound { get => GetBit(OutboundBit); set => SetBit(OutboundBit, value); }

    public bool Loopback { get => GetBit(LoopbackBit); set => SetBit(LoopbackBit, value); }

    /// <summary>True when WinDivert itself injected the packet - i.e. one of ours coming back around.</summary>
    public bool Impostor { get => GetBit(ImpostorBit); set => SetBit(ImpostorBit, value); }

    public bool IPv6 { get => GetBit(IPv6Bit); set => SetBit(IPv6Bit, value); }

    public bool IPChecksum { get => GetBit(IPChecksumBit); set => SetBit(IPChecksumBit, value); }

    public bool TCPChecksum { get => GetBit(TCPChecksumBit); set => SetBit(TCPChecksumBit, value); }

    public bool UDPChecksum { get => GetBit(UDPChecksumBit); set => SetBit(UDPChecksumBit, value); }
}

internal static partial class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    public const int InvalidHandle = -1;

    [LibraryImport(Dll, EntryPoint = "WinDivertOpen", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial nint Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags);

    [LibraryImport(Dll, EntryPoint = "WinDivertRecv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Recv(nint handle, ref byte packet, uint packetLen, out uint recvLen, ref WinDivertAddress addr);

    [LibraryImport(Dll, EntryPoint = "WinDivertSend", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Send(nint handle, ref byte packet, uint packetLen, out uint sendLen, ref WinDivertAddress addr);

    [LibraryImport(Dll, EntryPoint = "WinDivertShutdown", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Shutdown(nint handle, WinDivertShutdown how);

    [LibraryImport(Dll, EntryPoint = "WinDivertClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Close(nint handle);

    [LibraryImport(Dll, EntryPoint = "WinDivertSetParam", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetParam(nint handle, WinDivertParam param, ulong value);

    [LibraryImport(Dll, EntryPoint = "WinDivertGetParam", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetParam(nint handle, WinDivertParam param, out ulong value);

    [LibraryImport(Dll, EntryPoint = "WinDivertHelperCalcChecksums", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CalcChecksums(ref byte packet, uint packetLen, ref WinDivertAddress addr, ulong flags);

    [LibraryImport(Dll, EntryPoint = "WinDivertHelperCompileFilter", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CompileFilter(
        string filter,
        WinDivertLayer layer,
        nint obj,
        uint objLen,
        out nint errorStr,
        out uint errorPos);
}
