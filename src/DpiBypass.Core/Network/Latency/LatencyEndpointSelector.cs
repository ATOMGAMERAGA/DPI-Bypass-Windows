namespace DpiBypass.Core.Network;

/// <summary>The endpoint a run will measure, and whether the choice was a compromise.</summary>
public sealed record LatencyEndpointChoice
{
    public required LatencyEndpoint Endpoint { get; init; }

    /// <summary>The survey that settled the choice, when one was taken.</summary>
    public LatencyMeasurement? Survey { get; init; }

    /// <summary>True when the chosen endpoint answered the survey.</summary>
    public bool Responded => Survey?.HasRemoteConnectivity ?? false;

    /// <summary>Set when the user's own target did not answer and nothing stood in for it.</summary>
    public string? Notice { get; init; }
}

/// <summary>
/// Picks which of a target's endpoints a run measures, once, for every lane.
/// </summary>
/// <remarks>
/// <para>
/// Two lanes used to choose differently: the idle optimizer surveyed the list and took
/// the first address that answered, while the loaded lane took
/// <c>Endpoints[0]</c> unconditionally. On a target whose first address is silent that
/// is two lanes measuring two different things and calling both "your ping".
/// </para>
/// <para>
/// The one rule that is not "try the next one" concerns whose target it is. Falling
/// through the general-internet reference list is fine: any of those addresses is a
/// statement about the route, which is what that target means. Falling through to a
/// different server when the user asked for their own game server is not - the number
/// would be real and would be about somebody else's machine. So failover stays inside
/// the endpoints that belong to the same target, and a user target that does not answer
/// says so.
/// </para>
/// </remarks>
public static class LatencyEndpointSelector
{
    /// <summary>
    /// Surveys the candidates in order and returns the one the run should pin.
    /// </summary>
    /// <param name="survey">
    /// Takes one short measurement of one endpoint. Passed in so both lanes can share
    /// this logic without sharing a probe configuration.
    /// </param>
    public static async Task<LatencyEndpointChoice> ChooseAsync(
        LatencyTargetResolution resolution,
        Func<LatencyEndpoint, CancellationToken, Task<LatencyMeasurement>> survey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(survey);

        if (resolution.Endpoints.Count == 0)
        {
            throw new InvalidOperationException("Ölçülecek uç nokta yok.");
        }

        var first = resolution.Endpoints[0];

        // Only addresses of the same target are alternatives for each other. Anything of
        // a different kind in the list belongs to a different question.
        var candidates = resolution.Endpoints.Where(endpoint => endpoint.Kind == first.Kind).ToArray();

        LatencyMeasurement? bestSurvey = null;
        LatencyEndpoint? chosen = null;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var measurement = await survey(candidate, cancellationToken).ConfigureAwait(false);

            if (chosen is null || (measurement.HasRemoteConnectivity && bestSurvey?.HasRemoteConnectivity != true))
            {
                bestSurvey = measurement;
                chosen = candidate;
            }

            if (measurement.HasRemoteConnectivity)
            {
                break;
            }
        }

        var endpoint = chosen ?? first;
        var responded = bestSurvey?.HasRemoteConnectivity ?? false;

        return new LatencyEndpointChoice
        {
            Endpoint = endpoint,
            Survey = bestSurvey,
            Notice = responded || first.Kind == LatencyTargetKind.Reference
                ? null
                : $"Seçtiğiniz hedef ({endpoint.Label}) ölçüme yanıt vermedi. "
                    + "Yerine başka bir sunucu ölçülmedi; gösterilecek bir sonuç yok.",
        };
    }
}
