using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DpiBypass.Core.Network;

namespace DpiBypass.Core.MobileHotspot;

/// <summary>Where the adapter's IPv4 address sits, as far as that can be told locally.</summary>
public enum HotspotAddressKind
{
    Unknown = 0,

    /// <summary>A public address on the adapter itself.</summary>
    Public = 1,

    /// <summary>RFC 1918: the ordinary router or phone hotspot case.</summary>
    Private = 2,

    /// <summary>RFC 6598 100.64/10 shared address space observed on this local adapter.</summary>
    SharedAddressSpace = 3,

    /// <summary>The adapter has IPv4 addresses from more than one address class.</summary>
    Mixed = 4,
}

/// <summary>What the diagnostics pass could establish. Nothing here is inferred.</summary>
public sealed record HotspotDiagnosticResult
{
    /// <summary>
    /// The only honest answer about a plan.
    /// </summary>
    /// <remarks>
    /// TTL, SSID, carrier name, APN and address range are all things an operator can set
    /// for any reason, so none of them establishes what a subscription includes. The app
    /// says it does not know rather than guessing at somebody's contract.
    /// </remarks>
    public const string PlanEntitlement = "Bilinmiyor";

    public required string NetworkName { get; init; }

    public required string AdapterName { get; init; }

    public required bool HasIpv4 { get; init; }

    public required bool HasIpv6 { get; init; }

    public required bool Ipv4Works { get; init; }

    public required bool Ipv6Works { get; init; }

    public required bool DnsWorks { get; init; }

    public double? MedianRttMs { get; init; }

    public double? P95RttMs { get; init; }

    public double? PacketLossPercent { get; init; }

    /// <summary>Largest ICMP payload that crossed the path unfragmented, when measured.</summary>
    public int? LargestUnfragmentedPayload { get; init; }

    public bool? MtuLooksReduced { get; init; }

    public required HotspotAddressKind AddressKind { get; init; }

    public required bool VpnAdapterActive { get; init; }

    /// <summary>Only set when Windows itself names the operator; otherwise null.</summary>
    public string? CarrierHint { get; init; }

    public IReadOnlyList<string> Findings { get; init; } = [];

    public string? Remediation { get; init; }

    public bool HasInternet => Ipv4Works || Ipv6Works;

