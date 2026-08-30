using System.Text;
using DpiBypass.Core.Engine;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;

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

            case ControlProtocol.Commands.HotspotDiagnose:
            {
                var diagnostics = await _service.RunHotspotDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                return diagnostics.HasInternet
                    ? ControlResponse.Success(diagnostics.ToReport())
                    : ControlResponse.Failure(diagnostics.ToReport());
            }

            case ControlProtocol.Commands.HotspotStatus:
                return ControlResponse.Success(DescribeHotspot());

            case ControlProtocol.Commands.HotspotCleanup:
            {
                var migration = _service.CleanUpLegacyHotspotConfiguration();
                return ControlResponse.Success($"{migration.Summary}\n{DescribeHotspot()}");
            }

            case ControlProtocol.Commands.VodafoneOn:
                try
                {
                    _service.EnableVodafoneModeHere();
                    return ControlResponse.Success(DescribeHotspot());
                }
                catch (InvalidOperationException ex)
                {
                    return ControlResponse.Failure(ex.Message);
                }

            case ControlProtocol.Commands.VodafoneOff:
                _service.DisableVodafoneMode();
                _service.CleanUpLegacyHotspotConfiguration();
                return ControlResponse.Success(DescribeHotspot());

            case ControlProtocol.Commands.VodafoneStatus:
                return ControlResponse.Success(DescribeHotspot());

            case ControlProtocol.Commands.LatencyOn:
            {
                var result = await _service.SetLowLatencyModeAsync(true, cancellationToken).ConfigureAwait(false);
                return result.Status == LatencyOptimizationStatus.Failed
                    ? ControlResponse.Failure(result.StatusLine)
                    : ControlResponse.Success(result.StatusLine);
            }

            case ControlProtocol.Commands.LatencyOff:
            {
                var result = await _service.SetLowLatencyModeAsync(false, cancellationToken).ConfigureAwait(false);
                return result.Status == LatencyOptimizationStatus.Failed
                    ? ControlResponse.Failure(result.StatusLine)
                    : ControlResponse.Success(result.StatusLine);
            }

            case ControlProtocol.Commands.LatencyRestore:
            {
                var result = await _service.SetLowLatencyModeAsync(false, cancellationToken).ConfigureAwait(false);
                return result.Status == LatencyOptimizationStatus.Failed
                    ? ControlResponse.Failure(result.StatusLine)
                    : ControlResponse.Success(result.StatusLine);
            }

            case ControlProtocol.Commands.LatencyStatus:
                return ControlResponse.Success(DescribeLatency());

            case ControlProtocol.Commands.LatencyStatusJson:
                return ControlResponse.Success(_service.LatencyStatus.ToJson());

            case ControlProtocol.Commands.LatencyReport:
            {
                var status = _service.LatencyStatus;
                return ControlResponse.Success(string.IsNullOrWhiteSpace(status.Detail)
                    ? status.Headline
                    : $"{status.Headline}{Environment.NewLine}{Environment.NewLine}{status.Detail}");
            }

            case ControlProtocol.Commands.LatencyQuickTest:
            {
                var result = await _service.TestLatencyAsync(cancellationToken).ConfigureAwait(false);
                return result.Status == LatencyOptimizationStatus.Offline
                    ? ControlResponse.Failure(result.StatusLine)
                    : ControlResponse.Success(result.StatusLine);
            }

            case ControlProtocol.Commands.LatencyDeepTest:
            {
                var result = await _service.RunLoadedLatencyTestAsync(cancellationToken).ConfigureAwait(false);
                return result.Status == LatencyOptimizationStatus.Offline
                    ? ControlResponse.Failure(result.StatusLine)
                    : ControlResponse.Success(result.StatusLine);
            }

            case ControlProtocol.Commands.LatencyRetest:
            {
                var result = await _service.RetestLatencyAsync(cancellationToken).ConfigureAwait(false);
                return result.Status == LatencyOptimizationStatus.Failed
                    ? ControlResponse.Failure(result.StatusLine)
                    : ControlResponse.Success(result.StatusLine);
            }

            case ControlProtocol.Commands.LatencyProfilesClear:
            {
                var removed = _service.ClearLatencyProfiles();
                return ControlResponse.Success(removed
                    ? "Kayıtlı gecikme sonuçları silindi; sonraki ölçüm baştan yapılacak."
                    : "Silinecek kayıtlı gecikme sonucu yoktu.");
            }

            case ControlProtocol.Commands.LatencyTarget:
            {
                if (string.IsNullOrWhiteSpace(request.Argument))
                {
                    _service.SetLatencyPreferences(_service.Settings.Latency with
                    {
                        TargetKind = LatencyTargetKind.Reference,
                    });

                    return ControlResponse.Success("Hedef genel internet referansına alındı "
                        + "(oyun sunucusu değildir).");
                }

                if (!LatencyTargetSpec.TryParse(request.Argument, out var spec, out var error))
                {
                    return ControlResponse.Failure(error ?? "Hedef ayrıştırılamadı.");
                }

                _service.SetLatencyPreferences(_service.Settings.Latency with
                {
                    TargetKind = spec.Kind,
                    TargetHost = spec.Host,
                    TargetPort = spec.Port,
                    TargetProtocol = spec.Protocol,
                });

                return ControlResponse.Success($"Ölçüm hedefi: {spec.Describe()}");
            }

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
        builder.AppendLine($"Vodafone modu: {DescribeHotspot()}");
        builder.AppendLine($"Ping düşürme: {DescribeLatency().Replace(Environment.NewLine, " · ")}");

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

    private string DescribeHotspot()
    {
        var status = _service.HotspotStatus;
        var builder = new StringBuilder();

        builder.Append(status.VodafoneModeEnabled ? "etkin" : "kapalı");

        if (status.VodafoneModeEnabled)
        {
            builder.Append(status.RegisteredHere
                ? $" · {status.NetworkName} kayıtlı"
                : $" · bu ağ ('{status.NetworkName}') kayıtlı değil");
        }

        builder.Append(status.DiagnosticsEnabled ? " · otomatik tanılama açık" : " · otomatik tanılama kapalı");
        builder.Append($" · kayıtlı ağ {status.RegisteredNetworks}");

        if (status.LegacyCleanedAt is { } cleaned)
        {
            builder.Append($" · eski TTL alt özelliği {cleaned.LocalDateTime:yyyy-MM-dd} tarihinde devre dışı bırakıldı");
        }

        if (status.LastResult is { } last)
        {
            builder.Append($" · son tanılama: internet {(last.HasInternet ? "var" : "yok")}, "
                + $"DNS {(last.DnsWorks ? "çalışıyor" : "çalışmıyor")}");
        }

        return builder.ToString();
    }

    private string DescribeLatency() => _service.LatencyStatus.ToCompactLine();

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
