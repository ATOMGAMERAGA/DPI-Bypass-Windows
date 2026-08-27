using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiBypass.Core.Network;

public interface ILatencySnapshotStore
{
    Task<LatencyOptimizationSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LatencyOptimizationSnapshot snapshot, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Crash-safe record of every persistent NIC value touched by latency mode.
/// </summary>
public sealed class LatencySnapshotStore : ILatencySnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();

    public LatencySnapshotStore(string? path = null) => _path = path ?? AppPaths.LatencySnapshotFile;

    public Task<LatencyOptimizationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return Task.FromResult<LatencyOptimizationSnapshot?>(null);
            }

            var json = File.ReadAllText(_path);
            var snapshot = JsonSerializer.Deserialize<LatencyOptimizationSnapshot>(json, Options)
                ?? throw new InvalidDataException("Gecikme ayarları anlık görüntüsü boş.");

            return Task.FromResult<LatencyOptimizationSnapshot?>(snapshot);
        }
    }

    public Task SaveAsync(LatencyOptimizationSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Gecikme anlık görüntüsü için klasör belirlenemedi.");
            Directory.CreateDirectory(directory);

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));

            // Flush the file contents before the atomic rename/replace. A crash can
            // leave the old complete snapshot or the new complete snapshot, never a
            // half-written JSON document that prevents recovery.
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporary, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, _path);
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            var temporary = _path + ".tmp";
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return Task.CompletedTask;
    }
}
