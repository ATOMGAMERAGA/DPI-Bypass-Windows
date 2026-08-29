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

    public required double PacketLossPercent { get; init; }

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
public sealed record LatencyProfile
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Beyond this a profile is re-measured rather than trusted.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);

    public required string NetworkKey { get; init; }

    public required string AdapterId { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    /// <summary>Hash of the adapter's capability surface when this was measured.</summary>
    public required string CapabilityFingerprint { get; init; }

    public required DateTimeOffset VerifiedAt { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Properties a paired benchmark verified as an improvement here.</summary>
    public List<string> AcceptedProperties { get; init; } = [];

    /// <summary>Properties a paired benchmark measured and found not worth keeping.</summary>
    public List<string> RejectedProperties { get; init; } = [];

    public LatencySummary? Baseline { get; init; }

    public LatencySummary? Optimized { get; init; }

    public LatencyBottleneck Bottleneck { get; init; } = LatencyBottleneck.Unknown;

    public bool Matches(string networkKey, AdapterLatencyCapability adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        return SchemaVersion == CurrentSchemaVersion
            && string.Equals(NetworkKey, networkKey, StringComparison.Ordinal)
            && string.Equals(AdapterId, adapter.AdapterId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(CapabilityFingerprint, adapter.CapabilityFingerprint, StringComparison.Ordinal);
    }

    public bool IsFresh(DateTimeOffset now) => now - VerifiedAt < MaximumAge;
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
