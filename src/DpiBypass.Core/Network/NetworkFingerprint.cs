using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

/// <summary>
/// A stable identity for "the network we are on".
/// </summary>
/// <remarks>
/// Renaming a hotspot from <c>atom</c> to <c>atoms hotspot</c> produces a different
/// key, which is exactly what should happen: it may well be a different upstream
/// with a different filter, so the tuner has to run again rather than trust a
/// cached answer.
/// </remarks>
public sealed record NetworkFingerprint
{
    public string? Ssid { get; init; }

    public string? Bssid { get; init; }

    public string? AdapterId { get; init; }

    public string? AdapterName { get; init; }

    public string? GatewayAddress { get; init; }

    public string? GatewayMac { get; init; }

    public NetworkInterfaceType AdapterType { get; init; } = NetworkInterfaceType.Unknown;

    /// <summary>
    /// The kernel's index for this adapter, which is what a packet filter can be
    /// scoped to. Deliberately not part of <see cref="Key"/>: Windows may hand out a
    /// different index after a reboot, and that must not invalidate a saved profile.
    /// </summary>
    public int InterfaceIndex { get; init; }

    public bool IsWireless => AdapterType is NetworkInterfaceType.Wireless80211;

    public bool IsMobileBroadband => AdapterType is NetworkInterfaceType.Wman
        or NetworkInterfaceType.Wwanpp
        or NetworkInterfaceType.Wwanpp2;

    /// <summary>Short, stable key used to look up a saved profile.</summary>
    public string Key
    {
        get
        {
            var seed = string.Join('|',
                Ssid ?? "-",
                Bssid ?? "-",
                GatewayMac ?? GatewayAddress ?? "-",
                AdapterType.ToString());

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
    }

    /// <summary>What to show the user.</summary>
    public string DisplayName => Ssid
        ?? (IsMobileBroadband ? "Mobil veri" : null)
        ?? AdapterName
        ?? "Bilinmeyen ağ";

    public static NetworkFingerprint Capture()
    {
        var wlan = WlanApi.TryGetCurrentConnection();
        var adapter = PickPrimaryAdapter();

        if (adapter is null)
        {
            return new NetworkFingerprint { Ssid = wlan?.Ssid, Bssid = wlan?.Bssid };
        }

        var properties = adapter.GetIPProperties();
        var gateway = properties.GatewayAddresses
            .FirstOrDefault(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !g.Address.Equals(IPAddress.Any))
            ?.Address;

        return new NetworkFingerprint
        {
            Ssid = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? wlan?.Ssid : null,
            Bssid = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? wlan?.Bssid : null,
            AdapterId = adapter.Id,
            AdapterName = adapter.Name,
            AdapterType = adapter.NetworkInterfaceType,
            InterfaceIndex = ReadInterfaceIndex(properties),
            GatewayAddress = gateway?.ToString(),
            GatewayMac = gateway is null ? null : ArpTable.TryGetMac(gateway),
        };
    }

    private static int ReadInterfaceIndex(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties().Index;
        }
        catch (Exception)
        {
            // IPv4 may not be bound at all on this adapter.
        }

        try
        {
            return properties.GetIPv6Properties().Index;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>True when this looks like a phone sharing its mobile connection.</summary>
    public bool LooksLikeTethering => IsMobileBroadband
        || AdapterType == NetworkInterfaceType.Wireless80211
        || AdapterType == NetworkInterfaceType.Ethernet;

    public bool IsOnline => InterfaceIndex > 0 || GatewayAddress is not null;

    /// <summary>
    /// The adapter carrying traffic: up, not loopback, has a gateway. Preferring the
    /// one with the most received bytes handles machines with several live adapters.
    /// </summary>
    private static NetworkInterface? PickPrimaryAdapter()
    {
        NetworkInterface? best = null;
        long bestBytes = -1;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (Exception)
            {
                continue;
            }

            if (!properties.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
            {
                continue;
            }

            long bytes;
            try
            {
                bytes = nic.GetIPv4Statistics().BytesReceived;
            }
            catch (Exception)
            {
                bytes = 0;
            }

            if (bytes > bestBytes)
            {
                bestBytes = bytes;
                best = nic;
            }
        }

        return best;
    }

    public AccessKind GuessAccessKind()
    {
        if (IsMobileBroadband)
        {
            return AccessKind.Mobile;
        }

        if (Ssid is not null)
        {
            foreach (var profile in IspCatalog.All)
            {
                if (profile.Kind == AccessKind.Hotspot
                    && profile.SsidHints.Any(hint => Ssid.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                {
                    return AccessKind.Hotspot;
                }
            }
        }

        return AdapterType == NetworkInterfaceType.Ethernet || IsWireless ? AccessKind.Fixed : AccessKind.Unknown;
    }
}
