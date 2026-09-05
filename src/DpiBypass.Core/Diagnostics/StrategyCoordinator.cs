using DpiBypass.Core.Engine;

namespace DpiBypass.Core.Diagnostics;

/// <summary>Why a piece of strategy work was started.</summary>
public enum StrategyWorkKind
{
    /// <summary>Something the app decided to do: first start, a network change, the timer.</summary>
    Automatic = 0,

    /// <summary>Something the user asked for: the re-tune button, the tray menu, a CLI command.</summary>
    Manual = 1,
}

/// <summary>
/// The only way anything is allowed to change the engine's strategy.
/// </summary>
/// <remarks>
/// Handed out per run rather than held as a field, so the writer a run captured stops
/// working the moment that run has been superseded. Everything that writes the engine -
/// the sweep, the cached-recipe path, the manual override - goes through one of these,
/// which is what makes "only the newest run wins" a property of the type rather than of
/// each caller remembering to check.
/// </remarks>
public interface IStrategyWriter
{
    /// <summary>What the engine is set to right now.</summary>
    BypassStrategy Current { get; }

    /// <summary>False once a newer run, a network change or a restart has superseded this one.</summary>
    bool IsCurrent { get; }

    /// <summary>Writes the strategy, or does nothing and returns false when superseded.</summary>
    bool TryWrite(BypassStrategy strategy);
}

/// <summary>
/// One run's claim on the engine: which network it started on, which engine
/// instance, and where it sits in the order of runs.
/// </summary>
public sealed class StrategyLease : IStrategyWriter
{
    private readonly StrategyCoordinator _owner;

    internal StrategyLease(StrategyCoordinator owner, long generation, long session, string networkKey, string reason)
    {
        _owner = owner;
        Generation = generation;
        Session = session;
        NetworkKey = networkKey;
        Reason = reason;
    }

    /// <summary>Position in the order of runs. A newer run has a higher number.</summary>
    public long Generation { get; }

    /// <summary>Which engine instance this run belongs to. Zero means "no engine".</summary>
    public long Session { get; }

    /// <summary>The network this run started on, and the only profile it may write.</summary>
    public string NetworkKey { get; }

    /// <summary>Human readable reason, for the log line when a stale write is refused.</summary>
    public string Reason { get; }

    public BypassStrategy Current => _owner.CurrentStrategy;

    public bool IsCurrent => _owner.IsCurrent(this);

    public bool TryWrite(BypassStrategy strategy) => _owner.TryWrite(this, strategy);
}

/// <summary>
/// The single coordination point for everything that re-tunes the engine.
/// </summary>
/// <remarks>
/// <para>
/// Four things reach strategy selection: the first start, a network change, the
/// periodic re-check, and the user pressing re-tune (from the window, the tray, or the
/// CLI over IPC). They used to run concurrently on one shared engine field, so a sweep
/// that started on network A could install its winner - and write A's profile under B's
/// key - after the machine had already moved to network B.
/// </para>
/// <para>
/// Here every run takes a <see cref="StrategyLease"/> stamped with the network, the
/// engine instance and a generation. Runs are serialised, a newer run supersedes an
/// older one by cancelling it, and a superseded lease's writes are dropped - including
/// the one in the sweep's <c>finally</c> block, which is what used to let a dead run
/// undo the live one's winner on its way out.
/// </para>
/// </remarks>
public sealed class StrategyCoordinator : IDisposable
{
    private readonly Func<BypassStrategy> _read;
    private readonly Action<BypassStrategy> _write;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly Lock _gate = new();

    /// <summary>
    /// How long a new run waits for the run it supersedes to let go.
    /// </summary>
    /// <remarks>
    /// Serialising runs is what keeps two sweeps from interleaving their probes, but it
    /// must never be able to hold the newest run hostage: a superseded run that ignores
    /// its cancellation - a probe stuck in a kernel call, a provider that never answers -
    /// would otherwise block the network the user is actually on. After this long the new
    /// run proceeds anyway, which is safe because being the current run, not holding the
    /// turnstile, is what grants the right to write.
    /// </remarks>
    private readonly TimeSpan _handover;

    private long _generation;
    private long _session;
    private string _networkKey = string.Empty;

    /// <summary>The run currently holding, or queued for, the turnstile.</summary>
    private CancellationTokenSource? _running;

