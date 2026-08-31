using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// How a cycle is run: the order of the arms, the pauses between them, and what makes a
/// pair worth keeping.
/// </summary>
public sealed class LatencyExperimentOrderTests
{
    /// <summary>
    /// Consecutive cycles alternate, which over four cycles is A B / B A / A B / B A -
    /// the arrangement that cancels a linear drift instead of accumulating it on one arm.
    /// </summary>
    [Fact]
    public void CycleOrderAlternatesRatherThanRepeating()
    {
        var orders = Enumerable.Range(0, 4)
            .Select(cycle => PairedLatencyExperimentRunner.OrderFor(cycle, seed: 0))
            .ToArray();

        Assert.Equal(
            [
                LatencyCycleOrder.BaselineFirst,
                LatencyCycleOrder.CandidateFirst,
                LatencyCycleOrder.BaselineFirst,
                LatencyCycleOrder.CandidateFirst,
            ],
            orders);
    }

    [Fact]
    public void TheSeedDecidesWhichWayTheFirstCycleGoesAndIsReproducible()
    {
        Assert.Equal(LatencyCycleOrder.CandidateFirst, PairedLatencyExperimentRunner.OrderFor(0, seed: 1));
        Assert.Equal(LatencyCycleOrder.BaselineFirst, PairedLatencyExperimentRunner.OrderFor(1, seed: 1));

        // Same inputs, same answer, every time.
        Assert.Equal(
            PairedLatencyExperimentRunner.OrderFor(7, seed: 3),
            PairedLatencyExperimentRunner.OrderFor(7, seed: 3));
    }

    [Fact]
    public async Task TheRunnerActuallyMeasuresBothArmsBothWaysRound()
    {
        var runner = Runner(out var probe, out var controller);
        var arm = new RecordingArm(controller);

        var outcome = await runner.RunAsync(Plan(gain: 6), arm, CancellationToken.None);

        Assert.True(outcome.Verdict.Accepted);
        Assert.Equal(2, outcome.Pairs.Count);
        Assert.Contains(outcome.Pairs, pair => pair.Order == LatencyCycleOrder.BaselineFirst);
        Assert.Contains(outcome.Pairs, pair => pair.Order == LatencyCycleOrder.CandidateFirst);

        // Two arms per cycle, and the change is taken back off at the end of each.
        Assert.Equal(4, probe.Measurements);
        Assert.Equal(2, arm.Events.Count(entry => entry == "restore"));
        Assert.False(arm.Applied);
    }

    /// <summary>
    /// The first packets after a driver write measure the transition, not the state, so
    /// the runner waits the intervention's own settling period before measuring again.
    /// </summary>
    [Fact]
    public async Task EveryWriteIsFollowedByTheInterventionsSettlingPeriod()
    {
        var delay = new RecordingDelay();
        var runner = Runner(out _, out var controller, delay);
        var settling = TimeSpan.FromMilliseconds(1234);

        await runner.RunAsync(
            Plan(gain: 6) with
            {
                Candidate = Fake.Candidate() with
                {
                    Descriptor = new InterventionDescriptor
                    {
                        Id = "test",
                        Title = "test",
                        Mechanism = "test",
                        SettlingTime = settling,
                    },
                },
            },
            new RecordingArm(controller),
            CancellationToken.None);

        // Applied and restored once per cycle, two cycles: four settled pauses.
        Assert.Equal(4, delay.Waits.Count);
        Assert.All(delay.Waits, wait => Assert.Equal(settling, wait));
    }

    [Fact]
    public async Task ADriverThatDeclinesTheWriteEndsTheExperimentWithoutMeasuring()
    {
        var runner = Runner(out var probe, out _);

        var outcome = await runner.RunAsync(
            Plan(gain: 6),
            new RecordingArm { RefuseApply = true },
            CancellationToken.None);

        Assert.True(outcome.DriverRefused);
        Assert.False(outcome.Verdict.Accepted);
        Assert.Empty(outcome.Pairs);
        Assert.Contains("uygulamadı", outcome.Verdict.Reason, StringComparison.Ordinal);

        // One baseline was taken before the write; nothing after it.
        Assert.Equal(1, probe.Measurements);
    }

