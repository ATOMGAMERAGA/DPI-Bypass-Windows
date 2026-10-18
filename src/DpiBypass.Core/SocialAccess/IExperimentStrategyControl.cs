using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;

namespace DpiBypass.Core.SocialAccess;

/// <summary>Operation-scoped strategy changes that can cross the privileged helper boundary.</summary>
public interface IExperimentStrategyControl
{
    BypassStrategy CurrentStrategy { get; }
    bool TryAcquire(string operationId, string expectedBeforeId);
    bool TryWrite(string operationId, string strategyId);
    bool TryRelease(string operationId, string beforeId, string installedId);
}

/// <summary>Keeps real engine leases private to the process that owns the driver.</summary>
public sealed class LocalExperimentStrategyControl(StrategyCoordinator strategies) : IExperimentStrategyControl
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    public BypassStrategy CurrentStrategy => strategies.CurrentStrategy;

    public bool TryAcquire(string operationId, string expectedBeforeId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128
            || StrategyLibrary.Find(expectedBeforeId) is null) return false;
        lock (_gate)
        {
            if (_operations.TryGetValue(operationId, out var existing))
                return existing.BeforeId == expectedBeforeId && existing.Lease.IsCurrent;
            var lease = strategies.AcquireExperiment(expectedBeforeId);
            if (lease is null) return false;
            // Session changes invalidate old leases. Keep only the live operation.
            _operations.Clear();
            _operations.Add(operationId, new(lease, expectedBeforeId));
            return true;
        }
    }

    public bool TryWrite(string operationId, string strategyId)
    {
        var strategy = StrategyLibrary.Find(strategyId);
        if (strategy is null || strategy == StrategyLibrary.Passthrough) return false;
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation)
                || operation.InstalledId is { } installed && installed != strategyId) return false;
            if (!operation.Lease.TryWrite(strategy)) return false;
            operation.InstalledId = strategyId;
            return true;
        }
    }

    public bool TryRelease(string operationId, string beforeId, string installedId)
    {
        lock (_gate)
        {
            // A lease cannot survive its owning engine process. This also makes a
            // repeated successful rollback safe after the response was lost.
            if (!_operations.TryGetValue(operationId, out var operation)) return true;
            if (operation.BeforeId != beforeId
                || operation.InstalledId is { } installed && installed != installedId) return false;
            var before = StrategyLibrary.Find(beforeId);
            var candidate = StrategyLibrary.Find(installedId);
            if (before is null || candidate is null
                || !strategies.ReleaseExperiment(operation.Lease, before, candidate)) return false;
            _operations.Remove(operationId);
            return true;
        }
    }

    private sealed class Operation(StrategyLease lease, string beforeId)
    {
        public StrategyLease Lease { get; } = lease;
        public string BeforeId { get; } = beforeId;
        public string? InstalledId { get; set; }
    }
}
