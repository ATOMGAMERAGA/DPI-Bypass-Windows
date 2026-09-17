using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// How the sweep spends its probes, which is what decides whether its answer means anything.
/// </summary>
/// <remarks>
/// A sweep over a link nobody controls is a measurement with a moving baseline: a Wi-Fi
/// roam, somebody else's download, a DNS cache warming up. None of that can be removed,
/// but measuring A five times and then B five times attributes all of it to B, and
/// interleaving them spreads it across every candidate instead. These tests pin that,
/// along with the budget that stops a sweep running for ever on a network where nothing
/// answers.
/// </remarks>
public sealed class StrategySweepBehaviourTests
{
    private sealed class Engine
    {
        private readonly List<string> _writes = [];

        public BypassStrategy Strategy { get; set; } = StrategyLibrary.Default;

        public IReadOnlyList<string> Writes => _writes;

        public void Write(BypassStrategy strategy)
        {
            Strategy = strategy;
            _writes.Add(strategy.Id);
        }
    }

    private sealed class Writer : IStrategyWriter
    {
        private readonly Engine _engine;

        public Writer(Engine engine) => _engine = engine;

        public bool Revoked { get; set; }

        public BypassStrategy Current => _engine.Strategy;

        public bool IsCurrent => !Revoked;

        public bool TryWrite(BypassStrategy strategy)
        {
            if (Revoked)
            {
                return false;
            }

            _engine.Write(strategy);
            return true;
        }
    }

    /// <summary>
    /// Answers as a function of which recipe is currently installed and which host is
    /// being asked about, and records every probe in order.
    /// </summary>
    private sealed class Probe : IConnectivityProbe
    {
        private readonly Engine _engine;
        private readonly Func<string, string, int, ProbeResult> _answer;
        private readonly List<(string Strategy, string Host)> _calls = [];

        public Probe(Engine engine, Func<string, string, int, ProbeResult> answer)
        {
            _engine = engine;
            _answer = answer;
        }

        public IReadOnlyList<(string Strategy, string Host)> Calls => _calls;

        public Task<ProbeResult> ProbeAsync(string host, bool fetchHttp = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var strategy = _engine.Strategy.Id;
            _calls.Add((strategy, host));
            return Task.FromResult(_answer(strategy, host, _calls.Count));
        }
    }

    private static ProbeResult Blocked => new(ProbeOutcome.HandshakeReset, TimeSpan.FromMilliseconds(15));

    private static ProbeResult Reachable(double connectMs) => new(
        ProbeOutcome.Reachable,
        TimeSpan.FromMilliseconds(connectMs * 4),
        "TLS 1.3",
        HttpStatus: null,
        Dns: TimeSpan.FromMilliseconds(3),
        Connect: TimeSpan.FromMilliseconds(connectMs),
        Handshake: TimeSpan.FromMilliseconds(connectMs * 3));

    /// <summary>Passthrough is only ever the control arm; it is never a measured candidate.</summary>
    private static bool IsControl(string strategyId) => strategyId == StrategyLibrary.Passthrough.Id;

    [Fact]
    public async Task AnOpenNetworkIsReportedRatherThanDesynced()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var probe = new Probe(engine, (_, _, _) => Reachable(20));
        var tuner = new StrategyTuner(probe);

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        Assert.True(result.NetworkWasAlreadyOpen);
        Assert.Equal(SweepEnding.NetworkAlreadyOpen, result.Ending);
        Assert.Equal(StrategyLibrary.Passthrough, result.Winner);

