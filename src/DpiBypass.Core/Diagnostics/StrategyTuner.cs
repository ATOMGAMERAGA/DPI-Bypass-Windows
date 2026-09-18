using System.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Network;

namespace DpiBypass.Core.Diagnostics;

public sealed record StrategyTrial(BypassStrategy Strategy, ProbeResult Result)
{
    public bool Success => Result.Success;
}

/// <summary>Why a sweep stopped when it did.</summary>
public enum SweepEnding
{
    /// <summary>Everything it meant to measure was measured.</summary>
    Complete = 0,

    /// <summary>The network was open without any desync, so nothing needed choosing.</summary>
    NetworkAlreadyOpen = 1,

    /// <summary>The time budget ran out. What was measured still counts; what was not is absent.</summary>
    BudgetSpent = 2,

    /// <summary>A newer piece of work took over the engine. Nothing this measured applies.</summary>
    Superseded = 3,

    /// <summary>Nothing got through on this network.</summary>
    NothingWorked = 4,

    /// <summary>
    /// The machine does not appear to have a working connection at all, so there is
    /// nothing for a bypass recipe to be measured against.
    /// </summary>
    NetworkUnavailable = 5,
}

public sealed record TuningResult(
    BypassStrategy? Winner,
    IReadOnlyList<StrategyTrial> Trials,
    bool NetworkWasAlreadyOpen)
{
    public bool Found => Winner is not null;

    /// <summary>What each shortlisted candidate measured, field by field.</summary>
    public IReadOnlyList<StrategyMeasurement> Measurements { get; init; } = [];

    /// <summary>The policy's decision and its reasoning, or null when nothing was chosen.</summary>
    public StrategySelection? Selection { get; init; }

    public SweepEnding Ending { get; init; } = SweepEnding.Complete;

    /// <summary>The sentence to show beside the active profile.</summary>
    public string Rationale => Selection?.Reason ?? Ending switch
    {
        SweepEnding.NetworkAlreadyOpen => "Bu ağda hedefler zaten açık; hiçbir yöntem uygulanmadı.",
        SweepEnding.Superseded => "Ölçüm, ağ değiştiği için yarıda bırakıldı; hiçbir şey uygulanmadı.",
        SweepEnding.NothingWorked => "Denenen profillerin hiçbiri hedeflere ulaşamadı.",
        SweepEnding.BudgetSpent => "Süre bütçesi doldu; ölçülebilen adaylar arasından seçildi.",
        SweepEnding.NetworkUnavailable => "Çalışan bir ağ bağlantısı görünmüyor; tarama yapılmadı.",
        _ => "Ölçüm yapılmadı.",
    };
}

/// <summary>
/// Finds a recipe that actually works on the network we are on right now, and can say why.
/// </summary>
/// <remarks>
/// <para>
/// The sweep runs in two stages because the two questions are different and cost
/// different amounts. Screening asks "does this get through at all", once per candidate,
/// against one required host - it is cheap and it is what turns a library of a dozen
/// recipes into a shortlist of three or four. Measurement then asks "how well", and only
/// of the shortlist.
/// </para>
/// <para>
/// The measurement stage is round-robin, and that is the point of it. Measuring A five
/// times and then B five times attributes to B whatever the network did in the second
/// half of the sweep - a Wi-Fi roam, somebody else starting a download, the DNS cache
/// warming up. Interleaving them spreads that across every candidate instead, which is the
/// only way the comparison means anything at all over a link nobody controls.
/// </para>
/// <para>
/// Everything is applied through the writer, one candidate at a time. The engine has one
/// global strategy, so there is no such thing as measuring two in parallel, and a sweep
/// whose network has changed underneath it can install nothing at all - including the
/// restore on its way out, which used to be how a stale sweep undid the live one's winner.
/// </para>
/// <para>
/// The load test that measures sustained throughput is a third stage and it is off unless
/// the user turned it on, because it costs their data - a sweep that moved several
/// megabytes every time a network changed would spend a mobile allowance on their behalf.
/// When it is off the selection policy says "hız testi yapılmadı" rather than guessing,
/// and a transfer that fails leaves the candidate's throughput unknown rather than slow.
/// </para>
/// </remarks>
public sealed class StrategyTuner : IDisposable
{
    private readonly IConnectivityProbe _tester;
    private readonly Action<string>? _log;
    private readonly IReadOnlyList<ProbeTarget> _targets;

