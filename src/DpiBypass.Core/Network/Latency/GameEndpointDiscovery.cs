using System.Globalization;
using System.Net;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

/// <summary>One endpoint a running application might be playing against.</summary>
public sealed record GameEndpointCandidate
{
    public required LatencyEndpoint Endpoint { get; init; }

    /// <summary>Higher is more likely to be the session, never a certainty.</summary>
    public required double Score { get; init; }

    /// <summary>Why it ranked where it did, in words the user can check against.</summary>
    public required string Why { get; init; }

    /// <summary>How many separate flows this application has to that address.</summary>
    public int Flows { get; init; }

    public bool IsOpen { get; init; }

    public string Display => $"{Endpoint.Label} · {Endpoint.ProtocolLabel}";
}

/// <summary>
/// Works out which endpoint a running application is actually playing against.
/// </summary>
/// <remarks>
/// <para>
/// The rule this replaces was "whichever address has the most TCP connections", which is
/// wrong twice over: it cannot see UDP at all, and on anything with a launcher, a CDN and
/// a telemetry endpoint the busiest address is rarely the game server. What is used
/// instead is how a session actually behaves - it is one flow, it stays open, and it does
/// not sit on an ephemeral port at the far end.
/// </para>
/// <para>
/// Nothing here decides on the user's behalf when the answer is close. The ranking exists
/// to put the likely endpoint first and to let the user see why, and the caller is
/// expected to offer the rest rather than silently measuring the top one.
/// </para>
/// </remarks>
public static class GameEndpointDiscovery
{
    /// <summary>Below this a remote port is a service port rather than an ephemeral one.</summary>
    public const int EphemeralPortFloor = 49152;

    /// <summary>A flow has to have lasted this long before longevity counts for anything.</summary>
    public static readonly TimeSpan MinimumInterestingLifetime = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ranks everything the observer and the connection table found for one application.
    /// </summary>
    /// <param name="processName">The executable the user picked, for the labels.</param>
    /// <param name="pids">The process ids that name currently maps to.</param>
    /// <param name="flows">Flows seen since the observer started, TCP and UDP.</param>
    /// <param name="connections">Established TCP connections from the IP Helper table.</param>
    /// <param name="now">Clock, so lifetimes are testable.</param>
    public static IReadOnlyList<GameEndpointCandidate> Rank(
        string processName,
        IReadOnlySet<uint> pids,
        IReadOnlyList<ObservedFlow> flows,
        IReadOnlyList<ProcessTcpConnection> connections,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(connections);

        var mine = flows.Where(flow => pids.Contains(flow.ProcessId)).ToArray();
        var candidates = new List<GameEndpointCandidate>();

        foreach (var group in mine.GroupBy(flow => (flow.Remote.Address, flow.Remote.Port, flow.Protocol)))
        {
            var (address, port, protocol) = group.Key;
            var open = group.Where(flow => flow.IsOpen).ToArray();
            var longest = group.Max(flow => flow.LifetimeAt(now));
            var reasons = new List<string>();

            // A session is a flow that is still there. Everything else - a name lookup, a
            // patch download, a telemetry post - has ended by the time anyone looks.
            var score = 0.0;
            if (open.Length > 0)
            {
                score += 50;
                reasons.Add("bağlantı hâlâ açık");
            }

            if (longest >= MinimumInterestingLifetime)
            {
                score += Math.Min(30, longest.TotalSeconds);
                reasons.Add(string.Create(CultureInfo.CurrentCulture, $"{longest.TotalSeconds:F0} sn sürdü"));
            }

            // Real-time game traffic is overwhelmingly UDP, and a UDP flow that stays open
            // is close to a definition of a session.
            if (protocol == LatencyProtocol.Udp)
            {
                score += 25;
                reasons.Add("UDP oturumu");
            }

            if (port < EphemeralPortFloor)
            {
                score += 10;
                reasons.Add(string.Create(CultureInfo.CurrentCulture, $"sabit sunucu portu {port}"));
            }

            // Several flows to one address is weak evidence and is weighted as such: a web
            // front end opens six connections and is not a game server.
            score += Math.Min(6, group.Count());

            candidates.Add(new GameEndpointCandidate
            {
                Endpoint = ToEndpoint(processName, address, port, protocol, open.FirstOrDefault()?.Local),
                Score = score,
                Why = string.Join(" · ", reasons.DefaultIfEmpty("yalnız kısa süreli bağlantı görüldü")),
                Flows = group.Count(),
                IsOpen = open.Length > 0,
            });
        }

        // Anything the flow observer missed - a connection opened before it started - is
        // still worth offering from the connection table, just ranked below what was
        // actually watched being created.
        foreach (var group in connections
            .Where(connection => candidates.All(candidate =>
                !candidate.Endpoint.Address.Equals(connection.Remote.Address)
                || candidate.Endpoint.Port != connection.Remote.Port))
            .GroupBy(connection => (connection.Remote.Address, connection.Remote.Port)))
        {
            var (address, port) = group.Key;

            candidates.Add(new GameEndpointCandidate
            {
                Endpoint = ToEndpoint(processName, address, port, LatencyProtocol.Tcp, group.First().Local),
                Score = (port < EphemeralPortFloor ? 10 : 0) + Math.Min(6, group.Count()),
                Why = "akış gözlemi başlamadan önce kurulmuş TCP bağlantısı",
                Flows = group.Count(),
                IsOpen = true,
            });
        }

        return
        [
            .. candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Endpoint.Address.ToString(), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Endpoint.Port ?? 0),
        ];
    }

    /// <summary>
    /// Picks the instrument that can measure one endpoint, and says so honestly.
    /// </summary>
    /// <remarks>
    /// A TCP endpoint with a known local end can be measured through the stack's own
    /// estimate for that exact connection, which is the application's real round trip and
    /// costs the server nothing. A UDP endpoint cannot be measured from outside the
    /// protocol at all, so it is probed over ICMP and labelled a route reference - which
    /// is a useful number and is never called the game's ping.
    /// </remarks>
    private static LatencyEndpoint ToEndpoint(
        string processName,
        IPAddress address,
        int port,
        LatencyProtocol protocol,
        IPEndPoint? local)
    {
        var label = $"{processName} → {address}:{port}";

        if (protocol == LatencyProtocol.Udp)
        {
            return new LatencyEndpoint
            {
                Address = address,
                Port = port,
                Protocol = LatencyProtocol.Icmp,
                Kind = LatencyTargetKind.Application,
                Label = label,
                RouteReferenceOnly = true,
                ApplicationProtocol = LatencyProtocol.Udp,
            };
        }

        // Minecraft Java is the one protocol here whose own round trip is public, stable
        // and cheap to time, so where it is plainly what we are looking at, it is measured
        // properly rather than approximated.
        if (port == MinecraftStatusProbe.DefaultPort)
        {
            return new LatencyEndpoint
            {
                Address = address,
                Port = port,
                Protocol = LatencyProtocol.MinecraftStatus,
                Kind = LatencyTargetKind.Application,
                Label = label,
                ApplicationProtocol = LatencyProtocol.Tcp,
                Host = address.ToString(),
            };
        }

        return new LatencyEndpoint
        {
            Address = address,
            Port = port,
            Protocol = local is null ? LatencyProtocol.Tcp : LatencyProtocol.TcpEStats,
            Kind = LatencyTargetKind.Application,
            Label = label,
            ApplicationProtocol = LatencyProtocol.Tcp,
            LocalEndpoint = local,
        };
    }
}
