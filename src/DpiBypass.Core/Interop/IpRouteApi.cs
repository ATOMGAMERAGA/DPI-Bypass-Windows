using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>Resolves the Windows interface that the IP stack will use for one destination.</summary>
public static partial class IpRouteApi
{
    private const int RouteBufferSize = 256;
    private const int InterfaceIndexOffset = 8;

    public static int TryGetInterfaceIndex(IPAddress destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var address = SockaddrInet.From(destination);
        var route = Marshal.AllocHGlobal(RouteBufferSize);

        try
        {
            Marshal.Copy(new byte[RouteBufferSize], 0, route, RouteBufferSize);
            var status = GetBestRoute2(0, 0, 0, ref address, 0, route, out _);
            if (status == 0)
            {
                return Marshal.ReadInt32(route, InterfaceIndexOffset);
            }

            return GetBestInterfaceEx(ref address, out var index) == 0 ? checked((int)index) : 0;
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(route);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private unsafe struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Ipv4Address;
        [FieldOffset(8)] public fixed byte Ipv6Address[16];
        [FieldOffset(24)] public uint ScopeId;

        public static SockaddrInet From(IPAddress address)
        {
            var result = new SockaddrInet
            {
                Family = (ushort)address.AddressFamily,
                ScopeId = address.AddressFamily == AddressFamily.InterNetworkV6
                    ? checked((uint)address.ScopeId)
                    : 0,
            };

            var bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                result.Ipv4Address = BitConverter.ToUInt32(bytes);
                return result;
            }

            for (var index = 0; index < bytes.Length; index++)
            {
                result.Ipv6Address[index] = bytes[index];
            }

            return result;
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestRoute2(
        nint interfaceLuid,
        uint interfaceIndex,
        nint sourceAddress,
        ref SockaddrInet destinationAddress,
        uint addressSortOptions,
        nint bestRoute,
        out SockaddrInet bestSourceAddress);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetBestInterfaceEx(ref SockaddrInet destinationAddress, out uint bestIfIndex);
}
