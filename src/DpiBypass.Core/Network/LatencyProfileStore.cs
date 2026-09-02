using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiBypass.Core.Network;

/// <summary>The headline numbers of one measurement, small enough to keep on disk.</summary>
public sealed record LatencySummary
{
    public required double MedianRttMs { get; init; }

    public required double P95RttMs { get; init; }

    public required double P99RttMs { get; init; }

    public required double JitterMs { get; init; }

    /// <summary>Loss over the series, or null when the instrument does not count packets.</summary>
    public double? PacketLossPercent { get; init; }

    public static LatencySummary From(LatencyMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        return new LatencySummary
        {
            MedianRttMs = measurement.MedianRttMs,
            P95RttMs = measurement.P95RttMs,
            P99RttMs = measurement.P99RttMs,
            JitterMs = measurement.JitterMs,
            PacketLossPercent = measurement.PacketLossPercent,
        };
    }
}

/// <summary>
/// What was learned about one adapter on one network.
/// </summary>
/// <remarks>
/// Deliberately narrow. A setting that helped an Intel Ethernet adapter on a wired
/// office link says nothing about a Realtek Wi-Fi adapter on a phone hotspot, so a
/// profile is only ever reused when the network key, the adapter and the adapter's whole
/// capability surface all still match. Nothing identifying is stored: the network is a
/// hash the app already computes, and the adapter is described by its own name and a
/// hash of its driver's capabilities. None of it is sent anywhere.
/// </remarks>
/// <summary>
/// Everything outside the adapter that a saved result depended on.
/// </summary>
/// <remarks>
/// A result is only a result under the conditions it was measured in. "This setting did
/// not help" was true on mains power, on that access point, at that signal strength,
/// against that server, with the link idle - and any of those changing makes it an
/// answer to a question nobody is asking any more.
/// </remarks>
public sealed record LatencyProfileContext
{
    public string TargetKey { get; init; } = string.Empty;

    public PowerSource Power { get; init; } = PowerSource.Unknown;

    public string? AccessPointHash { get; init; }

    /// <summary>Wi-Fi signal quality in ten-point buckets, so a flicker is not a change.</summary>
    public int? SignalBucket { get; init; }

    /// <summary>Wi-Fi receive rate in 10 Mbit/s buckets.</summary>
    public int? LinkRateBucket { get; init; }

    /// <summary>Whether the run had a real loaded-versus-idle comparison behind it.</summary>
    public bool LoadedEvidence { get; init; }

    public bool QosAvailable { get; init; }

    /// <summary>
    /// Whether the run that produced this record was allowed to restart the adapter.
    /// </summary>
    /// <remarks>
    /// Part of the context because it changes which candidates can be measured at all.
    /// Most NDIS keywords only take effect after a miniport restart, so a run without
    /// consent simply cannot reach them - and a record written by that run must not be
    /// allowed to answer for the run where consent has since been given.
    /// </remarks>
    public bool RestartAllowed { get; init; }

    public static LatencyProfileContext From(
        LatencyTargetSpec target,
        LatencyEnvironment environment,
        bool loadedEvidence,
        bool qosAvailable,
        bool restartAllowed = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(environment);

        return new LatencyProfileContext
        {
            TargetKey = target.CacheKey,
            Power = environment.Power,
            AccessPointHash = environment.AccessPointHash,
            SignalBucket = environment.WifiSignalQuality / 10,
            LinkRateBucket = environment.WifiRxRateKbps is { } rate ? (int)(rate / 10_000) : null,
            LoadedEvidence = loadedEvidence,
            QosAvailable = qosAvailable,
            RestartAllowed = restartAllowed,
        };
    }

