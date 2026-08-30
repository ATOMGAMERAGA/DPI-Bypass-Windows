using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

/// <summary>Which transport a measurement uses. Part of the RFC 2681 "Type-P".</summary>
public enum LatencyProtocol
{
    Icmp = 0,
    Tcp = 1,
    Udp = 2,
}

/// <summary>What the user asked to measure.</summary>
public enum LatencyTargetKind
{
    /// <summary>Public anycast resolvers. General internet health, never a game server.</summary>
    Reference = 0,

    /// <summary>A host or address the user typed, optionally with a port.</summary>
    Custom = 1,

    /// <summary>An endpoint discovered from a running process's own sockets.</summary>
    Application = 2,
}

/// <summary>
/// The target of a measurement, before it is resolved to an address.
/// </summary>
/// <remarks>
/// Resolution happens once per experiment and the result is pinned, because a name that
/// resolves to a different address between the two halves of an A/B pair turns the
/// comparison into a measurement of two different routes. RFC 2681 makes the same point
/// in the language of Type-P: "The value of Type-P-Round-trip-Delay could change if the
/// protocol (UDP or TCP), port number, size, or arrangement for special treatment
/// changes."
/// </remarks>
public sealed record LatencyTargetSpec
{
    public static readonly LatencyTargetSpec Reference = new() { Kind = LatencyTargetKind.Reference };

    public LatencyTargetKind Kind { get; init; } = LatencyTargetKind.Reference;

    /// <summary>Host name or literal address for <see cref="LatencyTargetKind.Custom"/>.</summary>
    public string? Host { get; init; }

    public int? Port { get; init; }

    /// <summary>Requested transport. ICMP means "measure the route to that address".</summary>
    public LatencyProtocol Protocol { get; init; } = LatencyProtocol.Icmp;

    /// <summary>Executable name (no path) for <see cref="LatencyTargetKind.Application"/>.</summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// A stable, non-identifying key for the profile cache.
    /// </summary>
    /// <remarks>
    /// The host and process name are hashed rather than stored: a saved result has to be
    /// invalidated when the target changes, which needs equality, not the value itself.
    /// </remarks>
    public string CacheKey
    {
        get
        {
            var seed = string.Join('|',
                Kind.ToString(),
                (Host ?? "-").ToLowerInvariant(),
                Port?.ToString(CultureInfo.InvariantCulture) ?? "-",
                Protocol.ToString(),
                (ProcessName ?? "-").ToLowerInvariant());

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..12].ToLowerInvariant();
        }
    }

    public string Describe() => Kind switch
    {
        LatencyTargetKind.Custom => Port is { } port
            ? $"{Host}:{port} ({Protocol.ToLabel()})"
            : $"{Host} ({Protocol.ToLabel()})",
        LatencyTargetKind.Application => $"{ProcessName} (çalışan uygulama)",
        _ => "Genel internet referansı — oyun sunucusu değildir",
    };

    /// <summary>
    /// Parses <c>host</c>, <c>host:port</c>, <c>tcp://host:port</c> or <c>udp://host:port</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately strict. The value ends up in a socket call and in a QoS match
    /// condition, so anything that is not clearly a host and a port is rejected here
    /// rather than being passed on and interpreted by something else.
    /// </remarks>
    public static bool TryParse(string? text, out LatencyTargetSpec spec, out string? error)
    {
        spec = Reference;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Hedef boş olamaz.";
            return false;
        }

        var value = text.Trim();
        var protocol = LatencyProtocol.Icmp;

        foreach (var (prefix, parsed) in new[]
        {
            ("tcp://", LatencyProtocol.Tcp),
            ("udp://", LatencyProtocol.Udp),
            ("icmp://", LatencyProtocol.Icmp),
        })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                protocol = parsed;
                value = value[prefix.Length..];
                break;
            }
        }

        int? port = null;

        // A bracketed IPv6 literal keeps its colons; everything else splits on the last one.
        if (value.StartsWith('[') && value.IndexOf(']') > 0)
        {
            var close = value.IndexOf(']');
            var address = value[1..close];
            var rest = value[(close + 1)..];

            if (rest.StartsWith(':') && !TryPort(rest[1..], out port, out error))
            {
                return false;
            }

            value = address;
        }
        else if (value.LastIndexOf(':') is var separator && separator > 0 && value.IndexOf(':') == separator)
        {
            if (!TryPort(value[(separator + 1)..], out port, out error))
            {
                return false;
            }

            value = value[..separator];
        }

        if (value.Length == 0 || value.Length > 253 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ':')))
        {
            error = "Geçerli bir ana bilgisayar adı veya IP adresi girin.";
            return false;
        }

        if (protocol != LatencyProtocol.Icmp && port is null)
        {
            error = $"{protocol.ToLabel()} ölçümü için bir port gerekir (örnek: {value}:25565).";
            return false;
        }

        // A bare host:port is a transport endpoint; TCP is the only one that can be
        // probed actively, so that is what the port is taken to mean.
        if (protocol == LatencyProtocol.Icmp && port is not null)
        {
            protocol = LatencyProtocol.Tcp;
        }

        spec = new LatencyTargetSpec
        {
            Kind = LatencyTargetKind.Custom,
            Host = value,
            Port = port,
            Protocol = protocol,
        };

        return true;

        static bool TryPort(string text, out int? port, out string? error)
        {
            port = null;
            error = null;

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed is > 0 and <= 65535)
            {
                port = parsed;
                return true;
            }

            error = "Port 1 ile 65535 arasında olmalıdır.";
            return false;
        }
    }
}

