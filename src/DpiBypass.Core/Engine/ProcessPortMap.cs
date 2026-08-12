using System.Collections.Concurrent;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Engine;

/// <summary>
/// Learns which process owns which local TCP port by sniffing WinDivert's SOCKET
/// layer. The NETWORK layer sees packets but not owners, so without this the
/// "only protect Discord" option could not exist.
/// </summary>
public sealed class ProcessPortMap : IDisposable
{
    private const string Filter = "event == CONNECT and tcp and (remotePort == 443 or remotePort == 80 or remotePort == 8080)";

    /// <summary>
    /// How long a port keeps pointing at the process that opened it.
    /// </summary>
    /// <remarks>
    /// Nothing tells us when the socket closes, so this is a guess against Windows
    /// recycling the ephemeral port. Too long and a later connection inherits the
    /// previous owner - a browser's traffic attributed to Discord, or the reverse -
    /// which silently applies the wrong scope. A minute is comfortably longer than the
    /// gap between a connect and its first data packet, which is all we need it for.
    /// </remarks>
    private static readonly TimeSpan OwnerLifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<ushort, Owner> _owners = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Action<string>? _log;

    private WinDivertHandle? _handle;
    private Thread? _worker;

    public ProcessPortMap(Action<string>? log = null) => _log = log;

    public bool IsRunning => _handle?.IsOpen == true;

    public int KnownPorts => _owners.Count;

    public bool TryStart(short priority = 990)
    {
        try
        {
            _handle = WinDivertHandle.Open(
                Filter,
                WinDivertLayer.Socket,
                priority,
                WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        }
        catch (WinDivertException ex)
        {
            _log?.Invoke($"Process attribution unavailable: {ex.Message}");
            return false;
        }

        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "DpiBypass.SocketWatcher",
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();
        return true;
    }

    /// <summary>The executable that opened <paramref name="localPort"/>, if we saw it happen.</summary>
    public string? GetImagePath(ushort localPort)
    {
        if (!_owners.TryGetValue(localPort, out var owner))
        {
            return null;
        }

        if (owner.Expires <= DateTime.UtcNow)
        {
            _owners.TryRemove(localPort, out _);
            return null;
        }

        return owner.ImagePath;
    }

    private void Loop()
    {
        var buffer = new byte[1];
        var lastPrune = DateTime.UtcNow;

        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                // Read the field once. Dispose clears it from another thread, so
                // testing it and then dereferencing it are two different answers, and
                // the NullReferenceException in between would be unhandled on this
                // thread - which ends the process, not just the watcher.
                var handle = _handle;
                if (handle is null)
                {
                    return;
                }

                var address = default(WinDivertAddress);

                try
                {
                    if (!handle.Receive(buffer, out _, ref address))
                    {
                        return;
                    }
                }
                catch (WinDivertException ex)
                {
                    _log?.Invoke($"Socket watcher stopped: {ex.Message}");
                    return;
                }

                if (address.Layer != WinDivertLayer.Socket)
                {
                    continue;
                }

                var port = address.LocalPort;
                if (port == 0)
                {
                    continue;
                }

                var path = ProcessLookup.GetImagePath(address.ProcessId);
                if (path is not null)
                {
                    _owners[port] = new Owner(path, DateTime.UtcNow.Add(OwnerLifetime));
                }

                if (DateTime.UtcNow - lastPrune > TimeSpan.FromMinutes(1))
                {
                    lastPrune = DateTime.UtcNow;
                    Prune();
                }
            }
        }
        catch (Exception ex)
        {
            // Losing process attribution costs the narrow scopes their accuracy;
            // letting it escape this thread would cost the user the whole app.
            _log?.Invoke($"Socket watcher faulted: {ex.Message}");
        }
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var (port, owner) in _owners)
        {
            if (owner.Expires <= now)
            {
                _owners.TryRemove(port, out _);
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();

        // Shut down before closing: the worker is parked inside Receive, and pulling
        // the handle out from under it is what the loop is guarding against.
        var handle = Interlocked.Exchange(ref _handle, null);
        handle?.Shutdown();

        _worker?.Join(TimeSpan.FromSeconds(2));

        handle?.Dispose();
        _stopping.Dispose();
        _owners.Clear();
    }

    private readonly record struct Owner(string ImagePath, DateTime Expires);
}
