using System.Net;
using DpiBypass.Core.Interop;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The native contract the passive instrument depends on.
/// </summary>
/// <remarks>
/// Windows validates the structure size it is handed, so a declaration that is short by
/// one field makes every call fail - and the probe's fallback made that failure look like
/// a connection that had nothing to report. Checking the layout is the cheapest way to
/// stop that recurring, and it runs anywhere because it reflects over the type rather
/// than calling the API.
/// </remarks>
public sealed class TcpEStatsLayoutTests
{
    [Fact]
    public void ThePathStructureMatchesTheSizeTheApiValidatesAgainst()
    {
        // TCP_ESTATS_PATH_ROD_v0 is forty ULONG fields. An earlier declaration collapsed
        // CurMss, MaxMss and MinMss into one, giving 152 bytes instead of 160.
        Assert.Equal(TcpEStats.PathRodSize, TcpEStats.MarshalledPathRodSize);
        Assert.Equal(160, TcpEStats.MarshalledPathRodSize);
        Assert.Equal(TcpEStats.PathRwSize, TcpEStats.MarshalledPathRwSize);
    }

    [Fact]
    public void TheFieldsTheProbeReadsAreAtTheOffsetsTheSdkPutsThemAt()
    {
        Assert.Equal(104, TcpEStats.PathRodOffsetOf("SampleRtt"));
        Assert.Equal(TcpEStats.SmoothedRttOffset, TcpEStats.PathRodOffsetOf("SmoothedRtt"));
        Assert.Equal(112, TcpEStats.PathRodOffsetOf("RttVar"));
        Assert.Equal(TcpEStats.CountRttOffset, TcpEStats.PathRodOffsetOf("CountRtt"));

        // The three fields whose absence caused the size mismatch.
        Assert.Equal(144, TcpEStats.PathRodOffsetOf("CurMss"));
        Assert.Equal(148, TcpEStats.PathRodOffsetOf("MaxMss"));
        Assert.Equal(152, TcpEStats.PathRodOffsetOf("MinMss"));
    }

