namespace DpiBypass.Core.Network;

/// <summary>One running application and the endpoints it might be playing against.</summary>
/// <param name="ProcessName">The executable, as the connection table reports it.</param>
/// <param name="Ranked">Its endpoints, best first, from <see cref="GameEndpointDiscovery"/>.</param>
public readonly record struct AutomaticTargetCandidate(
    string ProcessName,
    IReadOnlyList<GameEndpointCandidate> Ranked);

/// <summary>What automatic mode decided to measure, and why.</summary>
public sealed record AutomaticLatencyTargetChoice
{
    /// <summary>The target to hand the run.</summary>
    public required LatencyTargetSpec Spec { get; init; }

    /// <summary>One line for the card, saying what is being measured.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Whether this is a real application session rather than the general reference.
    /// </summary>
    /// <remarks>
    /// The card may only call a number "your game's ping" when this is true. The general
    /// reference measures the route to a public resolver, which is a useful health check
    /// and says nothing about the route to a game server.
    /// </remarks>
    public required bool IsApplicationSession { get; init; }

    /// <summary>The application being measured, when there is one.</summary>
    public string? ProcessName { get; init; }
}

/// <summary>
/// Picks what to measure without asking, and refuses to guess.
/// </summary>
/// <remarks>
/// <para>
/// The card used to make the user choose between "general internet", "running game" and
/// "custom server" before it would measure anything, which is a question about this
/// application's internals dressed up as a question about the user's intent. The
/// distinction still matters to the measurement - the route to a public resolver is not
/// the route to a game server - so it stays here, as logic, and stops being a control.
/// </para>
/// <para>
/// The rule is deliberately strict. A live session is an open flow that has lasted, on a
/// port the far end is listening on rather than an ephemeral one, and it has to be clearly
/// ahead of whatever came second. One application meeting that bar gets measured. Two
/// applications meeting it is an ambiguous answer, and an ambiguous answer means the
/// general reference and a line saying so - never a coin flip presented as a game ping.
/// </para>
/// </remarks>
public static class AutomaticLatencyTarget
{
    /// <summary>
    /// The score a top candidate has to reach before it counts as a live session.
    /// </summary>
    /// <remarks>
    /// Calibrated against <see cref="GameEndpointDiscovery"/>'s own weights: an open flow
    /// is 50, a fixed server port is 10, and lasting counts a point a second up to 30. So
    /// 85 needs an open, long-lived flow to a listening port - a UDP session clears it
    /// comfortably at 115 or more, and a short-lived TCP connection to a CDN cannot reach
    /// it at all.
    /// </remarks>
    public const double ConfidenceFloor = 85;

    /// <summary>
    /// How far ahead the winner has to be before picking it is a decision rather than a
    /// guess.
    /// </summary>
    public const double LeadMargin = 25;

    /// <summary>
    /// The general-internet reference, chosen because nothing better was reliably found.
    /// </summary>
    public static AutomaticLatencyTargetChoice Reference(string reason) => new()
    {
        Spec = LatencyTargetSpec.Reference,
        Summary = $"Ölçülen hedef: genel internet referansı ({reason}). Bu, oyun sunucunuzun pingi değildir.",
        IsApplicationSession = false,
    };

    /// <summary>Chooses from what is running, or falls back to the reference.</summary>
    /// <param name="sessions">Every connected application with its ranked endpoints.</param>
    public static AutomaticLatencyTargetChoice Choose(IReadOnlyList<AutomaticTargetCandidate> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var live = sessions.Where(IsLiveSession).ToArray();

        if (live.Length == 0)
        {
            return Reference("çalışan bir oyun oturumu bulunamadı");
        }

        if (live.Length > 1)
        {
            // Two applications both look like a live session. Which one the user cares
            // about is not something the connection table can answer, so it is not
            // answered: measuring one of them and labelling it "your game" would be a
            // guess presented as a finding.
            return Reference(
                $"{live.Length} uygulama aynı anda canlı oturum gibi görünüyor, hangisi olduğu kesin değil");
        }

        var chosen = live[0];
        var best = chosen.Ranked[0];

        return new AutomaticLatencyTargetChoice
        {
            Spec = new LatencyTargetSpec
            {
                Kind = LatencyTargetKind.Application,
                ProcessName = chosen.ProcessName,

                // Pinned, so the run measures this endpoint and not whatever ranks first
                // when discovery is asked again a minute later. A comparison whose target
                // moves half way through is not a comparison.
                PreferredEndpoint = LatencyTargetResolver.EndpointKey(best.Endpoint),
            },
            Summary = best.Endpoint.RouteReferenceOnly
                ? $"Ölçülen hedef: {best.Endpoint.Label} — UDP oturumunun kendi süresi dışarıdan "
                    + "ölçülemediği için aynı adrese rota referansı ölçülüyor."
                : $"Ölçülen hedef: {best.Endpoint.Label} ({best.Endpoint.ProtocolLabel}).",
            IsApplicationSession = true,
            ProcessName = chosen.ProcessName,
        };
    }

    /// <summary>Whether this application is one an automatic run may measure.</summary>
    private static bool IsLiveSession(AutomaticTargetCandidate candidate)
    {
        if (candidate.Ranked.Count == 0)
        {
            return false;
        }

        var best = candidate.Ranked[0];

        if (!best.IsOpen
            || best.Score < ConfidenceFloor
            || best.Endpoint.Port is not { } port
            || port >= GameEndpointDiscovery.EphemeralPortFloor)
        {
            return false;
        }

        // Clearly ahead of the runner-up, or the only answer there was.
        return candidate.Ranked.Count == 1
            || best.Score - candidate.Ranked[1].Score >= LeadMargin;
    }
}
