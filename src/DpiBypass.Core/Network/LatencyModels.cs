using System.Net.NetworkInformation;

namespace DpiBypass.Core.Network;

public enum LatencyOptimizationStatus
{
    Disabled,
    Measuring,
    Optimizing,
    Active,
    NoGain,
    Unsupported,
    Offline,
    Restoring,
    Failed,
    Cancelled,
}

public enum LatencySettingKind
{
    PowerManagement,
    AdvancedProperty,
}

public enum LatencyRestoreOutcome
{
    Restored,
    AlreadyOriginal,
    MissingProperty,
    MissingAdapter,
    Failed,
}

/// <summary>A statistically useful latency sample; every number comes from real I/O.</summary>
public sealed record LatencyMeasurement
{
    public required DateTimeOffset MeasuredAt { get; init; }

    public required string RemoteEndpoint { get; init; }

    public required string Protocol { get; init; }

    public int RemoteAttempts { get; init; }

    public int RemoteReplies { get; init; }

    public int GatewayAttempts { get; init; }

    public int GatewayReplies { get; init; }

    public double MinimumRttMs { get; init; }

    public double MedianRttMs { get; init; }

    public double P95RttMs { get; init; }

    public double JitterMs { get; init; }

    public double PacketLossPercent { get; init; }

    public double? GatewayMedianRttMs { get; init; }

    public bool HasRemoteConnectivity => RemoteReplies > 0;

    public bool HasAnyConnectivity => HasRemoteConnectivity || GatewayReplies > 0;

    internal static LatencyMeasurement Create(
        string endpoint,
        string protocol,
        IReadOnlyList<double> remoteSamples,
        int remoteAttempts,
        IReadOnlyList<double> gatewaySamples,
        int gatewayAttempts,
        DateTimeOffset? measuredAt = null)
    {
        var ordered = remoteSamples.Order().ToArray();

        return new LatencyMeasurement
        {
            MeasuredAt = measuredAt ?? DateTimeOffset.UtcNow,
            RemoteEndpoint = endpoint,
            Protocol = protocol,
            RemoteAttempts = remoteAttempts,
            RemoteReplies = ordered.Length,
            GatewayAttempts = gatewayAttempts,
            GatewayReplies = gatewaySamples.Count,
            MinimumRttMs = ordered.Length == 0 ? 0 : ordered[0],
            MedianRttMs = Percentile(ordered, 0.50),
            P95RttMs = Percentile(ordered, 0.95),
            JitterMs = CalculateJitter(remoteSamples),
            PacketLossPercent = remoteAttempts == 0
                ? 100
                : Math.Clamp((remoteAttempts - ordered.Length) * 100d / remoteAttempts, 0, 100),
            GatewayMedianRttMs = gatewaySamples.Count == 0
                ? null
                : Percentile(gatewaySamples.Order().ToArray(), 0.50),
        };
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
        {
            return 0;
        }

        if (ordered.Count == 1)
        {
            return ordered[0];
        }

        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }

    private static double CalculateJitter(IReadOnlyList<double> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        var total = 0d;
        for (var index = 1; index < samples.Count; index++)
        {
            total += Math.Abs(samples[index] - samples[index - 1]);
        }

        return total / (samples.Count - 1);
    }
}

public sealed record AdapterAdvancedPropertyCapability
{
    public required string RegistryKeyword { get; init; }

    public List<string> RegistryValues { get; init; } = [];

    public List<string> ValidRegistryValues { get; init; } = [];
}

/// <summary>Only capability data returned by Windows; no display-language parsing.</summary>
public sealed record AdapterLatencyCapability
{
    public required string AdapterId { get; init; }

    public required string AdapterName { get; init; }

    public string InterfaceDescription { get; init; } = string.Empty;

    public NetworkInterfaceType AdapterType { get; init; }

    public bool IsPhysical { get; init; }

    public bool IsVirtual { get; init; }

    public bool IsUp { get; init; }

