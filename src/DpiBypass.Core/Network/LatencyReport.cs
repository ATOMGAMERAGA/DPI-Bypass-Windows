using System.Text;

namespace DpiBypass.Core.Network;

/// <summary>
/// Turns a run into the few lines the user reads.
/// </summary>
/// <remarks>
/// The one rule here: a number only appears next to the word "improvement" when paired
/// candidate benchmarks and an independent original-to-final measurement both support
/// it. A run that found nothing says so and says the original settings are back.
/// </remarks>
public static class LatencyReport
{
    public static string Verified(
        string adapterName,
        LatencyMeasurement baseline,
        LatencyMeasurement optimized,
        LatencyDelta improvement,
        IReadOnlyList<string> applied,
        LatencyPathAnalysis? path,
        LatencyEndpoint? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(optimized);
        ArgumentNullException.ThrowIfNull(improvement);
        ArgumentNullException.ThrowIfNull(applied);

        var builder = new StringBuilder();
        builder.AppendLine($"Ağ optimizasyonu · {adapterName}");
        AppendTarget(builder, endpoint);
        builder.AppendLine();
        AppendBlock(builder, "Başlangıç", baseline);
        builder.AppendLine();
        AppendBlock(builder, "Optimize", optimized);
        builder.AppendLine();
        builder.AppendLine("Doğrulanmış iyileşme (başlangıç → son ölçüm)");

        foreach (var line in ImprovementLines(improvement))
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();
        builder.AppendLine($"Uygulanan  : {string.Join(" · ", applied)}");
        AppendPath(builder, path);

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// A run that re-applied a result an earlier benchmark verified on this network.
    /// </summary>
    /// <remarks>
    /// A cached profile is only a shortcut to settings worth testing. The improvement
    /// shown here is freshly measured in this session, not copied from the old profile.
    /// </remarks>
    public static string Replayed(
        string adapterName,
        LatencyMeasurement baseline,
        LatencyMeasurement confirmation,
        LatencyDelta improvement,
        IReadOnlyList<string> applied,
        LatencyPathAnalysis? path,
        LatencyEndpoint? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(improvement);
        ArgumentNullException.ThrowIfNull(applied);

        var builder = new StringBuilder();
        builder.AppendLine($"Ağ optimizasyonu · {adapterName} (kayıtlı profil)");
        AppendTarget(builder, endpoint);
        builder.AppendLine();
        AppendBlock(builder, "Başlangıç", baseline);
        builder.AppendLine();
        AppendBlock(builder, "Şimdiki ölçüm", confirmation);
        builder.AppendLine();
        builder.AppendLine("Bu oturumda doğrulanan iyileşme");

        foreach (var line in ImprovementLines(improvement))
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();
        builder.AppendLine($"Uygulanan  : {string.Join(" · ", applied)}");
        AppendPath(builder, path);

        return builder.ToString().TrimEnd();
    }

    public static string NoGain(
        string headline,
        LatencyMeasurement? measurement,
        IReadOnlyList<LatencyVerdict> verdicts,
        LatencyPathAnalysis? path)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        var builder = new StringBuilder();
        builder.AppendLine(headline);

        if (measurement is not null)
        {
            builder.AppendLine();
            AppendBlock(builder, "Ölçüm", measurement);
        }

        var tried = verdicts.Where(verdict => !verdict.Accepted).ToArray();
        if (tried.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Denenen ve geri alınan");
            foreach (var verdict in tried)
            {
                builder.AppendLine($"  {verdict.Description} — {verdict.Reason}");
            }
        }

        AppendPath(builder, path);
        return builder.ToString().TrimEnd();
    }

    /// <summary>The measure-only view, used by the "just measure" button and the CLI.</summary>
    public static string Measurement(
        NetworkFingerprint network,
        LatencyMeasurement measurement,
        LatencyPathAnalysis? path,
        LatencyEndpoint? endpoint = null,
        string? notice = null)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(measurement);

        var builder = new StringBuilder();
        builder.AppendLine($"Ağ         : {network.DisplayName}");
        builder.AppendLine($"Bağdaştırıcı: {network.AdapterName ?? "-"}");
        builder.AppendLine($"Hedef      : {endpoint?.Label ?? measurement.RemoteEndpoint} "
            + $"({measurement.RemoteEndpoint} · {measurement.Protocol})");
        builder.AppendLine($"Ağ geçidi  : {(measurement.GatewayMedianRttMs is { } gateway ? $"{gateway:F1} ms median" : "yanıt yok")}");

        if (endpoint?.RouteReferenceOnly == true)
        {
            builder.AppendLine("Not        : Bu değer uygulamanın kendi gidiş-dönüş süresi değil, aynı adrese rota referansıdır.");
        }

        if (!string.IsNullOrWhiteSpace(notice))
        {
            builder.AppendLine($"Uyarı      : {notice}");
        }