    public StrategyTuner(
        IConnectivityProbe tester,
        Action<string>? log = null,
        IReadOnlyList<ProbeTarget>? targets = null)
    {
        _tester = tester;
        _log = log;
        _targets = targets is { Count: > 0 } ? targets : StrategyTargets.Default;
    }

    /// <summary>How many candidates survive screening and get measured properly.</summary>
    /// <remarks>
    /// Four. Each one costs a full round of probes against every target on every round, so
    /// this multiplies the whole measurement stage; and past the first few the ordering a
    /// profile supplies is guesswork anyway.
    /// </remarks>
    public int ShortlistSize { get; init; } = DefaultShortlistSize;

    /// <summary>The shortlist size a sweep uses unless it is told otherwise.</summary>
    public const int DefaultShortlistSize = 4;

    /// <summary>How many times each shortlisted candidate is measured.</summary>
    /// <remarks>
    /// Three rounds against three targets is nine samples per candidate, which is enough
    /// for a median and a jitter figure and not enough for a p95 - so nothing downstream
    /// claims one.
    /// </remarks>
    public int Rounds { get; init; } = 3;

    /// <summary>How long screening may take before the shortlist is closed early.</summary>
    public TimeSpan ScreeningBudget { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How many candidates may fail in a way that does not look like filtering before the
    /// sweep concludes the machine has no working connection and stops.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point. A reset mid-handshake, a swallowed ClientHello
    /// or somebody else's certificate are a filter acting; a DNS failure or a connect that
    /// never completes are what an unplugged cable looks like, and no bypass recipe fixes
    /// one of those. Without this, a laptop that wakes up out of range spends a minute
    /// installing a dozen recipes on an engine with nothing to send through it, and the
    /// only thing the user sees is the app being busy for no reason.
    /// </remarks>
    public int OfflineEvidenceThreshold { get; init; } = 3;

    /// <summary>How long the measurement stage may take.</summary>
    public TimeSpan MeasurementBudget { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The transfer probe, or null when no throughput is to be measured.
    /// </summary>
    /// <remarks>
    /// Null is the default and the normal case. A sweep runs whenever a network changes,
    /// and one that moved several megabytes every time would spend a mobile allowance on
    /// somebody's behalf. The user turns this on, for a run or as a preference, and the
    /// selection policy reports its absence rather than guessing around it.
    /// </remarks>
    public IThroughputProbe? Throughput { get; init; }

    /// <summary>How much data each candidate's transfer may move.</summary>
    public long ThroughputBytesPerCandidate { get; init; } = DefaultThroughputBytesPerCandidate;

    /// <summary>What one candidate's transfer costs, so the interface can say so first.</summary>
    public const long DefaultThroughputBytesPerCandidate = 6 * 1024 * 1024;

    /// <summary>
    /// The whole cost of a throughput-measuring sweep, for the sentence shown before one
    /// is started. A user finding out what a button spent after pressing it is not a
    /// choice they made.
    /// </summary>
    public static long DefaultThroughputCostBytes
        => DefaultThroughputBytesPerCandidate * DefaultShortlistSize;

    /// <summary>What a throughput run would cost in total, for the confirmation the user sees.</summary>
    public long EstimatedThroughputBytes => Throughput is null ? 0 : ThroughputBytesPerCandidate * ShortlistSize;

    public event Action<string, int, int>? Progress;

    /// <summary>
    /// Measures candidates on the live engine and installs the one the policy picks.
    /// </summary>
    /// <param name="writer">
    /// The lease this sweep may install candidates through. Every write goes through it,
    /// including the restore on the way out, so a sweep that has been superseded - by a
    /// network change, a restart, or the user pressing re-tune - installs nothing at all.
    /// </param>
    /// <param name="profile">The operator profile, which supplies an ordering and nothing more.</param>
    /// <param name="checkUnfilteredFirst">Whether to test the network without any desync first.</param>
    /// <param name="incumbentSince">
    /// When the profile currently installed was chosen, for the policy's dwell rule. Null
    /// means "no idea", which the policy reads as "no reason to protect it".
    /// </param>
    public async Task<TuningResult> FindBestAsync(
        IStrategyWriter writer,
        IspProfile profile,
        bool checkUnfilteredFirst = true,
        CancellationToken cancellationToken = default,
        DateTimeOffset? incumbentSince = null)
    {
        var previous = writer.Current;
        var trials = new List<StrategyTrial>();
        var samples = new Dictionary<string, List<ProbeSample>>(StringComparer.Ordinal);
        var names = new Dictionary<string, BypassStrategy>(StringComparer.Ordinal);
        BypassStrategy? winner = null;

        var primary = _targets.FirstOrDefault(target => target.Required) ?? _targets[0];

        try
        {
            if (checkUnfilteredFirst)
            {
                if (!writer.TryWrite(StrategyLibrary.Passthrough))
                {
                    return Superseded(trials);
                }

                var control = await ProbeAsync(primary.Host, cancellationToken).ConfigureAwait(false);
                trials.Add(new StrategyTrial(StrategyLibrary.Passthrough, control));

                if (control.Success)
                {
                    _log?.Invoke($"{primary.Host} is reachable without any desync; this network is not filtering it.");
                    winner = StrategyLibrary.Passthrough;

                    return new TuningResult(StrategyLibrary.Passthrough, trials, NetworkWasAlreadyOpen: true)
                    {
                        Ending = SweepEnding.NetworkAlreadyOpen,
                    };
                }

                _log?.Invoke($"Baseline blocked ({control.Outcome}); searching for a working strategy.");
            }

            var candidates = ResolveCandidates(profile);
            var clock = Stopwatch.StartNew();

            // --- Stage one: screening. One probe each, cheapest question first.
            var shortlist = new List<BypassStrategy>();
            var index = 0;
            var offlineEvidence = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (shortlist.Count >= ShortlistSize || clock.Elapsed > ScreeningBudget)
                {
                    break;
                }

                index++;
                Progress?.Invoke(candidate.Name, index, candidates.Count);

                if (!writer.TryWrite(candidate))
                {
                    return Superseded(trials);
                }

                var result = await ProbeAsync(primary.Host, cancellationToken).ConfigureAwait(false);
                trials.Add(new StrategyTrial(candidate, result));
                Record(samples, names, candidate, primary.Host, result);

                if (result.Success)
                {
                    shortlist.Add(candidate);
                    _log?.Invoke($"'{candidate.Id}' got through the screening probe.");
                    offlineEvidence = 0;
                    continue;
                }

                // A failure that does not look like filtering is evidence about the link,
                // not about the recipe. Enough of them in a row and there is nothing here
                // for a sweep to measure.
                offlineEvidence = result.LooksBlocked ? 0 : offlineEvidence + 1;

                if (offlineEvidence >= OfflineEvidenceThreshold)
                {
                    _log?.Invoke(
                        $"{offlineEvidence} candidates failed without any sign of filtering "
                        + $"({result.Outcome}); this machine does not appear to have a working connection.");

                    return new TuningResult(null, trials, NetworkWasAlreadyOpen: false)
                    {
                        Measurements = Build(samples, names, EmptyThroughput),
                        Ending = SweepEnding.NetworkUnavailable,
                    };
                }
            }

            if (shortlist.Count == 0)
            {
                _log?.Invoke("No strategy got through. Leaving the previous setting in place.");

                return new TuningResult(null, trials, NetworkWasAlreadyOpen: false)
                {
                    Measurements = Build(samples, names, EmptyThroughput),
                    Ending = SweepEnding.NothingWorked,
                };
            }

            // --- Stage two: measurement, round-robin so the clock is not attributed to a
            //     candidate. Each round installs every shortlisted recipe once and probes
            //     every target with it.
            var budgetSpent = false;
            clock.Restart();

            for (var round = 0; round < Rounds && !budgetSpent; round++)
            {
                foreach (var candidate in shortlist)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (clock.Elapsed > MeasurementBudget)
                    {
                        budgetSpent = true;
                        break;
                    }

                    if (!writer.TryWrite(candidate))
                    {
                        return Superseded(trials);
                    }

                    Progress?.Invoke(candidate.Name, round + 1, Rounds);

                    foreach (var target in _targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var result = await ProbeAsync(target.Host, cancellationToken).ConfigureAwait(false);
                        trials.Add(new StrategyTrial(candidate, result));
                        Record(samples, names, candidate, target.Host, result);
                    }
                }
            }

            // --- Stage three, only when the user asked for it: what each candidate does to
            //     the link's throughput. Run after the latency rounds rather than
            //     interleaved with them, because a transfer in progress is exactly the
            //     load the latency rounds are trying not to measure through.
            var throughput = new Dictionary<string, ThroughputResult>(StringComparer.Ordinal);

            if (Throughput is not null)
            {
                foreach (var candidate in shortlist)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!writer.TryWrite(candidate))
                    {
                        return Superseded(trials);
                    }

                    Progress?.Invoke($"{candidate.Name} · hız", 1, 1);

                    var measured = await Throughput
                        .MeasureAsync(ThroughputBytesPerCandidate, cancellationToken)
                        .ConfigureAwait(false);

                    // A transfer that did not complete measured nothing. Leaving the entry
                    // out is what makes the policy treat it as unknown rather than slow.
                    if (measured is not null)
                    {
                        throughput[candidate.Id] = measured;
                    }
                }
            }

