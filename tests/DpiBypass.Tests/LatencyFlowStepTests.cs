using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The steps the card shows while it works, and what each one is allowed to claim.
/// </summary>
/// <remarks>
/// The card already said what state a run was in; what it could not say was where in the
/// process that state sat, which is what makes a two minute measurement indistinguishable
/// from a stuck one. These rows are derived from the result rather than reported by the
/// run, so each is a statement about evidence that either exists or does not.
/// </remarks>
public sealed class LatencyFlowStepTests
{
    private static LatencyFlowStep Step(LatencyStatusView status, int ordinal)
        => LatencyFlowSteps.From(status).Single(step => step.Ordinal == ordinal);

    private static LatencyStatusView View(LatencyOptimizationResult result, bool modeEnabled = true)
        => LatencyStatusView.From(modeEnabled, result);

    [Fact]
    public void TheStepsAreTheSixOfTheFlowInOrder()
    {
        var steps = LatencyFlowSteps.From(View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Disabled,
            StatusLine = "kapalı",
        }));

        Assert.Equal([1, 2, 3, 4, 5, 6], steps.Select(s => s.Ordinal));
        Assert.Equal(
            ["Hedef seçimi", "Bağlantı doğrulaması", "Başlangıç ölçümü", "Aday denemeleri", "Karşılaştırma", "Kabul / geri alma"],
            steps.Select(s => s.Title));
    }

    /// <summary>Nothing measured says so, rather than showing a zero for every row.</summary>
    [Fact]
    public void ARunThatHasNotStartedSaysNothingWasMeasured()
    {
        var steps = LatencyFlowSteps.From(View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Disabled,
            StatusLine = "kapalı",
        }));

        Assert.All(steps.Skip(1), step => Assert.Equal(LatencyFlowStepState.Pending, step.State));
        Assert.All(steps.Skip(1), step => Assert.Contains("ölçülmedi", step.Detail));
        Assert.DoesNotContain(steps, step => step.Detail.Contains("0.0 ms", StringComparison.Ordinal));
    }

    /// <summary>A measured baseline shows the numbers it actually has, loss included or not.</summary>
    [Fact]
    public void AMeasuredBaselineCarriesItsOwnNumbersAndNeverAZeroForUnmeasuredLoss()
    {
        var passive = Fake.Measurement(24, load: LatencyLoadState.Idle) with
        {
            Source = LatencySampleSource.PassiveObservation,
            PacketLossPercent = null,
        };

        var step = Step(
            View(new LatencyOptimizationResult
            {
                Status = LatencyOptimizationStatus.MonitoringOnly,
                StatusLine = "ölçüldü",
                Before = passive,
            }),
            3);

        Assert.Equal(LatencyFlowStepState.Done, step.State);
        Assert.Contains("ortanca 24", step.Detail);
        Assert.Contains("kayıp ölçülmedi", step.Detail);
        Assert.DoesNotContain("kayıp 0", step.Detail);
    }

    /// <summary>
    /// A candidate an obstacle blocked is not reported as one that was measured and lost.
    /// </summary>
    /// <remarks>
    /// The distinction the user acts on: one is fixed by a single permission, the other
    /// cannot be fixed at all. Collapsing them turned "you have not allowed adapter
    /// restarts" into "there is nothing here to gain".
    /// </remarks>
    [Fact]
    public void ABlockedCandidateIsNotCountedAsOneThatWasMeasuredAndLost()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.NoGain,
            StatusLine = "fark yok",
            Before = Fake.Measurement(30, load: LatencyLoadState.Idle),
            Verdicts =
            [
                new()
                {
                    Outcome = LatencyVerdictOutcome.NotMeasured,
                    PropertyName = "InterruptModeration",
                    Description = "Kesme yumuşatma",
                    Reason = "bağdaştırıcı yeniden başlatma izni yok",
                    Cycles = 0,
                    Cause = LatencyOutcomeCause.AwaitingPermission,
                },
            ],
        });

        var step = Step(status, 4);

        Assert.Equal(LatencyFlowStepState.Skipped, step.State);
        Assert.Contains("denenemedi", step.Detail);
        Assert.DoesNotContain("elendi", step.Detail);
        Assert.Equal("warn", step.Severity);
    }

    /// <summary>A candidate that really was measured and lost says exactly that.</summary>
    [Fact]
    public void ACandidateThatWasMeasuredAndLostIsReportedAsMeasured()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.NoGain,
            StatusLine = "fark yok",
            Before = Fake.Measurement(30, load: LatencyLoadState.Idle),
            Verdicts =
            [
                new()
                {
                    Outcome = LatencyVerdictOutcome.Rejected,
                    PropertyName = "RSS",
                    Description = "Alım tarafı ölçekleme",
                    Reason = "ölçüldü, anlamlı fark yok",
                    Cycles = 6,
                    Cause = LatencyOutcomeCause.MeasuredNoGain,
                },
            ],
        });

        var step = Step(status, 4);

        Assert.Equal(LatencyFlowStepState.Done, step.State);
        Assert.Contains("ölçüldü ve elendi", step.Detail);
        Assert.DoesNotContain("denenemedi", step.Detail);
    }

    /// <summary>
    /// The comparison row names the metric that moved, so a median gain cannot be read
    /// into a run that only steadied the delay variation.
    /// </summary>
    [Fact]
    public void TheComparisonNamesTheMetricThatActuallyImproved()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = "iyileşti",
            Before = Fake.Measurement(30, load: LatencyLoadState.Idle),
            After = Fake.Measurement(29, load: LatencyLoadState.Idle),
            AppliedChanges = ["InterruptModeration"],
            VerifiedImprovement = new LatencyDelta { MedianMs = 0.4, P95Ms = 1, P99Ms = 6, JitterMs = 3.2 },
            ImprovedMetric = "dalgalanma",
        });

        var step = Step(status, 5);

        Assert.Equal(LatencyFlowStepState.Done, step.State);
        Assert.StartsWith("dalgalanma", step.Detail);
        Assert.Contains("p99", step.Detail);
        Assert.Contains("dalgalanma 3.2 ms", step.Detail);
    }

    /// <summary>A change that regressed and was taken back reads as taken back.</summary>
    [Fact]
    public void ARolledBackChangeSaysItWasRolledBack()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.NoGain,
            StatusLine = "geriledi",
            Before = Fake.Measurement(30, load: LatencyLoadState.Idle),
            Verdicts =
            [
                new()
                {
                    Outcome = LatencyVerdictOutcome.Rejected,
                    PropertyName = "FlowControl",
                    Description = "Akış denetimi",
                    Reason = "geriledi, geri alındı",
                    Cycles = 6,
                    Cause = LatencyOutcomeCause.MeasuredRegression,
                },
            ],
        });

        Assert.Equal("geriledi", Step(status, 5).Detail);
        Assert.Equal("geri alındı", Step(status, 6).Detail);
        Assert.Equal(LatencyFlowStepState.Done, Step(status, 6).State);
    }

    /// <summary>A rollback that failed is the one state that needs the user to act.</summary>
    [Fact]
    public void AFailedRollbackIsAnErrorRatherThanAnOutcome()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Failed,
            StatusLine = "geri alınamadı",
            RestoreFailed = true,
        });

        var step = Step(status, 6);

        Assert.Equal(LatencyFlowStepState.Failed, step.State);
        Assert.Equal("error", step.Severity);
        Assert.Contains("kurtarma", step.Detail);
    }

    /// <summary>A run the user stopped is not a failure.</summary>
    [Fact]
    public void ACancelledRunReadsAsStoppedRatherThanAsFailed()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Cancelled,
            StatusLine = "durduruldu",
        });

        Assert.Equal(LatencyFlowStepState.Skipped, Step(status, 6).State);
        Assert.DoesNotContain(
            LatencyFlowSteps.From(status),
            step => step.State == LatencyFlowStepState.Failed);
    }

    /// <summary>An adapter with nothing to try says so, rather than "no gain found".</summary>
    [Fact]
    public void AnUnsupportedAdapterSkipsTheCandidateStepWithAReason()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Unsupported,
            StatusLine = "desteklenmiyor",
        });

        Assert.Equal(LatencyFlowStepState.Skipped, Step(status, 4).State);
        Assert.Equal(LatencyFlowStepState.Skipped, Step(status, 2).State);
        Assert.Contains("desteklenen ayar yok", Step(status, 2).Detail);
    }

    /// <summary>
    /// A route-reference measurement is labelled as one, because it is not the
    /// application's own round trip and must never be compared with one.
    /// </summary>
    [Fact]
    public void ARouteReferenceTargetIsLabelledAsSuch()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.MonitoringOnly,
            StatusLine = "ölçüldü",
            TargetLabel = "1.1.1.1",
            TargetProtocol = "ICMP",
            RouteReferenceOnly = true,
            Before = Fake.Measurement(20, load: LatencyLoadState.Idle),
        });

        Assert.Contains("yol referansı", Step(status, 1).Detail);
        Assert.Contains("ICMP", Step(status, 1).Detail);
    }

    /// <summary>Every row carries a word for its state, not only a colour.</summary>
    [Fact]
    public void EveryStepCarriesAWordForItsStateAndNotOnlyAColour()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Measuring,
            StatusLine = "ölçülüyor",
        });

        Assert.All(LatencyFlowSteps.From(status), step => Assert.False(string.IsNullOrWhiteSpace(step.StateLabel)));
    }

    /// <summary>A run in flight shows what is happening rather than a made-up percentage.</summary>
    [Fact]
    public void ARunInFlightShowsTheStepItIsOnRatherThanAPercentage()
    {
        var status = View(new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Measuring,
            StatusLine = "ölçülüyor",
        });

        var steps = LatencyFlowSteps.From(status);

        Assert.Contains(steps, step => step.State == LatencyFlowStepState.Running);
        Assert.DoesNotContain(steps, step => step.Detail.Contains('%', StringComparison.Ordinal));
    }
}