        builder.AppendLine();
        AppendBlock(builder, "Boştaki gecikme", measurement);
        builder.AppendLine();
        builder.AppendLine("Yük altındaki gecikme ölçülmedi; bunun için \"Yük altında derin test\" gerekir.");
        AppendPath(builder, path);

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The loaded-latency view: idle against a real upload and a real download window.
    /// </summary>
    /// <remarks>
    /// The two directions are reported apart because they are two different problems.
    /// Delay that appears while sending is this machine filling a queue and can be paced
    /// from here; delay that appears while receiving is filled by the far end into the
    /// operator's equipment, where nothing set on this computer arrives in time.
    /// </remarks>
    public static string Loaded(
        NetworkFingerprint network,
        LatencyMeasurement idle,
        LoadExperimentResult? upload,
        LoadExperimentResult? download,
        LatencyPathAnalysis? path,
        LatencyEndpoint? endpoint,
        TrafficGuardState? guard)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(idle);

        var builder = new StringBuilder();
        builder.AppendLine($"Yük altında derin test · {network.DisplayName}");
        AppendTarget(builder, endpoint);
        builder.AppendLine();
        AppendBlock(builder, "Boşta", idle);

        AppendLoadedBlock(builder, "Gönderim sırasında", upload);
        AppendLoadedBlock(builder, "İndirme sırasında", download);

        if (guard is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"Traffic Guard: {guard.Summary}");
        }

        AppendPath(builder, path);
        return builder.ToString().TrimEnd();
    }

    private static void AppendLoadedBlock(StringBuilder builder, string title, LoadExperimentResult? result)
    {
        builder.AppendLine();

        if (result is null)
        {
            builder.AppendLine($"{title}: ölçülmedi.");
            return;
        }

        if (!result.Succeeded)
        {
            builder.AppendLine($"{title}: {result.Failure ?? "ölçülemedi"}.");
            return;
        }

        AppendBlock(builder, title, result.Loaded!);
        builder.AppendLine($"  Kuyruk : {result.QueueingMs ?? 0,6:F1} ms (boştakine göre artış)");
    }

    private static void AppendTarget(StringBuilder builder, LatencyEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return;
        }

        builder.AppendLine($"Hedef      : {endpoint.Label} ({endpoint.Address} · {endpoint.ProtocolLabel})");

        if (endpoint.RouteReferenceOnly)
        {
            builder.AppendLine("Not        : rota referansı — uygulamanın kendi gidiş-dönüş süresi değil.");
        }
    }

    /// <summary>One line for the command line and the status page summary.</summary>
    public static string Compact(LatencyMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        return $"median {measurement.MedianRttMs:F1} ms · p95 {measurement.P95RttMs:F1} ms · "
            + $"p99 {measurement.P99RttMs:F1} ms · jitter {measurement.JitterMs:F1} ms · "
            + $"kayıp %{measurement.PacketLossPercent:F1}";
    }

    private static void AppendBlock(StringBuilder builder, string title, LatencyMeasurement measurement)
    {
        builder.AppendLine(title);
        builder.AppendLine($"  Median : {measurement.MedianRttMs,6:F1} ms");
        builder.AppendLine($"  p95    : {measurement.P95RttMs,6:F1} ms");
        builder.AppendLine($"  p99    : {measurement.P99RttMs,6:F1} ms");
        builder.AppendLine($"  Jitter : {measurement.JitterMs,6:F1} ms");
        builder.AppendLine($"  Kayıp  : {measurement.PacketLossPercent,6:F1} %  ({measurement.RemoteReplies}/{measurement.RemoteAttempts} yanıt)");

        if (measurement.Load.IsLoaded)
        {
            builder.AppendLine($"  Yük    : {DescribeLoad(measurement.Load)}");
        }
    }

    /// <summary>
    /// Only metrics that actually moved are listed, and always with the sign the user
    /// expects: a negative number is less delay.
    /// </summary>
    private static IEnumerable<string> ImprovementLines(LatencyDelta improvement)
    {
        var any = false;

        foreach (var (label, value) in new[]
        {
            ("Median", improvement.MedianMs),
            ("p95   ", improvement.P95Ms),
            ("p99   ", improvement.P99Ms),
            ("Jitter", improvement.JitterMs),
        })
        {
            if (Math.Abs(value) < 0.05)
            {
                continue;
            }

            any = true;
            yield return $"  {label} : {-value,6:F1} ms";
        }

        if (improvement.LossPercent >= 0.05)
        {
            any = true;
            yield return $"  Kayıp  : {-improvement.LossPercent,6:F1} %";
        }

        if (!any)
        {
            yield return "  (ölçülebilir bir fark yok)";
        }
    }

    private static void AppendPath(StringBuilder builder, LatencyPathAnalysis? path)
    {
        if (path is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"Gecikme kaynağı: {path.Summary}");
    }

    private static string DescribeLoad(NetworkLoadSample load) => load.State switch
    {
        LatencyLoadState.UplinkLoaded => $"gönderim {load.UplinkKbps:F0} kbit/s",
        LatencyLoadState.DownlinkLoaded => $"indirme {load.DownlinkKbps:F0} kbit/s",
        LatencyLoadState.BidirectionalLoaded =>
            $"gönderim {load.UplinkKbps:F0} · indirme {load.DownlinkKbps:F0} kbit/s",
        _ => "boşta",
    };
}