    /// <summary>Raw NetAdapter power values: 0 unsupported, 1 disabled, 2 enabled.</summary>
    public Dictionary<string, int> PowerManagement { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<AdapterAdvancedPropertyCapability> AdvancedProperties { get; init; } = [];

    public bool IsEligible => IsPhysical && !IsVirtual && IsUp
        && AdapterType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;

    public IReadOnlyList<LatencyOptimizationCandidate> BuildSafeCandidates()
    {
        if (!IsEligible)
        {
            return [];
        }

        var candidates = new List<LatencyOptimizationCandidate>();
        AddPowerCandidate(candidates, "SelectiveSuspend", "Seçmeli askıya alma kapalı");
        AddPowerCandidate(candidates, "DeviceSleepOnDisconnect", "Bağlantı kesilince aygıt uyutma kapalı");
        AddPowerCandidate(candidates, "D0PacketCoalescing", "D0 paket birleştirme kapalı");

        // *InterruptModeration is an NDIS registry keyword. DisplayName and
        // DisplayValue are deliberately ignored because Windows localises them.
        if (AdapterType == NetworkInterfaceType.Ethernet)
        {
            var moderation = AdvancedProperties.FirstOrDefault(property =>
                string.Equals(property.RegistryKeyword, "*InterruptModeration", StringComparison.OrdinalIgnoreCase));

            if (moderation is not null
                && moderation.ValidRegistryValues.Contains("0", StringComparer.OrdinalIgnoreCase)
                && !moderation.RegistryValues.SequenceEqual(["0"], StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(new LatencyOptimizationCandidate
                {
                    Kind = LatencySettingKind.AdvancedProperty,
                    PropertyName = moderation.RegistryKeyword,
                    OriginalValues = [.. moderation.RegistryValues],
                    DesiredValues = ["0"],
                    Description = "Interrupt Moderation kapalı · daha düşük gecikme, biraz daha yüksek CPU kullanımı",
                });
            }
        }

        return candidates;
    }

    private void AddPowerCandidate(List<LatencyOptimizationCandidate> candidates, string propertyName, string description)
    {
        // Disabled and unsupported properties are already safe/no-op. Only an
        // explicitly enabled setting is ever considered.
        if (PowerManagement.GetValueOrDefault(propertyName) != 2)
        {
            return;
        }

        candidates.Add(new LatencyOptimizationCandidate
        {
            Kind = LatencySettingKind.PowerManagement,
            PropertyName = propertyName,
            OriginalPowerValue = 2,
            DesiredPowerValue = 1,
            Description = description,
        });
    }
}

public sealed record LatencyOptimizationCandidate
{
    public required LatencySettingKind Kind { get; init; }

    public required string PropertyName { get; init; }

    public int? OriginalPowerValue { get; init; }

    public int? DesiredPowerValue { get; init; }

    public List<string> OriginalValues { get; init; } = [];

    public List<string> DesiredValues { get; init; } = [];

    public required string Description { get; init; }

    public LatencySettingSnapshot ToSnapshot(AdapterLatencyCapability adapter) => new()
    {
        AdapterId = adapter.AdapterId,
        AdapterName = adapter.AdapterName,
        Kind = Kind,
        PropertyName = PropertyName,
        OriginalPowerValue = OriginalPowerValue,
        OriginalValues = [.. OriginalValues],
        AppliedDescription = Description,
        CapturedAt = DateTimeOffset.UtcNow,
    };
}

public sealed record LatencySettingSnapshot
{
    public required string AdapterId { get; init; }

    public required string AdapterName { get; init; }

    public required LatencySettingKind Kind { get; init; }

    public required string PropertyName { get; init; }

    public int? OriginalPowerValue { get; init; }

    public List<string> OriginalValues { get; init; } = [];

    public required string AppliedDescription { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

public sealed record LatencyOptimizationSnapshot
{
    public required string AdapterId { get; init; }

    public required string AdapterName { get; init; }

    public required string NetworkKey { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<LatencySettingSnapshot> Settings { get; init; } = [];
}

public sealed record LatencyOptimizationResult
{
    public required LatencyOptimizationStatus Status { get; init; }

    public required string StatusLine { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    public string NetworkKey { get; init; } = string.Empty;

    public LatencyMeasurement? Before { get; init; }

    public LatencyMeasurement? After { get; init; }

    public IReadOnlyList<string> AppliedChanges { get; init; } = [];

    public bool HasVerifiedGain => Status == LatencyOptimizationStatus.Active
        && Before is not null
        && After is not null
        && AppliedChanges.Count > 0;
}
