using System.Net.NetworkInformation;

namespace AtomDpi.Core.Network;

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

    private void OnSystemNetworkEvent(object? sender, EventArgs e) => ScheduleCheck();

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
        lock (_gate)
        {
            _pendingDebounce?.Cancel();
            _pendingDebounce?.Dispose();
            _pendingDebounce = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            pending = _pendingDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, pending.Token).ConfigureAwait(false);
                CheckNow();
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer event.
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
                // Never announce the same transition twice in quick succession.
                if (DateTimeOffset.UtcNow - _lastRaise < TimeSpan.FromSeconds(2))
                {
                    _current = captured;
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

        lock (_gate)
        {
            _pendingDebounce?.Dispose();
            _pendingDebounce = null;
        }

        _stopping.Dispose();
    }
}
