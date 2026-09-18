namespace DpiBypass.Core.Interop;

/// <summary>Owns the WLAN clients that request streaming mode, independently per interface.</summary>
internal sealed class WlanMediaStreamingSessions(
    Func<nint> open,
    Func<nint, Guid, bool, bool> set,
    Func<Guid, bool?> query,
    Func<nint, bool> close)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, nint> _clients = [];

    internal bool TrySet(Guid adapter, bool enabled)
    {
        lock (_gate)
        {
            if (!enabled)
            {
                // Closing our client removes only our request. Another application's
                // streaming request may legitimately keep the aggregate state enabled.
                return Release(adapter);
            }

            var existing = _clients.TryGetValue(adapter, out var client);
            if (!existing)
            {
                client = open();
                if (client == 0)
                {
                    return false;
                }

                _clients.Add(adapter, client);
            }

            var verified = false;
            try
            {
                // Reassert even on a retained client: a Wi-Fi disconnect clears the
                // interface's streaming state without closing the client's handle.
                verified = set(client, adapter, true) && query(adapter) == true;
                return verified;
            }
            finally
            {
                if (!verified && !existing)
                {
                    Release(adapter);
                }
            }
        }
    }

    private bool Release(Guid adapter)
    {
        if (!_clients.TryGetValue(adapter, out var client))
        {
            // After a crash Windows already released the old process's client.
            return true;
        }

        if (!close(client))
        {
            return false; // Keep ownership so snapshot recovery can retry.
        }

        _clients.Remove(adapter);
        return true;
    }
}
