using System.Text.Json;
using DpiBypass.Core.Config;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the user is told the mode is doing, which is never simply "on" or "off".
/// </summary>
public sealed class LatencyStatusViewTests
{
    /// <summary>
    /// The distinction the whole feature turns on. A run that found nothing locally
    /// fixable is a mode that is on and watching, not a mode that is off.
    /// </summary>
    [Fact]
    public void NoGainWithTheModeOnIsMonitoringRatherThanOff()
    {
        var on = LatencyStatusView.From(modeEnabled: true, Result(LatencyOptimizationStatus.NoGain));
        var off = LatencyStatusView.From(modeEnabled: false, Result(LatencyOptimizationStatus.NoGain));

        Assert.Equal(LatencyModeState.NoLocalGain, on.State);
        Assert.True(on.ModeEnabled);
        Assert.StartsWith("Açık", on.Headline, StringComparison.Ordinal);
        Assert.Contains("kazanç bulunamadı", on.Headline, StringComparison.Ordinal);

        Assert.Equal(LatencyModeState.Off, off.State);
        Assert.NotEqual(on.State, off.State);
        Assert.NotEqual(on.Headline, off.Headline);
    }

    [Fact]
    public void AVerifiedGainNamesTheMetricsItActuallyMoved()
    {
        var result = Result(LatencyOptimizationStatus.Active) with
        {
            VerifiedImprovement = new LatencyDelta
            {
                MedianMs = 1.4,
                P95Ms = 3.8,
                P99Ms = 0,
                JitterMs = 0,
                LossPercent = 0,
            },
        };

        var status = LatencyStatusView.From(modeEnabled: true, result);

        Assert.Equal(LatencyModeState.GainApplied, status.State);
        Assert.Contains("1.4 ms", status.Headline, StringComparison.Ordinal);
        Assert.Contains("3.8 ms", status.Headline, StringComparison.Ordinal);
        Assert.Equal("ok", status.Severity);
    }