public static class LatencyProtocolExtensions
{
    public static string ToLabel(this LatencyProtocol protocol) => protocol switch
    {
        LatencyProtocol.Tcp => "TCP",
        LatencyProtocol.Udp => "UDP",
        _ => "ICMP",
    };
}

/// <summary>
/// One resolved, pinned measurement endpoint.
/// </summary>
/// <remarks>
/// Both halves of every A/B pair use the same instance of this. Nothing here is
/// re-resolved, re-selected or re-ordered once an experiment has started.
/// </remarks>
public sealed record LatencyEndpoint
{
    public required IPAddress Address { get; init; }

    public int? Port { get; init; }

    public required LatencyProtocol Protocol { get; init; }

    public LatencyTargetKind Kind { get; init; } = LatencyTargetKind.Reference;

    /// <summary>What the user should see. Never re-parsed by anything.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// True when the transport the application actually uses cannot be probed, so this
    /// measures the route to the same address by another means.
    /// </summary>
    /// <remarks>
    /// A UDP game's own round trip is inside a protocol we do not speak. Pinging the
    /// same address measures the path, which is useful and honest; calling the result
    /// "your game ping" would not be.
    /// </remarks>
    public bool RouteReferenceOnly { get; init; }

    /// <summary>The transport the application really uses, when it differs.</summary>
    public LatencyProtocol? ApplicationProtocol { get; init; }

    /// <summary>Type-P identity: two measurements are only comparable if these match.</summary>
    public string Key => $"{Address}|{Protocol}|{Port?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    public string ProtocolLabel => Protocol switch
    {
        LatencyProtocol.Tcp => Port is { } port ? $"TCP/{port}" : "TCP",
        LatencyProtocol.Udp => Port is { } udpPort ? $"UDP/{udpPort}" : "UDP",
        _ => "ICMP",
    };

    public static LatencyEndpoint Icmp(IPAddress address, string label, LatencyTargetKind kind = LatencyTargetKind.Reference) => new()
    {
        Address = address,
        Protocol = LatencyProtocol.Icmp,
        Kind = kind,
        Label = label,
    };
}

/// <summary>What target resolution produced, and anything the user must be told about it.</summary>
public sealed record LatencyTargetResolution
{
    public IReadOnlyList<LatencyEndpoint> Endpoints { get; init; } = [];

    /// <summary>Set when the result is usable but means less than it appears to.</summary>
    public string? Notice { get; init; }

    /// <summary>Set when nothing could be measured; <see cref="Endpoints"/> is then empty.</summary>
    public string? Failure { get; init; }

    public bool Succeeded => Endpoints.Count > 0;

    public static LatencyTargetResolution Failed(string reason) => new() { Failure = reason };
}

