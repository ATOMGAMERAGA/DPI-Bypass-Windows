namespace DpiBypass.Core.Network;

/// <summary>
/// Undoes everything a run recorded, from the snapshot file alone.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is the whole contract: whatever is in it was changed, whatever is not
/// was not. Recovery therefore never asks the live system what it thinks should be true,
/// and never depends on the objects that made the changes still existing - the machine
/// that needs recovering is usually the one that crashed.
/// </para>
/// <para>
/// Resources are undone before adapter settings, and each list in reverse order. A QoS
/// policy that depends on an adapter still being configured a certain way is removed
/// while that is still true, and the adapter is walked backwards so the last write is
/// the first thing put back.
/// </para>
/// </remarks>
public sealed class LatencySnapshotRestorer
{
    private readonly ILatencySnapshotStore _snapshots;
    private readonly ILatencyAdapterController _controller;
    private readonly IReadOnlyList<ILatencyResourceRestorer> _restorers;
    private readonly Action<string>? _log;

    public LatencySnapshotRestorer(
        ILatencySnapshotStore snapshots,
        ILatencyAdapterController controller,
        IReadOnlyList<ILatencyResourceRestorer>? restorers = null,
        Action<string>? log = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _restorers = restorers ?? [];
        _log = log;
    }

    /// <summary>True when nothing is left changed; false leaves the snapshot for next time.</summary>
    public async Task<bool> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _snapshots.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return true;
        }

        var unresolvedResources = new List<LatencyResourceSnapshot>();
        foreach (var resource in snapshot.Resources.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await RestoreResourceAsync(resource, cancellationToken).ConfigureAwait(false))
            {
                unresolvedResources.Insert(0, resource);
            }
        }

        var unresolvedSettings = new List<LatencySettingSnapshot>();
        foreach (var setting in snapshot.Settings.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();

            LatencyRestoreOutcome outcome;
            try
            {
                outcome = await _controller.RestoreAsync(setting, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"latency.rollback.failed: '{setting.PropertyName}' ({ex.Message}).");
                outcome = LatencyRestoreOutcome.Failed;
            }

            if (!IsTerminal(outcome))
            {
                // Added at the front to preserve original apply order in the retained file.
                unresolvedSettings.Insert(0, setting);
            }
        }

        if (unresolvedSettings.Count == 0 && unresolvedResources.Count == 0)
        {
            await _snapshots.ClearAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        // One resource refusing to come back must not strand the ones that already did:
        // the file is rewritten with only what is still outstanding.
        await _snapshots.SaveAsync(
            snapshot with
            {
                Settings = unresolvedSettings,
                Resources = unresolvedResources,
                State = LatencyTransactionState.CandidateApplied,
                PendingProperty = unresolvedSettings.Count > 0
                    ? unresolvedSettings[0].PropertyName
                    : unresolvedResources.FirstOrDefault()?.TargetId,
            },
            cancellationToken).ConfigureAwait(false);

        return false;
    }

    private async Task<bool> RestoreResourceAsync(
        LatencyResourceSnapshot resource,
        CancellationToken cancellationToken)
    {
        var restorer = _restorers.FirstOrDefault(entry => entry.CanRestore(resource.Kind));
        if (restorer is null)
        {
            // Nothing here can undo it, and pretending otherwise would drop the record
            // of a change that is still in place.
            _log?.Invoke($"latency.rollback.failed: '{resource.TargetId}' için geri yükleyici yok.");
            return false;
        }

        try
        {
            var outcome = await restorer.RestoreAsync(resource, cancellationToken).ConfigureAwait(false);
            return IsTerminal(outcome);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"latency.rollback.failed: '{resource.TargetId}' ({ex.Message}).");
            return false;
        }
    }

    public static bool IsTerminal(LatencyRestoreOutcome outcome) => outcome is
        LatencyRestoreOutcome.Restored
        or LatencyRestoreOutcome.AlreadyOriginal
        or LatencyRestoreOutcome.MissingProperty;
}