    /// <summary>Whether a result measured under <paramref name="other"/> still applies here.</summary>
    public bool Covers(LatencyProfileContext other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(TargetKey, other.TargetKey, StringComparison.Ordinal)
            && Power == other.Power
            && SameOrUnknown(AccessPointHash, other.AccessPointHash)
            && SameOrUnknown(SignalBucket, other.SignalBucket)
            && SameOrUnknown(LinkRateBucket, other.LinkRateBucket)
            && LoadedEvidence == other.LoadedEvidence
            && QosAvailable == other.QosAvailable

            // A record made without restart permission covers a run that also has none.
            // The moment permission is granted, more candidates become reachable and the
            // old record no longer describes the same experiment.
            && (RestartAllowed || !other.RestartAllowed);
    }

    private static bool SameOrUnknown(string? first, string? second)
        => first is null || second is null || string.Equals(first, second, StringComparison.Ordinal);

    private static bool SameOrUnknown(int? first, int? second)
        => first is null || second is null || first == second;
}

/// <summary>One candidate a run could not measure, with the obstacle that stopped it.</summary>
public sealed record LatencyUnmeasuredEntry
{
    public required string PropertyName { get; init; }

    public required LatencyOutcomeCause Cause { get; init; }
}

public sealed record LatencyProfile
{
    /// <summary>Bumped when the shape changes so an older record is re-measured.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Bumped when the measurement method changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A result reached by a weaker method is not evidence for a stronger one. Version 2
    /// is the alternating, settled, environment-validated design; anything recorded by
    /// version 1 was accepted on rules this build no longer trusts.
    /// </para>
    /// <para>
    /// Version 3 is the migration for the rejection bug: versions 1 and 2 wrote every
    /// non-acceptance into <see cref="RejectedProperties"/>, including candidates that
    /// were never applied at all, and then skipped them for three days. Those records
    /// cannot be repaired - the file does not say which entries were measured - so the
    /// version bump retires them wholesale rather than replaying a wrong answer. Nothing
    /// is lost but one re-measurement.
    /// </remarks>
    public const int CurrentMethodologyVersion = 3;

    /// <summary>Beyond this a profile is re-measured rather than trusted.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a "this did not help" answer is allowed to suppress re-testing.
    /// </summary>
    /// <remarks>
    /// Far shorter than the accepted-result lifetime, and deliberately so. An acceptance
    /// is re-proved every time it is replayed; a rejection is never re-proved at all, it
    /// just quietly stops a candidate being measured. A month of that hides every change
    /// in the conditions that produced it.
    /// </remarks>
    public static readonly TimeSpan RejectionMaximumAge = TimeSpan.FromDays(3);

    public required string NetworkKey { get; init; }

