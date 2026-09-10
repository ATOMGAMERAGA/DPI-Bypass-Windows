namespace DpiBypass.Core.Network;

/// <summary>
/// A non-owning view of a network watch somebody else started.
/// </summary>
/// <remarks>
/// <para>
/// The protection service keeps a watch for the whole life of the process - the
/// Vodafone card has to know which network the machine is on whether or not protection
/// is running - and the latency lane used to create a second one when low latency mode
/// was switched on. Both then polled <see cref="NetworkFingerprint.Capture"/> every ten
/// seconds, and that call enumerates every adapter, its addresses and its byte
/// counters, asks the wireless stack for the association and looks the gateway up in
/// the ARP table. Two watches meant paying all of it twice, for ever, to be told the
/// same thing.
/// </para>
/// <para>
/// This hands the second feature the first one's answers. <see cref="Start"/> does
/// nothing because the owner has already started it, and <see cref="Dispose"/> only
/// drops this view's subscriptions - disposing the underlying watch here would stop the
/// network name updating for everything else the moment low latency mode was turned
/// off.
/// </para>
/// </remarks>
public sealed class SharedNetworkWatch(INetworkWatch inner) : INetworkWatch
{
    private readonly INetworkWatch _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>
    /// Guards the subscriber list, because attaching to the inner watch is conditional
    /// on it: two threads subscribing at once could otherwise both find it empty and
    /// forward every change twice, or both find it occupied and never attach at all.
    /// </summary>
    private readonly Lock _gate = new();

    private Action<NetworkFingerprint>? _subscribers;
    private bool _disposed;

    public NetworkFingerprint Current => _inner.Current;

    public event Action<NetworkFingerprint>? Changed
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_subscribers is null)
                {
                    _inner.Changed += Forward;
                }

                _subscribers += value;
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_gate)
            {
                _subscribers -= value;

                if (_subscribers is null)
                {
                    _inner.Changed -= Forward;
                }
            }
        }
    }

    /// <summary>Nothing to do: the watch this views is owned and started elsewhere.</summary>
    public void Start()
    {
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_subscribers is not null)
            {
                _inner.Changed -= Forward;
                _subscribers = null;
            }
        }
    }

    private void Forward(NetworkFingerprint network) => _subscribers?.Invoke(network);
}
