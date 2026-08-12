using System.Text;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Net;
using Xunit;

namespace DpiBypass.Tests;

public class DesyncPlannerTests
{
    private static byte[] Hello(string host = "discord.com") => FakePayloadFactory.CreateTlsClientHello(host);

    /// <summary>
    /// The single most important invariant in the whole engine: whatever we do to the
    /// packet, the bytes the server reassembles must be byte-for-byte what the
    /// application sent, at the right sequence numbers. If this holds, TLS completes.
    /// </summary>
    private static void AssertStreamIsIntact(DesyncPlan plan)
    {
        var real = plan.Segments.Where(s => !s.IsDecoy).OrderBy(s => s.SequenceOffset).ToList();

        Assert.NotEmpty(real);

        var expectedOffset = 0u;
        var rebuilt = new List<byte>();

        foreach (var segment in real)
        {
            Assert.Equal(expectedOffset, segment.SequenceOffset);
            Assert.Equal(0, segment.SequenceSkew);
            rebuilt.AddRange(plan.Payload.Skip(segment.Offset).Take(segment.Length));
            expectedOffset += (uint)segment.Length;
        }

        Assert.Equal(plan.Payload.Length, rebuilt.Count);
        Assert.Equal(plan.Payload, rebuilt.ToArray());
    }

    [Fact]
    public void EveryStrategyInTheLibraryPreservesTheTlsStream()
    {
        var payload = Hello();

        foreach (var strategy in StrategyLibrary.All)
        {
            var plan = DesyncPlanner.Plan(strategy, payload, isTls: true, hostName: "discord.com");
            AssertStreamIsIntact(plan);
            Assert.Equal(payload, plan.Payload);
        }
    }

    [Fact]
    public void NoStrategyChangesHowManyBytesAreSent()
    {
        // The engine is handed outbound packets only, so the local TCP stack keeps the
        // byte count it already committed to. A plan that lengthens or shortens the
        // payload makes the peer acknowledge data that stack never sent, and Windows
        // answers an out-of-window acknowledgement by discarding the segment - the
        // handshake then hangs until it times out. This is the invariant that a TLS
        // record re-framing strategy quietly broke, so it is asserted for the whole
        // catalogue rather than per strategy.
        var hello = Hello();
        var request = PacketFactory.HttpRequest("discord.com");

        foreach (var strategy in StrategyLibrary.All)
        {
            var tls = DesyncPlanner.Plan(strategy, hello, isTls: true, hostName: "discord.com");
            Assert.Equal(hello.Length, tls.Payload.Length);
            Assert.Equal(hello.Length, tls.Segments.Where(s => !s.IsDecoy).Sum(s => s.Length));

            var http = DesyncPlanner.Plan(strategy, request, isTls: false, hostName: "discord.com");
            Assert.Equal(request.Length, http.Payload.Length);
            Assert.Equal(request.Length, http.Segments.Where(s => !s.IsDecoy).Sum(s => s.Length));
        }
    }

    [Fact]
    public void EveryStrategyInTheLibraryPreservesTheHttpStream()
    {
        var payload = PacketFactory.HttpRequest("discord.com");

        foreach (var strategy in StrategyLibrary.All)
        {
            var plan = DesyncPlanner.Plan(strategy, payload, isTls: false, hostName: "discord.com");
            AssertStreamIsIntact(plan);
        }
    }