public interface ILatencyTargetResolver
{
    Task<LatencyTargetResolution> ResolveAsync(
        LatencyTargetSpec spec,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a target the user chose into fixed addresses, once per experiment.
/// </summary>
public sealed class LatencyTargetResolver : ILatencyTargetResolver
{
    /// <summary>
    /// Public anycast resolvers, used only as a general-internet reference.
    /// </summary>
    /// <remarks>
    /// These answer ICMP from almost everywhere and are close to almost everybody,
    /// which is what makes them a good health check and a bad stand-in for a game
    /// server: the route to them is not the route to the server, so a change measured
    /// here says nothing about a change there.
    /// </remarks>
    public static readonly IReadOnlyList<IPAddress> ReferenceAddresses =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("9.9.9.9"),
    ];

    public const string ReferenceLabel = "Genel internet referansı — oyun sunucusu değildir";

    private readonly IProcessEndpointProvider _endpoints;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Action<string>? _log;

    public LatencyTargetResolver(
        IProcessEndpointProvider? endpoints = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Action<string>? log = null)
    {
        _endpoints = endpoints ?? new WindowsProcessEndpointProvider(log);
        _resolve = resolve ?? ((host, token) => System.Net.Dns.GetHostAddressesAsync(host, token));
        _log = log;
    }

    public async Task<LatencyTargetResolution> ResolveAsync(
        LatencyTargetSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        return spec.Kind switch
        {
            LatencyTargetKind.Custom => await ResolveCustomAsync(spec, cancellationToken).ConfigureAwait(false),
            LatencyTargetKind.Application => ResolveApplication(spec),
            _ => new LatencyTargetResolution
            {
                Endpoints = [.. ReferenceAddresses.Select(address => LatencyEndpoint.Icmp(address, ReferenceLabel))],
                Notice = ReferenceLabel,
            },
        };
    }

    private async Task<LatencyTargetResolution> ResolveCustomAsync(
        LatencyTargetSpec spec,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.Host))
        {
            return LatencyTargetResolution.Failed("Ölçülecek bir ana bilgisayar belirtilmedi.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(spec.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolve(spec.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return LatencyTargetResolution.Failed($"'{spec.Host}' çözümlenemedi ({ex.Message}).");
            }
        }

        // One address, chosen now and kept. IPv4 first because the probe path and the
        // gateway comparison are both IPv4, and mixing families inside one experiment
        // would compare two different routes.
        var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();

        if (address is null)
        {
            return LatencyTargetResolution.Failed($"'{spec.Host}' için adres bulunamadı.");
        }

        var label = spec.Port is { } port ? $"{spec.Host}:{port}" : spec.Host;

        if (spec.Protocol == LatencyProtocol.Udp)
        {
            return new LatencyTargetResolution
            {
                Endpoints =
                [
                    new LatencyEndpoint
                    {
                        Address = address,
                        Port = spec.Port,
                        Protocol = LatencyProtocol.Icmp,
                        Kind = LatencyTargetKind.Custom,
                        Label = label,
                        RouteReferenceOnly = true,
                        ApplicationProtocol = LatencyProtocol.Udp,
                    },
                ],
                Notice = "UDP oturumunun kendi gidiş-dönüş süresi dışarıdan ölçülemez; "
                    + "aynı adrese ICMP ile yalnızca rota referansı ölçülür.",
            };
        }

        return new LatencyTargetResolution
        {
            Endpoints =
            [
                new LatencyEndpoint
                {
                    Address = address,
                    Port = spec.Protocol == LatencyProtocol.Tcp ? spec.Port : null,
                    Protocol = spec.Protocol,
                    Kind = LatencyTargetKind.Custom,
                    Label = label,
                },
            ],
        };
    }

    private LatencyTargetResolution ResolveApplication(LatencyTargetSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.ProcessName))
        {
            return LatencyTargetResolution.Failed("Ölçülecek bir uygulama seçilmedi.");
        }

        ProcessEndpointSet endpoints;
        try
        {
            endpoints = _endpoints.ForProcess(spec.ProcessName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"latency.target: uygulama uç noktaları okunamadı ({ex.Message}).");
            return LatencyTargetResolution.Failed("Uygulamanın bağlantıları okunamadı.");
        }

        if (!endpoints.ProcessFound)
        {
            return LatencyTargetResolution.Failed($"'{spec.ProcessName}' çalışmıyor.");
        }

        // Established TCP connections to a routable address: the control or game
        // channel of anything that uses TCP at all.
        var remote = endpoints.TcpRemoteEndpoints
            .GroupBy(endpoint => endpoint.Address, EqualityComparer<IPAddress>.Default)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();

        if (remote is null)
        {
            return endpoints.HasUdpSockets
                ? new LatencyTargetResolution
                {
                    Failure = $"'{spec.ProcessName}' yalnız UDP soketi kullanıyor. Windows, UDP soketinin uzak "
                        + "adresini bildirmez; sunucu adresini 'Özel hedef' alanına yazın.",
                }
                : LatencyTargetResolution.Failed($"'{spec.ProcessName}' için etkin bir uzak bağlantı bulunamadı.");
        }

        var port = remote.First().Port;

        return new LatencyTargetResolution
        {
            Endpoints =
            [
                new LatencyEndpoint
                {
                    Address = remote.Key,
                    Port = port,
                    Protocol = LatencyProtocol.Tcp,
                    Kind = LatencyTargetKind.Application,
                    Label = $"{spec.ProcessName} → {remote.Key}:{port}",
                },
            ],
            Notice = endpoints.HasUdpSockets
                ? "Uygulama ayrıca UDP kullanıyor; ölçülen değer TCP kanalının gidiş-dönüş süresidir."
                : null,
        };
    }
}
