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

        while (!_stopping.IsCancellationRequested && _handle is not null)
        {
            var address = default(WinDivertAddress);

            try
            {
                if (!_handle.Receive(buffer, out _, ref address))
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
                _owners[port] = new Owner(path, DateTime.UtcNow.AddMinutes(10));
            }

            if (DateTime.UtcNow - lastPrune > TimeSpan.FromMinutes(1))
            {
                lastPrune = DateTime.UtcNow;
                Prune();
            }
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
        _handle?.Dispose();
        _handle = null;
        _worker?.Join(TimeSpan.FromSeconds(2));
        _stopping.Dispose();
        _owners.Clear();
    }

    private readonly record struct Owner(string ImagePath, DateTime Expires);
}