    public required string AdapterId { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    /// <summary>Hash of the adapter's capability surface when this was measured.</summary>
    public required string CapabilityFingerprint { get; init; }

    public required DateTimeOffset VerifiedAt { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Properties a paired benchmark verified as an improvement here.</summary>
    public List<string> AcceptedProperties { get; init; } = [];

    /// <summary>
    /// Properties a completed paired benchmark measured and found not worth keeping.
    /// </summary>
    /// <remarks>
    /// Only measured outcomes belong here. A candidate that was never applied - awaiting
    /// permission, unsupported, cut short by the time budget, interrupted by a network
    /// change - has produced no evidence about its performance, and writing it here made
    /// the next run skip a setting nobody had ever tried.
    /// </remarks>
    public List<string> RejectedProperties { get; init; } = [];

    /// <summary>
    /// Candidates this run could not measure, and why, kept for the report only.
    /// </summary>
    /// <remarks>
    /// Deliberately never consulted by candidate selection. It exists so the card can say
    /// "this needs permission" instead of silently offering the same setting again with
    /// no explanation, and so a support log shows what a run could not reach.
    /// </remarks>
    public List<LatencyUnmeasuredEntry> Unmeasured { get; init; } = [];

    public LatencySummary? Baseline { get; init; }

    public LatencySummary? Optimized { get; init; }

    public LatencyBottleneck Bottleneck { get; init; } = LatencyBottleneck.Unknown;

    /// <summary>The measurement method that produced this record.</summary>
    public int MethodologyVersion { get; init; } = CurrentMethodologyVersion;

    /// <summary>The conditions the record was measured under.</summary>
    public LatencyProfileContext Context { get; init; } = new();

    public bool Matches(string networkKey, AdapterLatencyCapability adapter, LatencyProfileContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var matches = SchemaVersion == CurrentSchemaVersion
            && MethodologyVersion == CurrentMethodologyVersion
            && string.Equals(NetworkKey, networkKey, StringComparison.Ordinal)
            && string.Equals(AdapterId, adapter.AdapterId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CapabilityFingerprint, adapter.CapabilityFingerprint, StringComparison.Ordinal);

        return matches && (context is null || Context.Covers(context));
    }

    public bool IsFresh(DateTimeOffset now) => now - VerifiedAt < MaximumAge;

    /// <summary>
    /// Whether the candidates this record turned down may still be skipped.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The record has to be recent, because conditions drift; and it
    /// has to have been measured under conditions that still hold, because a rejection
    /// reached on an idle link says nothing about the same link mid-upload.
    /// </remarks>
    public bool RejectionsUsable(DateTimeOffset now, LatencyProfileContext? context = null)
        => now - VerifiedAt < RejectionMaximumAge
        && MethodologyVersion == CurrentMethodologyVersion
        && (context is null || Context.Covers(context));
}

public interface ILatencyProfileStore
{
    Task<LatencyProfile?> FindAsync(string networkKey, string adapterId, CancellationToken cancellationToken = default);

    Task SaveAsync(LatencyProfile profile, CancellationToken cancellationToken = default);

    Task RemoveAsync(string networkKey, string adapterId, CancellationToken cancellationToken = default);
}

/// <summary>A small, bounded, local cache of verified per-network results.</summary>
public sealed class LatencyProfileStore : ILatencyProfileStore
{
    /// <summary>Enough for every network a laptop sees in a month; the oldest fall off.</summary>
    public const int MaxProfiles = 24;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();

    public LatencyProfileStore(string? path = null, Action<string>? log = null)
    {
        _path = path ?? AppPaths.LatencyProfilesFile;
        _log = log;
    }

    public Task<LatencyProfile?> FindAsync(
        string networkKey,
        string adapterId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var profile = Read().FirstOrDefault(entry => Same(entry, networkKey, adapterId));
            return Task.FromResult(profile);
        }
    }

    public Task SaveAsync(LatencyProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var profiles = Read()
                .Where(entry => !Same(entry, profile.NetworkKey, profile.AdapterId))
                .Append(profile)
                .OrderByDescending(entry => entry.VerifiedAt)
                .Take(MaxProfiles)
                .ToList();

            Write(profiles);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string networkKey, string adapterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var profiles = Read().ToList();
            if (profiles.RemoveAll(entry => Same(entry, networkKey, adapterId)) > 0)
            {
                Write(profiles);
            }
        }

        return Task.CompletedTask;
    }

    private static bool Same(LatencyProfile profile, string networkKey, string adapterId)
        => string.Equals(profile.NetworkKey, networkKey, StringComparison.Ordinal)
        && string.Equals(profile.AdapterId, adapterId, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<LatencyProfile> Read()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var profiles = JsonSerializer.Deserialize<List<LatencyProfile>>(File.ReadAllText(_path), Options);
            return profiles is null
                ? []
                : [.. profiles.Where(profile => profile is not null
                    && !string.IsNullOrWhiteSpace(profile.NetworkKey)
                    && !string.IsNullOrWhiteSpace(profile.AdapterId))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be read is a cache miss, never a reason to stop: the
            // benchmark simply runs again and rewrites the file.
            _log?.Invoke($"latency.profile: önbellek okunamadı ({ex.Message}); yeniden ölçülecek.");
            return [];
        }
    }

    private void Write(IReadOnlyList<LatencyProfile> profiles)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, Options));

            if (File.Exists(_path))
            {
                File.Replace(temporary, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the cache costs one re-measurement, so it is reported and dropped
            // rather than failing an optimization that has already succeeded.
            _log?.Invoke($"latency.profile: önbellek yazılamadı ({ex.Message}).");
        }
    }
}