    /// <summary>The automatic run other automatic requests for the same network join.</summary>
    private Task? _pendingAutomatic;
    private string? _pendingAutomaticNetworkKey;

    private bool _disposed;
    private long _supersededWrites;

    public StrategyCoordinator(
        Func<BypassStrategy> read,
        Action<BypassStrategy> write,
        Action<string>? log = null,
        TimeSpan? handoverTimeout = null)
    {
        _read = read;
        _write = write;
        _log = log;
        _handover = handoverTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>The engine instance number. Zero while there is no engine.</summary>
    public long Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    /// <summary>The newest run number handed out.</summary>
    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    /// <summary>The network every new run will be stamped with.</summary>
    public string NetworkKey
    {
        get
        {
            lock (_gate)
            {
                return _networkKey;
            }
        }
    }

    public BypassStrategy CurrentStrategy => _read();

    /// <summary>How many writes were refused because their run had been superseded.</summary>
    public long SupersededWrites => Interlocked.Read(ref _supersededWrites);

    /// <summary>
    /// A new engine exists. Every lease from the previous one is now invalid, whether or
    /// not its run has noticed yet.
    /// </summary>
    public long BeginSession(string networkKey)
    {
        CancellationTokenSource? cancel;
        long session;

        lock (_gate)
        {
            session = ++_session;
            _generation++;
            _networkKey = networkKey ?? string.Empty;
            cancel = _running;
            _running = null;
            _pendingAutomatic = null;
            _pendingAutomaticNetworkKey = null;
        }

        Cancel(cancel);
        return session;
    }

    /// <summary>
    /// The engine is gone. Leases stop being current immediately, so work still unwinding
    /// cannot write to the engine that replaces it.
    /// </summary>
    public void EndSession()
    {
        CancellationTokenSource? cancel;

        lock (_gate)
        {
            _session = 0;
            _generation++;
            cancel = _running;
            _running = null;
            _pendingAutomatic = null;
            _pendingAutomaticNetworkKey = null;
        }

        Cancel(cancel);
    }

    /// <summary>
    /// The machine is on a different network. Returns true when the key actually changed,
    /// in which case any run still going belongs to the previous network and is cancelled.
    /// </summary>
    public bool AdoptNetwork(string networkKey)
    {
        CancellationTokenSource? cancel;
        var key = networkKey ?? string.Empty;

        lock (_gate)
        {
            if (string.Equals(_networkKey, key, StringComparison.Ordinal))
            {
                return false;
            }

            _networkKey = key;
            _generation++;
            cancel = _running;
            _running = null;
            _pendingAutomatic = null;
            _pendingAutomaticNetworkKey = null;
        }

        Cancel(cancel);
        return true;
    }

    /// <summary>
    /// Installs a strategy straight away on the user's behalf, superseding whatever run
    /// is in flight.
    /// </summary>
    /// <remarks>
    /// For the direct choices that are not a search: picking a recipe from the list, or
    /// the CLI forcing one. It goes through the coordinator like everything else so the
    /// sweep it interrupts cannot put its own candidate back on the way out, and it does
    /// not queue, because a user who picked a recipe should not watch a sweep finish
    /// measuring the ones they did not pick.
    /// </remarks>
    /// <returns>False when there is no engine to write to.</returns>
    public bool ApplyImmediate(string reason, BypassStrategy strategy)
    {
        CancellationTokenSource? superseded;

        lock (_gate)
        {
            if (_disposed || _session == 0)
            {
                return false;
            }

            _generation++;
            superseded = _running;
            _running = null;
            _pendingAutomatic = null;
            _pendingAutomaticNetworkKey = null;
            _write(strategy);
        }

        Cancel(superseded);
        _log?.Invoke($"strategy.manual: '{reason}' nedeniyle '{strategy.Id}' uygulandı.");
        return true;
    }

    /// <summary>Whether this lease still describes the live engine, network and run.</summary>
    public bool IsCurrent(StrategyLease lease)
    {
        lock (_gate)
        {
            return !_disposed
                && lease.Session != 0
                && lease.Session == _session
                && lease.Generation == _generation
                && string.Equals(lease.NetworkKey, _networkKey, StringComparison.Ordinal);
        }
    }

    /// <summary>Writes through a lease, or refuses and says so once.</summary>
    public bool TryWrite(StrategyLease lease, BypassStrategy strategy)
    {
        lock (_gate)
        {
            if (_disposed
                || lease.Session == 0
                || lease.Session != _session
                || lease.Generation != _generation
                || !string.Equals(lease.NetworkKey, _networkKey, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _supersededWrites);
                _log?.Invoke(
                    $"strategy.stale: '{lease.Reason}' işi '{strategy.Id}' yazmak istedi; "
                    + "iş devredışı kaldığı için yok sayıldı.");
                return false;
            }

            _write(strategy);
            return true;
        }
    }

    /// <summary>
    /// Runs a piece of strategy work under a lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Automatic work coalesces: a periodic re-check arriving while the start-up tune is
    /// still going for the same network joins that run rather than queueing a second
    /// sweep behind it. Manual work never coalesces - the user pressed a button - and
    /// supersedes whatever automatic run is in flight.
    /// </para>
    /// <para>
    /// The turnstile is never held while awaiting somebody else's task, which is what
    /// keeps the coalescing path from deadlocking against the run it joins.
    /// </para>
    /// </remarks>
    public async Task<T> RunAsync<T>(
        StrategyWorkKind kind,
        string reason,
        Func<StrategyLease, CancellationToken, Task<T>> work,
        T coalescedResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (kind == StrategyWorkKind.Automatic)
        {
            Task? join;
            lock (_gate)
            {
                join = _pendingAutomatic is { IsCompleted: false }
                    && string.Equals(_pendingAutomaticNetworkKey, _networkKey, StringComparison.Ordinal)
                        ? _pendingAutomatic
                        : null;
            }

            if (join is not null)
            {
                _log?.Invoke($"strategy.coalesce: '{reason}' zaten süren ağ işine bağlandı.");
                try
                {
                    await join.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // The run we joined failed. That is its caller's problem to report;
                    // this request asked for the same work, and it has been done.
                }

                return coalescedResult;
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? superseded = null;
        var run = new CancellationTokenSource();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (kind == StrategyWorkKind.Manual)
            {
                // The user is asking for this now, so whatever the app decided to do on
                // its own stands down rather than the button waiting behind it. The
                // pending automatic run is dropped with it: a later automatic request
                // must start its own work rather than join a run that has been told to
                // stop and will answer without having tuned anything.
                superseded = _running;
                _generation++;
                _pendingAutomatic = null;
                _pendingAutomaticNetworkKey = null;
            }

            _running = run;

            if (kind == StrategyWorkKind.Automatic)
            {
                _pendingAutomatic = completion.Task;
                _pendingAutomaticNetworkKey = _networkKey;
            }
        }

        Cancel(superseded);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token);
        var holdsTurnstile = false;

        try
        {
            holdsTurnstile = await _turnstile.WaitAsync(_handover, linked.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Settle(completion, run, kind);
            throw;
        }

        if (!holdsTurnstile)
        {
            _log?.Invoke(
                $"strategy.handover: '{reason}' işi, devredışı bırakılan önceki işin bitmesini beklemeden başladı.");
        }

        StrategyLease lease;
        lock (_gate)
        {
            lease = new StrategyLease(this, _generation, _session, _networkKey, reason);
        }

        try
        {
            return await work(lease, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (holdsTurnstile)
            {
                _turnstile.Release();
            }

            Settle(completion, run, kind);
        }
    }

    /// <summary>Convenience overload for work with nothing to return.</summary>
    public Task RunAsync(
        StrategyWorkKind kind,
        string reason,
        Func<StrategyLease, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
        => RunAsync<object?>(
            kind,
            reason,
            async (lease, token) =>
            {
                await work(lease, token).ConfigureAwait(false);
                return null;
            },
            coalescedResult: null,
            cancellationToken);

    private void Settle(TaskCompletionSource completion, CancellationTokenSource run, StrategyWorkKind kind)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_running, run))
            {
                _running = null;
            }

            if (kind == StrategyWorkKind.Automatic && ReferenceEquals(_pendingAutomatic, completion.Task))
            {
                _pendingAutomatic = null;
                _pendingAutomaticNetworkKey = null;
            }
        }

        completion.TrySetResult();
        run.Dispose();
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Its own run disposed it on the way out; there is nothing left to cancel.
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancel;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session = 0;
            _generation++;
            cancel = _running;
            _running = null;
            _pendingAutomatic = null;
            _pendingAutomaticNetworkKey = null;
        }

        Cancel(cancel);
        _turnstile.Dispose();
    }
}