            var measurements = Build(samples, names, throughput);

            var selection = StrategySelectionPolicy.Choose(
                measurements,
                _targets,
                previous.IsPassthrough ? null : previous.Id,
                incumbentSince,
                DateTimeOffset.UtcNow);

            winner = selection.StrategyId is { Length: > 0 } chosen
                ? shortlist.FirstOrDefault(candidate => candidate.Id == chosen) ?? StrategyLibrary.Find(chosen)
                : null;

            _log?.Invoke(selection.Reason);

            return new TuningResult(winner, trials, NetworkWasAlreadyOpen: false)
            {
                Measurements = measurements,
                Selection = selection,
                Ending = budgetSpent ? SweepEnding.BudgetSpent : SweepEnding.Complete,
            };
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

        return new TuningResult(null, trials, NetworkWasAlreadyOpen: false)
        {
            Ending = SweepEnding.Superseded,
        };
    }

    /// <summary>
    /// Re-checks the current strategy; used after a network change before a full re-tune.
    /// </summary>
    /// <remarks>
    /// Every required target, not just one. "discord.com answered" and "the network is
    /// open" are different claims, and a remembered profile that reaches the web endpoint
    /// but not the gateway is one that will connect and then fail to carry a voice call.
    /// </remarks>
    public async Task<bool> VerifyCurrentAsync(CancellationToken cancellationToken = default)
    {
        foreach (var target in _targets.Where(target => target.Required))
        {
            var result = await ProbeAsync(target.Host, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _log?.Invoke($"Remembered strategy could not reach {target.Host} ({result.Outcome}).");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One probe, retried once on failure.
    /// </summary>
    /// <remarks>
    /// The retry is there for a flaky first packet and nothing else, and it stops at the
    /// first pass: a candidate that works does not need a second opinion, and paying for
    /// one on every probe would double the length of every sweep.
    /// </remarks>
    private async Task<ProbeResult> ProbeAsync(string host, CancellationToken cancellationToken)
    {
        var first = await _tester.ProbeAsync(host, fetchHttp: false, cancellationToken).ConfigureAwait(false);
        if (first.Success)
        {
            return first;
        }

        var second = await _tester.ProbeAsync(host, fetchHttp: false, cancellationToken).ConfigureAwait(false);
        return second.Success ? second : first;
    }

    private static void Record(
        Dictionary<string, List<ProbeSample>> samples,
        Dictionary<string, BypassStrategy> names,
        BypassStrategy strategy,
        string host,
        ProbeResult result)
    {
        names[strategy.Id] = strategy;

        if (!samples.TryGetValue(strategy.Id, out var list))
        {
            list = [];
            samples[strategy.Id] = list;
        }

        list.Add(new ProbeSample(
            host,
            ProbeMethod.TlsHandshake,
            result.Success,
            result.Outcome,
            result.Dns,

            // A probe that never opened a socket has no connect time, and the elapsed
            // total is not one: substituting it would make a failure look like a slow
            // success. The fall-back to Elapsed only applies to a probe that did succeed
            // but came from an instrument that does not split its timings.
            result.Connect ?? (result.Success ? result.Elapsed : null),
            result.Handshake));
    }

    private static readonly IReadOnlyDictionary<string, ThroughputResult> EmptyThroughput =
        new Dictionary<string, ThroughputResult>(StringComparer.Ordinal);

    private static IReadOnlyList<StrategyMeasurement> Build(
        Dictionary<string, List<ProbeSample>> samples,
        Dictionary<string, BypassStrategy> names,
        IReadOnlyDictionary<string, ThroughputResult> throughput)
        => samples
            .Select(entry => new StrategyMeasurement
            {
                StrategyId = entry.Key,
                StrategyName = names[entry.Key].Name,
                Samples = entry.Value,
                MeasuredAt = DateTimeOffset.UtcNow,
                Throughput = throughput.GetValueOrDefault(entry.Key),
            })
            .ToArray();

    /// <summary>Releases the transfer probe's connection pool, when there is one.</summary>
    public void Dispose()
    {
        if (Throughput is IDisposable disposable)
        {
            disposable.Dispose();
        }
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
