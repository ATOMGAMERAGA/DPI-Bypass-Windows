using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

public sealed class LatencySmallGainTests
{
    [Theory]
    [InlineData(29)]
    [InlineData(60)]
    [InlineData(150)]
    public void OneMillisecondRepeatableGainSurvivesCandidateAndBundleEvaluation(double baseline)
    {
        var pairs = Pairs(baseline, 1, 1, 1, 1);
        var verdict = LatencyComparison.Evaluate(Fake.Candidate(), pairs, LatencyEvaluationOptions.Strict);

        Assert.True(verdict.Accepted, verdict.Reason);
        Assert.Equal("median", verdict.WinningMetric);
        Assert.True(verdict.ConfidenceLowerMs > 0);
        Assert.True(LatencyComparison.ConfirmsBundle(pairs, cpuSensitive: false));
        Assert.Equal(LatencyGainKind.Median,
            LatencyComparison.ConfirmGain(pairs[0].Baseline, pairs[0].Candidate));
    }

    [Fact]
    public void AContradictoryOneMillisecondGainIsNotKept()
        => Assert.False(LatencyComparison.Evaluate(Fake.Candidate(),
            Pairs(60, 2, -1, 2, 1), LatencyEvaluationOptions.Strict).Accepted);

    [Fact]
    public void NewlyEligibleSmallGainsNeedFourCyclesInProduction()
    {
        var verdict = LatencyComparison.Evaluate(Fake.Candidate(),
            Pairs(60, 1, 1), LatencyEvaluationOptions.Strict);
        Assert.Equal(LatencyVerdictOutcome.Inconclusive, verdict.Outcome);
    }

    [Fact]
    public void OneLargeCycleCannotHideThatOnlySmallGainsRepeat()
    {
        var pairs = Pairs(60, 1, 4);
        var verdict = LatencyComparison.Evaluate(Fake.Candidate(), pairs, LatencyEvaluationOptions.Strict);
        Assert.Equal(LatencyVerdictOutcome.Inconclusive, verdict.Outcome);
        Assert.False(LatencyComparison.ConfirmsBundle(pairs, cpuSensitive: false));
    }

    [Fact]
    public async Task OldWifiRejectionIsRetestedAndOneMillisecondIsCommittedAndShown()
    {
        const string property = AdapterInterventionCatalog.WlanMediaStreamingProperty;
        var network = Fake.Network("wifi-small-gain");
        var adapter = Fake.Capability(network) with
        {
            AdapterType = System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211,
            AdvancedProperties = [],
            WlanMediaStreamingEnabled = false,
        };
        var controller = new FakeController { Detect = _ => adapter };
        var probe = new FakeProbe(controller, (live, _) =>
            Fake.Measurement(live.Contains(property) ? 59 : 60, p95: 70, p99: 75));
        var scenario = new LatencyScenario(controller, probe,
            options: new LatencyOptimizerOptions { MinimumCycles = 2, MaximumCycles = 4 });
        scenario.Profiles.Profiles.Add(new LatencyProfile
        {
            NetworkKey = network.Key,
            AdapterId = adapter.AdapterId,
            CapabilityFingerprint = adapter.CapabilityFingerprint,
            VerifiedAt = DateTimeOffset.UtcNow,
            MethodologyVersion = 4,
            RejectedProperties = [property],
        });

        var result = await scenario.Optimizer.OptimizeAsync(network);
        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);
        Assert.Contains(property, controller.Live);
        Assert.Equal(1, result.VerifiedImprovement!.MedianMs);
        Assert.Equal(LatencyGainKind.Median, result.GainKind);
        Assert.All(controller.RestartPolicies, restart => Assert.False(restart.Allowed));
        Assert.Equal(LatencyTransactionState.Committed, scenario.Snapshots.Value!.State);
        Assert.True(controller.Applied.Count >= 10, "four candidate cycles and four independent bundle cycles plus reapplies");
        var headline = LatencyHeadline.From(LatencyStatusView.From(true, result), DateTimeOffset.UtcNow);
        Assert.True(headline.ShowsReduction);
        Assert.Equal("1 ms azalma", headline.Gain);

        await scenario.Optimizer.StopAndRestoreAsync();
        Assert.Empty(controller.Live);
        Assert.Null(scenario.Snapshots.Value);
    }

    [Fact]
    public void SmallGainDoesNotBypassPacketLossGuard()
    {
        var pairs = Pairs(60, 1, 1, 1, 1)
            .Select(pair => pair with { Candidate = pair.Candidate with { PacketLossPercent = 20, RemoteReplies = 19 } })
            .ToArray();
        Assert.False(LatencyComparison.Evaluate(Fake.Candidate(), pairs, LatencyEvaluationOptions.Strict).Accepted);
    }

    [Fact]
    public void ProfilesFromTheClosedWifiSessionImplementationAreRemeasured()
    {
        var network = Fake.Network("old-wifi");
        var adapter = Fake.Capability(network);
        var profile = new LatencyProfile
        {
            NetworkKey = network.Key,
            AdapterId = adapter.AdapterId,
            CapabilityFingerprint = adapter.CapabilityFingerprint,
            VerifiedAt = DateTimeOffset.UtcNow,
            MethodologyVersion = 4,
            RejectedProperties = [AdapterInterventionCatalog.WlanMediaStreamingProperty],
        };
        Assert.False(profile.Matches(network.Key, adapter));
    }

    private static LatencyPair[] Pairs(double baseline, params double[] gains)
        => gains.Select((gain, index) => new LatencyPair
        {
            Baseline = Fake.Measurement(baseline, p95: baseline + 10, p99: baseline + 15),
            Candidate = Fake.Measurement(baseline - gain, p95: baseline + 10, p99: baseline + 15),
            Order = PairedLatencyExperimentRunner.OrderFor(index, 0),
        }).ToArray();
}
