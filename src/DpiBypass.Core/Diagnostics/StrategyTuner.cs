using DpiBypass.Core.Engine;
using DpiBypass.Core.Network;

namespace DpiBypass.Core.Diagnostics;

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

    private readonly IConnectivityProbe _tester;
    private readonly Action<string>? _log;

    public StrategyTuner(IConnectivityProbe tester, Action<string>? log = null)
    {
        _tester = tester;
        _log = log;
    }

    public event Action<string, int, int>? Progress;

    /// <summary>
    /// Measures candidates on the live engine and returns the fastest that works.
    /// </summary>
    /// <param name="writer">
    /// The lease this sweep may install candidates through. Every write goes through it,
    /// including the restore on the way out, so a sweep that has been superseded - by a
    /// network change, a restart, or the user pressing re-tune - installs nothing at all.
    /// </param>
    public async Task<TuningResult> FindBestAsync(
        IStrategyWriter writer,
        IspProfile profile,
        bool checkUnfilteredFirst = true,
        CancellationToken cancellationToken = default)
    {
        var previous = writer.Current;
        var trials = new List<StrategyTrial>();
        BypassStrategy? winner = null;

        try
        {
            if (checkUnfilteredFirst)
            {
                if (!writer.TryWrite(StrategyLibrary.Passthrough))
                {
                    return Superseded(trials);
                }

                var control = await MeasureAsync(cancellationToken).ConfigureAwait(false);
                trials.Add(new StrategyTrial(StrategyLibrary.Passthrough, control));

                if (control.Success)
                {
                    _log?.Invoke("discord.com is reachable without any desync; this network is not filtering it.");
                    winner = StrategyLibrary.Passthrough;
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

                if (!writer.TryWrite(candidate))
                {
                    return Superseded(trials);
                }

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

            var fastest = successes.OrderBy(t => t.Result.Elapsed).First();
            winner = fastest.Strategy;
            _log?.Invoke($"Selected '{fastest.Strategy.Id}' ({fastest.Result.Elapsed.TotalMilliseconds:F0} ms).");
            return new TuningResult(fastest.Strategy, trials, NetworkWasAlreadyOpen: false);
        }
        finally
        {
            // Every candidate is installed on the engine in order to measure it, so a
            // sweep that ends without a winner - cancelled, failed, or nothing got
            // through - would otherwise leave the machine desyncing every connection
            // with whichever recipe happened to be tried last. Through the lease, so a
            // sweep whose network is already gone cannot undo the live sweep's winner on
            // its way out: that restore belonged to a link nobody is on any more.
            writer.TryWrite(winner ?? previous);
        }
    }

    /// <summary>What a sweep returns once it has been superseded mid-flight.</summary>
    /// <remarks>
    /// No winner, because nothing it measured describes the engine as it is now, and the
    /// trials it did get through are kept so the caller can still report what happened.
    /// </remarks>
    private TuningResult Superseded(List<StrategyTrial> trials)
    {
        _log?.Invoke("Strategy sweep was superseded before it finished; nothing was installed.");
        return new TuningResult(null, trials, NetworkWasAlreadyOpen: false);
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

            // A reset lands far quicker than a real handshake, so elapsed time only
            // separates two passes; a pass always outranks a failure however slow it was.
            if (best is null
                || (result.Success && !best.Success)
                || (result.Success && best.Success && result.Elapsed < best.Elapsed))
            {
                best = result;
            }

            if (result.Success)
            {
                // A pass is enough to keep the whole sweep quick; the further attempts
                // only exist to catch a flaky first packet.
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
