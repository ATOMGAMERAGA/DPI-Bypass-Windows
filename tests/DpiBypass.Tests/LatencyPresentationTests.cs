using DpiBypass.Core;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the card is allowed to say about a set of measurements.
/// </summary>
/// <remarks>
/// The numbers in these tests are fixtures, not readings from any machine. What is being
/// pinned is which field ends up under which heading.
/// </remarks>
public sealed class LatencyPresentationTests
{
    /// <summary>
    /// A round trip measured while the link was busy is never shown as the idle ping.
    /// </summary>
    /// <remarks>
    /// The deep test put its loaded window into <c>After</c>, and the status view read
    /// <c>After ?? Before</c> as the idle measurement - so a card could report the delay
    /// measured mid-upload as what the user sees when nothing is happening.
    /// </remarks>
    [Fact]
    public void TheIdleFigureStaysIdleWhenALoadedWindowIsAlsoPresent()
    {
        var idle = Fake.Measurement(25, load: LatencyLoadState.Idle);
        var loaded = Fake.Measurement(140, load: LatencyLoadState.UplinkLoaded);

        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.MonitoringOnly,
            StatusLine = "ölçüldü",
            Before = idle,

            // Even if a lane were to put a loaded window here, it is not the idle ping.
            After = loaded,
            UploadLoaded = loaded,
        });

        Assert.Equal(25, status.Idle!.MedianRttMs);
        Assert.Equal(140, status.UploadLoaded!.MedianRttMs);
        Assert.Null(status.IdleAfter);
    }

    /// <summary>The loaded lane leaves the idle-after field empty rather than filling it.</summary>
    [Fact]
    public void TheLoadedLaneDoesNotClaimAnIdleAfterMeasurement()
    {
        var lane = File.ReadAllText(Path.Combine(
            RepoFiles.CoreProjectDirectory, "Network", "Latency", "LoadedLatencyLane.cs"));

        Assert.DoesNotContain("After = upload.Loaded", lane, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verified gain is the confirmation experiment's own paired difference.
    /// </summary>
    /// <remarks>
    /// Not the start-to-finish difference. The two are deliberately different here: the
    /// link drifted 8 ms faster between the first baseline and the last reading, and only
    /// 3 ms of that is what the alternating confirmation attributed to the settings.
    /// </remarks>
    [Fact]
    public void TheVerifiedGainComesFromTheConfirmationNotFromTheFirstBaseline()
    {
        var confirmation = new LatencyDelta { MedianMs = 3, P95Ms = 4, P99Ms = 4, JitterMs = 1 };
        var drift = new LatencyDelta { MedianMs = 8, P95Ms = 9, P99Ms = 9, JitterMs = 2 };

        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = "uygulandı",
            Before = Fake.Measurement(33),
            After = Fake.Measurement(25),
            AppliedChanges = ["Kesme yumuşatma kapalı"],
            VerifiedImprovement = confirmation,
            BaselineComparison = drift,
            ImprovedMetric = "median",
        });

        Assert.Equal(3, status.Improvement!.MedianMs);
        Assert.Equal(8, status.BaselineComparison!.MedianMs);
        Assert.Equal("median", status.ImprovedMetric);

        // And the two are separate keys in the machine-readable form as well.
        var json = status.ToJson();
        Assert.Contains("\"improvement\"", json, StringComparison.Ordinal);
        Assert.Contains("\"baselineComparison\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A gain in jitter alone is described as a gain in jitter alone.
    /// </summary>
    [Fact]
    public void AJitterOnlyGainSaysSoRatherThanImplyingAMedianGain()
    {
        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = "uygulandı",
            Before = Fake.Measurement(25),
            After = Fake.Measurement(25),
            AppliedChanges = ["Kesme yumuşatma kapalı"],
            VerifiedImprovement = new LatencyDelta { MedianMs = 0, P95Ms = 0, P99Ms = 0, JitterMs = 2.4 },
            ImprovedMetric = "jitter",
        });

        Assert.Equal("jitter", status.ImprovedMetric);
        Assert.Equal(0, status.Improvement!.MedianMs);
    }

    /// <summary>
    /// A run with no adapter candidate still reports what it did measure.
    /// </summary>
    /// <remarks>
    /// The old flow returned "unsupported" before it measured anything at all, so a
    /// machine with no driver candidate got no connection measurement, no path split and
    /// no suggestion - only a sentence saying the feature did not apply.
    /// </remarks>
    [Fact]
    public void AMachineWithNoAdapterCandidateStillGetsAMeasurementAndANextStep()
    {
        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Unsupported,
            StatusLine = "ağ kartı adayı yok",
            Before = Fake.Measurement(31),
            Path = LatencyPathAnalysis.Describe(Fake.Measurement(31)),
            Lanes =
            [
                new LatencyLaneReport
                {
                    Lane = LatencyLane.TargetMeasurement,
                    State = LatencyLaneState.Completed,
                    Detail = "1.1.1.1 ölçüldü",
                },
                new LatencyLaneReport
                {
                    Lane = LatencyLane.AdapterSettings,
                    State = LatencyLaneState.NotApplicable,
                    Detail = "Uygun ağ kartı yok",
                },
                new LatencyLaneReport
                {
                    Lane = LatencyLane.LoadedLatency,
                    State = LatencyLaneState.Available,
                    Detail = "Henüz ölçülmedi",
                },
            ],
        });

        Assert.Equal(LatencySituation.NotAvailableNow, status.Situation);
        Assert.Equal(31, status.Idle!.MedianRttMs);
        Assert.NotNull(status.Path);

        // The lane that has not been run is the one offered.
        Assert.Equal(LatencyNextAction.LoadTest, status.NextAction);
        Assert.Equal(3, status.Lanes.Count);
    }

    /// <summary>
    /// An incomplete measurement is not reported as an absence of gain.
    /// </summary>
    [Fact]
    public void AnIncompleteLoadRunSaysSoRatherThanReportingNoGain()
    {
        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.MonitoringOnly,
            StatusLine = "yeterli yük oluşmadı",
            Before = Fake.Measurement(25),
            Lanes =
            [
                new LatencyLaneReport
                {
                    Lane = LatencyLane.LoadedLatency,
                    State = LatencyLaneState.Incomplete,
                    Detail = "Yeterli yük oluşmadığı için ölçüm tamamlanamadı.",
                },
            ],
        });

        Assert.Equal(LatencySituation.Incomplete, status.Situation);
        Assert.NotEqual(LatencySituation.NoDifference, status.Situation);
        Assert.Contains("tamamlanmadı", status.Suggestion, StringComparison.Ordinal);
    }

    /// <summary>
    /// A successful optimization and a run that only watched do not share a colour.
    /// </summary>
    [Fact]
    public void MonitoringOnlyDoesNotWearTheSuccessColour()
    {
        var applied = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = "uygulandı",
            Before = Fake.Measurement(30),
            After = Fake.Measurement(25),
            AppliedChanges = ["bir ayar"],
            VerifiedImprovement = new LatencyDelta { MedianMs = 5, P95Ms = 6, P99Ms = 6, JitterMs = 1 },
        });

        var watching = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.MonitoringOnly,
            StatusLine = "izleniyor",
            Before = Fake.Measurement(30),
        });

        Assert.Equal("ok", applied.Severity);
        Assert.NotEqual("ok", watching.Severity);
        Assert.NotEqual(applied.Situation, watching.Situation);
    }

    /// <summary>
    /// The rollback state is read from the verdict's cause, not from its Turkish wording.
    /// </summary>
    [Fact]
    public void TheRollbackStateSurvivesAChangeOfWording()
    {
        var rolled = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.NoGain,
            StatusLine = "geri alındı",
            Before = Fake.Measurement(30),
            Verdicts =
            [
                new LatencyVerdict
                {
                    Outcome = LatencyVerdictOutcome.Rejected,
                    Cause = LatencyOutcomeCause.MeasuredRegression,
                    PropertyName = Fake.DefaultKeyword,
                    Description = "bir ayar",

                    // Deliberately none of the words the old implementation searched for.
                    Reason = "sonuç bu turlarda daha iyi olmadı",
                    Cycles = 3,
                },
            ],
        });

        Assert.Equal(LatencySituation.RolledBack, rolled.Situation);
    }

    /// <summary>
    /// The measurement source and whether loss was counted both reach the JSON contract.
    /// </summary>
    [Fact]
    public void TheJsonSaysWhetherLossWasMeasuredAtAll()
    {
        var passive = LatencyMeasurement.Create(
            "203.0.113.9",
            "TCP/25565 (EStats)",
            [30, 31, 32],
            remoteAttempts: 0,
            [],
            0,
            source: LatencySampleSource.PassiveObservation);

        var json = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.MonitoringOnly,
            StatusLine = "ölçüldü",
            Before = passive,
        }).ToJson();

        Assert.Contains("\"source\": \"PassiveObservation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"packetLossMeasured\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"packetLossPercent\": null", json, StringComparison.Ordinal);
    }
}

