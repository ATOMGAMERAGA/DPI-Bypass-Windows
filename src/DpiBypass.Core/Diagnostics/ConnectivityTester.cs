using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DpiBypass.Core.Dns;

namespace DpiBypass.Core.Diagnostics;

public enum ProbeOutcome
{
    Reachable = 0,
    DnsFailed = 1,
    ConnectRefused = 2,
    ConnectTimedOut = 3,

    /// <summary>The connection was torn down mid-handshake - the classic DPI reset.</summary>
    HandshakeReset = 4,

    /// <summary>Handshake timed out after the ClientHello, i.e. the request was swallowed.</summary>
    HandshakeTimedOut = 5,

    /// <summary>A certificate that is not the real site's - an interception box answered.</summary>
    CertificateRejected = 6,

    HttpFailed = 7,
}

public sealed record ProbeResult(
    ProbeOutcome Outcome,
    TimeSpan Elapsed,
    string? Detail = null,
    int? HttpStatus = null)
{
    public bool Success => Outcome == ProbeOutcome.Reachable;

    /// <summary>True when the failure looks like filtering rather than an offline machine.</summary>
    public bool LooksBlocked => Outcome is ProbeOutcome.HandshakeReset
        or ProbeOutcome.HandshakeTimedOut
        or ProbeOutcome.CertificateRejected;
}

/// <summary>
/// Verifies a site end to end: encrypted DNS, TCP connect, real TLS handshake with
/// the hostname in SNI, then an HTTP request.
/// </summary>
/// <remarks>
/// Completing the handshake is the actual proof that a filter did not act on the
/// SNI, and validating the certificate chain catches the interception case where a
/// box answers on the site's behalf. That makes this both the "is it working?"
/// button and the scoring function the auto-tuner optimises.
/// </remarks>
public sealed class ConnectivityTester
{
    /// <summary>The site the user cares about, and what the tuner scores against.</summary>
    public const string PrimaryHost = "discord.com";

    public static readonly string[] DiscordEndpoints = ["discord.com", "gateway.discord.gg", "cdn.discordapp.com"];

    private readonly DohResolver _resolver;
    private readonly TimeSpan _timeout;

    public ConnectivityTester(DohResolver resolver, TimeSpan? timeout = null)
    {
        _resolver = resolver;
        _timeout = timeout ?? TimeSpan.FromSeconds(6);
    }

    public async Task<ProbeResult> ProbeAsync(
        string host,
        bool fetchHttp = false,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(host, includeIPv6: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ProbeResult(ProbeOutcome.DnsFailed, stopwatch.Elapsed, ex.Message);
        }

        if (addresses.Count == 0)
        {
            return new ProbeResult(ProbeOutcome.DnsFailed, stopwatch.Elapsed, "no A record");
        }

        ProbeResult? last = null;
        foreach (var address in addresses.Take(2))
        {
            last = await ProbeAddressAsync(host, address, fetchHttp, cancellationToken).ConfigureAwait(false);
            if (last.Success)
            {
                return last;
            }
        }

        return last ?? new ProbeResult(ProbeOutcome.DnsFailed, stopwatch.Elapsed, "no candidate address");
    }

    private async Task<ProbeResult> ProbeAddressAsync(
        string host,
        IPAddress address,
        bool fetchHttp,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_timeout);

        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, 443), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(ProbeOutcome.ConnectTimedOut, stopwatch.Elapsed, $"{address}:443");
        }
        catch (SocketException ex)
        {
            return new ProbeResult(ProbeOutcome.ConnectRefused, stopwatch.Elapsed, ex.SocketErrorCode.ToString());
        }

        await using var network = new NetworkStream(socket, ownsSocket: false);
        await using var tls = new SslStream(network, leaveInnerStreamOpen: true);

        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    // Chain validation stays on: a filter answering with its own
                    // certificate must read as a failure, not as success.
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                linked.Token).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            return new ProbeResult(ProbeOutcome.CertificateRejected, stopwatch.Elapsed, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(ProbeOutcome.HandshakeTimedOut, stopwatch.Elapsed, "no ServerHello");
        }
        catch (IOException ex)
        {
            return new ProbeResult(ProbeOutcome.HandshakeReset, stopwatch.Elapsed, ex.InnerException?.Message ?? ex.Message);
        }

        if (!fetchHttp)
        {
            return new ProbeResult(ProbeOutcome.Reachable, stopwatch.Elapsed, tls.SslProtocol.ToString());
        }

        try
        {
            var request = Encoding.ASCII.GetBytes(
                $"GET /robots.txt HTTP/1.1\r\nHost: {host}\r\nUser-Agent: DpiBypass/1.0\r\nConnection: close\r\nAccept: */*\r\n\r\n");
            await tls.WriteAsync(request, linked.Token).ConfigureAwait(false);

            var buffer = new byte[512];
            var read = await tls.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                return new ProbeResult(ProbeOutcome.HttpFailed, stopwatch.Elapsed, "empty response");
            }

            var statusLine = Encoding.ASCII.GetString(buffer, 0, read).Split('\r')[0];
            var status = ParseStatus(statusLine);
            return new ProbeResult(ProbeOutcome.Reachable, stopwatch.Elapsed, statusLine, status);
        }
        catch (OperationCanceledException)
        {
            return new ProbeResult(ProbeOutcome.HttpFailed, stopwatch.Elapsed, "response timed out");
        }
        catch (IOException ex)
        {
            return new ProbeResult(ProbeOutcome.HttpFailed, stopwatch.Elapsed, ex.Message);
        }
    }

    private static int? ParseStatus(string statusLine)
    {
        var parts = statusLine.Split(' ');
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : null;
    }
}