        // One probe, then it stopped. It does not go on to measure a dozen recipes for a
        // network that is not filtering anything.
        Assert.Single(probe.Calls);
    }

    /// <summary>
    /// Screening is one probe per candidate, and it stops as soon as the shortlist is full.
    /// </summary>
    [Fact]
    public async Task ScreeningCostsOneProbePerCandidateAndStopsAtTheShortlist()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var probe = new Probe(engine, (strategy, _, _) => IsControl(strategy) ? Blocked : Reachable(20));
        var tuner = new StrategyTuner(probe) { ShortlistSize = 3, Rounds = 1 };

        await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        // The control, then one screening probe each for exactly three candidates.
        var screening = probe.Calls
            .Skip(1)
            .TakeWhile((call, index) => index < 3)
            .ToArray();

        Assert.Equal(3, screening.Select(call => call.Strategy).Distinct().Count());
        Assert.All(screening, call => Assert.Equal(StrategyTargets.Default[0].Host, call.Host));
    }

    /// <summary>
    /// The measurement stage interleaves candidates instead of finishing one before
    /// starting the next.
    /// </summary>
    [Fact]
    public async Task TheMeasurementStageInterleavesTheShortlist()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var probe = new Probe(engine, (strategy, _, _) => IsControl(strategy) ? Blocked : Reachable(20));
        var tuner = new StrategyTuner(probe) { ShortlistSize = 2, Rounds = 3 };

        await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        // Everything that is not the control arm, collapsed into runs of one candidate.
        var blocks = new List<string>();
        foreach (var strategy in probe.Calls.Where(call => !IsControl(call.Strategy)).Select(call => call.Strategy))
        {
            if (blocks.Count == 0 || blocks[^1] != strategy)
            {
                blocks.Add(strategy);
            }
        }

        Assert.Equal(2, blocks.Distinct().Count());

        // One screening block each, then one block per candidate per round. The blocks
        // strictly alternate, which is exactly what "never measured back to back" means:
        // whatever the link did during round two was done to both of them.
        Assert.Equal(2 + (2 * 3), blocks.Count);

        for (var i = 1; i < blocks.Count; i++)
        {
            Assert.NotEqual(blocks[i - 1], blocks[i]);
        }
    }

    /// <summary>Every target is probed, not just the one screening used.</summary>
    [Fact]
    public async Task EveryTargetIsMeasuredNotJustTheFirst()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var probe = new Probe(engine, (strategy, _, _) => IsControl(strategy) ? Blocked : Reachable(20));
        var tuner = new StrategyTuner(probe) { ShortlistSize = 1, Rounds = 1 };

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        var hosts = probe.Calls.Skip(2).Select(call => call.Host).Distinct().ToArray();

        Assert.Equal(StrategyTargets.Default.Count, hosts.Length);
        Assert.All(StrategyTargets.Default, target => Assert.Contains(target.Host, hosts));
        Assert.NotEmpty(result.Measurements);
    }

    /// <summary>
    /// A candidate that reaches the web endpoint but never the gateway does not win, even
    /// when it is the fastest thing measured.
    /// </summary>
    [Fact]
    public async Task ACandidateThatOnlyReachesOneTargetDoesNotWin()
    {
        var engine = new Engine();
        var writer = new Writer(engine);

        var gateway = StrategyTargets.Default.First(target => target.Required && target.Host != StrategyTargets.Default[0].Host).Host;
        var screened = new List<string>();

        var probe = new Probe(engine, (strategy, host, _) =>
        {
            if (IsControl(strategy))
            {
                return Blocked;
            }

            if (screened.Count < 2 && !screened.Contains(strategy))
            {
                screened.Add(strategy);
            }

            // The first candidate screened is quick but never reaches the gateway.
            var partial = screened.Count > 0 && strategy == screened[0];
            if (partial && host == gateway)
            {
                return Blocked;
            }

            return Reachable(partial ? 5 : 40);
        });

        var tuner = new StrategyTuner(probe) { ShortlistSize = 2, Rounds = 3 };
        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        Assert.NotNull(result.Selection);
        Assert.NotEqual(screened[0], result.Winner?.Id);
        Assert.Contains(
            result.Selection!.Eliminated,
            rejection => rejection.StrategyId == screened[0] && rejection.Reason.Contains(gateway, StringComparison.Ordinal));
    }

    /// <summary>A sweep where nothing gets through installs nothing and says so.</summary>
    [Fact]
    public async Task ASweepWhereNothingGetsThroughRestoresAndReportsIt()
    {
        var engine = new Engine { Strategy = StrategyLibrary.FakeTtl6SplitSni };
        var writer = new Writer(engine);
        var tuner = new StrategyTuner(new Probe(engine, (_, _, _) => Blocked));

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        Assert.Null(result.Winner);
        Assert.Equal(SweepEnding.NothingWorked, result.Ending);
        Assert.Equal(StrategyLibrary.FakeTtl6SplitSni, engine.Strategy);
        Assert.Contains("ulaşamadı", result.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// The result carries the split timings, so nothing downstream has to infer a round
    /// trip from a handshake.
    /// </summary>
    [Fact]
    public async Task TheMeasurementKeepsTheConnectAndTheHandshakeApart()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var probe = new Probe(engine, (strategy, _, _) => IsControl(strategy) ? Blocked : Reachable(25));
        var tuner = new StrategyTuner(probe) { ShortlistSize = 1, Rounds = 3 };

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);
        var measurement = Assert.Single(result.Measurements);

        Assert.Equal(25, measurement.MedianConnectMs!.Value, 1);
        Assert.Equal(75, measurement.MedianHandshakeMs!.Value, 1);
        Assert.Equal(3, measurement.MedianDnsMs!.Value, 1);
    }

    /// <summary>
    /// A sweep that measured nothing but reachability says the speed test did not run,
    /// rather than implying one did.
    /// </summary>
    [Fact]
    public async Task ASweepWithNoLoadTestSaysTheSpeedTestDidNotRun()
    {
        // No incumbent, so the hysteresis rule does not answer first and the rationale is
        // the one written for a fresh choice.
        var engine = new Engine { Strategy = StrategyLibrary.Passthrough };
        var writer = new Writer(engine);
        var probe = new Probe(engine, (strategy, _, call) => IsControl(strategy) ? Blocked : Reachable(20 + (call % 5)));
        var tuner = new StrategyTuner(probe) { ShortlistSize = 2, Rounds = 3 };

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        Assert.NotNull(result.Selection);
        Assert.Equal(SelectionConfidence.LatencyOnly, result.Selection!.Confidence);
        Assert.Contains("hız testi yapılmadı", result.Rationale, StringComparison.OrdinalIgnoreCase);

        // And no candidate carries a throughput figure nobody measured.
        Assert.All(result.Measurements, measurement => Assert.Null(measurement.Throughput));
    }

    /// <summary>
    /// A superseded sweep installs nothing, including the restore in its finally block.
    /// </summary>
    [Fact]
    public async Task ASupersededSweepInstallsNothing()
    {
        var engine = new Engine { Strategy = StrategyLibrary.Disorder2 };
        var writer = new Writer(engine) { Revoked = true };
        var probe = new Probe(engine, (_, _, _) => Blocked);

        var result = await new StrategyTuner(probe).FindBestAsync(writer, IspCatalog.Unknown);

        Assert.Null(result.Winner);
        Assert.Equal(SweepEnding.Superseded, result.Ending);
        Assert.Empty(engine.Writes);
        Assert.Empty(probe.Calls);
        Assert.Contains("ağ değiştiği için", result.Rationale, StringComparison.Ordinal);
    }

    /// <summary>A sweep revoked half way through stops measuring and installs nothing more.</summary>
    [Fact]
    public async Task ASweepRevokedMidFlightStopsAtOnce()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var probe = new Probe(engine, (_, _, call) =>
        {
            if (call >= 3)
            {
                writer.Revoked = true;
            }

            return Blocked;
        });

        var result = await new StrategyTuner(probe).FindBestAsync(writer, IspCatalog.Unknown);

        Assert.Equal(SweepEnding.Superseded, result.Ending);
        Assert.Null(result.Winner);

        // It gave up on the write that came after the revocation rather than carrying on
        // probing a strategy it had failed to install.
        Assert.True(probe.Calls.Count <= 4, $"kept probing after revocation: {probe.Calls.Count} calls");
    }

    /// <summary>Cancelling restores what the session was using rather than the last candidate.</summary>
    [Fact]
    public async Task ACancelledSweepRestoresWhatTheSessionWasUsing()
    {
        var engine = new Engine { Strategy = StrategyLibrary.FakeBadSeqSplitSni };
        var writer = new Writer(engine);
        using var cancellation = new CancellationTokenSource();

        var probe = new Probe(engine, (_, _, call) =>
        {
            if (call >= 2)
            {
                cancellation.Cancel();
            }

            return Blocked;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new StrategyTuner(probe).FindBestAsync(writer, IspCatalog.Unknown, true, cancellation.Token));

        Assert.Equal(StrategyLibrary.FakeBadSeqSplitSni, engine.Strategy);
    }

    /// <summary>
    /// Verifying a remembered profile checks every required target, not one of them.
    /// </summary>
    [Fact]
    public async Task VerifyingARememberedProfileChecksEveryRequiredTarget()
    {
        var engine = new Engine();
        var gateway = StrategyTargets.Default.First(target => target.Required && target.Host != StrategyTargets.Default[0].Host).Host;
        var probe = new Probe(engine, (_, host, _) => host == gateway ? Blocked : Reachable(20));

        var verified = await new StrategyTuner(probe).VerifyCurrentAsync();

        Assert.False(verified);
        Assert.Contains(probe.Calls, call => call.Host == gateway);
    }

    [Fact]
    public async Task VerifyingPassesOnlyWhenEveryRequiredTargetAnswers()
    {
        var engine = new Engine();
        var probe = new Probe(engine, (_, _, _) => Reachable(20));

        Assert.True(await new StrategyTuner(probe).VerifyCurrentAsync());

        var required = StrategyTargets.Default.Count(target => target.Required);
        Assert.Equal(required, probe.Calls.Count);
    }

    /// <summary>
    /// The measurement stage stops when its budget is spent, and reports the ending rather
    /// than presenting a partial sweep as a complete one.
    /// </summary>
    [Fact]
    public async Task AnExhaustedBudgetEndsTheSweepAndIsReported()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var probe = new Probe(engine, (strategy, _, _) => IsControl(strategy) ? Blocked : Reachable(20));

        var tuner = new StrategyTuner(probe)
        {
            ShortlistSize = 2,
            Rounds = 50,
            MeasurementBudget = TimeSpan.Zero,
        };

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);

        Assert.Equal(SweepEnding.BudgetSpent, result.Ending);

        // The screening probes still happened; the rounds did not.
        Assert.True(probe.Calls.Count < 10, $"kept measuring past the budget: {probe.Calls.Count} calls");
    }

    /// <summary>
    /// A failed probe contributes a failure, never a connect time - so a recipe that times
    /// out cannot look like a slow but working one.
    /// </summary>
    [Fact]
    public async Task AFailedProbeNeverContributesATiming()
    {
        var engine = new Engine();
        var writer = new Writer(engine);
        var seen = 0;

        var probe = new Probe(engine, (strategy, host, _) =>
        {
            if (IsControl(strategy))
            {
                return Blocked;
            }

            // Fails on the optional target only, so the candidate stays eligible.
            return host == StrategyTargets.Default[^1].Host && ++seen > 0 ? Blocked : Reachable(30);
        });

        var tuner = new StrategyTuner(probe) { ShortlistSize = 1, Rounds = 3 };
        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown);
        var measurement = Assert.Single(result.Measurements);

        Assert.Contains(measurement.Samples, sample => !sample.Success);
        Assert.All(
            measurement.Samples.Where(sample => !sample.Success),
            sample =>
            {
                Assert.Null(sample.Connect);
                Assert.Null(sample.Handshake);
            });

        Assert.Equal(30, measurement.MedianConnectMs!.Value, 1);
    }
}
