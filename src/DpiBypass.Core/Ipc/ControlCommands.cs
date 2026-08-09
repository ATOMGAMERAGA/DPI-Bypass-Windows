using System.Text;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Network;
using DpiBypass.Core.Vodafone;

namespace DpiBypass.Core.Ipc;

/// <summary>
/// Turns a control request into the text the command line prints.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="ControlServer"/> so the mapping from command to
/// behaviour can be tested without a pipe, and apart from
/// <see cref="ProtectionService"/> so the service does not grow a formatting layer.
/// </remarks>
public sealed class ControlCommands
{
    private readonly ProtectionService _service;

    public ControlCommands(ProtectionService service) => _service = service;

    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken cancellationToken = default)
    {
        switch (request.Command)
        {
            case ControlProtocol.Commands.Status:
                return ControlResponse.Success(DescribeStatus());

            case ControlProtocol.Commands.Test:
            {
                var host = string.IsNullOrWhiteSpace(request.Argument) ? "discord.com" : request.Argument.Trim();
                var probe = await _service.ProbeAsync(host, cancellationToken).ConfigureAwait(false);

                return probe.Success
                    ? ControlResponse.Success($"{host}: erişilebilir · {probe.Elapsed.TotalMilliseconds:F0} ms")
                    : ControlResponse.Failure($"{host}: {ProtectionService.DescribeOutcome(probe.Outcome)}");
            }

            case ControlProtocol.Commands.Search:
            {
                var result = await _service.RetuneAsync(cancellationToken).ConfigureAwait(false);
                return result?.Winner is null
                    ? ControlResponse.Failure("Çalışan bir yöntem bulunamadı.")
                    : ControlResponse.Success($"Seçilen yöntem: {result.Winner.Name} ({result.Trials.Count} deneme)");
            }

            case ControlProtocol.Commands.Enable:
                await _service.StartAsync(cancellationToken).ConfigureAwait(false);
                return ControlResponse.Success("Koruma açıldı.");

            case ControlProtocol.Commands.Disable:
                await _service.StopAsync(cancellationToken).ConfigureAwait(false);
                return ControlResponse.Success("Koruma kapatıldı.");

            case ControlProtocol.Commands.VodafoneOn:
                try
                {
                    _service.EnableTtlFixHere();
                    return ControlResponse.Success(DescribeVodafone());
                }
                catch (TtlFixException ex)
                {
                    return ControlResponse.Failure(ex.Message);
                }

            case ControlProtocol.Commands.VodafoneOff:
                _service.DisableTtlFix();
                return ControlResponse.Success(DescribeVodafone());

            case ControlProtocol.Commands.VodafoneStatus:
                return ControlResponse.Success(DescribeVodafone());

            case ControlProtocol.Commands.Domains:
                return ControlResponse.Success(DescribeDomains());

            default:
                return ControlResponse.Failure($"bilinmeyen komut: {request.Command}");
        }
    }

    private string DescribeStatus()
    {
        var builder = new StringBuilder();
        var stats = _service.Stats;

        builder.AppendLine($"Durum       : {DescribeState(_service.State)} — {_service.StatusDetail}");
        builder.AppendLine($"Ağ          : {_service.Network.DisplayName}");
        builder.AppendLine($"Operatör    : {_service.Detection?.Summary ?? _service.Isp.DisplayName}");
        builder.AppendLine($"Yöntem      : {_service.Strategy.Name}");
        builder.AppendLine($"Kapsam      : {ProtectionService.DescribeScope(_service.Settings.Scope)}");
        builder.AppendLine($"DNS         : {DescribeDns()}");
        builder.AppendLine($"Alan adları : {_service.ProtectedDomainCount} ({_service.LearnedDomains.Count} kendiliğinden bulundu)");
        builder.AppendLine($"Sınırsız mod: {DescribeVodafone()}");

        if (stats is not null)
        {
            builder.AppendLine(
                $"Sayaçlar    : incelenen {stats.Inspected:N0} · yeniden yazılan {stats.Rewritten:N0} · "
                    + $"parça {stats.SegmentsSent:N0} · sahte {stats.DecoysSent:N0} · QUIC {stats.QuicHandshakesBlocked:N0}");
        }

        if (_service.LastProbe is { } probe)
        {
            builder.AppendLine($"Son test    : {(probe.Success ? "başarılı" : ProtectionService.DescribeOutcome(probe.Outcome))}"
                + $" ({probe.Elapsed.TotalMilliseconds:F0} ms)");
        }

        return builder.ToString().TrimEnd();
    }

    private string DescribeDns() => _service.ActiveDnsMode switch
    {
        Dns.DnsMode.EncryptedLoopback =>
            $"şifreli (DoH) · {_service.DnsProviderInUse ?? "Cloudflare"} · sorgu {_service.DnsQueriesServed:N0}",
        Dns.DnsMode.PublicResolvers => "genel çözümleyiciler",
        _ => "sistem ayarı",
    };

    private string DescribeVodafone()
    {
        var status = _service.TtlFixStatus;

        if (!status.Enabled)
        {
            return "kapalı";
        }

        if (!status.Active)
        {
            return status.RegisteredHere
                ? "açık, kural kurulamadı"
                : $"açık, bu ağ ('{status.NetworkName}') kayıtlı değil";
        }

        return $"etkin · TTL {status.TimeToLive} · düzeltilen paket {status.RewrittenPackets:N0}";
    }

    private string DescribeDomains()
    {
        var builder = new StringBuilder();

        foreach (var domain in _service.ProtectedDomains())
        {
            builder.AppendLine(domain);
        }

        return builder.Length == 0 ? "(liste boş)" : builder.ToString().TrimEnd();
    }

    public static string DescribeState(ProtectionState state) => state switch
    {
        ProtectionState.Running => "etkin",
        ProtectionState.Degraded => "etkin, engel sürüyor",
        ProtectionState.Starting => "başlatılıyor",
        ProtectionState.Stopping => "durduruluyor",
        _ => "kapalı",
    };

    /// <summary>The static catalogue listings, which need no running instance.</summary>
    public static string DescribeStrategies()
    {
        var builder = new StringBuilder();

        foreach (var strategy in StrategyLibrary.All)
        {
            builder.AppendLine($"{strategy.Id,-26} {strategy.Name}");
            if (!string.IsNullOrEmpty(strategy.Description))
            {
                builder.AppendLine($"{string.Empty,-27}{strategy.Description}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static string DescribeIsps()
    {
        var builder = new StringBuilder();

        foreach (var profile in IspCatalog.All)
        {
            builder.AppendLine($"{profile.Id,-18} {profile.DisplayName}");
        }

        return builder.ToString().TrimEnd();
    }
}