    public string ToReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Ağ            : {NetworkName}");
        builder.AppendLine($"Bağdaştırıcı  : {AdapterName}");
        builder.AppendLine($"IPv4          : {Describe(HasIpv4, Ipv4Works)}");
        builder.AppendLine($"IPv6          : {Describe(HasIpv6, Ipv6Works)}");
        builder.AppendLine($"DNS           : {(DnsWorks ? "çalışıyor" : "ad çözülemiyor")}");
        builder.AppendLine($"Adres türü    : {DescribeAddress(AddressKind)}");
        builder.AppendLine($"VPN           : {(VpnAdapterActive
            ? "etkin olabilecek bir VPN/tünel bağdaştırıcısı saptandı (en iyi çaba)"
            : "etkin tünel saptanmadı (tespit en iyi çabadır)")}");
        builder.AppendLine($"Operatör      : {CarrierHint ?? "Bilinmiyor"}");
        builder.AppendLine($"Plan / hotspot hakkı: {PlanEntitlement}");

        if (MedianRttMs is { } median)
        {
            builder.AppendLine($"Gecikme       : median {median:F1} ms · p95 {P95RttMs:F1} ms · kayıp %{PacketLossPercent:F1}");
        }

        if (LargestUnfragmentedPayload is { } payload)
        {
            builder.AppendLine($"MTU           : ölçülen parçalanmasız üst sınır {payload + 28} bayt"
                + (MtuLooksReduced == true ? " (1500'ün altında)" : string.Empty));
        }

        if (Findings.Count > 0)
        {
            builder.AppendLine();
            foreach (var finding in Findings)
            {
                builder.AppendLine($"• {finding}");
            }
        }

        if (!string.IsNullOrWhiteSpace(Remediation))
        {
            builder.AppendLine();
            builder.AppendLine($"Öneri: {Remediation}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string Describe(bool configured, bool works) => (configured, works) switch
    {
        (false, _) => "adres yok",
        (true, true) => "çalışıyor",
        _ => "adres var ama trafik geçmiyor",
    };

    private static string DescribeAddress(HotspotAddressKind kind) => kind switch
    {
        HotspotAddressKind.Public => "genel IP",
        HotspotAddressKind.Private => "özel aralık (NAT arkasında)",
        HotspotAddressKind.SharedAddressSpace => "yerel bağdaştırıcıda paylaşılan adres alanı (100.64/10)",
        HotspotAddressKind.Mixed => "birden çok yerel IPv4 adres sınıfı",
        _ => "belirlenemedi",
    };
}

public interface IMobileHotspotDiagnostics
{
    Task<HotspotDiagnosticResult> RunAsync(NetworkFingerprint network, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only checks on a tethered or mobile connection.
/// </summary>
/// <remarks>
/// <para>
/// This is what replaced the TTL rewrite: it answers "is this connection working, and
/// if not, what is wrong with it" using ordinary reachability checks that any diagnostic
/// tool performs. It makes no persistent network change and disguises no traffic, but it
/// does send ordinary ICMP, DNS and connectivity probes to gather the reported facts.
/// </para>
/// <para>
/// It deliberately refuses to guess at a subscription. What an operator counts as
/// tethering, and what a plan includes, are contract questions that no packet on the
/// wire answers - so where the app does not know, it says so.
/// </para>
/// </remarks>
public sealed class MobileHotspotDiagnostics : IMobileHotspotDiagnostics
{
    /// <summary>1472 bytes of ICMP payload is exactly a 1500 byte Ethernet MTU.</summary>
    private const int EthernetProbePayload = 1472;

    /// <summary>Lowest payload included in the bounded path-MTU search (1228-byte MTU).</summary>
    private const int MinimumProbePayload = 1200;

    private static readonly string[] TunnelNameHints =
    [
        "wireguard",
        "wintun",
        "tap-windows",
        "openvpn",
        "nordlynx",
        "mullvad",
        "protonvpn",
        "tailscale",
        "zerotier",
        " vpn",
        "vpn ",
    ];

    private static readonly IPAddress Ipv4Target = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress Ipv6Target = IPAddress.Parse("2606:4700:4700::1111");
    private const string DnsProbeHost = "cloudflare-dns.com";

    private readonly ILatencyProbe _probe;
    private readonly Action<string>? _log;

    public MobileHotspotDiagnostics(ILatencyProbe? probe = null, Action<string>? log = null)
    {
        _probe = probe ?? new LatencyProbe();
        _log = log;
    }

    public async Task<HotspotDiagnosticResult> RunAsync(
        NetworkFingerprint network,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);

        _log?.Invoke($"hotspot.diagnostics.started: {network.DisplayName} ({network.Key})");

        var addresses = ReadAddresses(network);
        var latency = await MeasureAsync(network, cancellationToken).ConfigureAwait(false);
        var ipv6Works = addresses.HasIpv6 && await ReachesAsync(Ipv6Target, cancellationToken).ConfigureAwait(false);
        var dnsWorks = await ResolvesAsync(cancellationToken).ConfigureAwait(false);
        var mtu = await ProbeMtuAsync(cancellationToken).ConfigureAwait(false);

        var result = new HotspotDiagnosticResult
        {
            NetworkName = network.DisplayName,
            AdapterName = network.AdapterName ?? "-",
            HasIpv4 = addresses.HasIpv4,
            HasIpv6 = addresses.HasIpv6,
            Ipv4Works = latency?.HasRemoteConnectivity ?? false,
            Ipv6Works = ipv6Works,
            DnsWorks = dnsWorks,
            MedianRttMs = latency?.HasRemoteConnectivity == true ? latency.MedianRttMs : null,
            P95RttMs = latency?.HasRemoteConnectivity == true ? latency.P95RttMs : null,
            PacketLossPercent = latency?.PacketLossPercent,
            LargestUnfragmentedPayload = mtu.Largest,
            MtuLooksReduced = mtu.Reduced,
            AddressKind = addresses.Kind,
            VpnAdapterActive = HasActiveTunnel(network),
            CarrierHint = null,
        };

        result = result with { Findings = Findings(result), Remediation = Remediation(result) };
        _log?.Invoke($"hotspot.diagnostics.completed: internet={result.HasInternet} dns={result.DnsWorks} ipv6={result.Ipv6Works}");

        return result;
    }

    /// <summary>
    /// What the readings add up to, as plain statements a user can act on.
    /// </summary>
    /// <remarks>
    /// Internal so the wording and the thresholds can be pinned by tests without the
    /// network being involved: the I/O above gathers facts, this decides what they mean.
    /// </remarks>
    internal static IReadOnlyList<string> Findings(HotspotDiagnosticResult result)
    {
        var findings = new List<string>();

        if (!result.HasInternet)
        {
            findings.Add("Hiçbir IP sürümünde internet erişimi yok.");
        }
        else if (!result.Ipv4Works && result.HasIpv4)
        {
            findings.Add("IPv4 adresi var ama trafik geçmiyor.");
        }

        if (result.HasIpv6 && !result.Ipv6Works)
        {
            findings.Add("IPv6 adresi atanmış ama IPv6 trafiği geçmiyor; bu bağlantı IPv4 üzerinden çalışıyor.");
        }
        else if (!result.HasIpv6)
        {
            findings.Add("Bu ağda IPv6 adresi yok. Mobil paylaşımda olağandır ve tek başına bir sorun değildir.");
        }

        if (!result.DnsWorks)
        {
            findings.Add("Ad çözümleme başarısız; adresler açılıyorsa sorun DNS tarafındadır.");
        }

        if (result.MtuLooksReduced == true && result.LargestUnfragmentedPayload is { } payload)
        {
            findings.Add(
                $"1500 baytlık paketler geçmiyor; ikili aramada {payload + 28} baytlık yol MTU'su ölçüldü. "
                + "Bazı siteler yarım yüklenirse sebebi budur.");
        }

        if (result.AddressKind == HotspotAddressKind.SharedAddressSpace)
        {
            findings.Add(
                "Yerel bağdaştırıcı adresi 100.64/10 paylaşılan adres alanında. "
                + "Bu gözlem, telefonun veya operatörün yukarısındaki CGNAT'ı tek başına kanıtlamaz.");
        }
        else if (result.AddressKind == HotspotAddressKind.Mixed)
        {
            findings.Add("Bağdaştırıcıda birden çok IPv4 adres sınıfı var; tek bir NAT türü çıkarılamaz.");
        }

        if (result.VpnAdapterActive)
        {
            findings.Add("Etkin olabilecek bir VPN/tünel bağdaştırıcısı saptandı; ölçümler o yolu içerebilir.");
        }

        if (result.PacketLossPercent is > 2)
        {
            findings.Add($"Paket kaybı %{result.PacketLossPercent:F1}; sinyal veya hücre yükü kaynaklı olabilir.");
        }

        return findings;
    }

    internal static string? Remediation(HotspotDiagnosticResult result)
    {
        if (!result.HasInternet)
        {
            return "Telefonda paylaşımı kapatıp açın, ardından bilgisayarda bağdaştırıcıyı devre dışı bırakıp etkinleştirin.";
        }

        if (!result.DnsWorks)
        {
            return "DNS ayarlarını sistem varsayılanına alın veya şifreli DNS'i kapatıp yeniden deneyin.";
        }

        if (result.MtuLooksReduced == true)
        {
            return "Yalnızca yarım yüklenen sayfa veya takılan büyük aktarım belirtisi varsa, "
                + "bağdaştırıcı MTU'sunu ölçülen sınıra yakın bir değerle deneyip yeniden doğrulayın; "
                + "bu tarama tek başına kalıcı ayar değişikliği gerektirmez.";
        }

        return null;
    }

    private async Task<LatencyMeasurement?> MeasureAsync(NetworkFingerprint network, CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.MeasureAsync(network, LatencyProbeRequest.Survey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"hotspot.diagnostics: gecikme ölçülemedi ({ex.Message}).");
            return null;
        }
    }

    private static AddressSummary ReadAddresses(NetworkFingerprint network)
    {
        var summary = new AddressSummary();
        var ipv4Addresses = new List<IPAddress>();

        if (string.IsNullOrWhiteSpace(network.AdapterId))
        {
            return summary;
        }

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(adapter.Id, network.AdapterId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    switch (address.Address.AddressFamily)
                    {
                        case AddressFamily.InterNetwork:
                            summary.HasIpv4 = true;
                            ipv4Addresses.Add(address.Address);
                            break;

                        // Link local answers nothing beyond this cable, so it does not
                        // count as the adapter having IPv6.
                        case AddressFamily.InterNetworkV6 when !address.Address.IsIPv6LinkLocal:
                            summary.HasIpv6 = true;
                            break;
                    }
                }

                summary.Kind = SummarizeAddressKinds(ipv4Addresses);

                break;
            }
        }
        catch (NetworkInformationException)
        {
            // The adapter can be pulled between enumerating and reading it. What is
            // already known is still reported.
        }

        return summary;
    }

    internal static HotspotAddressKind Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return HotspotAddressKind.Unknown;
        }

        var octets = address.GetAddressBytes();

        // RFC 6598 100.64.0.0/10.
        if (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127)
        {
            return HotspotAddressKind.SharedAddressSpace;
        }

        var isPrivate = octets[0] == 10
            || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 169 && octets[1] == 254);

        return isPrivate ? HotspotAddressKind.Private : HotspotAddressKind.Public;
    }

    internal static HotspotAddressKind SummarizeAddressKinds(IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var kinds = addresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address)
                && !address.Equals(IPAddress.Any))
            .Select(Classify)
            .Where(kind => kind != HotspotAddressKind.Unknown)
            .Distinct()
            .Take(2)
            .ToArray();

        return kinds.Length switch
        {
            0 => HotspotAddressKind.Unknown,
            1 => kinds[0],
            _ => HotspotAddressKind.Mixed,
        };
    }

    private static bool HasActiveTunnel(NetworkFingerprint network)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            {
                if (string.Equals(adapter.Id, network.AdapterId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var hasUsableAddress = false;
                try
                {
                    hasUsableAddress = adapter.GetIPProperties().UnicastAddresses.Any(address =>
                        !IPAddress.IsLoopback(address.Address)
                        && !address.Address.Equals(IPAddress.Any)
                        && !address.Address.Equals(IPAddress.IPv6Any)
                        && !address.Address.IsIPv6LinkLocal);
                }
                catch (NetworkInformationException)
                {
                    // The adapter can disappear while being enumerated.
                }

                return LooksLikeActiveTunnel(
                    adapter.OperationalStatus,
                    adapter.NetworkInterfaceType,
                    adapter.Name,
                    adapter.Description,
                    hasUsableAddress);
            });
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    internal static bool LooksLikeActiveTunnel(
        OperationalStatus status,
        NetworkInterfaceType type,
        string? name,
        string? description,
        bool hasUsableAddress)
    {
        if (status != OperationalStatus.Up)
        {
            return false;
        }

        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
        {
            return true;
        }

        if (!hasUsableAddress)
        {
            return false;
        }

        var identity = $" {name} {description} ".ToLowerInvariant();
        return TunnelNameHints.Any(identity.Contains);
    }

    private async Task<bool> ResolvesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await System.Net.Dns
                .GetHostAddressesAsync(DnsProbeHost, cancellationToken)
                .ConfigureAwait(false);

            return addresses.Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> ReachesAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, 1200).WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Bounded don't-fragment search for the largest successful IPv4 payload.
    /// </summary>
    /// <remarks>
    /// A lower successful probe is only a lower bound. Once a full-size probe returns an
    /// explicit PacketTooBig result, binary search finds the boundary instead of calling
    /// a 1400-byte success the largest possible MTU. Any timeout/filtering result makes
    /// the diagnosis inconclusive rather than inventing a boundary.
    /// </remarks>
    private async Task<(int? Largest, bool? Reduced)> ProbeMtuAsync(CancellationToken cancellationToken)
        => await FindLargestUnfragmentedPayloadAsync(PassesUnfragmentedAsync, cancellationToken).ConfigureAwait(false);

    internal static async Task<(int? Largest, bool? Reduced)> FindLargestUnfragmentedPayloadAsync(
        Func<int, CancellationToken, Task<bool?>> probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var full = await probe(EthernetProbePayload, cancellationToken).ConfigureAwait(false);
        if (full == true)
        {
            return (EthernetProbePayload, false);
        }

        // A timeout or filtered echo says nothing about MTU. Search only after the path
        // explicitly reported that the full payload was too large.
        if (full != false)
        {
            return (null, null);
        }

        var minimum = await probe(MinimumProbePayload, cancellationToken).ConfigureAwait(false);
        if (minimum != true)
        {
            return (null, null);
        }

        var largest = MinimumProbePayload;
        var upper = EthernetProbePayload - 1;

        while (largest < upper)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = largest + ((upper - largest + 1) / 2);
            var result = await probe(payload, cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                return (null, null);
            }

            if (result == true)
            {
                largest = payload;
            }
            else
            {
                upper = payload - 1;
            }
        }

        return (largest, true);
    }

    private async Task<bool?> PassesUnfragmentedAsync(int payloadBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(Ipv4Target, 1500, new byte[payloadBytes], new PingOptions { DontFragment = true })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return reply.Status switch
            {
                IPStatus.Success => true,
                IPStatus.PacketTooBig => false,
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Null, not false: a probe that could not be sent says nothing about the
            // path's MTU, and reporting "1500 does not fit" from a failure to ask would
            // send the user off changing an adapter setting for no reason.
            _log?.Invoke($"hotspot.diagnostics: {payloadBytes} baytlık MTU denemesi yapılamadı ({ex.Message}).");
            return null;
        }
    }

    private sealed class AddressSummary
    {
        public bool HasIpv4 { get; set; }

        public bool HasIpv6 { get; set; }

        public HotspotAddressKind Kind { get; set; } = HotspotAddressKind.Unknown;
    }
}
