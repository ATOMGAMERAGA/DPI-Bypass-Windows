using System.Net;
using System.Net.Sockets;

namespace DpiBypass.Core.Dns;

/// <summary>
/// Plain UDP DNS, used only when the encrypted path is not answering.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the app can never be the reason a machine has no name resolution.
/// Pointing Windows at our loopback proxy makes every program on the box depend on
/// this process answering, and an operator that blocks DNS-over-HTTPS outright -
/// which is a thing that happens on exactly the networks this app is for - would
/// otherwise turn that into a total outage: browsers, updates, games, everything.
/// </para>
/// <para>
/// A plain answer is worse than an encrypted one, because it is the answer an
/// operator is free to rewrite. It is still enormously better than no answer, and
/// the packet engine keeps working on top of it, so a poisoned name is not the same
/// as a blocked site here. The public resolvers are tried before the ones the
/// machine was using, because they are the ones least likely to be lying.
/// </para>
/// </remarks>
public sealed class PlainDnsClient
{
    /// <summary>Per-server wait. Short: this is already the slow path.</summary>
    private static readonly TimeSpan PerServerTimeout = TimeSpan.FromMilliseconds(1200);

    private const int MaxResponse = 4096;

    private static readonly IPAddress[] PublicServers =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("9.9.9.9"),
    ];

    private volatile IPAddress[] _fallbackServers = [];

    /// <summary>Public resolvers first, then whatever the machine was using before us.</summary>
    public IReadOnlyList<IPAddress> Servers => [.. PublicServers, .. _fallbackServers];

    /// <summary>
    /// Records the resolvers the machine had before they were redirected, so they can
    /// be used as a last resort. Loopback addresses are dropped: those are us.
    /// </summary>
    public void UseAsLastResort(IEnumerable<string> servers)
    {
        var parsed = new List<IPAddress>();

        foreach (var server in servers)
        {
            if (!IPAddress.TryParse(server?.Trim(), out var address) || IPAddress.IsLoopback(address))
            {
                continue;
            }

            if (!parsed.Contains(address) && !PublicServers.Contains(address))
            {
                parsed.Add(address);
            }
        }

        _fallbackServers = [.. parsed];
    }

    /// <summary>Asks each server in turn and returns the first well-formed answer.</summary>
    public async Task<byte[]?> QueryAsync(byte[] query, CancellationToken cancellationToken)
    {
        foreach (var server in Servers)
        {
            var answer = await AskAsync(server, query, cancellationToken).ConfigureAwait(false);
            if (answer is not null)
            {
                return answer;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        return null;
    }

    private static async Task<byte[]?> AskAsync(IPAddress server, byte[] query, CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PerServerTimeout);

        try
        {
            var endpoint = new IPEndPoint(server, 53);
            await socket.SendToAsync(query, SocketFlags.None, endpoint, timeout.Token).ConfigureAwait(false);

            var buffer = new byte[MaxResponse];
            var any = new IPEndPoint(
                server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                0);

            while (true)
            {
                var received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, any, timeout.Token)
                    .ConfigureAwait(false);

                if (received.ReceivedBytes < DnsMessage.HeaderLength)
                {
                    continue;
                }

                // An unsolicited datagram on an unconnected socket is somebody else's
                // answer, or an attempt to be somebody else's answer. Match the id.
                var reply = buffer[..received.ReceivedBytes];
                if (DnsMessage.GetId(reply) != DnsMessage.GetId(query))
                {
                    continue;
                }

                return reply;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Timed out, refused, or no route. The next server gets its turn.
            return null;
        }
    }
}
