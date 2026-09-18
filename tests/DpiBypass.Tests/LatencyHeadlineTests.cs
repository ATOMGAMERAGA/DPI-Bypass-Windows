using DpiBypass.Core.Network;
using DpiBypass.Core.Network.Latency;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the ping card is allowed to say, for each thing a run can conclude.
/// </summary>
/// <remarks>
/// The card is three numbers and a sentence, so every rule about honesty has to hold here
/// or it holds nowhere. These pin the ones that were broken: a gain shown for a change that
/// is no longer applied, a steadiness result rendered as milliseconds off the ping, a
/// percentage computed against a baseline that cannot carry one, and a zero standing in for
/// something nobody measured.
/// </remarks>
public sealed class LatencyHeadlineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AVerifiedMedianGainIsShownAsABeforeAfterAndAReduction()
    {
        var headline = LatencyHeadline.From(
            Status(
                LatencySituation.VerifiedGain,
                before: 48,
                after: 34,
                applied: ["Interrupt Moderation kapalı"],
                gain: LatencyGainKind.Median),
            Now);

        Assert.Equal(LatencyHeadlineState.Active, headline.State);
        Assert.Equal("Etkin", headline.Status);
        Assert.Equal("48 ms", headline.Before);
        Assert.Equal("34 ms", headline.After);
        Assert.Equal("14 ms azalma", headline.Gain);
        Assert.True(headline.ShowsReduction);

        // Percentage is secondary and correct: 14 of 48.
        Assert.Equal("%29", headline.Percent);
        Assert.Null(headline.StabilityNote);
    }

    [Fact]
    public void AJitterOnlyWinIsNeverAPingReduction()
    {
        // The measurement is real and the change is applied - it just did not move the
        // median, so it gets a sentence rather than a number of milliseconds.
        var headline = LatencyHeadline.From(
            Status(
                LatencySituation.VerifiedGain,
                before: 48,
                after: 47.6,
                applied: ["Interrupt Moderation kapalı"],
                gain: LatencyGainKind.Stability),
            Now);

        Assert.False(headline.ShowsReduction);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.Gain);
        Assert.Null(headline.Percent);

        Assert.NotNull(headline.StabilityNote);
        Assert.Contains("daha kararlı", headline.StabilityNote!, StringComparison.Ordinal);
        Assert.DoesNotContain("azalma", headline.StabilityNote!, StringComparison.Ordinal);

        // The state is still Active, because something was applied and is working.
        Assert.Equal(LatencyHeadlineState.Active, headline.State);
    }

    [Fact]
    public void ARolledBackRunShowsNoPairAndSaysTheSettingsWereKept()
    {
        // The reported card. Nothing is applied, so there is no before/after to draw, and
        // the rejected candidate's number is nowhere near the gain line.
        var headline = LatencyHeadline.From(
            Status(
                LatencySituation.NoDifference,
                before: 48,
                after: null,
                applied: [],
                gain: LatencyGainKind.None,
                current: 48.3),
            Now);

        Assert.Equal(LatencyHeadlineState.Unchanged, headline.State);
        Assert.Equal("Mevcut ayarlar korundu", headline.Status);

        Assert.Equal(LatencyHeadline.NotMeasured, headline.Before);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.After);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.Gain);
        Assert.False(headline.ShowsReduction);

        Assert.Equal(
            "Belirgin bir düşüş doğrulanmadı; mevcut ayarlarınız korundu.",
            headline.Explanation);

        // And the current ping is the re-measured one, labelled as current rather than as
        // the "after" of anything.
        Assert.Equal("48 ms", headline.CurrentPing);
        Assert.Contains("Şu anki ping", headline.MeasuredAtLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmeasuredValueIsADashAndNeverAZero()
    {
        var headline = LatencyHeadline.From(
            Status(LatencySituation.NotMeasuredYet, before: null, after: null, applied: [], gain: LatencyGainKind.None),
            Now);

        Assert.Equal(LatencyHeadline.NotMeasured, headline.Before);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.After);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.Gain);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.CurrentPing);

        Assert.DoesNotContain("0 ms", headline.Before, StringComparison.Ordinal);
        Assert.Null(headline.Percent);
    }

    [Fact]
    public void AFailedReMeasurementSaysSoInsteadOfShowingAStaleNumber()
    {
        var headline = LatencyHeadline.From(
            Status(
                LatencySituation.NoDifference,
                before: 48,
                after: null,
                applied: [],
                gain: LatencyGainKind.None,
                current: null,
                remeasure: LatencyRemeasureState.Failed),
            Now);

        Assert.Equal(LatencyHeadline.NotMeasured, headline.CurrentPing);
        Assert.Equal("Şu anki ping ölçülemedi.", headline.MeasuredAtLine);
        Assert.Contains("yeniden ölçülemedi", headline.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AReMeasurementInFlightSaysItIsMeasuringAgain()
    {
        var headline = LatencyHeadline.From(
            Status(
                LatencySituation.NoDifference,
                before: 48,
                after: null,
                applied: [],
                gain: LatencyGainKind.None,
                current: 48.3,
                remeasure: LatencyRemeasureState.InProgress),
            Now);

        Assert.Equal("Yeniden ölçülüyor…", headline.MeasuredAtLine);
    }

    [Fact]
    public void AGainIsNotShownOnceTheChangeIsNoLongerApplied()
    {
        // Both halves exist and the arithmetic would work. What stops it is that nothing is
        // applied any more, so the difference is history rather than something the user has.
        var headline = LatencyHeadline.From(
            Status(
                LatencySituation.VerifiedGain,
                before: 48,
                after: 34,
                applied: [],
                gain: LatencyGainKind.Median),
            Now);

        Assert.False(headline.ShowsReduction);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.Gain);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.Before);
    }

    [Fact]
    public void AComparisonAcrossADifferentTargetIsRefused()
    {
        // Before on one endpoint and after on another is two measurements of two questions.
        // Subtracting them produces a number about neither.
        var before = Fake.Measurement(48, endpoint: "1.1.1.1") with
        {
            Role = LatencyMeasurementRole.Baseline,
            NetworkKey = "net-a",
        };

        var after = Fake.Measurement(34, endpoint: "203.0.113.9") with
        {
            Role = LatencyMeasurementRole.Verification,
            NetworkKey = "net-a",
        };

        var headline = LatencyHeadline.From(
            new LatencyStatusView
            {
                Situation = LatencySituation.VerifiedGain,
                NextAction = LatencyNextAction.ViewResult,
                State = LatencyModeState.GainApplied,
                Headline = string.Empty,
                Severity = "ok",
                IdleBefore = before,
                IdleAfter = after,
                Idle = after,
                Applied = ["bir ayar"],
                GainKind = LatencyGainKind.Median,
            },
            Now);

        Assert.False(headline.ShowsReduction);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.Before);
    }

    [Fact]
    public void AComparisonAcrossADifferentNetworkIsRefused()
    {
        // The same endpoint on a different network is a different route. A gain measured on
        // the home link is not a property of the phone hotspot the user just moved to.
        var before = Fake.Measurement(48) with
        {
            Role = LatencyMeasurementRole.Baseline,
            NetworkKey = "home",
        };

        var after = Fake.Measurement(34) with
        {
            Role = LatencyMeasurementRole.Verification,
            NetworkKey = "hotspot",
        };

        Assert.False(before.ComparableWith(after));

        var headline = LatencyHeadline.From(
            new LatencyStatusView
            {
                Situation = LatencySituation.VerifiedGain,
                NextAction = LatencyNextAction.ViewResult,
                State = LatencyModeState.GainApplied,
                Headline = string.Empty,
                Severity = "ok",
                IdleBefore = before,
                IdleAfter = after,
                Idle = after,
                Applied = ["bir ayar"],
                GainKind = LatencyGainKind.Median,
            },
            Now);

        Assert.False(headline.ShowsReduction);
        Assert.Equal(LatencyHeadline.NotMeasured, headline.Gain);
    }

    [Fact]
    public void ARouteReferenceIsNeverPresentedAsTheApplicationsOwnPing()
    {
        var headline = LatencyHeadline.From(
            new LatencyStatusView
            {
                Situation = LatencySituation.NoDifference,
                NextAction = LatencyNextAction.ViewResult,
                State = LatencyModeState.NoLocalGain,
                Headline = string.Empty,
                Severity = "warn",
                Target = "VALORANT → 203.0.113.7:7000",
                Protocol = "ICMP",
                RouteReferenceOnly = true,
                Idle = Fake.Measurement(34) with { Role = LatencyMeasurementRole.Reference },
            },
            Now);

        Assert.Contains("rota referansı", headline.TargetLine, StringComparison.Ordinal);
        Assert.Contains("kendi süresi değil", headline.TargetLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRollbackIsNotSoftenedIntoUnchanged()
    {
        var headline = LatencyHeadline.From(
            Status(LatencySituation.RestoreFailed, before: 48, after: null, applied: [], gain: LatencyGainKind.None),
            Now);

        Assert.Equal(LatencyHeadlineState.NeedsAttention, headline.State);
        Assert.NotEqual("Mevcut ayarlar korundu", headline.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void APercentageNeedsABaselineThatCanCarryOne(double baseline)
    {
        // A share of zero, or of a number that should never have been negative, is not a
        // percentage. It comes back null rather than as an infinity or a nonsense figure.
        var headline = LatencyHeadline.From(
            Status(
                LatencySituation.VerifiedGain,
                before: baseline,
                after: baseline - 5,
                applied: ["bir ayar"],
                gain: LatencyGainKind.Median),
            Now);

        Assert.Null(headline.Percent);
    }

    /// <summary>A status view with just the fields the card reads.</summary>
    private static LatencyStatusView Status(
        LatencySituation situation,
        double? before,
        double? after,
        IReadOnlyList<string> applied,
        LatencyGainKind gain,
        double? current = null,
        LatencyRemeasureState remeasure = LatencyRemeasureState.NotNeeded)
    {
        var baseline = before is { } b
            ? Fake.Measurement(b) with { Role = LatencyMeasurementRole.Baseline, NetworkKey = "net" }
            : null;

        var verified = after is { } a
            ? Fake.Measurement(a) with { Role = LatencyMeasurementRole.Verification, NetworkKey = "net" }
            : null;

        var now = current is { } c
            ? Fake.Measurement(c) with { Role = LatencyMeasurementRole.PostRollback, NetworkKey = "net" }
            : verified;

        return new LatencyStatusView
        {
            Situation = situation,
            NextAction = LatencyNextAction.ViewResult,
            State = LatencyModeState.GainApplied,
                Headline = string.Empty,
                Severity = "ok",
            IdleBefore = baseline,
            IdleAfter = verified,
            Idle = now,
            Applied = applied,
            GainKind = gain,
            Remeasure = remeasure,
            Target = "1.1.1.1",
            Protocol = "ICMP",
        };
    }
}
