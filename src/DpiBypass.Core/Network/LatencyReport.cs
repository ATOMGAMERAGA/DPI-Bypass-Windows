using System.Text;

namespace DpiBypass.Core.Network;

/// <summary>
/// Turns a run into the few lines the user reads.
/// </summary>
/// <remarks>
/// The one rule here: a number only appears next to the word "improvement" when a paired
/// benchmark produced it. A run that found nothing says so and says the original settings
/// are back, which is a better answer than a decorated zero.
/// </remarks>
public static class LatencyReport
{
    public static string Verified(
        string adapterName,
        LatencyMeasurement baseline,
        LatencyMeasurement optimized,
        LatencyDelta improvement,
        IReadOnlyList<string> applied,
        LatencyPathAnalysis? path)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(optimized);
        ArgumentNullException.ThrowIfNull(improvement);
        ArgumentNullException.ThrowIfNull(applied);

        var builder = new StringBuilder();
        builder.AppendLine($"Ağ optimizasyonu · {adapterName}");
        builder.AppendLine();
        AppendBlock(builder, "Başlangıç", baseline);
        builder.AppendLine();
        AppendBlock(builder, "Optimize", optimized);
        builder.AppendLine();
        builder.AppendLine("Doğrulanmış iyileşme (eşli A/B ölçümü)");

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
    /// The improvement shown is the one that earlier paired benchmark measured, and it
    /// is labelled with its date rather than presented as something measured just now.
    /// This session only confirms the settings still apply and the link is no worse.
    /// </remarks>
    public static string Replayed(
        string adapterName,
        LatencyProfile profile,
        LatencyMeasurement confirmation,
        IReadOnlyList<string> applied,
        LatencyPathAnalysis? path)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(applied);

        var builder = new StringBuilder();
        builder.AppendLine($"Ağ optimizasyonu · {adapterName} (kayıtlı profil)");
        builder.AppendLine();
        AppendBlock(builder, "Şimdiki ölçüm", confirmation);

        if (profile.Baseline is { } before && profile.Optimized is { } after)
        {
            builder.AppendLine();
            builder.AppendLine($"{profile.VerifiedAt.LocalDateTime:yyyy-MM-dd} tarihinde doğrulanan iyileşme");

            foreach (var line in ImprovementLines(new LatencyDelta
            {
                MedianMs = before.MedianRttMs - after.MedianRttMs,
                P95Ms = before.P95RttMs - after.P95RttMs,
                P99Ms = before.P99RttMs - after.P99RttMs,
                JitterMs = before.JitterMs - after.JitterMs,
                LossPercent = before.PacketLossPercent - after.PacketLossPercent,
            }))
            {
                builder.AppendLine(line);
            }
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
    public static string Measurement(NetworkFingerprint network, LatencyMeasurement measurement, LatencyPathAnalysis? path)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(measurement);

        var builder = new StringBuilder();
        builder.AppendLine($"Ağ         : {network.DisplayName}");
        builder.AppendLine($"Bağdaştırıcı: {network.AdapterName ?? "-"}");
        builder.AppendLine($"Hedef      : {measurement.RemoteEndpoint} ({measurement.Protocol})");
        builder.AppendLine($"Ağ geçidi  : {(measurement.GatewayMedianRttMs is { } gateway ? $"{gateway:F1} ms median" : "yanıt yok")}");
        builder.AppendLine();
        AppendBlock(builder, "Internet", measurement);
        AppendPath(builder, path);

        return builder.ToString().TrimEnd();
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
