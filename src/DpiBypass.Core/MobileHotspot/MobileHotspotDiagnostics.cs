using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DpiBypass.Core.Dns;
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

    /// <summary>
    /// The network this ran on, so a result cannot be shown under a different one.
    /// </summary>
    /// <remarks>
    /// The display name is not enough: two networks can share one, and moving between
    /// them is exactly when a user is looking at this card. The key is the same hash the
    /// rest of the app uses to identify a network.
    /// </remarks>
    public string NetworkKey { get; init; } = string.Empty;

    /// <summary>When the checks finished, so the card can say how fresh they are.</summary>
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

    public required string AdapterName { get; init; }

    public required bool HasIpv4 { get; init; }

    public required bool HasIpv6 { get; init; }

    public required bool Ipv4Works { get; init; }

    public required bool Ipv6Works { get; init; }

    public required bool DnsWorks { get; init; }

    /// <summary>
    /// True when the network resolves names but this machine's own configuration does not.
    /// </summary>
    /// <remarks>
    /// Established by asking an IPv4 resolver directly once the system path has failed.
    /// The two faults look identical from the outside and have opposite remedies: one is
    /// the link, the other is what Windows was told to ask - most often an IPv6 resolver
    /// on a link whose IPv6 does not carry traffic.
    /// </remarks>
    public bool SystemResolverFaulty { get; init; }

    /// <summary>Whether the adapter carries IPv6 DNS servers.</summary>
    /// <remarks>
    /// A phone advertises these in its router advertisements, and Windows asks them
    /// before the IPv4 servers sitting beside them. That ordering is why an adapter with
    /// unusable IPv6 can resolve nothing at all while every IPv4 route works, so whether
    /// the servers are there is a fact the card has to carry rather than assume.
    /// </remarks>
    public bool HasIpv6Dns { get; init; }

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
        builder.AppendLine($"DNS           : {DescribeDns()}");
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

    private string DescribeDns() => (DnsWorks, SystemResolverFaulty, HasIpv6Dns) switch
    {
        (true, _, _) => "çalışıyor",
        (false, true, true) => "bu bilgisayarın ayarlarında çözülemiyor (IPv6 DNS sunucusu var); ağ çözüyor",
        (false, true, false) => "bu bilgisayarın ayarlarında çözülemiyor; ağ çözüyor",
        _ => "ad çözülemiyor",
    };

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

    /// <summary>
    /// How long the machine's own resolvers get before the probe calls it a failure.
    /// </summary>
    /// <remarks>
    /// Bounded rather than open-ended because the failure this card exists to catch is
    /// precisely a resolver that never answers: Windows works through its server list
    /// with its own retries, and without a ceiling the check sat there for most of a
    /// minute and the card said nothing at all while it did.
    /// </remarks>
    private static readonly TimeSpan SystemResolverBudget = TimeSpan.FromSeconds(5);

    /// <summary>How long one direct UDP query to a named resolver gets.</summary>
    private static readonly TimeSpan DirectQueryBudget = TimeSpan.FromSeconds(2);

    /// <summary>How many resolvers the direct probe will try before giving up.</summary>
    private const int MaxDirectResolvers = 2;

    private readonly ILatencyProbe _probe;
    private readonly Action<string>? _log;
    private readonly Func<DateTimeOffset> _now;

    public MobileHotspotDiagnostics(
        ILatencyProbe? probe = null,
        Action<string>? log = null,
        Func<DateTimeOffset>? now = null)
    {
        _probe = probe ?? new LatencyProbe();
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
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
        var dns = await ProbeDnsAsync(addresses, cancellationToken).ConfigureAwait(false);
        var mtu = await ProbeMtuAsync(cancellationToken).ConfigureAwait(false);

        var result = new HotspotDiagnosticResult
        {
            NetworkName = network.DisplayName,
            NetworkKey = network.Key,
            CompletedAt = _now(),
            AdapterName = network.AdapterName ?? "-",
            HasIpv4 = addresses.HasIpv4,
            HasIpv6 = addresses.HasIpv6,
            Ipv4Works = latency?.HasRemoteConnectivity ?? false,
            Ipv6Works = ipv6Works,
            DnsWorks = dns.SystemResolves,
            SystemResolverFaulty = dns is { SystemResolves: false, NetworkResolves: true },
            HasIpv6Dns = addresses.HasIpv6Dns,
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
        _log?.Invoke("hotspot.diagnostics.completed: "
            + $"internet={result.HasInternet} dns={result.DnsWorks} ipv6={result.Ipv6Works} "
            + $"ipv6dns={result.HasIpv6Dns} networkdns={dns.NetworkResolves}");

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
            findings.Add(result.SystemResolverFaulty
                ? "Ad çözümleme bu bilgisayarın DNS ayarlarında başarısız: doğrudan sorulan bir "
                    + "IPv4 çözümleyici aynı anda yanıt veriyor, yani ağın kendisi adları çözebiliyor."
                : "Ad çözümleme başarısız; adresler açılıyorsa sorun DNS tarafındadır.");

            // The specific shape this card exists to name. Windows asks an adapter's IPv6
            // resolvers before the IPv4 ones beside them, so an unusable IPv6 link takes
            // name resolution down with it while every IPv4 route keeps working - which
            // reads to a user as "internet is fine, nothing loads".
            if (result.HasIpv6Dns && !result.Ipv6Works)
            {
                findings.Add(
                    "Bu ağ IPv6 DNS sunucusu veriyor ama IPv6 trafiği geçmiyor. Windows önce o "
                    + "sunuculara sorduğu için adlar çözülemiyor; IPv4 bağlantısının çalışıyor "
                    + "olması bunu değiştirmez.");
            }
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
            if (result.HasIpv6Dns && !result.Ipv6Works)
            {
                return "Bağdaştırıcıdaki IPv6 DNS sunucularını kaldırın ya da IPv6'yı yeniden açın. "
                    + "Şifreli DNS'i açıp bağlantıyı yeniden kontrol edin. Sorun sürerse "
                    + "DNS ayarlarının uygulanamadığına ilişkin günlük kaydını inceleyin.";
            }

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

                var properties = adapter.GetIPProperties();
                ReadResolvers(properties, summary);

                foreach (var address in properties.UnicastAddresses)
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

    /// <summary>
    /// Records which resolvers the adapter carries, split by family.
    /// </summary>
    /// <remarks>
    /// <c>::1</c> and <c>127.0.0.1</c> are this app's own loopback proxy rather than
    /// anything the network offered, so a machine resolving through us does not count as
    /// one the network handed an IPv6 resolver. Getting that wrong would put the IPv6
    /// finding on a card where it explains nothing.
    /// </remarks>
    internal static void ReadResolvers(IPInterfaceProperties properties, AddressSummary summary)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(summary);

        foreach (var server in properties.DnsAddresses)
        {
            if (IPAddress.IsLoopback(server))
            {
                continue;
            }

            switch (server.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    summary.Ipv4Dns.Add(server);
                    break;

                case AddressFamily.InterNetworkV6:
                    summary.HasIpv6Dns = true;
                    break;
            }
        }
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

    /// <summary>
    /// Whether names resolve, and - when they do not - whether the fault is this machine.
    /// </summary>
    /// <remarks>
    /// Two questions, because "DNS is broken" was never actionable. The system path is
    /// what every application on the machine uses, so its failure is the symptom. Asking
    /// an IPv4 resolver directly afterwards says whether the network could have answered,
    /// which is what separates a dead link from a machine pointed at a resolver that
    /// cannot be reached - the failure a phone's IPv6 servers produce on a link whose
    /// IPv6 does not carry traffic.
    /// </remarks>
    private async Task<DnsProbe> ProbeDnsAsync(AddressSummary addresses, CancellationToken cancellationToken)
    {
        if (await ResolvesAsync(cancellationToken).ConfigureAwait(false))
        {
            return new DnsProbe(true, true);
        }

        var network = await ResolvesDirectlyAsync(addresses, cancellationToken).ConfigureAwait(false);
        if (network)
        {
            _log?.Invoke("hotspot.diagnostics: sistem çözümleyicisi yanıt vermedi, "
                + "doğrudan sorulan IPv4 çözümleyici yanıt verdi.");
        }

        return new DnsProbe(false, network);
    }

    private async Task<bool> ResolvesAsync(CancellationToken cancellationToken)
    {
        // The probe's own ceiling. A resolver that is being black-holed does not refuse,
        // it says nothing, and Windows works through its server list with its own retries
        // before giving up - so without this the check outlives the card that is waiting
        // on it.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(SystemResolverBudget);

        try
        {
            var addresses = await System.Net.Dns
                .GetHostAddressesAsync(DnsProbeHost, budget.Token)
                .ConfigureAwait(false);

            return addresses.Length > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our ceiling, not the caller's. A resolver that never answered inside the
            // budget is a resolver this machine cannot use, which is the finding.
            _log?.Invoke($"hotspot.diagnostics: ad çözümleme {SystemResolverBudget.TotalSeconds:F0} "
                + "saniyede yanıtlanmadı.");
            return false;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Asks an IPv4 resolver directly, bypassing whatever Windows was told to use.</summary>
    private async Task<bool> ResolvesDirectlyAsync(AddressSummary addresses, CancellationToken cancellationToken)
    {
        foreach (var server in DirectResolvers(addresses.Ipv4Dns))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await QueryAsync(server, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The IPv4 resolvers worth asking directly, most representative first.
    /// </summary>
    /// <remarks>
    /// The adapter's own servers come first because they are what the network offered and
    /// therefore what "can this network resolve" actually means. Loopback is skipped: it
    /// is this app's own proxy, and asking it would answer a different question. A public
    /// resolver finishes the list so an adapter with no usable IPv4 server of its own -
    /// or one whose only servers are ours - still produces an answer.
    /// </remarks>
    internal static IReadOnlyList<IPAddress> DirectResolvers(IEnumerable<IPAddress> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        var servers = new List<IPAddress>(MaxDirectResolvers);

        foreach (var server in configured)
        {
            if (server.AddressFamily != AddressFamily.InterNetwork
                || IPAddress.IsLoopback(server)
                || server.Equals(IPAddress.Any)
                || servers.Contains(server))
            {
                continue;
            }

            servers.Add(server);

            if (servers.Count == MaxDirectResolvers)
            {
                return servers;
            }
        }

        servers.Add(Ipv4Target);
        return servers;
    }

    /// <summary>One UDP question to one resolver. True only when it answers with an address.</summary>
    private async Task<bool> QueryAsync(IPAddress server, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DirectQueryBudget);

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var query = DnsMessage.BuildQuery(id, DnsProbeHost, DnsRecordType.A);

            await socket
                .SendToAsync(query, new IPEndPoint(server, 53), budget.Token)
                .ConfigureAwait(false);

            var buffer = new byte[512];
            var received = await socket
                .ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), budget.Token)
                .ConfigureAwait(false);

            var answer = buffer.AsSpan(0, received.ReceivedBytes);

            // The id has to match, or this is a stray datagram rather than our answer.
            return DnsMessage.GetId(answer) == id
                && DnsMessage.ReadAnswers(answer).Any(record => record.Type is DnsRecordType.A or DnsRecordType.Cname);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A resolver that will not answer is the answer. Nothing here is retried:
            // the next server on the list is the retry.
            return false;
        }
    }

    /// <param name="SystemResolves">Whether the machine's own resolver configuration answered.</param>
    /// <param name="NetworkResolves">Whether an IPv4 resolver answered when asked directly.</param>
    private readonly record struct DnsProbe(bool SystemResolves, bool NetworkResolves);

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

    /// <summary>What reading the adapter established, before anything was probed.</summary>
    internal sealed class AddressSummary
    {
        public bool HasIpv4 { get; set; }

        public bool HasIpv6 { get; set; }

        /// <summary>Whether the adapter carries an IPv6 resolver the network gave it.</summary>
        public bool HasIpv6Dns { get; set; }

        /// <summary>The adapter's own IPv4 resolvers, in the order Windows lists them.</summary>
        public List<IPAddress> Ipv4Dns { get; } = [];

        public HotspotAddressKind Kind { get; set; } = HotspotAddressKind.Unknown;
    }
}
