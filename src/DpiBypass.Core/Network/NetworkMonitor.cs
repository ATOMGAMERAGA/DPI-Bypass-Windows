using System.Net.NetworkInformation;

namespace DpiBypass.Core.Network;

/// <summary>
/// Watches for the network underneath us changing.
/// </summary>
/// <remarks>
/// The OS events fire on address and availability changes, but not when you roam
/// between two access points with the same address range - and roaming is exactly
/// when the upstream filter changes. So a slow poll of the fingerprint runs
/// alongside them and compares keys. Events give a fast reaction; the poll
/// guarantees we never miss one.
/// </remarks>
public sealed class NetworkMonitor : IDisposable
{
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _debounce;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    private Task? _poller;
    private NetworkFingerprint _current = new();
    private DateTimeOffset _lastRaise = DateTimeOffset.MinValue;
    private CancellationTokenSource? _pendingDebounce;

    public NetworkMonitor(TimeSpan? pollInterval = null, TimeSpan? debounce = null, Action<string>? log = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
        _debounce = debounce ?? TimeSpan.FromSeconds(3);
        _log = log;
    }

    /// <summary>The network we believe we are on.</summary>
    public NetworkFingerprint Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised once, after things settle, when the network identity has changed.</summary>
    public event Action<NetworkFingerprint>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            _current = NetworkFingerprint.Capture();
        }

        NetworkChange.NetworkAddressChanged += OnSystemNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged += OnSystemNetworkEvent;

        _poller = Task.Run(PollAsync);
        _log?.Invoke($"Network monitor started on '{_current.DisplayName}' ({_current.Key}).");
    }

    /// <summary>
    /// Windows told us an adapter changed. Runs on a thread pool thread owned by
    /// <see cref="NetworkChange"/>.
    /// </summary>
    /// <remarks>
    /// Guarded because of where it runs, not because a failure matters. NetworkChange
    /// raises this on a thread nobody in this process owns and does not catch what a
    /// handler throws, so an exception escaping here ends the process rather than the
    /// notification - and there is a real way to throw. Unsubscribing does not recall
    /// a notification already in flight, so a change arriving while the monitor is
    /// being disposed reaches this after the cancellation source it is about to use
    /// has been disposed. The next poll notices the same change anyway.
    /// </remarks>
    private void OnSystemNetworkEvent(object? sender, EventArgs e)
    {
        try
        {
            ScheduleCheck();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Network change notification dropped: {ex.Message}");
        }
    }

    private async Task PollAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            CheckNow();
        }
    }

    /// <summary>
    /// Coalesces the burst of events Windows emits while an adapter comes up, so the
    /// tuner runs once on the settled network instead of three times on a half
    /// configured one.
    /// </summary>
    private void ScheduleCheck()
    {
        CancellationTokenSource pending;
        CancellationToken token;

        lock (_gate)
        {
            // Cancelled, not disposed. Disposing the superseded source here raced its own
            // waiter: the queued task had not necessarily read Token yet, and reading it
            // from a disposed source throws ObjectDisposedException onto the thread pool -
            // during exactly the burst of events the debounce exists to absorb. Each
            // source is now disposed by the task it belongs to, once that task is done
            // with it, and the token is read here while the source is certainly alive.
            _pendingDebounce?.Cancel();
            _pendingDebounce = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            pending = _pendingDebounce;
            token = pending.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, token).ConfigureAwait(false);
                CheckNow();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer event, or the monitor is shutting down.
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_pendingDebounce, pending))
                    {
                        _pendingDebounce = null;
                    }
                }

                pending.Dispose();
            }
        });
    }

    public void CheckNow()
    {
        NetworkFingerprint captured;
        try
        {
            captured = NetworkFingerprint.Capture();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Network probe failed: {ex.Message}");
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = captured.Key != _current.Key;
            if (changed)
            {
                // Never announce the same transition twice in quick succession. The
                // remembered key is deliberately left alone: an adapter settles in
                // steps - association first, then the DHCP lease and gateway - and
                // adopting the half configured state here would mean no later poll
                // ever sees the network the user actually ends up on.
                if (DateTimeOffset.UtcNow - _lastRaise < TimeSpan.FromSeconds(2))
                {
                    return;
                }

                _lastRaise = DateTimeOffset.UtcNow;
                _current = captured;
            }
        }

        if (changed)
        {
            _log?.Invoke($"Network changed to '{captured.DisplayName}' ({captured.Key}).");
            Changed?.Invoke(captured);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnSystemNetworkEvent;
        NetworkChange.NetworkAvailabilityChanged -= OnSystemNetworkEvent;

        _stopping.Cancel();

        try
        {
            _poller?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Cancellation is the expected way out.
        }

        // Cancelling _stopping above already cancelled the linked source; the task that
        // owns it disposes it on its way out, so it is only dropped here.
        lock (_gate)
        {
            _pendingDebounce = null;
        }

        _stopping.Dispose();
    }
}
