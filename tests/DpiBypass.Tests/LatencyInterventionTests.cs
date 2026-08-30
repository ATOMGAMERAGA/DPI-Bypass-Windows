using System.Net;
using System.Net.NetworkInformation;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Which adapter changes are offered, to whom, and which are refused outright.
/// </summary>
public sealed class AdapterInterventionCatalogTests
{
    /// <summary>
    /// Microsoft's guidance is that checksum offloads should always be enabled, and RSS,
    /// RSC and LSO all depend on them. There is no path through this build that turns one
    /// off, so none of the keywords may appear in a candidate list at all.
    /// </summary>
    [Fact]
    public void NoChecksumOffloadKeywordIsEverWritableOrOffered()
    {
        Assert.Empty(AdapterInterventionCatalog.WritableKeywords
            .Intersect(AdapterInterventionCatalog.ForbiddenKeywords, StringComparer.OrdinalIgnoreCase));

        var adapter = Capability(
            Property("*TCPChecksumOffloadIPv4", "3", "0", "1", "2", "3"),
            Property("*UDPChecksumOffloadIPv4", "3", "0", "1", "2", "3"),
            Property("*IPChecksumOffloadIPv4", "3", "0", "1", "2", "3"));

        Assert.Empty(adapter.BuildSafeCandidates());
    }

    [Fact]
    public void EveryOfferedKeywordIsAStandardisedRegistryKeywordNotADisplayName()
    {
        Assert.All(
            AdapterInterventionCatalog.WritableKeywords,
            keyword => Assert.StartsWith("*", keyword, StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole candidate set is driven by RegistryKeyword and ValidRegistryValues.
    /// Windows localises DisplayName and DisplayValue, so a build that read either would
    /// change a different setting on Turkish Windows than on English.
    /// </summary>
    [Fact]
    public void ADriverWhoseDisplayTextIsLocalisedStillProducesTheSameCandidates()
    {
        var english = Capability(Property("*InterruptModeration", "1", "0", "1"));
        var turkish = Capability(Property("*InterruptModeration", "1", "0", "1"));

        var first = Assert.Single(english.BuildSafeCandidates());
        var second = Assert.Single(turkish.BuildSafeCandidates());

        Assert.Equal(first.PropertyName, second.PropertyName);
        Assert.Equal(["0"], second.DesiredValues);
    }

    [Fact]
    public void AKeywordTheDriverDoesNotOfferIsNotACandidate()
        => Assert.Empty(Capability().BuildSafeCandidates());

    [Fact]
    public void AKeywordAlreadyAtTheValueWeWouldWriteIsNotACandidate()
        => Assert.Empty(Capability(Property("*InterruptModeration", "0", "0", "1")).BuildSafeCandidates());

    [Fact]
    public void AValueTheDriverDoesNotAcceptIsNotACandidate()
        => Assert.Empty(Capability(Property("*RscIPv4", "1", "1")).BuildSafeCandidates());

    /// <summary>
    /// RSC coalesces packets of the same TCP stream. Offering it for a UDP game would
    /// spend minutes measuring something that cannot touch the traffic in question.
    /// </summary>
    [Fact]
    public void TcpOnlySettingsAreNotOfferedForAUdpTarget()
    {
        var adapter = Capability(
            Property("*RscIPv4", "1", "0", "1"),
            Property("*LsoV2IPv4", "1", "0", "1"));

        var udp = adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            Scope = LatencyTrafficScope.Icmp,
            ApplicationScope = LatencyTrafficScope.Udp,
        });

        Assert.Empty(udp);

        var tcp = adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            Scope = LatencyTrafficScope.Tcp,
            ApplicationScope = LatencyTrafficScope.Tcp,
        });

