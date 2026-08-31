using System.Diagnostics;

namespace DpiBypass.Core.Network;

/// <summary>
/// Whether an arm is now genuinely in place, and why not when it is not.
/// </summary>
/// <remarks>
/// The reason travels with the answer because "the driver declined it" and "the value is
/// written but only takes effect after a restart nobody consented to" are different
/// facts, and a user reading a rejection deserves the one that actually happened.
/// </remarks>
public readonly record struct LatencyArmOutcome(bool Applied, string? Reason = null)
{
    public static readonly LatencyArmOutcome Success = new(true);

    public static LatencyArmOutcome Failed(string reason) => new(false, reason);
}

/// <summary>The two things an experiment needs to be able to do to a machine.</summary>
/// <remarks>
/// Kept behind an interface so the runner never learns what it is switching. Snapshot
/// bookkeeping, driver quirks and rollback belong to whoever implements this; ordering,
/// settling, validity and statistics belong to the runner.
/// </remarks>
public interface ILatencyExperimentArm
{
    /// <summary>
    /// Puts the change in place and establishes that the machine is running with it.
    /// </summary>
    /// <remarks>
    /// "In place" means operationally in effect, never merely stored. An arm that reports
    /// success on a registry write the driver has not picked up would have the runner
    /// measure the original behaviour twice and call the difference a result.
    /// </remarks>
    Task<LatencyArmOutcome> ApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>Puts the original back. Throws when it cannot, which ends the run.</summary>
    Task RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the link still carries traffic in the current state.</summary>
    Task<bool> IsUsableAsync(CancellationToken cancellationToken = default);
}

public sealed record LatencyExperimentPlan
{
    public required NetworkFingerprint Network { get; init; }

    public required LatencyOptimizationCandidate Candidate { get; init; }

    public LatencyProbeRequest Probe { get; init; } = LatencyProbeRequest.Benchmark;

    public int MinimumCycles { get; init; } = 2;

    public int MaximumCycles { get; init; } = 4;

    /// <summary>Cycles that may be thrown away for unequal conditions before giving up.</summary>
    public int MaximumDiscardedCycles { get; init; } = 2;

    /// <summary>
    /// A hard wall-clock ceiling, so a link that never settles cannot hold the user for ever.
    /// </summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>Chooses the order of the arms; fixed so a run can be reproduced exactly.</summary>
    public int Seed { get; init; }

    public LatencyEvaluationOptions Evaluation { get; init; } = LatencyEvaluationOptions.Strict;

    /// <summary>Probe size an inconclusive run grows to before it gives up.</summary>
    public int AdaptiveProbeCount { get; init; } = 120;

    /// <summary>Machine and radio state when the plan was made; a change ends the run.</summary>
    public LatencyEnvironment? Reference { get; init; }

    /// <summary>How long the driver and link get after a write before the next measurement.</summary>
    public TimeSpan Settling => Candidate.Descriptor.SettlingTime;
}

public sealed record LatencyExperimentOutcome
{
    public required LatencyVerdict Verdict { get; init; }

    public IReadOnlyList<LatencyPair> Pairs { get; init; } = [];

    /// <summary>The most recent measurement taken with the change applied.</summary>
    public LatencyMeasurement? LastOptimised { get; init; }

    public bool LostConnectivity { get; init; }

    public bool DriverRefused { get; init; }

    /// <summary>Set when conditions changed underneath the experiment and it was stopped.</summary>
    public string? Aborted { get; init; }
}