    [Fact]
    public async Task LosingTheLinkEndsTheExperimentAndSaysSo()
    {
        var runner = Runner(out _, out _);

        var outcome = await runner.RunAsync(
            Plan(gain: 6),
            new RecordingArm { BreakLink = true },
            CancellationToken.None);

        Assert.True(outcome.LostConnectivity);
        Assert.Contains("bağlantı koptu", outcome.Verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Roaming to another access point, or onto another adapter, changes the path being
    /// measured. Anything after that belongs to a different experiment.
    /// </summary>
    [Fact]
    public async Task AChangedRouteOrAccessPointAbortsTheRound()
    {
        var controller = new FakeController();
        var probe = FakeProbe.Improves(controller, gain: 6);
        var environment = new FakeEnvironmentSampler(
            Steady(),
            Steady(),
            Steady() with { AccessPointHash = "moved" });

        var runner = new PairedLatencyExperimentRunner(probe, environment, (_, _) => Task.CompletedTask);

        var outcome = await runner.RunAsync(
            Plan(gain: 6) with { Reference = Steady() },
            new RecordingArm(controller),
            CancellationToken.None);

        Assert.Equal("network-changed", outcome.Aborted);
        Assert.False(outcome.Verdict.Accepted);
        Assert.Contains("ağ/rota değişti", outcome.Verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cycle where the machine itself changed - a CPU spike, mains to battery, a Wi-Fi
    /// rate drop - is not a measurement of the setting, so it is thrown away and re-run.
    /// </summary>
    [Theory]
    [InlineData("cpu")]
    [InlineData("power")]
    [InlineData("signal")]
    public async Task ACycleWhereTheMachineChangedIsDiscardedRatherThanCounted(string change)
    {
        var disturbed = change switch
        {
            "cpu" => Steady() with { CpuBusyPercent = 95 },
            "power" => Steady() with { Power = PowerSource.Battery },
            _ => Steady() with { WifiSignalQuality = 20 },
        };

        var controller = new FakeController();
        var probe = FakeProbe.Improves(controller, gain: 6);

        // First arm normal, second arm disturbed, then steady for the rest.
        var environment = new FakeEnvironmentSampler(Steady(), disturbed, Steady());
        var runner = new PairedLatencyExperimentRunner(
            probe,
            environment,
            (_, _) => Task.CompletedTask,
            log: _ => { });

        var outcome = await runner.RunAsync(Plan(gain: 6), new RecordingArm(controller), CancellationToken.None);

        Assert.All(
            outcome.Pairs,
            pair => Assert.True(pair.HasComparableEnvironment, "an incomparable pair reached the verdict"));
    }

    [Fact]
    public void AnUnsampledEnvironmentIsNotTreatedAsAMismatch()
    {
        var pair = new LatencyPair { Baseline = Fake.Measurement(20), Candidate = Fake.Measurement(19) };

        Assert.True(pair.HasComparableEnvironment);
        Assert.True(pair.IsComparable);
    }

    /// <summary>
    /// Still undecided after the minimum cycles means the sample is what is short, so the
    /// remaining cycles get a bigger one rather than the run simply giving up.
    /// </summary>
    [Fact]
    public async Task AnUndecidedExperimentGrowsItsSampleBeforeGivingUp()
    {
        var controller = new FakeController();

        // A link that helps in one cycle and hurts in the next never settles, which is
        // exactly the state the extra samples exist for.
        var candidateCall = 0;
        var probe = new FakeProbe(controller, (live, _) => live.Contains(Fake.DefaultKeyword)
            ? Fake.Measurement(candidateCall++ % 2 == 0 ? 24 : 36)
            : Fake.Measurement(30));

        var runner = new PairedLatencyExperimentRunner(
            probe,
            new FakeEnvironmentSampler(),
            (_, _) => Task.CompletedTask,
            log: _ => { });

        var outcome = await runner.RunAsync(
            Plan(gain: 0) with { MinimumCycles = 2, MaximumCycles = 4, AdaptiveProbeCount = 200 },
            new RecordingArm(controller),
            CancellationToken.None);

        Assert.False(outcome.Verdict.Accepted);
        Assert.Contains(probe.ProbeCounts, count => count >= 200);
    }

    private static LatencyEnvironment Steady() => new()
    {
        CpuBusyPercent = 10,
        Power = PowerSource.Mains,
        InterfaceIndex = 10,
        RouteHash = "route",
        AccessPointHash = "ap",
        WifiSignalQuality = 80,
    };

    private static LatencyExperimentPlan Plan(double gain) => new()
    {
        Network = Fake.Network("experiment"),
        Candidate = Fake.Candidate(),
        Probe = LatencyProbeRequest.Benchmark,
        MinimumCycles = 2,
        MaximumCycles = 3,
        Seed = 0,
    };

    private static PairedLatencyExperimentRunner Runner(
        out FakeProbe probe,
        out FakeController controller,
        RecordingDelay? delay = null)
    {
        controller = new FakeController();
        probe = FakeProbe.Improves(controller, gain: 6);

        return new PairedLatencyExperimentRunner(
            probe,
            new FakeEnvironmentSampler(),
            delay is null ? (_, _) => Task.CompletedTask : delay.WaitAsync);
    }
}

/// <summary>The rules that decide what a number is allowed to prove.</summary>
public sealed class LatencyEvidenceTests
{
    /// <summary>
    /// A p99 estimated from forty replies is the worst sample wearing a percentile's
    /// name, so it cannot accept a candidate on its own however clean it looks.
    /// </summary>
    [Fact]
    public void ATailOnlyWinNeedsAHundredRepliesBeforeItCanDecideAnything()
    {
        var thin = P99Pairs(40, gain: 20);
        var thick = P99Pairs(120, gain: 20);

        Assert.NotEqual(LatencyVerdictOutcome.Accepted, Evaluate(thin).Outcome);
        Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(thick).Outcome);
        Assert.Equal("p99", Evaluate(thick).WinningMetric);
    }

    /// <summary>
    /// Every cycle running the same way round means anything drifting over the run landed
    /// on one arm, which no amount of repetition separates from an effect.
    /// </summary>
    [Fact]
    public void AProductionRunWillNotDecideOnCyclesThatAllRanTheSameWayRound()
    {
        var unbalanced = Fake.Pairs((42, 36), (42, 36))
            .Select(pair => pair with { Order = LatencyCycleOrder.BaselineFirst })
            .ToArray();

        var balanced = new[]
        {
            unbalanced[0],
            unbalanced[1] with { Order = LatencyCycleOrder.CandidateFirst },
        };

        Assert.NotEqual(LatencyVerdictOutcome.Accepted, Evaluate(unbalanced).Outcome);
        Assert.Equal(LatencyVerdictOutcome.Accepted, Evaluate(balanced).Outcome);
    }

    /// <summary>
    /// Cycles that disagree as much as they agree leave "no difference" on the table,
    /// which the resampled interval says and the point estimate does not.
    /// </summary>
    [Fact]
    public void AGainWhoseIntervalStillIncludesZeroIsNotAccepted()
    {
        var contradictory = new[]
        {
            Pair(42, 30, LatencyCycleOrder.BaselineFirst),
            Pair(42, 52, LatencyCycleOrder.CandidateFirst),
            Pair(42, 31, LatencyCycleOrder.BaselineFirst),
            Pair(42, 51, LatencyCycleOrder.CandidateFirst),
        };

        var verdict = Evaluate(contradictory, maximumCycles: 4);

        Assert.NotEqual(LatencyVerdictOutcome.Accepted, verdict.Outcome);
    }

    [Fact]
    public void TheResampledIntervalIsDeterministicAndBracketsTheMean()
    {
        double[] deltas = [4.0, 5.0, 6.0, 5.5];

        var first = LatencyStatistics.PairedMeanInterval(deltas);
        var second = LatencyStatistics.PairedMeanInterval(deltas);

        Assert.Equal(first, second);
        Assert.True(first.Lower > 0, $"lower bound {first.Lower} should exclude zero");
        Assert.True(first.Lower <= 5.125 && first.Upper >= 5.125);
    }

    [Fact]
    public void OneDifferenceCannotBoundAnythingSoItsIntervalIncludesZero()
    {
        var (lower, upper) = LatencyStatistics.PairedMeanInterval([7.0]);

        Assert.True(lower <= 0);
        Assert.True(upper >= 7.0);
    }

    [Fact]
    public void TheSignFlipTestIsExactForSmallSamplesAndSymmetric()
    {
        // With two pairs there are four sign patterns and two of them are at least as
        // extreme as the observed one, so the smallest attainable p-value is 0.5.
        Assert.Equal(0.5, LatencyStatistics.PairedSignFlipPValue([5.0, 5.0]), precision: 6);

        // A sample with no effect at all cannot be extreme.
        Assert.Equal(1.0, LatencyStatistics.PairedSignFlipPValue([0.0, 0.0]), precision: 6);
    }

    /// <summary>
    /// Accepting settings one at a time proves each on its own. It does not prove the
    /// machine is better with all of them, which is a separate paired measurement.
    /// </summary>
    [Fact]
    public void TheBundleConfirmationIsItselfPairedRatherThanASingleReading()
    {
        var paired = new[]
        {
            Pair(40, 34, LatencyCycleOrder.BaselineFirst),
            Pair(40, 34, LatencyCycleOrder.CandidateFirst),
        };

        Assert.True(LatencyComparison.ConfirmsBundle(paired, cpuSensitive: false));

        // The same total improvement in one direction only is not confirmation.
        var oneWay = paired.Select(pair => pair with { Order = LatencyCycleOrder.BaselineFirst }).ToArray();
        Assert.False(LatencyComparison.ConfirmsBundle(oneWay, cpuSensitive: false));
    }

    [Fact]
    public void APairMeasuringADifferentEndpointOrProtocolIsNeverComparable()
    {
        var baseline = Fake.Measurement(30, endpoint: "1.1.1.1");

        Assert.False(new LatencyPair
        {
            Baseline = baseline,
            Candidate = Fake.Measurement(20, endpoint: "8.8.8.8"),
        }.IsComparable);

        Assert.False(new LatencyPair
        {
            Baseline = baseline,
            Candidate = Fake.Measurement(20) with { Protocol = "TCP/25565" },
        }.IsComparable);
    }

    private static LatencyVerdict Evaluate(IReadOnlyList<LatencyPair> pairs, int maximumCycles = 3)
        => LatencyComparison.Evaluate(
            Fake.Candidate(),
            pairs,
            LatencyEvaluationOptions.Strict with { MinimumCycles = 2, MaximumCycles = maximumCycles });

    private static LatencyPair Pair(double baseline, double candidate, LatencyCycleOrder order) => new()
    {
        Baseline = Fake.Measurement(baseline),
        Candidate = Fake.Measurement(candidate),
        Order = order,
    };

    private static IReadOnlyList<LatencyPair> P99Pairs(int attempts, double gain) =>
    [
        new LatencyPair
        {
            Baseline = Fake.Measurement(30, p95: 60, p99: 100, attempts: attempts),
            Candidate = Fake.Measurement(30, p95: 60, p99: 100 - gain, attempts: attempts),
            Order = LatencyCycleOrder.BaselineFirst,
        },
        new LatencyPair
        {
            Baseline = Fake.Measurement(30, p95: 60, p99: 100, attempts: attempts),
            Candidate = Fake.Measurement(30, p95: 60, p99: 100 - gain, attempts: attempts),
            Order = LatencyCycleOrder.CandidateFirst,
        },
    ];
}
