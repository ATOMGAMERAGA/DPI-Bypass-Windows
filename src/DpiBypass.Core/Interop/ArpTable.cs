using System.Net;
using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>
/// Resolves the default gateway's hardware address. Two networks can share an SSID
/// and a gateway IP (192.168.1.1 is not exactly rare), so the router's MAC is what
/// actually distinguishes them.
/// </summary>
public static class ArpTable
{
    public static string? TryGetMac(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return null;
        }

        try
        {
            var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
            var mac = new byte[6];
            var length = mac.Length;

            if (SendARP(destination, 0, mac, ref length) != 0 || length < 6)
            {
                return null;
            }

            return string.Join(':', mac.Take(6).Select(b => b.ToString("x2")));
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destinationIp, uint sourceIp, byte[] macAddress, ref int macAddressLength);
}
