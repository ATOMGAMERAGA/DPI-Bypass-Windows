using System.Collections.Frozen;
using System.Net.NetworkInformation;

namespace DpiBypass.Core.Net;

/// <summary>
/// Recognises the virtual adapters that game boosters (ExitLag, GearUP, NoPing,
/// WTFast, LagoFast…) and VPN clients put on the machine.
/// </summary>
/// <remarks>
/// <para>
/// A packet that leaves through one of these is wrapped in the booster's own tunnel
/// before it reaches the ISP, so the operator's inspector never sees its ClientHello
/// and there is nothing to bypass. Desyncing it anyway is actively harmful: the decoy
/// segments are tuned against the hop count of the real line, but inside the tunnel
/// the server may be only two or three hops past the booster's node, so a "low TTL"
/// fake ClientHello reaches Discord and the connection is torn down. That is how
/// "Discord only + GearUP" broke.
/// </para>
/// <para>
/// The adapter's DNS settings belong to the booster for the same reason, so the DNS
/// configurator leaves them alone too.
/// </para>
/// <para>
/// PPP is deliberately not on the list: a PPPoE connection dialled on the PC itself is
/// the real line, and the inspector sits right behind it. Hyper-V's vEthernet is not
/// either - with an external switch it carries the machine's ordinary traffic.
/// </para>
/// </remarks>
public static class TunnelAdapters
{
    /// <summary>IF_TYPE_PROP_VIRTUAL: what Wintun and most booster drivers report.</summary>
    private const int PropVirtual = 53;

    /// <summary>Lower-case substrings of adapter names or descriptions that mean "tunnel".</summary>
    internal static readonly string[] NameHints =
    [
        // Game boosters.
        "exitlag",
        "gearup",
        "gear up",
        "gearupbooster",
        "noping",
        "wtfast",
        "lagofast",
        "mudfish",
        "haste",
        "outfox",
        "booster",

        // Tunnel drivers and VPN clients.
        "wintun",
        "tap-windows",
        "tap-win32",
        "tap adapter",
        "wireguard",
        "openvpn",
        "nordlynx",
        "mullvad",
        "protonvpn",
        "tailscale",
        "zerotier",
        "radmin vpn",
        "hamachi",
        "softether",
        "cloudflare warp",
        " vpn",
        "vpn ",
    ];

    /// <summary>True when the adapter described looks like a booster or VPN tunnel.</summary>
    public static bool IsTunnel(string? name, string? description, NetworkInterfaceType type)
    {
        if ((int)type == PropVirtual)
        {
            return true;
        }

        return MatchesHint(name) || MatchesHint(description);
    }

    /// <summary>Safe on a fake or half-initialised interface: anything unreadable is "not a tunnel".</summary>
    public static bool IsTunnel(NetworkInterface nic)
    {
        string? name = null;
        string? description = null;
        var type = NetworkInterfaceType.Unknown;

        try { name = nic.Name; } catch (Exception) { }
        try { description = nic.Description; } catch (Exception) { }
        try { type = nic.NetworkInterfaceType; } catch (Exception) { }

        return IsTunnel(name, description, type);
    }

