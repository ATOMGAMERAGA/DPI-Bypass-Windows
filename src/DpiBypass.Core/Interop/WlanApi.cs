using System.Runtime.InteropServices;
using System.Text;

namespace DpiBypass.Core.Interop;

/// <summary>
/// Currently associated Wi-Fi network, if any.
/// </summary>
/// <param name="Ssid">The network name.</param>
/// <param name="Bssid">The access point's MAC address.</param>
/// <param name="SignalQuality">
/// Windows' 0-100 signal quality, or -1 when the driver did not report one. Roaming to
/// another access point or simply walking away from this one changes the radio the
/// measurement runs over, so a latency comparison spanning such a change is not a
/// comparison of anything else.
/// </param>
/// <param name="RxRateKbps">Current receive PHY rate in kbit/s, 0 when unknown.</param>
/// <param name="TxRateKbps">Current transmit PHY rate in kbit/s, 0 when unknown.</param>
public readonly record struct WlanConnection(
    string Ssid,
    string Bssid,
    int SignalQuality = -1,
    uint RxRateKbps = 0,
    uint TxRateKbps = 0);

/// <summary>
/// Reads the connected SSID/BSSID straight from the Native Wi-Fi API. Shelling out
/// to <c>netsh</c> would be shorter, but its output is localised and this app is
/// built for Turkish Windows first.
/// </summary>
public static class WlanApi
{
    private const uint ClientVersion = 2;
    private const uint OpcodeCurrentConnection = 7;

    // Offsets inside WLAN_CONNECTION_ATTRIBUTES.
    private const int AssociationOffset = 4 + 4 + 512;
    private const int SsidLengthOffset = AssociationOffset;
    private const int SsidBytesOffset = AssociationOffset + 4;
    private const int BssidOffset = AssociationOffset + 36 + 4;

    // WLAN_ASSOCIATION_ATTRIBUTES continues after the six BSSID bytes: the MAC address
    // is followed by two bytes of padding before the next 4-byte field.
    private const int PhyTypeOffset = BssidOffset + 8;
    private const int SignalQualityOffset = PhyTypeOffset + 8;
    private const int RxRateOffset = SignalQualityOffset + 4;
    private const int TxRateOffset = RxRateOffset + 4;

    public static WlanConnection? TryGetCurrentConnection()
    {
        nint client = 0;
        nint interfaceList = 0;

        try
        {
            if (WlanOpenHandle(ClientVersion, 0, out _, out client) != 0)
            {
                return null;
            }

            if (WlanEnumInterfaces(client, 0, out interfaceList) != 0 || interfaceList == 0)
            {
                return null;
            }

            var count = Marshal.ReadInt32(interfaceList);
            for (var i = 0; i < count; i++)
            {
                // dwNumberOfItems + dwIndex, then fixed size WLAN_INTERFACE_INFO entries.
                var entry = interfaceList + 8 + (i * 532);
                var state = Marshal.ReadInt32(entry + 16 + 512);
                if (state != 1)
                {
                    continue; // not connected
                }

                var guidBytes = new byte[16];
                Marshal.Copy(entry, guidBytes, 0, 16);
                var guid = new Guid(guidBytes);

                var connection = QueryConnection(client, guid);
                if (connection is not null)
                {
                    return connection;
                }
            }

            return null;
        }
        catch (DllNotFoundException)
        {
            return null; // no WLAN service on this box (server SKUs, some VMs)
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (interfaceList != 0)
            {
                WlanFreeMemory(interfaceList);
            }

            if (client != 0)
            {
                WlanCloseHandle(client, 0);
            }
        }
    }

    private static WlanConnection? QueryConnection(nint client, Guid interfaceGuid)
    {
        nint data = 0;
        try
        {
            if (WlanQueryInterface(client, ref interfaceGuid, OpcodeCurrentConnection, 0, out var size, out data, 0) != 0
                || data == 0
                || size < BssidOffset + 6)
            {
                return null;
            }

            var ssidLength = Marshal.ReadInt32(data + SsidLengthOffset);
            if (ssidLength is < 0 or > 32)
            {
                return null;
            }

            var ssidBytes = new byte[ssidLength];
            Marshal.Copy(data + SsidBytesOffset, ssidBytes, 0, ssidLength);

            var bssid = new byte[6];
            Marshal.Copy(data + BssidOffset, bssid, 0, 6);

            // The radio fields sit past the BSSID. An older or unusual structure that
            // stops short is not an error; the connection itself is still valid, so the
            // rates are simply reported as unknown.
            var signalQuality = -1;
            uint rxRate = 0;
            uint txRate = 0;

            if (size >= TxRateOffset + 4)
            {
                var quality = Marshal.ReadInt32(data + SignalQualityOffset);
                signalQuality = quality is >= 0 and <= 100 ? quality : -1;
                rxRate = (uint)Marshal.ReadInt32(data + RxRateOffset);
                txRate = (uint)Marshal.ReadInt32(data + TxRateOffset);
            }

            return new WlanConnection(
                Encoding.UTF8.GetString(ssidBytes),
                string.Join(':', bssid.Select(b => b.ToString("x2"))),
                signalQuality,
                rxRate,
                txRate);
        }
        finally
        {
            if (data != 0)
            {
                WlanFreeMemory(data);
            }
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(nint clientHandle, nint reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        nint clientHandle,
        ref Guid interfaceGuid,
        uint opCode,
        nint reserved,
        out uint dataSize,
        out nint data,
        nint opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(nint memory);
}