    /// <summary>
    /// A failed call reports the native error rather than looking like "no data".
    /// </summary>
    [Fact]
    public void AFailedCallCarriesTheWindowsErrorIntoTheReport()
    {
        // 122 is ERROR_INSUFFICIENT_BUFFER: exactly what a short structure produced.
        var read = new TcpEStats.PathRead(TcpEStats.PathReadStatus.CallFailed, null, 122);

        Assert.False(read.HasSample);
        Assert.Contains("122", read.Describe(), StringComparison.Ordinal);

        // And the two non-failures do not read as failures.
        Assert.DoesNotContain("başarısız", new TcpEStats.PathRead(
            TcpEStats.PathReadStatus.NoEstimateYet, null).Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("başarısız", new TcpEStats.PathRead(
            TcpEStats.PathReadStatus.CollectionDisabled, null).Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The raw-mean and the smoothed estimate are kept apart, because they are not the
    /// same distribution and one must never stand in for the other.
    /// </summary>
    [Fact]
    public void TheRawMeanAndTheSmoothedEstimateAreSeparateNumbers()
    {
        var sample = new TcpEStats.PathSample(
            SmoothedRttMs: 30,
            SampleRttMs: 28,
            RttVarianceMs: 4,
            CountRtt: 10,
            SumRtt: 400);

        Assert.Equal(30, sample.SmoothedRttMs);
        Assert.Equal(40, sample.MeanRawRttMs);
    }
}

/// <summary>
/// What a passive observation may and may not be turned into.
/// </summary>
/// <remarks>
/// The instrument reads a counter the OS maintains and sends nothing. Every failure mode
/// the old code had came from treating the number of times it read that counter as a
/// number of packets sent.
/// </remarks>
public sealed class PassiveObservationTests
{
    /// <summary>
    /// A stable link produces no packet loss, however few readings it yields.
    /// </summary>
    /// <remarks>
    /// This is the shape of the bug: forty polls, six of which saw a new RTT, reported as
    /// "6 replies out of 40 attempts" and rendered as 85 percent packet loss on a link
    /// that had lost nothing.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(40)]
    public void APassiveSeriesNeverProducesPacketLossHoweverFewSamplesItYields(int samples)
    {
        var measurement = Passive([.. Enumerable.Repeat(30d, samples)]);

        Assert.Null(measurement.PacketLossPercent);
        Assert.False(measurement.LossMeasured);
        Assert.Null(measurement.LossQuantumPercent);

        // Attempts is what was sent, and this instrument sends nothing.
        Assert.Equal(0, measurement.RemoteAttempts);
        Assert.Equal(samples, measurement.RemoteReplies);
    }

    /// <summary>Repeated identical RTTs are readings, not lost packets.</summary>
    [Fact]
    public void AnUnchangingRoundTripIsNotLoss()
    {
        var measurement = Passive([30, 30, 30, 30]);

        Assert.Null(measurement.PacketLossPercent);
        Assert.Equal(30, measurement.MedianRttMs);
        Assert.Equal(0, measurement.JitterMs);
    }

    /// <summary>
    /// A connection that ends mid-series keeps the readings it gave and invents nothing.
    /// </summary>
    [Fact]
    public void AConnectionThatEndsMidSeriesKeepsWhatItGave()
    {
        var measurement = Passive([28, 31]);

        Assert.Equal(2, measurement.RemoteReplies);
        Assert.True(measurement.HasRemoteConnectivity);
        Assert.Null(measurement.PacketLossPercent);
    }

    /// <summary>An active probe still measures loss, and still reports zero as zero.</summary>
    [Fact]
    public void AnActiveProbeStillMeasuresLoss()
    {
        var clean = LatencyMeasurement.Create("1.1.1.1", "ICMP", [20, 21, 22, 23], 4, [], 0);
        var lossy = LatencyMeasurement.Create("1.1.1.1", "ICMP", [20, 21, 22], 4, [], 0);

        Assert.Equal(0, clean.PacketLossPercent);
        Assert.True(clean.LossMeasured);
        Assert.Equal(25, lossy.PacketLossPercent);
    }

    /// <summary>
    /// A comparison involving an instrument that does not count packets reports the loss
    /// difference as unknown rather than as zero.
    /// </summary>
    [Fact]
    public void ALossDeltaIsUnknownWhenEitherHalfDidNotCountPackets()
    {
        var passive = Passive([30, 31, 32]);
        var active = LatencyMeasurement.Create("1.1.1.1", "ICMP", [20, 21, 22], 3, [], 0);

        Assert.Null(LatencyDelta.Between(passive, active).LossPercent);
        Assert.Null(LatencyDelta.Between(active, passive).LossPercent);
        Assert.NotNull(LatencyDelta.Between(active, active).LossPercent);
    }

    private static LatencyMeasurement Passive(IReadOnlyList<double> samples)
        => LatencyMeasurement.Create(
            "203.0.113.9",
            "TCP/25565 (EStats)",
            samples,

            // Whatever a caller passes as attempts, a passive series records none.
            remoteAttempts: 40,
            [],
            0,
            source: LatencySampleSource.PassiveObservation);
}

/// <summary>
/// The line between "we measured this and it did not help" and "we never tried it".
/// </summary>
public sealed class UnmeasuredOutcomeTests
{
    /// <summary>
    /// A candidate held back for want of consent is offered again once consent is given.
    /// </summary>
    /// <remarks>
    /// The failure this pins: the first run reported the candidate as rejected because a
    /// restart was not permitted, the profile cached that as a measured result, and the
    /// second run - with permission now granted - skipped the candidate on the strength
    /// of an experiment that never ran.
    /// </remarks>
    [Fact]
    public async Task ACandidateSkippedForWantOfPermissionIsMeasuredOncePermissionArrives()
    {
        var network = Fake.Network("permission");
        var controller = new FakeController { NeedsRestart = Fake.DefaultKeyword };
        var profiles = new FakeProfileStore();

        var first = new LatencyScenario(controller, FakeProbe.Flat(controller), profiles: profiles);
        await first.Optimizer.OptimizeAsync(network);

        // Nothing was measured, so nothing about its performance was remembered.
        var profile = Assert.Single(profiles.Profiles);
        Assert.Empty(profile.RejectedProperties);
        Assert.Contains(profile.Unmeasured, entry =>
            entry.PropertyName == Fake.DefaultKeyword
            && entry.Cause == LatencyOutcomeCause.AwaitingPermission);

        // Consent arrives, and the candidate is tried for the first time.
        var consenting = new FakeController { NeedsRestart = Fake.DefaultKeyword };
        var second = new LatencyScenario(
            consenting,
            FakeProbe.Improves(consenting, gain: 6),
            profiles: profiles);
        second.Optimizer.Restart = new AdapterRestartPolicy { UserConsented = true };

        var result = await second.Optimizer.OptimizeAsync(network);

        Assert.Contains(Fake.DefaultKeyword, consenting.Applied);
        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);
    }

    /// <summary>
    /// A profile written without restart permission does not answer for a run that has it.
    /// </summary>
    [Fact]
    public void APermissionlessProfileDoesNotCoverAPermittedRun()
    {
        var withoutPermission = new LatencyProfileContext { TargetKey = "t", RestartAllowed = false };
        var withPermission = new LatencyProfileContext { TargetKey = "t", RestartAllowed = true };

        Assert.False(withoutPermission.Covers(withPermission));

        // The other direction is fine: more was reachable then than is reachable now.
        Assert.True(withPermission.Covers(withoutPermission));
    }

    /// <summary>
    /// Running out of time is not a finding, so it is not remembered as one.
    /// </summary>
    [Fact]
    public void ARunThatRanOutOfTimeIsNotStoredAsMeasuredIneffectiveness()
    {
        Assert.False(LatencyOutcomeCause.BudgetExhausted.IsPerformanceEvidence());
        Assert.False(LatencyOutcomeCause.Cancelled.IsPerformanceEvidence());
        Assert.False(LatencyOutcomeCause.InsufficientData.IsPerformanceEvidence());
        Assert.False(LatencyOutcomeCause.EnvironmentChanged.IsPerformanceEvidence());
        Assert.False(LatencyOutcomeCause.AwaitingPermission.IsPerformanceEvidence());
        Assert.False(LatencyOutcomeCause.Unsupported.IsPerformanceEvidence());
        Assert.False(LatencyOutcomeCause.NotApplied.IsPerformanceEvidence());

        // Only these two are answers about performance.
        Assert.True(LatencyOutcomeCause.MeasuredNoGain.IsPerformanceEvidence());
        Assert.True(LatencyOutcomeCause.MeasuredRegression.IsPerformanceEvidence());
    }

    /// <summary>
    /// A network change mid-experiment leaves the candidate unmeasured rather than judged.
    /// </summary>
    [Fact]
    public async Task AnEnvironmentChangeLeavesTheCandidateUnmeasured()
    {
        var probe = new ScriptedProbe(Fake.Measurement(25));
        var moved = new LatencyEnvironment { InterfaceIndex = 99, RouteHash = "other" };
        var runner = new PairedLatencyExperimentRunner(
            probe,
            new FakeEnvironmentSampler(moved),
            delay: (_, _) => Task.CompletedTask);

        var outcome = await runner.RunAsync(
            new LatencyExperimentPlan
            {
                Network = Fake.Network("moved"),
                Candidate = Fake.Candidate(),
                Reference = new LatencyEnvironment { InterfaceIndex = 10, RouteHash = "route" },
            },
            new AlwaysAppliesArm());

        Assert.Equal(LatencyVerdictOutcome.NotMeasured, outcome.Verdict.Outcome);
        Assert.Equal(LatencyOutcomeCause.EnvironmentChanged, outcome.Verdict.Cause);
        Assert.False(outcome.Verdict.Cause.IsPerformanceEvidence());
    }

    /// <summary>An arm the driver refused is unmeasured, not a rejected setting.</summary>
    [Fact]
    public async Task ARefusedArmIsUnmeasuredRatherThanRejected()
    {
        var runner = new PairedLatencyExperimentRunner(
            new ScriptedProbe(Fake.Measurement(25)),
            new FakeEnvironmentSampler(),
            delay: (_, _) => Task.CompletedTask);

        var outcome = await runner.RunAsync(
            new LatencyExperimentPlan { Network = Fake.Network("refused"), Candidate = Fake.Candidate() },
            new RefusingArm(LatencyOutcomeCause.AwaitingPermission));

        Assert.Equal(LatencyVerdictOutcome.NotMeasured, outcome.Verdict.Outcome);
        Assert.Equal(LatencyOutcomeCause.AwaitingPermission, outcome.Verdict.Cause);
    }

    /// <summary>The old profile records cannot be replayed by this build.</summary>
    /// <remarks>
    /// Versions 1 and 2 wrote every non-acceptance into the rejection list, including
    /// candidates that were never applied. The file does not record which were which, so
    /// the methodology version retires them rather than replaying a wrong answer.
    /// </remarks>
    [Fact]
    public void ProfilesWrittenByTheBuildWithTheRejectionBugAreNotTrusted()
    {
        var network = Fake.Network("migration");
        var adapter = Fake.Capability(network);

        var old = new LatencyProfile
        {
            NetworkKey = network.Key,
            AdapterId = adapter.AdapterId,
            CapabilityFingerprint = adapter.CapabilityFingerprint,
            VerifiedAt = DateTimeOffset.UtcNow,
            MethodologyVersion = 2,
            RejectedProperties = [Fake.DefaultKeyword],
        };

        Assert.False(old.Matches(network.Key, adapter));
        Assert.False(old.RejectionsUsable(DateTimeOffset.UtcNow));

        // The same record written by this build is usable again.
        Assert.True((old with { MethodologyVersion = LatencyProfile.CurrentMethodologyVersion })
            .RejectionsUsable(DateTimeOffset.UtcNow));
    }

    /// <summary>An arm that always applies, so the runner reaches its measurements.</summary>
    private sealed class AlwaysAppliesArm : ILatencyExperimentArm
    {
        public Task<LatencyArmOutcome> ApplyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(LatencyArmOutcome.Success);

        public Task RestoreAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsUsableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>An arm that cannot be put in place, for a stated reason.</summary>
    private sealed class RefusingArm : ILatencyExperimentArm
    {
        private readonly LatencyOutcomeCause _cause;

        public RefusingArm(LatencyOutcomeCause cause) => _cause = cause;

        public Task<LatencyArmOutcome> ApplyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(LatencyArmOutcome.Failed("yeniden başlatma onayı yok", _cause));

        public Task RestoreAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> IsUsableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    /// <summary>A probe that returns the same measurement for every request.</summary>
    private sealed class ScriptedProbe : ILatencyProbe
    {
        private readonly LatencyMeasurement _measurement;

        public ScriptedProbe(LatencyMeasurement measurement) => _measurement = measurement;

        public Task<LatencyMeasurement> MeasureAsync(
            NetworkFingerprint network,
            LatencyProbeRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_measurement);

        public Task<LatencyConnectivity> CheckConnectivityAsync(
            NetworkFingerprint network,
            string remoteEndpoint,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new LatencyConnectivity(true, true));
    }
}