public interface ILatencyExperimentRunner
{
    Task<LatencyExperimentOutcome> RunAsync(
        LatencyExperimentPlan plan,
        ILatencyExperimentArm arm,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs one candidate as alternating, settled, validated A/B cycles.
/// </summary>
/// <remarks>
/// <para>
/// Four things separate this from "measure, change, measure". The arms alternate, so a
/// link that drifts over the run cannot land its drift on one of them. Every write is
/// followed by a settling pause, because the first packets after a driver change measure
/// the change rather than the state. Every arm carries the machine state it ran under, so
/// a cycle where the CPU spiked or the Wi-Fi dropped a rate is thrown away instead of
/// counted. And the whole thing is bounded in both cycles and wall-clock time, because a
/// link that never gives a clean answer has given an answer: no.
/// </para>
/// <para>
/// The adapter is left exactly as it was found, on every path including cancellation.
/// </para>
/// </remarks>
public sealed class PairedLatencyExperimentRunner : ILatencyExperimentRunner
{
    private readonly ILatencyProbe _probe;
    private readonly ILatencyEnvironmentSampler _environment;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<string>? _log;

    public PairedLatencyExperimentRunner(
        ILatencyProbe probe,
        ILatencyEnvironmentSampler? environment = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Action<string>? log = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _environment = environment ?? new WindowsLatencyEnvironmentSampler(log);
        _delay = delay ?? Task.Delay;
        _log = log;
    }

    /// <summary>
    /// Which arm runs first in a given cycle.
    /// </summary>
    /// <remarks>
    /// Strict alternation, which over consecutive cycles produces A B / B A / A B - the
    /// ABBA arrangement that cancels a linear drift rather than accumulating it. The seed
    /// decides which way the first cycle goes so a rerun is not always identical, and it
    /// is an input rather than a random draw so a test can pin the sequence.
    /// </remarks>
    public static LatencyCycleOrder OrderFor(int cycleIndex, int seed)
        => ((cycleIndex + seed) & 1) == 0 ? LatencyCycleOrder.BaselineFirst : LatencyCycleOrder.CandidateFirst;

    public async Task<LatencyExperimentOutcome> RunAsync(
        LatencyExperimentPlan plan,
        ILatencyExperimentArm arm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(arm);

        var pairs = new List<LatencyPair>();
        var probe = plan.Probe;
        var discarded = 0;
        var elapsed = Stopwatch.StartNew();
        LatencyMeasurement? lastOptimised = null;
        var verdict = LatencyComparison.Evaluate(plan.Candidate, pairs, plan.Evaluation);

        while (pairs.Count < plan.MaximumCycles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (elapsed.Elapsed > plan.Budget)
            {
                _log?.Invoke($"latency.cycle.budget: {plan.Candidate.PropertyName} · süre sınırına ulaşıldı.");
                break;
            }

            var order = OrderFor(pairs.Count + discarded, plan.Seed);
            LatencyMeasurement baseline;
            LatencyMeasurement optimised;
            LatencyEnvironment baselineEnvironment;
            LatencyEnvironment optimisedEnvironment;

            if (order == LatencyCycleOrder.BaselineFirst)
            {
                (baseline, baselineEnvironment) = await MeasureAsync(plan, probe, cancellationToken).ConfigureAwait(false);

                var applied = await arm.ApplyAsync(cancellationToken).ConfigureAwait(false);
                if (!applied.Applied)
                {
                    return Refused(verdict, pairs, lastOptimised, applied.Reason);
                }

                await SettleAsync(plan, cancellationToken).ConfigureAwait(false);

                if (!await arm.IsUsableAsync(cancellationToken).ConfigureAwait(false))
                {
                    return LostLink(verdict, pairs, lastOptimised);
                }

                (optimised, optimisedEnvironment) = await MeasureAsync(plan, probe, cancellationToken).ConfigureAwait(false);
                await arm.RestoreAsync(cancellationToken).ConfigureAwait(false);
                await SettleAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var applied = await arm.ApplyAsync(cancellationToken).ConfigureAwait(false);
                if (!applied.Applied)
                {
                    return Refused(verdict, pairs, lastOptimised, applied.Reason);
                }

                await SettleAsync(plan, cancellationToken).ConfigureAwait(false);

                if (!await arm.IsUsableAsync(cancellationToken).ConfigureAwait(false))
                {
                    return LostLink(verdict, pairs, lastOptimised);
                }

                (optimised, optimisedEnvironment) = await MeasureAsync(plan, probe, cancellationToken).ConfigureAwait(false);
                await arm.RestoreAsync(cancellationToken).ConfigureAwait(false);
                await SettleAsync(plan, cancellationToken).ConfigureAwait(false);

                (baseline, baselineEnvironment) = await MeasureAsync(plan, probe, cancellationToken).ConfigureAwait(false);
            }

            lastOptimised = optimised;

            var pair = new LatencyPair
            {
                Baseline = baseline,
                Candidate = optimised,
                Order = order,
                BaselineEnvironment = baselineEnvironment,
                CandidateEnvironment = optimisedEnvironment,
            };

            // A route, adapter or access point change means the later cycles are not
            // measuring the same path as the earlier ones. Nothing measured after that
            // belongs in the same experiment.
            if (MovedNetwork(plan.Reference, baselineEnvironment) || MovedNetwork(plan.Reference, optimisedEnvironment))
            {
                _log?.Invoke($"latency.cycle.aborted: {plan.Candidate.PropertyName} · ağ koşulları değişti.");
                return new LatencyExperimentOutcome
                {
                    Verdict = verdict with
                    {
                        Outcome = LatencyVerdictOutcome.Rejected,
                        Reason = "ölçüm sırasında ağ/rota değişti",
                        Cycles = pairs.Count,
                    },
                    Pairs = pairs,
                    LastOptimised = lastOptimised,
                    Aborted = "network-changed",
                };
            }

            if (!pair.IsComparable && discarded < plan.MaximumDiscardedCycles)
            {
                discarded++;
                _log?.Invoke(
                    $"latency.cycle.discarded: {plan.Candidate.PropertyName} · "
                    + $"{DescribeMismatch(pair)}, tekrarlanıyor.");
                continue;
            }

            pairs.Add(pair);
            verdict = LatencyComparison.Evaluate(plan.Candidate, pairs, plan.Evaluation);

            _log?.Invoke(
                $"latency.cycle.completed: {plan.Candidate.PropertyName} · tur {pairs.Count} · "
                + $"sıra {order} · median {pair.Delta.MedianMs:+0.0;-0.0;0.0} ms · karar {verdict.Outcome}");

            if (verdict.Outcome != LatencyVerdictOutcome.Inconclusive)
            {
                break;
            }

            // Still undecided after the cycles that were meant to settle it: the sample
            // is what is short, so the remaining cycles get a bigger one.
            if (pairs.Count >= plan.MinimumCycles && probe.ProbeCount < plan.AdaptiveProbeCount)
            {
                probe = probe.Widened(plan.AdaptiveProbeCount);
                _log?.Invoke(
                    $"latency.cycle.widened: {plan.Candidate.PropertyName} · örnek sayısı {probe.ProbeCount}.");
            }
        }

        if (verdict.Outcome == LatencyVerdictOutcome.Inconclusive)
        {
            verdict = verdict with
            {
                Outcome = LatencyVerdictOutcome.Rejected,
                Reason = $"{pairs.Count} turda kararlı bir sonuç çıkmadı ({verdict.Reason})",
            };
        }

        return new LatencyExperimentOutcome
        {
            Verdict = verdict,
            Pairs = pairs,
            LastOptimised = lastOptimised,
        };
    }

    private async Task<(LatencyMeasurement Measurement, LatencyEnvironment Environment)> MeasureAsync(
        LatencyExperimentPlan plan,
        LatencyProbeRequest probe,
        CancellationToken cancellationToken)
    {
        // The first read only arms the CPU counter; the second reports what the machine
        // was doing across the measurement itself.
        _environment.Sample(plan.Network);
        var measurement = await _probe.MeasureAsync(plan.Network, probe, cancellationToken).ConfigureAwait(false);
        return (measurement, _environment.Sample(plan.Network));
    }

    private Task SettleAsync(LatencyExperimentPlan plan, CancellationToken cancellationToken)
        => plan.Settling > TimeSpan.Zero ? _delay(plan.Settling, cancellationToken) : Task.CompletedTask;

    private static bool MovedNetwork(LatencyEnvironment? reference, LatencyEnvironment current)
    {
        if (reference is null)
        {
            return false;
        }

        return reference.InterfaceIndex != current.InterfaceIndex
            || Differs(reference.RouteHash, current.RouteHash)
            || Differs(reference.AccessPointHash, current.AccessPointHash);

        static bool Differs(string? first, string? second)
            => first is not null && second is not null && !string.Equals(first, second, StringComparison.Ordinal);
    }

    private static string DescribeMismatch(LatencyPair pair)
    {
        if (!pair.HasSameMeasurementPath)
        {
            return "iki yarı aynı hedefi ölçmedi";
        }

        if (!pair.HasComparableEnvironment
            && pair.BaselineEnvironment is { } baseline
            && pair.CandidateEnvironment is { } candidate)
        {
            return baseline.DescribeDifference(candidate);
        }

        return $"yük durumu eşleşmedi ({pair.Baseline.Load.State} / {pair.Candidate.Load.State})";
    }

    private static LatencyExperimentOutcome Refused(
        LatencyVerdict verdict,
        IReadOnlyList<LatencyPair> pairs,
        LatencyMeasurement? last,
        string? reason = null) => new()
        {
            Verdict = verdict with
            {
                Outcome = LatencyVerdictOutcome.Rejected,
                Reason = reason ?? "sürücü değeri canlı olarak uygulamadı",
                Cycles = pairs.Count,
            },
            Pairs = pairs,
            LastOptimised = last,
            DriverRefused = true,
        };

    private static LatencyExperimentOutcome LostLink(
        LatencyVerdict verdict,
        IReadOnlyList<LatencyPair> pairs,
        LatencyMeasurement? last) => new()
        {
            Verdict = verdict with
            {
                Outcome = LatencyVerdictOutcome.Rejected,
                Reason = "bağlantı koptu",
                Cycles = pairs.Count,
            },
            Pairs = pairs,
            LastOptimised = last,
            LostConnectivity = true,
        };
}