/// <summary>The Vodafone card's states, as values rather than as report text.</summary>
public sealed class VodafoneCardTests
{
    /// <summary>The mode off is not a failure, and it does not hide the saved networks.</summary>
    [Fact]
    public void TheModeBeingOffIsNotAFailureState()
    {
        var view = HotspotStatusView.From(Status(enabled: false, registeredNetworks: 2));

        Assert.Equal("off", view.Severity);
        Assert.Equal(HotspotRunState.NotRun, view.Run);
        Assert.Equal(2, view.RegisteredNetworks);
        Assert.Empty(view.Cards);
    }

    /// <summary>An unregistered current network can be saved without toggling the mode.</summary>
    [Fact]
    public void TheCurrentNetworkCanBeSavedWithoutTurningTheModeOffAndOn()
    {
        var unregistered = HotspotStatusView.From(Status(enabled: true, registeredHere: false));
        var registered = HotspotStatusView.From(Status(enabled: true, registeredHere: true));

        Assert.True(unregistered.CanRememberThisNetwork);
        Assert.False(registered.CanRememberThisNetwork);
    }

    /// <summary>
    /// "Not used", "not supported" and "not measured" are not the same as "failed".
    /// </summary>
    /// <remarks>
    /// IPv6 being absent is normal on most mobile links, the plan entitlement is a
    /// question nothing here can answer, and an unmeasured latency is neither good nor
    /// bad. Painting all three red sends users to fix things that are not broken.
    /// </remarks>
    [Fact]
    public void AbsentUnsupportedAndUnmeasuredAreNotShownAsFailures()
    {
        var view = HotspotStatusView.From(Status(enabled: true, result: Result(
            hasIpv6: false,
            ipv6Works: false,
            medianRtt: null)));

        var quality = Assert.Single(view.Cards, card => card.Title == "Bağlantı kalitesi");
        Assert.Equal(HotspotCheckState.NotMeasured, quality.State);

        var plan = Assert.Single(view.Cards, card => card.Title.StartsWith("Plan", StringComparison.Ordinal));
        Assert.Equal(HotspotCheckState.NotSupported, plan.State);
        Assert.Equal(HotspotDiagnosticResult.PlanEntitlement, plan.Value);

        var ipv6 = Assert.Single(view.TechnicalDetails, card => card.Title == "IPv6");
        Assert.Equal(HotspotCheckState.NotUsed, ipv6.State);

        // Only a real fault is a failure.
        Assert.DoesNotContain(view.Cards, card => card.State == HotspotCheckState.Failed);
    }