    [Fact]
    public void PassthroughIsRecognisedAsANoOp()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.Passthrough, Hello(), isTls: true, hostName: "discord.com");
        Assert.True(plan.IsNoOp);
        Assert.Single(plan.Segments);
    }

    [Fact]
    public void SplitAtTheHostnameCutsInsideTheHostname()
    {
        var payload = Hello();
        Assert.True(TlsClientHello.TryParse(payload, out var info));

        var plan = DesyncPlanner.Plan(StrategyLibrary.SplitSni, payload, isTls: true, hostName: "discord.com");

        Assert.Equal(2, plan.Segments.Count);
        var cut = plan.Segments[0].Length;

        // The cut has to land strictly inside "discord.com" - that is what stops the
        // inspector from ever seeing the whole name in one segment.
        Assert.InRange(cut, info.ServerNameOffset + 1, info.ServerNameEnd - 1);
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void AbsoluteSplitCutsAtTheConfiguredOffset()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.Split2, Hello(), isTls: true, hostName: "discord.com");

        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(2, plan.Segments[0].Length);
        Assert.Equal(2u, plan.Segments[1].SequenceOffset);
    }

    [Fact]
    public void DisorderSendsTheLaterSegmentFirst()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.DisorderSni, Hello(), isTls: true, hostName: "discord.com");

        Assert.Equal(2, plan.Segments.Count);
        Assert.True(plan.Segments[0].SequenceOffset > plan.Segments[1].SequenceOffset);
        Assert.Equal(0u, plan.Segments[1].SequenceOffset);
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void MultiSplitProducesThreeSegments()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.MultiSplitSni, Hello(), isTls: true, hostName: "discord.com");

        Assert.Equal(3, plan.Segments.Count(s => !s.IsDecoy));
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void DecoysComeFirstAndCarryTheOriginalSequenceNumber()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.FakeTtlSplitSni, Hello(), isTls: true, hostName: "discord.com");

        var first = plan.Segments[0];
        Assert.True(first.IsDecoy);
        Assert.Equal(0u, first.SequenceOffset);
        Assert.Equal((byte)4, first.TimeToLive);
        Assert.False(first.CorruptChecksum);

        // A decoy has to be a plausible ClientHello or the inspector ignores it.
        Assert.True(TlsClientHello.TryParse(first.FakePayload!, out var decoy));
        Assert.True(decoy.HasServerName);
        Assert.NotEqual("discord.com", decoy.ServerName);
    }

    [Fact]
    public void BadSequenceDecoysAreParkedOutsideTheWindow()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.FakeBadSeqSplitSni, Hello(), isTls: true, hostName: "discord.com");

        var decoy = plan.Segments.First(s => s.IsDecoy);
        Assert.True(decoy.SequenceSkew < -1000);
        Assert.Null(decoy.TimeToLive);
    }

    [Fact]
    public void BadChecksumDecoysAreFlaggedForCorruption()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.FakeBadSumDisorderSni, Hello(), isTls: true, hostName: "discord.com");

        var decoy = plan.Segments.First(s => s.IsDecoy);
        Assert.True(decoy.CorruptChecksum);
        Assert.Equal(0, decoy.SequenceSkew);
    }

    [Fact]
    public void RepeatedDecoysAreEmittedAsRequested()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.FakeTtlDisorderHostEnd, Hello(), isTls: true, hostName: "discord.com");
        Assert.Equal(2, plan.Segments.Count(s => s.IsDecoy));
    }

    [Fact]
    public void OutOfBandSegmentIsUrgentAndSitsBeforeTheStream()
    {
        var plan = DesyncPlanner.Plan(StrategyLibrary.OobSplitSni, Hello(), isTls: true, hostName: "discord.com");

        var oob = plan.Segments[0];
        Assert.True(oob.Urgent);
        Assert.Equal(-1, oob.SequenceSkew);
        Assert.Single(oob.FakePayload!);
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void HostCaseTrickRewritesTheHeaderNameWithoutMovingAnything()
    {
        var payload = PacketFactory.HttpRequest("discord.com");
        var strategy = StrategyLibrary.Split2 with { Http = HttpTricks.HostCase };

        var plan = DesyncPlanner.Plan(strategy, payload, isTls: false, hostName: "discord.com");

        Assert.Equal(payload.Length, plan.Payload.Length);
        Assert.Contains("hOSt: discord.com", Encoding.ASCII.GetString(plan.Payload));
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void HostTabTrickSwapsTheSeparatorWithoutMovingAnything()
    {
        var payload = PacketFactory.HttpRequest("discord.com");
        var strategy = StrategyLibrary.Split2 with { Http = HttpTricks.HostTab };

        var plan = DesyncPlanner.Plan(strategy, payload, isTls: false, hostName: "discord.com");

        Assert.Equal(payload.Length, plan.Payload.Length);
        Assert.Contains("Host:\tdiscord.com", Encoding.ASCII.GetString(plan.Payload));
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void CombinedHttpTricksApplyTogetherAndKeepTheStreamIntact()
    {
        var payload = PacketFactory.HttpRequest("discord.com");
        var strategy = StrategyLibrary.Split2 with
        {
            Http = HttpTricks.HostCase | HttpTricks.HostTab,
        };

        var plan = DesyncPlanner.Plan(strategy, payload, isTls: false, hostName: "discord.com");
        var text = Encoding.ASCII.GetString(plan.Payload);

        Assert.Equal(payload.Length, plan.Payload.Length);
        Assert.Contains("hOSt:\tdiscord.com\r\n", text);
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void AnHttpOnlyTrickIsNotMistakenForNothingToDo()
    {
        // One full length segment over a payload that was rewritten in place looks
        // exactly like an untouched packet unless the plan says otherwise, and the
        // engine forwards the original whenever it reads a plan as a no-op.
        var payload = PacketFactory.HttpRequest("discord.com");
        var strategy = StrategyLibrary.Passthrough with { Id = "http-only", Http = HttpTricks.HostCase };

        var plan = DesyncPlanner.Plan(strategy, payload, isTls: false, hostName: "discord.com");

        Assert.True(plan.PayloadRewritten);
        Assert.False(plan.IsNoOp);
        Assert.Contains("hOSt:", Encoding.ASCII.GetString(plan.Payload));
    }

    [Fact]
    public void HttpTricksAreNotAppliedToTlsPayloads()
    {
        var payload = Hello();
        var strategy = StrategyLibrary.Split2 with { Http = HttpTricks.HostCase | HttpTricks.HostTab };

        var plan = DesyncPlanner.Plan(strategy, payload, isTls: true, hostName: "discord.com");

        Assert.Equal(payload, plan.Payload);
    }

    [Fact]
    public void TinyPayloadsAreLeftWhole()
    {
        // Nothing useful can be cut out of four bytes, and splitting them would only
        // add packets to the wire.
        var plan = DesyncPlanner.Plan(StrategyLibrary.SplitSni, [1, 2, 3, 4], isTls: true, hostName: null);
        Assert.Single(plan.Segments);
    }

    [Fact]
    public void FallsBackToAFixedOffsetWhenThereIsNoHostname()
    {
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);

        var plan = DesyncPlanner.Plan(StrategyLibrary.SplitSni, payload, isTls: true, hostName: null);

        Assert.Equal(2, plan.Segments.Count);
        AssertStreamIsIntact(plan);
    }

    [Fact]
    public void SegmentsNeverExceedThePayloadBounds()
    {
        foreach (var strategy in StrategyLibrary.All)
        {
            foreach (var length in new[] { 8, 9, 40, 41, 300, 1400 })
            {
                var payload = new byte[length];
                Random.Shared.NextBytes(payload);

                var plan = DesyncPlanner.Plan(strategy, payload, isTls: false, hostName: null);

                foreach (var segment in plan.Segments.Where(s => !s.IsDecoy))
                {
                    Assert.InRange(segment.Offset, 0, plan.Payload.Length - 1);
                    Assert.InRange(segment.Offset + segment.Length, 0, plan.Payload.Length);
                }
            }
        }
    }
}

public class StrategyLibraryTests
{
    [Fact]
    public void StrategyIdsAreUnique()
    {
        var ids = StrategyLibrary.All.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryStrategyIsResolvableById()
    {
        foreach (var strategy in StrategyLibrary.All)
        {
            Assert.Same(strategy, StrategyLibrary.Find(strategy.Id));
        }

        Assert.Null(StrategyLibrary.Find("no-such-strategy"));
        Assert.Null(StrategyLibrary.Find(null));
    }

    [Fact]
    public void EveryStrategyIsNamedForTheUi()
    {
        foreach (var strategy in StrategyLibrary.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(strategy.Name));
        }
    }

    [Fact]
    public void OnlyPassthroughIsANoOp()
    {
        Assert.True(StrategyLibrary.Passthrough.IsPassthrough);
        Assert.Single(StrategyLibrary.All, s => s.IsPassthrough);
    }

    [Fact]
    public void DefaultStrategyIsAnActiveOne() => Assert.False(StrategyLibrary.Default.IsPassthrough);
}