    private static bool MatchesHint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Padded so " vpn" / "vpn " also match at either end of the name.
        var padded = " " + text.ToLowerInvariant() + " ";
        foreach (var hint in NameHints)
        {
            if (padded.Contains(hint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The set of interface indexes that currently belong to tunnel adapters, kept fresh
/// enough for the packet path to ask about every handshake without enumerating
/// adapters on it.
/// </summary>
/// <remarks>
/// Boosters create and remove their adapter when the user switches them on and off,
/// so the snapshot is refreshed in the background whenever Windows reports an address
/// change, and at most every few seconds when an index the snapshot has never seen
/// turns up. The packet path only ever reads a frozen set.
/// </remarks>
public sealed class TunnelInterfaceSet : IDisposable
{
    private static readonly TimeSpan UnknownIndexRefresh = TimeSpan.FromSeconds(3);

    private readonly Func<IEnumerable<NetworkInterface>> _interfaces;
    private readonly bool _watchChanges;
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private int _refreshing;
    private long _lastRefreshTicks;
    private bool _disposed;

    public TunnelInterfaceSet(Func<IEnumerable<NetworkInterface>>? interfaces = null, bool watchChanges = true)
    {
        _interfaces = interfaces ?? NetworkInterface.GetAllNetworkInterfaces;
        _watchChanges = watchChanges && interfaces is null;

        Refresh();

        if (_watchChanges)
        {
            try
            {
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            }
            catch (Exception)
            {
                // No change notifications: the unknown-index refresh still catches up.
            }
        }
    }

    /// <summary>True when <paramref name="interfaceIndex"/> is a booster or VPN adapter.</summary>
    public bool IsTunnel(uint interfaceIndex)
    {
        if (interfaceIndex == 0)
        {
            return false;
        }

        var snapshot = _snapshot;
        if (snapshot.Tunnels.Contains(interfaceIndex))
        {
            return true;
        }

        if (!snapshot.Known.Contains(interfaceIndex))
        {
            // A new adapter, most likely one a booster has just brought up. Answer from
            // what is known now and catch up off the packet path.
            var last = Interlocked.Read(ref _lastRefreshTicks);
            if (DateTime.UtcNow.Ticks - last >= UnknownIndexRefresh.Ticks)
            {
                RefreshInBackground();
            }
        }

        return false;
    }

    /// <summary>How many tunnel indexes the current snapshot holds. For the log.</summary>
    public int TunnelCount => _snapshot.Tunnels.Count;

    /// <summary>Rebuilds the snapshot now.</summary>
    public void Refresh()
    {
        Interlocked.Exchange(ref _lastRefreshTicks, DateTime.UtcNow.Ticks);

        var known = new HashSet<uint>();
        var tunnels = new HashSet<uint>();

        IEnumerable<NetworkInterface> interfaces;
        try
        {
            interfaces = _interfaces();
        }
        catch (Exception)
        {
            return;
        }

        foreach (var nic in interfaces)
        {
            var indexes = IndexesOf(nic);
            if (indexes.Count == 0)
            {
                continue;
            }

            var tunnel = TunnelAdapters.IsTunnel(nic);
            foreach (var index in indexes)
            {
                known.Add(index);
                if (tunnel)
                {
                    tunnels.Add(index);
                }
            }
        }

        _snapshot = new Snapshot(known.ToFrozenSet(), tunnels.ToFrozenSet());
    }

    private static List<uint> IndexesOf(NetworkInterface nic)
    {
        var indexes = new List<uint>(2);
        IPInterfaceProperties properties;
        try
        {
            properties = nic.GetIPProperties();
        }
        catch (Exception)
        {
            return indexes;
        }

        try
        {
            if (properties.GetIPv4Properties() is { Index: > 0 } v4)
            {
                indexes.Add((uint)v4.Index);
            }
        }
        catch (Exception)
        {
            // No IPv4 on this adapter.
        }

        try
        {
            if (properties.GetIPv6Properties() is { Index: > 0 } v6 && !indexes.Contains((uint)v6.Index))
            {
                indexes.Add((uint)v6.Index);
            }
        }
        catch (Exception)
        {
            // No IPv6 on this adapter.
        }

        return indexes;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => RefreshInBackground();

    private void RefreshInBackground()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        Interlocked.Exchange(ref _lastRefreshTicks, DateTime.UtcNow.Ticks);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                Refresh();
            }
            catch (Exception)
            {
                // Keep the previous snapshot.
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        }, null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watchChanges)
        {
            try
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            }
            catch (Exception)
            {
                // Nothing to undo.
            }
        }
    }

    private sealed record Snapshot(FrozenSet<uint> Known, FrozenSet<uint> Tunnels)
    {
        public static readonly Snapshot Empty = new(FrozenSet<uint>.Empty, FrozenSet<uint>.Empty);
    }
}