    [Fact]
    public void AGuardThatEmptiedTheQueueSaysHowMuchItRemoved()
    {
        var result = Result(LatencyOptimizationStatus.TrafficGuardActive) with
        {
            TrafficGuard = new TrafficGuardState
            {
                Status = TrafficGuardStatus.Active,
                Summary = "ok",
                UploadQueueingBeforeMs = 58,
                UploadQueueingAfterMs = 16,
            },
        };

        var status = LatencyStatusView.From(modeEnabled: true, result);

        Assert.Equal(LatencyModeState.TrafficGuardActive, status.State);
        Assert.Contains("42 ms", status.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sixty milliseconds of distance is not something a registry value can move, and the
    /// user is better served by being told that than by watching eight settings fail.
    /// </summary>
    [Fact]
    public void DelayThatLivesOnTheInternetPathIsAttributedToItByName()
    {
        var measurement = Fake.Measurement(62, gateway: 1.1);
        var result = Result(LatencyOptimizationStatus.NoGain) with
        {
            After = measurement,
            Path = LatencyPathAnalysis.Describe(measurement),
        };

        var status = LatencyStatusView.From(modeEnabled: true, result);

        Assert.Contains("ISP/WAN", status.Headline, StringComparison.Ordinal);
        Assert.Contains("yerel ayar bunu değiştiremez", status.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeTakenBackOffSaysWhyRatherThanJustSayingNothingHelped()
    {
        var result = Result(LatencyOptimizationStatus.NoGain) with
        {
            Verdicts =
            [
                new LatencyVerdict
                {
                    Outcome = LatencyVerdictOutcome.Rejected,

                    // The cause, not the wording, is what tells the card this was a
                    // measured regression that got taken back off.
                    Cause = LatencyOutcomeCause.MeasuredRegression,
                    PropertyName = Fake.DefaultKeyword,
                    Description = "Seçmeli askıya alma kapalı",
                    Reason = "bir turda paket kaybı %8.3 arttı",
                    Cycles = 2,
                },
            ],
        };

        var status = LatencyStatusView.From(modeEnabled: true, result);

        Assert.Equal(LatencySituation.RolledBack, status.Situation);
        Assert.Contains("müdahale geri alındı", status.Headline, StringComparison.Ordinal);
        Assert.Contains("paket kaybı", status.Headline, StringComparison.Ordinal);
        Assert.Single(status.Rejected);
        Assert.True(status.Rejected[0].WasMeasured);
    }

    /// <summary>
    /// The same shape of result, with a cause that means nothing was ever measured, must
    /// not be presented as a change that was tried and taken back off.
    /// </summary>
    [Fact]
    public void ACandidateBlockedOnPermissionIsNotShownAsARollback()
    {
        var result = Result(LatencyOptimizationStatus.NoGain) with
        {
            Verdicts =
            [
                new LatencyVerdict
                {
                    Outcome = LatencyVerdictOutcome.NotMeasured,
                    Cause = LatencyOutcomeCause.AwaitingPermission,
                    PropertyName = Fake.DefaultKeyword,
                    Description = "Kesme yumuşatma kapalı",
                    Reason = "yeniden başlatma onayı verilmedi",
                    Cycles = 0,
                },
            ],
        };

        var status = LatencyStatusView.From(modeEnabled: true, result);

        Assert.Equal(LatencySituation.NotAvailableNow, status.Situation);
        Assert.Equal(LatencyNextAction.AllowRestart, status.NextAction);
        Assert.False(status.Rejected[0].WasMeasured);
    }

    [Fact]
    public void AnUnsupportedAdapterStillOffersDiagnostics()
    {
        var status = LatencyStatusView.From(modeEnabled: true, Result(LatencyOptimizationStatus.Unsupported));

        Assert.Equal(LatencyModeState.UnsupportedAdapter, status.State);
        Assert.Contains("tanılaması kullanılabilir", status.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyMeasuringIdleLatencySaysSoRatherThanImplyingTheWholePicture()
    {
        var status = LatencyStatusView.From(modeEnabled: true, Result(LatencyOptimizationStatus.NeedsDeepTest));

        Assert.Equal(LatencyModeState.NeedsDeepTest, status.State);
        Assert.Contains("yalnız boşta", status.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// The JSON is a contract for whoever automates against it, so its shape is pinned
    /// here rather than being whatever the serialiser felt like on the day.
    /// </summary>
    [Fact]
    public void TheJsonStatusCarriesAStableSchema()
    {
        var result = Result(LatencyOptimizationStatus.Active) with
        {
            After = Fake.Measurement(21, attempts: 120),
            TargetLabel = "mc.example.com:25565",
            TargetProtocol = "TCP/25565",
        };

        using var document = JsonDocument.Parse(LatencyStatusView.From(true, result).ToJson());
        var root = document.RootElement;

        Assert.Equal(LatencyStatusView.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("GainApplied", root.GetProperty("state").GetString());
        Assert.True(root.GetProperty("modeEnabled").GetBoolean());
        Assert.Equal("mc.example.com:25565", root.GetProperty("target").GetProperty("label").GetString());
        Assert.Equal("TCP/25565", root.GetProperty("target").GetProperty("protocol").GetString());

        foreach (var name in new[]
        {
            "headline", "severity", "adapter", "idle", "uploadLoaded", "downloadLoaded",
            "path", "applied", "rejected", "notices", "improvement", "trafficGuard",
        })
        {
            Assert.True(root.TryGetProperty(name, out _), $"missing '{name}'");
        }
    }

    /// <summary>
    /// A p99 estimated from forty replies is the worst sample wearing a percentile's
    /// name, so the JSON reports null rather than a number that reads as one.
    /// </summary>
    [Fact]
    public void ThePublishedP99IsNullWhenTheSampleCannotSupportIt()
    {
        var thin = Result(LatencyOptimizationStatus.Active) with { After = Fake.Measurement(21, attempts: 40) };
        var thick = Result(LatencyOptimizationStatus.Active) with { After = Fake.Measurement(21, attempts: 120) };

        using var thinJson = JsonDocument.Parse(LatencyStatusView.From(true, thin).ToJson());
        using var thickJson = JsonDocument.Parse(LatencyStatusView.From(true, thick).ToJson());

        Assert.Equal(
            JsonValueKind.Null,
            thinJson.RootElement.GetProperty("idle").GetProperty("p99Ms").ValueKind);
        Assert.Equal(
            JsonValueKind.Number,
            thickJson.RootElement.GetProperty("idle").GetProperty("p99Ms").ValueKind);
    }

    private static LatencyOptimizationResult Result(LatencyOptimizationStatus status) => new()
    {
        Status = status,
        StatusLine = "detail",
        AdapterName = "Intel I225-V",
        NetworkKey = "net",
    };
}

/// <summary>What a saved result is allowed to be reused for, and for how long.</summary>
public sealed class LatencyProfileContextTests
{
    /// <summary>
    /// An acceptance is re-proved every time it is replayed. A rejection is never
    /// re-proved at all - it silently stops a candidate being measured - so it expires
    /// far sooner.
    /// </summary>
    [Fact]
    public void RejectionsExpireFarSoonerThanAcceptances()
    {
        Assert.True(LatencyProfile.RejectionMaximumAge < LatencyProfile.MaximumAge);

        var now = DateTimeOffset.UtcNow;
        var profile = Profile() with { VerifiedAt = now };

        Assert.True(profile.IsFresh(now + TimeSpan.FromDays(20)));
        Assert.False(profile.RejectionsUsable(now + TimeSpan.FromDays(20)));
        Assert.True(profile.RejectionsUsable(now + TimeSpan.FromHours(6)));
    }

    /// <summary>
    /// "This did not help" was true against that server, on that power source, on that
    /// access point, with the link idle. Any of those changing makes it an answer to a
    /// question nobody is asking.
    /// </summary>
    [Theory]
    [InlineData("target")]
    [InlineData("power")]
    [InlineData("accessPoint")]
    [InlineData("signal")]
    [InlineData("loaded")]
    public void AChangeInTheConditionsInvalidatesTheSavedRejections(string change)
    {
        var measured = Context();
        var now = DateTimeOffset.UtcNow;
        var profile = Profile() with { VerifiedAt = now, Context = measured };

        var current = change switch
        {
            "target" => measured with { TargetKey = "other" },
            "power" => measured with { Power = PowerSource.Battery },
            "accessPoint" => measured with { AccessPointHash = "moved" },
            "signal" => measured with { SignalBucket = 3 },
            _ => measured with { LoadedEvidence = true },
        };

        Assert.True(profile.RejectionsUsable(now, measured));
        Assert.False(profile.RejectionsUsable(now, current));
    }

    [Fact]
    public void AResultMeasuredByAnEarlierMethodIsNeverReused()
    {
        var profile = Profile() with { MethodologyVersion = LatencyProfile.CurrentMethodologyVersion - 1 };

        Assert.False(profile.Matches("net", Fake.Capability(Fake.Network("m"))));
        Assert.False(profile.RejectionsUsable(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AResultMeasuredAgainstAnotherTargetDoesNotMatch()
    {
        var network = Fake.Network("target-key");
        var adapter = Fake.Capability(network);
        var profile = Profile() with
        {
            NetworkKey = network.Key,
            AdapterId = adapter.AdapterId,
            CapabilityFingerprint = adapter.CapabilityFingerprint,
            Context = Context(),
        };

        Assert.True(profile.Matches(network.Key, adapter, Context()));
        Assert.False(profile.Matches(network.Key, adapter, Context() with { TargetKey = "elsewhere" }));
    }

    [Fact]
    public void AnUnknownConditionIsNotTreatedAsAMismatch()
    {
        var measured = Context() with { AccessPointHash = null, SignalBucket = null };

        Assert.True(measured.Covers(Context()));
        Assert.True(Context().Covers(measured));
    }

    private static LatencyProfileContext Context() => new()
    {
        TargetKey = "abc123",
        Power = PowerSource.Mains,
        AccessPointHash = "ap",
        SignalBucket = 8,
        LinkRateBucket = 4,
        LoadedEvidence = false,
        QosAvailable = false,
    };

    private static LatencyProfile Profile() => new()
    {
        NetworkKey = "net",
        AdapterId = "adapter",
        CapabilityFingerprint = "fingerprint",
        VerifiedAt = DateTimeOffset.UtcNow,
        RejectedProperties = [Fake.DefaultKeyword],
        Context = Context(),
    };
}

/// <summary>Latency preferences as they are stored and turned back into a target.</summary>
public sealed class LatencyPreferencesTests
{
    [Fact]
    public void TheDefaultIsTheGeneralReferenceAndTheGuardIsOff()
    {
        var preferences = new AppSettings().Latency;

        Assert.Equal(LatencyTargetKind.Reference, preferences.TargetKind);
        Assert.False(preferences.TrafficGuardEnabled);
        Assert.Null(preferences.TrafficGuardApplication);
        Assert.Equal(LatencyTargetKind.Reference, preferences.ToSpec().Kind);
    }

    [Fact]
    public void AnOlderSettingsFileLoadsWithSafeLatencyDefaults()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(path, "{\"StartWithWindows\":false,\"Latency\":null}");

        var settings = new ConfigStore(path, Path.Combine(directory.Path, "networks.json")).Load();

        Assert.NotNull(settings.Latency);
        Assert.Equal(LatencyTargetKind.Reference, settings.Latency.TargetKind);
        Assert.False(settings.Latency.TrafficGuardEnabled);
    }

    [Fact]
    public void ImpossibleStoredValuesAreDiscardedOnLoad()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(
            path,
            "{\"Latency\":{\"TargetKind\":\"Custom\",\"TargetHost\":\"  host  \",\"TargetPort\":70000,\"ManualUplinkMbps\":-5}}");

        var settings = new ConfigStore(path, Path.Combine(directory.Path, "networks.json")).Load();

        Assert.Equal("host", settings.Latency.TargetHost);
        Assert.Null(settings.Latency.TargetPort);
        Assert.Null(settings.Latency.ManualUplinkMbps);
    }

    [Fact]
    public void AnIncompleteCustomTargetFallsBackToTheReferenceRatherThanFailing()
    {
        var preferences = new LatencyPreferences { TargetKind = LatencyTargetKind.Custom, TargetHost = null };

        Assert.Equal(LatencyTargetKind.Reference, preferences.ToSpec().Kind);
    }

    [Fact]
    public void AManualUplinkFigureIsBelievedOverAnyObservation()
    {
        var capacity = new LatencyPreferences { ManualUplinkMbps = 12, ManualDownlinkMbps = 90 }.ToCapacity();

        Assert.Equal(LinkCapacityConfidence.UserSupplied, capacity.UplinkConfidence);
        Assert.Equal(12_000, capacity.UplinkKbps);
        Assert.Equal(90_000, capacity.DownlinkKbps);
        Assert.True(capacity.IsConfident(LoadDirection.Download));
    }

    /// <summary>
    /// Restarting the adapter is never something a preference alone can authorise.
    /// </summary>
    [Fact]
    public void AdapterRestartConsentIsOffByDefaultAndNeverAppliesRemotely()
    {
        Assert.False(new LatencyPreferences().ToRestartPolicy().UserConsented);
        Assert.True(new LatencyPreferences { AllowAdapterRestart = true }.ToRestartPolicy().UserConsented);
    }
}
