using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace DpiBypass.Core.SocialAccess;

public sealed record NetworkPathResult(string Outcome, string ProblemClass, string Path, double ProbeLatencyMs,
    int Requests, long Bytes, string? PauseReason = null, double RetryAfterSeconds = 0);

public sealed class NetworkProbeAdapter
{
    // A socket is pinned to its checked public DNS answer. Redirects are checked anew.
    public async Task<NetworkPathResult> ProbeAsync(SocialTarget target, ExperimentBudget budget, bool throughCandidate, CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(20, Math.Max(0.1, 120 - budget.ActiveSeconds))));
        var path = throughCandidate ? "THROUGH_CANDIDATE" : "DIRECT_CONTROL";
        var initialRequests = budget.Requests; var initialBytes = budget.Bytes;
        try
        {
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                MaxResponseHeadersLength = 16, ConnectTimeout = TimeSpan.FromSeconds(8),
                ConnectCallback = async (context, ct) =>
                {
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    var address = addresses.FirstOrDefault(IsPublic) ?? throw new SocketException((int)SocketError.HostNotFound);
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                } };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var url = target.Url;
            for (var hop = 0; hop < 4; hop++)
            {
                if (!SocialTarget.IsAllowedProbe(url)) return Result("UNKNOWN", "REDIRECT_OUTSIDE_ALLOWLIST");
                budget.Request();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new(0, Math.Min(65535, budget.RemainingBytes - 1));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode == 429)
                {
                    var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 300;
                    return Result("UNKNOWN", "RATE_LIMITED", "COOLDOWN", Math.Clamp(retry, 1, 3600));
                }
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                { url = location.IsAbsoluteUri ? location : new Uri(url, location); continue; }
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[Math.Min(4096, Math.Max(1, budget.RemainingBytes))];
                var readTotal = 0;
                while (readTotal < 65536)
                {
                    var permitted = Math.Min(buffer.Length, Math.Min(65536 - readTotal, budget.RemainingBytes));
                    if (permitted <= 0) throw new BudgetExceededException();
                    var read = await body.ReadAsync(buffer.AsMemory(0, permitted), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    budget.AddBytes(read); readTotal += read;
                }
                // Only transport reachability. Never parse body or turn HTTP status into account/content PASS.
                return Result("PASS", (int)response.StatusCode >= 400 ? "HTTP_STATUS_NOT_CONTENT_EVIDENCE" : "NETWORK_PATH");
            }
            return Result("UNKNOWN", "REDIRECT_LIMIT");
        }
        catch (BudgetExceededException) { throw; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Result("UNKNOWN", "TRANSPORT_TIMEOUT"); }
        catch (HttpRequestException) { return Result("UNKNOWN", "TRANSPORT_ERROR"); }
        catch (SocketException) { return Result("UNKNOWN", "DNS_FAILURE_OR_INTERFERENCE"); }
        finally { budget.ActiveSeconds += Stopwatch.GetElapsedTime(start).TotalSeconds; }
        NetworkPathResult Result(string outcome, string problem, string? pause = null, double retry = 0) => new(outcome, problem, path,
            Stopwatch.GetElapsedTime(start).TotalMilliseconds, budget.Requests - initialRequests, budget.Bytes - initialBytes, pause, retry);
    }
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.IsIPv6LinkLocal || a.IsIPv6Multicast || a.IsIPv6SiteLocal) return false;
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return (b[0] & 0xe0) == 0x20; // global unicast only
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168)
            && !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 198 && b[1] is 18 or 19);
    }
}