    /// <summary>A configured address family that carries nothing is a real failure.</summary>
    [Fact]
    public void AConfiguredAddressThatCarriesNoTrafficIsAFailure()
    {
        var view = HotspotStatusView.From(Status(enabled: true, result: Result(
            hasIpv6: true,
            ipv6Works: false)));

        var ipv6 = Assert.Single(view.TechnicalDetails, card => card.Title == "IPv6");
        Assert.Equal(HotspotCheckState.Failed, ipv6.State);
    }

    /// <summary>Loss that nothing counted is not rendered as zero percent.</summary>
    [Fact]
    public void UnmeasuredLossIsNotShownAsZero()
    {
        var view = HotspotStatusView.From(Status(enabled: true, result: Result(loss: null)));
        var quality = Assert.Single(view.Cards, card => card.Title == "Bağlantı kalitesi");

        Assert.Contains("kayıp ölçülmedi", quality.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("%0", quality.Value, StringComparison.Ordinal);
    }

    /// <summary>A check that could not finish is not shown as a completed one.</summary>
    [Fact]
    public void ACheckThatFailedIsNotShownAsAResult()
    {
        var view = HotspotStatusView.From(
            Status(enabled: true, result: Result()),
            failure: "ağ değişti");

        Assert.Equal(HotspotRunState.Failed, view.Run);
        Assert.Empty(view.Cards);
        Assert.Null(view.CheckedAt);
        Assert.Equal("ağ değişti", view.Suggestion);
    }

    /// <summary>A check in progress owns the card and shows no stale numbers.</summary>
    [Fact]
    public void ACheckInProgressDoesNotShowThePreviousResult()
    {
        var view = HotspotStatusView.From(Status(enabled: true, result: Result()), busy: true);

        Assert.Equal(HotspotRunState.Running, view.Run);
        Assert.Empty(view.Cards);
        Assert.Null(view.CheckedAt);
    }

    /// <summary>The legacy cleanup is offered only when there is something to clean.</summary>
    [Fact]
    public void TheLegacyCleanupIsHiddenOnAMachineThatNeverHadTheOldMode()
    {
        Assert.False(HotspotStatusView.From(Status(enabled: true)).LegacyCleanupAvailable);
        Assert.True(HotspotStatusView.From(Status(enabled: true), legacyResidue: true).LegacyCleanupAvailable);
    }

    /// <summary>
    /// The ordinary state these tests are about: the mode on and the rewrite running.
    /// </summary>
    /// <remarks>
    /// <c>TtlActive</c> follows the switch by default because a rule that is on for this
    /// network and not installed is its own headline and its own suggestion - see
    /// <see cref="VodafoneRewriteCardTests"/> - and it would otherwise mask the wording
    /// each of these tests is checking.
    /// </remarks>
    private static HotspotStatus Status(
        bool enabled,
        bool registeredHere = true,
        int registeredNetworks = 1,
        HotspotDiagnosticResult? result = null,
        bool? ttlActive = null) => new(
        VodafoneModeEnabled: enabled,
        DiagnosticsEnabled: enabled,
        RegisteredHere: registeredHere,
        RegisteredNetworks: registeredNetworks,
        NetworkName: "Telefonum",
        AdapterName: "Wi-Fi",
        LegacyCleanedAt: null,
        LastResult: result,
        TtlActive: ttlActive ?? (enabled && registeredHere));

    private static HotspotDiagnosticResult Result(
        bool hasIpv6 = true,
        bool ipv6Works = true,
        double? medianRtt = 42,
        double? loss = 0) => new()
    {
        NetworkName = "Telefonum",
        NetworkKey = "network",
        AdapterName = "Wi-Fi",
        HasIpv4 = true,
        HasIpv6 = hasIpv6,
        Ipv4Works = true,
        Ipv6Works = ipv6Works,
        DnsWorks = true,
        MedianRttMs = medianRtt,
        P95RttMs = medianRtt is { } median ? median + 20 : null,
        PacketLossPercent = loss,
        AddressKind = HotspotAddressKind.SharedAddressSpace,
        VpnAdapterActive = false,
    };
}

/// <summary>
/// A failure that put everything back is not the same as one that could not.
/// </summary>
/// <remarks>
/// Only the second needs the user to run a recovery. Mapping every failure to "some
/// settings could not be restored" sends people looking for damage that is not there,
/// and - worse - makes the message meaningless on the day it is true.
/// </remarks>
public sealed class RestoreFailureTests
{
    [Fact]
    public void AFailureThatRolledBackCleanlyDoesNotAskForARecovery()
    {
        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Failed,
            StatusLine = "optimizasyon başarısız oldu; değişiklikler geri alındı",
            RestoreFailed = false,
        });