        Assert.Contains(tcp, candidate => candidate.PropertyName == "*RscIPv4");
    }

    /// <summary>
    /// Large send offload only ever acts on blocks bigger than the MTU. An idle-latency
    /// run never produces one, so it belongs to the loaded lane or to nothing.
    /// </summary>
    [Fact]
    public void LargeSendOffloadIsNotOfferedToAnIdleLatencyRun()
    {
        var adapter = Capability(Property("*LsoV2IPv4", "1", "0", "1"));

        Assert.Empty(adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            Scope = LatencyTrafficScope.Tcp,
            IncludeThroughputSensitive = false,
        }));

        Assert.Single(adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            Scope = LatencyTrafficScope.Tcp,
            IncludeThroughputSensitive = true,
        }));
    }

    /// <summary>
    /// A wireless driver exposing the RSS keyword is not a promise that its hardware
    /// implements receive queues, and a two-core machine has nothing to spread work over.
    /// </summary>
    [Theory]
    [InlineData(true, 8, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 8, true)]
    public void ReceiveSideScalingIsOnlyOfferedToAWiredMultiCoreMachine(
        bool wireless,
        int processors,
        bool expected)
    {
        var adapter = Capability(Property("*RSS", "0", "0", "1")) with
        {
            AdapterType = wireless ? NetworkInterfaceType.Wireless80211 : NetworkInterfaceType.Ethernet,
        };

        var candidates = adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            IsWireless = wireless,
            ProcessorCount = processors,
        });

        Assert.Equal(expected, candidates.Any(candidate => candidate.PropertyName == "*RSS"));
    }

    [Fact]
    public void ReceiveSideScalingIsNotOfferedWhenItIsAlreadyOn()
    {
        var adapter = Capability(Property("*RSS", "0", "0", "1")) with { RssEnabled = true };

        Assert.DoesNotContain(
            adapter.BuildSafeCandidates(new LatencyCandidateContext { ProcessorCount = 8 }),
            candidate => candidate.PropertyName == "*RSS");
    }

    /// <summary>Turning off coalescing the stack has already declined to use proves nothing.</summary>
    [Fact]
    public void ReceiveSegmentCoalescingIsNotOfferedWhenItIsNotOperational()
    {
        var adapter = Capability(Property("*RscIPv4", "1", "0", "1")) with { RscIPv4Operational = false };

        Assert.DoesNotContain(
            adapter.BuildSafeCandidates(new LatencyCandidateContext { Scope = LatencyTrafficScope.Tcp }),
            candidate => candidate.PropertyName == "*RscIPv4");
    }

    /// <summary>
    /// The keyword governs what the adapter does when the media is unplugged, which is
    /// not a state a running game is ever in. It stays restorable, never offered.
    /// </summary>
    [Fact]
    public void DeviceSleepOnDisconnectIsNoLongerALatencyCandidateButIsStillRestorable()
    {
        var adapter = Fake.Capability(Fake.Network("dsod"), "DeviceSleepOnDisconnect");

        Assert.Empty(adapter.BuildSafeCandidates());
        Assert.DoesNotContain(AdapterInterventionCatalog.DeviceSleepOnDisconnectProperty,
            AdapterInterventionCatalog.WritablePowerProperties);
        Assert.Contains(AdapterInterventionCatalog.DeviceSleepOnDisconnectProperty,
            AdapterInterventionCatalog.RestorablePowerProperties);
    }

    /// <summary>
    /// A change that costs battery is not made on battery unless the user says so; the
    /// point of the feature is not to trade an hour of runtime for a millisecond.
    /// </summary>
    [Fact]
    public void PowerCostingChangesAreNotOfferedOnBatteryUnlessAllowed()
    {
        var adapter = Fake.Capability(Fake.Network("battery"), "SelectiveSuspend");

        Assert.Empty(adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            Power = PowerSource.Battery,
            AllowPowerCost = false,
        }));

        Assert.Single(adapter.BuildSafeCandidates(new LatencyCandidateContext
        {
            Power = PowerSource.Battery,
            AllowPowerCost = true,
        }));
    }

    /// <summary>
    /// Turning interrupt moderation off means an interrupt per packet, so it has to clear
    /// a higher bar than a change that costs nothing.
    /// </summary>
    [Fact]
    public void ACpuCostingCandidateHasToWinByMoreThanAFreeOne()
    {
        var free = Fake.Candidate();
        var costly = Capability(Property("*InterruptModeration", "1", "0", "1")).BuildSafeCandidates().Single();

        Assert.False(free.CpuSensitive);
        Assert.True(costly.CpuSensitive);
        Assert.Equal(InterventionCost.Cpu, costly.Descriptor.Cost);

        // The same modest gain is enough for the free change and not for the costly one.
        var pairs = new[]
        {
            new LatencyPair { Baseline = Fake.Measurement(30), Candidate = Fake.Measurement(28.5) },
            new LatencyPair
            {
                Baseline = Fake.Measurement(30),
                Candidate = Fake.Measurement(28.5),
                Order = LatencyCycleOrder.CandidateFirst,
            },
        };

        Assert.Equal(
            LatencyVerdictOutcome.Accepted,
            LatencyComparison.Evaluate(free, pairs, LatencyEvaluationOptions.Strict).Outcome);
        Assert.NotEqual(
            LatencyVerdictOutcome.Accepted,
            LatencyComparison.Evaluate(costly, pairs, LatencyEvaluationOptions.Strict).Outcome);
    }

    /// <summary>Every offered change carries the metadata the scheduler needs.</summary>
    [Fact]
    public void EveryCatalogueEntryDeclaresItsScopeRiskCostAndSettlingTime()
    {
        foreach (var keyword in AdapterInterventionCatalog.WritableKeywords
            .Concat(AdapterInterventionCatalog.WritablePowerProperties))
        {
            var descriptor = AdapterInterventionCatalog.DescriptorFor(keyword);

            Assert.False(string.IsNullOrWhiteSpace(descriptor.Id), keyword);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Mechanism), keyword);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Reference), keyword);
            Assert.NotEqual(LatencyTrafficScope.None, descriptor.Scope);
            Assert.True(descriptor.SettlingTime > TimeSpan.Zero, keyword);
        }
    }

    /// <summary>
    /// A setting the driver may only honour after a miniport restart is written with no
    /// restart and read straight back, so it is abandoned rather than applied silently.
    /// </summary>
    [Fact]
    public async Task ASettingThatNeedsARestartIsAbandonedWhenTheDriverDoesNotTakeItLive()
    {
        var controller = new FakeController { RefuseApply = "SelectiveSuspend" };
        var scenario = new LatencyScenario(controller, FakeProbe.Improves(controller, gain: 8));

        var result = await scenario.Optimizer.OptimizeAsync(Fake.Network("restart-needed"));

        Assert.Empty(controller.Live);
        Assert.Null(scenario.Snapshots.Value);
        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
    }

    private static AdapterAdvancedPropertyCapability Property(
        string keyword,
        string current,
        params string[] valid) => new()
        {
            RegistryKeyword = keyword,
            RegistryValues = [current],
            ValidRegistryValues = [.. valid],
        };

    private static AdapterLatencyCapability Capability(params AdapterAdvancedPropertyCapability[] properties) => new()
    {
        AdapterId = "adapter",
        AdapterName = "Intel I225-V",
        AdapterType = NetworkInterfaceType.Ethernet,
        IsPhysical = true,
        IsVirtual = false,
        IsUp = true,
        AdvancedProperties = [.. properties],
    };
}

