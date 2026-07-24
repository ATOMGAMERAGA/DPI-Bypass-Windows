using AtomDpi.Core.Engine;
using AtomDpi.Core.Network;

namespace AtomDpi.Core.Diagnostics;

public sealed record StrategyTrial(BypassStrategy Strategy, ProbeResult Result)
{
    public bool Success => Result.Success;
}

public sealed record TuningResult(
    BypassStrategy? Winner,
    IReadOnlyList<StrategyTrial> Trials,
    bool NetworkWasAlreadyOpen)
{
    public bool Found => Winner is not null;
}

/// <summary>
/// Finds a recipe that actually works on the network we are on right now.
/// </summary>
/// <remarks>
/// The operator profile only supplies an ordering; nothing is assumed to work until
/// a real TLS handshake to discord.com completes. Once the first candidate
/// succeeds a couple more are still measured, and the fastest of them wins - so the
/// chosen recipe is the one that adds the least latency, not merely the first that
/// happened to work.
/// </remarks>
public sealed class StrategyTuner
{
    /// <summary>How many additional candidates to measure once one has worked.</summary>
    private const int ExtraCandidatesAfterFirstSuccess = 2;

    private const int AttemptsPerCandidate = 2;

    private readonly BypassEngine _engine;
    private readonly ConnectivityTester _tester;
    private readonly Action<string>? _log;

    public StrategyTuner(BypassEngine engine, ConnectivityTester tester, Action<string>? log = null)
    {
        _engine = engine;
        _tester = tester;
        _log = log;
    }

    public event Action<string, int, int>? Progress;

    public async Task<TuningResult> FindBestAsync(
        IspProfile profile,
        bool checkUnfilteredFirst = true,
        CancellationToken cancellationToken = default)
    {
        var previous = _engine.Strategy;
        var trials = new List<StrategyTrial>();

        try
        {
            if (checkUnfilteredFirst)
            {
                _engine.Strategy = StrategyLibrary.Passthrough;
                var control = await MeasureAsync(cancellationToken).ConfigureAwait(false);
                trials.Add(new StrategyTrial(StrategyLibrary.Passthrough, control));

                if (control.Success)
                {
                    _log?.Invoke("discord.com is reachable without any desync; this network is not filtering it.");
                    return new TuningResult(StrategyLibrary.Passthrough, trials, NetworkWasAlreadyOpen: true);
                }

                _log?.Invoke($"Baseline blocked ({control.Outcome}); searching for a working strategy.");
            }

            var candidates = ResolveCandidates(profile);
            var successes = new List<StrategyTrial>();
            var index = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                Progress?.Invoke(candidate.Name, index, candidates.Count);

                _engine.Strategy = candidate;
                var result = await MeasureAsync(cancellationToken).ConfigureAwait(false);
                trials.Add(new StrategyTrial(candidate, result));

                if (result.Success)
                {
                    successes.Add(new StrategyTrial(candidate, result));
                    _log?.Invoke($"'{candidate.Id}' worked in {result.Elapsed.TotalMilliseconds:F0} ms.");

                    if (successes.Count > ExtraCandidatesAfterFirstSuccess)
                    {
                        break;
                    }
                }
            }

            if (successes.Count == 0)
            {
                _log?.Invoke("No strategy got through. Leaving the previous setting in place.");
                return new TuningResult(null, trials, NetworkWasAlreadyOpen: false);
            }

            var winner = successes.OrderBy(t => t.Result.Elapsed).First();
            _log?.Invoke($"Selected '{winner.Strategy.Id}' ({winner.Result.Elapsed.TotalMilliseconds:F0} ms).");
            return new TuningResult(winner.Strategy, trials, NetworkWasAlreadyOpen: false);
        }
        catch (OperationCanceledException)
        {
            _engine.Strategy = previous;
            throw;
        }
    }

    /// <summary>Re-checks the current strategy; used after a network change before a full re-tune.</summary>
    public async Task<bool> VerifyCurrentAsync(CancellationToken cancellationToken = default)
    {
        var result = await MeasureAsync(cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    private async Task<ProbeResult> MeasureAsync(CancellationToken cancellationToken)
    {
        ProbeResult? best = null;

        for (var attempt = 0; attempt < AttemptsPerCandidate; attempt++)
        {
            var result = await _tester.ProbeAsync(ConnectivityTester.PrimaryHost, fetchHttp: false, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success && (best is null || result.Elapsed < best.Elapsed))
            {
                best = result;
            }
            else
            {
                best ??= result;
            }

            if (result.Success)
            {
                // A pass on the first try is enough to keep the whole sweep quick;
                // the second attempt only exists to catch a flaky first packet.
                if (attempt == 0)
                {
                    continue;
                }

                break;
            }
        }

        return best!;
    }

    private List<BypassStrategy> ResolveCandidates(IspProfile profile)
    {
        var ordered = new List<BypassStrategy>();

        foreach (var id in profile.PreferredStrategies)
        {
            var strategy = StrategyLibrary.Find(id);
            if (strategy is not null && !strategy.IsPassthrough && !ordered.Contains(strategy))
            {
                ordered.Add(strategy);
            }
        }

        // Anything the profile did not mention still gets a turn at the end, so a new
        // recipe in the library is never unreachable just because a profile is stale.
        foreach (var strategy in StrategyLibrary.All)
        {
            if (!strategy.IsPassthrough && !ordered.Contains(strategy))
            {
                ordered.Add(strategy);
            }
        }

        return ordered;
    }
}