        Assert.NotEqual(LatencySituation.RestoreFailed, status.Situation);
        Assert.NotEqual(LatencyNextAction.Recover, status.NextAction);
    }

    [Fact]
    public void AFailureThatLeftSomethingBehindAsksForARecovery()
    {
        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Failed,
            StatusLine = "bazı ayarlar geri yüklenemedi",
            RestoreFailed = true,
        });

        Assert.Equal(LatencySituation.RestoreFailed, status.Situation);
        Assert.Equal(LatencyNextAction.Recover, status.NextAction);
        Assert.Equal("warn", status.Severity);
    }

    /// <summary>
    /// The before/after note is two measurements, not one plus a delta.
    /// </summary>
    [Fact]
    public void TheIdleBeforeAndAfterAreBothRealMeasurements()
    {
        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.Active,
            StatusLine = "uygulandı",
            Before = Fake.Measurement(31, load: LatencyLoadState.Idle),
            After = Fake.Measurement(28, load: LatencyLoadState.Idle),
            AppliedChanges = ["bir ayar"],
            VerifiedImprovement = new LatencyDelta { MedianMs = 3, P95Ms = 3, P99Ms = 3, JitterMs = 0 },
        });

        Assert.Equal(31, status.IdleBefore!.MedianRttMs);
        Assert.Equal(28, status.IdleAfter!.MedianRttMs);
        Assert.Equal(28, status.Idle!.MedianRttMs);
    }

    /// <summary>A loaded "after" never becomes the idle before/after pair.</summary>
    [Fact]
    public void ALoadedAfterDoesNotBecomeTheIdlePair()
    {
        var status = LatencyStatusView.From(modeEnabled: true, new LatencyOptimizationResult
        {
            Status = LatencyOptimizationStatus.MonitoringOnly,
            StatusLine = "ölçüldü",
            Before = Fake.Measurement(25, load: LatencyLoadState.Idle),
            After = Fake.Measurement(140, load: LatencyLoadState.UplinkLoaded),
        });

        Assert.Equal(25, status.IdleBefore!.MedianRttMs);
        Assert.Null(status.IdleAfter);
    }
}