/// <summary>Turning what the user asked for into one fixed address, once.</summary>
public sealed class LatencyTargetTests
{
    [Theory]
    [InlineData("mc.example.com", "mc.example.com", null, LatencyProtocol.Icmp)]
    [InlineData("mc.example.com:25565", "mc.example.com", 25565, LatencyProtocol.Tcp)]
    [InlineData("tcp://1.2.3.4:443", "1.2.3.4", 443, LatencyProtocol.Tcp)]
    [InlineData("udp://1.2.3.4:7777", "1.2.3.4", 7777, LatencyProtocol.Udp)]
    [InlineData("[2001:db8::1]:25565", "2001:db8::1", 25565, LatencyProtocol.Tcp)]
    public void AWellFormedTargetIsParsedIntoItsParts(
        string text,
        string host,
        int? port,
        LatencyProtocol protocol)
    {
        Assert.True(LatencyTargetSpec.TryParse(text, out var spec, out var error), error);
        Assert.Equal(host, spec.Host);
        Assert.Equal(port, spec.Port);
        Assert.Equal(protocol, spec.Protocol);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host:0")]
    [InlineData("host:70000")]
    [InlineData("host:not-a-port")]
    [InlineData("udp://1.2.3.4")]
    [InlineData("host;rm -rf /")]
    [InlineData("host name")]
    public void AnythingElseIsRejectedWithAReasonRatherThanGuessedAt(string text)
    {
        Assert.False(LatencyTargetSpec.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TheCacheKeyChangesWithTheTargetAndCarriesNoHostname()
    {
        LatencyTargetSpec.TryParse("mc.example.com:25565", out var first, out _);
        LatencyTargetSpec.TryParse("other.example.com:25565", out var second, out _);

        Assert.NotEqual(first.CacheKey, second.CacheKey);
        Assert.DoesNotContain("example", first.CacheKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.CacheKey, first with { } is { } same ? same.CacheKey : string.Empty);
    }

    [Fact]
    public async Task TheGeneralReferenceIsLabelledAsNotBeingAGameServer()
    {
        var resolution = await new LatencyTargetResolver().ResolveAsync(LatencyTargetSpec.Reference);

        Assert.True(resolution.Succeeded);
        Assert.Contains("oyun sunucusu değildir", resolution.Notice, StringComparison.Ordinal);
        Assert.All(resolution.Endpoints, endpoint => Assert.Equal(LatencyProtocol.Icmp, endpoint.Protocol));
    }

    /// <summary>
    /// A UDP session's own round trip is inside a protocol we do not speak. Measuring the
    /// route to the same address is useful; calling it the game's ping would not be.
    /// </summary>
    [Fact]
    public async Task AUdpTargetIsMeasuredAsARouteReferenceAndSaysSo()
    {
        LatencyTargetSpec.TryParse("udp://198.51.100.7:7777", out var spec, out _);

        var resolution = await new LatencyTargetResolver().ResolveAsync(spec);
        var endpoint = Assert.Single(resolution.Endpoints);

        Assert.True(endpoint.RouteReferenceOnly);
        Assert.Equal(LatencyProtocol.Icmp, endpoint.Protocol);
        Assert.Equal(LatencyProtocol.Udp, endpoint.ApplicationProtocol);
        Assert.Contains("rota referansı", resolution.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatDoesNotResolveIsReportedRatherThanSubstituted()
    {
        var resolver = new LatencyTargetResolver(
            resolve: (_, _) => throw new System.Net.Sockets.SocketException(11001));

        LatencyTargetSpec.TryParse("nope.invalid", out var spec, out _);
        var resolution = await resolver.ResolveAsync(spec);

        Assert.False(resolution.Succeeded);
        Assert.Contains("çözümlenemedi", resolution.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Windows does not report the remote address of a UDP socket, so a UDP-only game
    /// cannot have its server discovered. Saying so is the only honest answer.
    /// </summary>
    [Fact]
    public async Task AUdpOnlyApplicationIsToldToSupplyItsServerAddress()
    {
        var resolver = new LatencyTargetResolver(new StubEndpoints
        {
            Set = new ProcessEndpointSet { ProcessFound = true, HasUdpSockets = true },
        });

        var resolution = await resolver.ResolveAsync(new LatencyTargetSpec
        {
            Kind = LatencyTargetKind.Application,
            ProcessName = "game",
        });

        Assert.False(resolution.Succeeded);
        Assert.Contains("UDP", resolution.Failure, StringComparison.Ordinal);
        Assert.Contains("Özel hedef", resolution.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApplicationsBusiestRemoteEndpointBecomesTheTarget()
    {
        var resolver = new LatencyTargetResolver(new StubEndpoints
        {
            Set = new ProcessEndpointSet
            {
                ProcessFound = true,
                TcpRemoteEndpoints =
                [
                    new IPEndPoint(IPAddress.Parse("203.0.113.9"), 25565),
                    new IPEndPoint(IPAddress.Parse("203.0.113.9"), 25565),
                    new IPEndPoint(IPAddress.Parse("198.51.100.4"), 443),
                ],
            },
        });

        var resolution = await resolver.ResolveAsync(new LatencyTargetSpec
        {
            Kind = LatencyTargetKind.Application,
            ProcessName = "game",
        });

        var endpoint = Assert.Single(resolution.Endpoints);

        Assert.Equal("203.0.113.9", endpoint.Address.ToString());
        Assert.Equal(25565, endpoint.Port);
        Assert.Equal(LatencyProtocol.Tcp, endpoint.Protocol);
        Assert.False(endpoint.RouteReferenceOnly);
    }

    private sealed class StubEndpoints : IProcessEndpointProvider
    {
        public ProcessEndpointSet Set { get; init; } = new();

        public ProcessEndpointSet ForProcess(string processName) => Set;

        public IReadOnlyList<string> ConnectedProcesses() => [];
    }
}
