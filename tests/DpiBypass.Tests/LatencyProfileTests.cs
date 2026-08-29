using DpiBypass.Core.Network;
using DpiBypass.Tests.Latency;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>The on-disk cache of what a benchmark already worked out about a network.</summary>
public sealed class LatencyProfileStoreTests
{
    [Fact]
    public async Task AProfileRoundTripsAndIsFoundByNetworkAndAdapter()
    {
        using var directory = new TempDirectory();
        var store = new LatencyProfileStore(directory.File("latency-profiles.json"));

        await store.SaveAsync(Profile("net-a", "adapter-1"));
        await store.SaveAsync(Profile("net-b", "adapter-1"));

        var found = await store.FindAsync("net-a", "adapter-1");

        Assert.NotNull(found);
        Assert.Equal(["SelectiveSuspend"], found!.AcceptedProperties);
        Assert.Equal(LatencyBottleneck.LocalLink, found.Bottleneck);
        Assert.Null(await store.FindAsync("net-c", "adapter-1"));
    }

    /// <summary>Two adapters on the same network are two different answers.</summary>
    [Fact]
    public async Task ProfilesAreKeyedByAdapterAsWellAsNetwork()
    {
        using var directory = new TempDirectory();
        var store = new LatencyProfileStore(directory.File("latency-profiles.json"));

        await store.SaveAsync(Profile("net", "ethernet") with { AcceptedProperties = ["SelectiveSuspend"] });
        await store.SaveAsync(Profile("net", "wifi") with { AcceptedProperties = [] });

        Assert.Equal(["SelectiveSuspend"], (await store.FindAsync("net", "ethernet"))!.AcceptedProperties);
        Assert.Empty((await store.FindAsync("net", "wifi"))!.AcceptedProperties);
    }

    [Fact]
    public async Task SavingTheSameNetworkTwiceReplacesRatherThanDuplicates()
    {
        using var directory = new TempDirectory();
        var store = new LatencyProfileStore(directory.File("latency-profiles.json"));

        await store.SaveAsync(Profile("net", "adapter") with { AcceptedProperties = ["SelectiveSuspend"] });
        await store.SaveAsync(Profile("net", "adapter") with { AcceptedProperties = ["D0PacketCoalescing"] });

        Assert.Equal(["D0PacketCoalescing"], (await store.FindAsync("net", "adapter"))!.AcceptedProperties);
    }

    [Fact]
    public async Task RemovingAProfileIsIdempotent()
    {
        using var directory = new TempDirectory();
        var store = new LatencyProfileStore(directory.File("latency-profiles.json"));
        await store.SaveAsync(Profile("net", "adapter"));

        await store.RemoveAsync("net", "adapter");
        await store.RemoveAsync("net", "adapter");

        Assert.Null(await store.FindAsync("net", "adapter"));
    }

    /// <summary>The file is a cache on a shipped product, so it cannot grow forever.</summary>
    [Fact]
    public async Task TheOldestProfilesFallOffOnceTheCacheIsFull()
    {
        using var directory = new TempDirectory();
        var store = new LatencyProfileStore(directory.File("latency-profiles.json"));
        var start = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);

        for (var index = 0; index < LatencyProfileStore.MaxProfiles + 5; index++)
        {
            await store.SaveAsync(Profile($"net-{index}", "adapter") with
            {
                VerifiedAt = start + TimeSpan.FromMinutes(index),
            });
        }

        Assert.Null(await store.FindAsync("net-0", "adapter"));
        Assert.NotNull(await store.FindAsync($"net-{LatencyProfileStore.MaxProfiles + 4}", "adapter"));
    }

    [Fact]
    public async Task AnUnreadableCacheIsAMissRatherThanAFailure()
    {
        using var directory = new TempDirectory();
        var path = directory.File("latency-profiles.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        var store = new LatencyProfileStore(path, log: _ => { });

        Assert.Null(await store.FindAsync("net", "adapter"));

        // And it recovers: the next save rewrites the file.
        await store.SaveAsync(Profile("net", "adapter"));
        Assert.NotNull(await store.FindAsync("net", "adapter"));
    }

    [Fact]
    public void AProfileOnlyMatchesTheSameNetworkAdapterAndDriverSurface()
    {
        var network = Fake.Network("match");
        var adapter = Fake.Capability(network);
        var profile = Profile(network.Key, adapter.AdapterId) with
        {
            CapabilityFingerprint = adapter.CapabilityFingerprint,
        };

        Assert.True(profile.Matches(network.Key, adapter));
        Assert.False(profile.Matches("another-network", adapter));
        Assert.False(profile.Matches(network.Key, adapter with { AdapterId = "adapter-other" }));
        Assert.False(profile.Matches(network.Key, adapter with { InterfaceDescription = "driver 2.0" }));
    }

    [Fact]
    public void AProfileGoesStaleAfterAMonth()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = Profile("net", "adapter") with { VerifiedAt = now };

        Assert.True(profile.IsFresh(now + TimeSpan.FromDays(29)));
        Assert.False(profile.IsFresh(now + LatencyProfile.MaximumAge + TimeSpan.FromMinutes(1)));
    }

    private static LatencyProfile Profile(string networkKey, string adapterId) => new()
    {
        NetworkKey = networkKey,
        AdapterId = adapterId,
        AdapterName = adapterId,
        CapabilityFingerprint = "fingerprint",
        VerifiedAt = DateTimeOffset.UtcNow,
        AcceptedProperties = ["SelectiveSuspend"],
        RejectedProperties = ["D0PacketCoalescing"],
        Baseline = new LatencySummary { MedianRttMs = 32, P95RttMs = 44, P99RttMs = 51, JitterMs = 4, PacketLossPercent = 0 },
        Optimized = new LatencySummary { MedianRttMs = 29, P95RttMs = 36, P99RttMs = 40, JitterMs = 2, PacketLossPercent = 0 },
        Bottleneck = LatencyBottleneck.LocalLink,
    };
}

/// <summary>Re-applying a result the benchmark reached on an earlier visit.</summary>
public sealed class LatencyProfileReplayTests
{
    [Fact]
    public async Task ASecondVisitReAppliesTheVerifiedSettingWithoutBenchmarkingAgain()
    {
        var network = Fake.Network("returning");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        var controller = new FakeController();
        var second = new LatencyScenario(controller, FakeProbe.Improves(controller, gain: 6), profiles: first.Profiles);
        var result = await second.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.Active, result.Status);
        Assert.Contains("SelectiveSuspend", controller.Live);

        // Applied once, not applied-and-restored through several paired cycles.
        Assert.Single(controller.Applied);
        Assert.Empty(controller.Restored);
        Assert.Contains("kayıtlı profil", result.StatusLine, StringComparison.Ordinal);
        Assert.Equal(LatencyTransactionState.Committed, second.Snapshots.Value!.State);
    }

    /// <summary>
    /// The saved answer is a starting point, not a promise. A link the setting no longer
    /// suits has to end up back on the driver's own values.
    /// </summary>
    [Fact]
    public async Task AProfileThatNoLongerHoldsUpIsRolledBackAndForgotten()
    {
        var network = Fake.Network("changed");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        // The same setting now makes things clearly worse than the fresh baseline.
        var controller = new FakeController();
        var probe = new FakeProbe(controller, (live, _) => live.Contains("SelectiveSuspend")
            ? Fake.Measurement(48)
            : Fake.Measurement(30));
        var second = new LatencyScenario(controller, probe, profiles: first.Profiles);

        var result = await second.Optimizer.OptimizeAsync(network);

        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
        Assert.Empty(controller.Live);
        Assert.Null(second.Snapshots.Value);
        Assert.DoesNotContain(second.Profiles.Profiles, profile => profile.AcceptedProperties.Count > 0);
    }

    [Fact]
    public async Task AProfileNamingASettingTheDriverNoLongerOffersIsDiscarded()
    {
        var network = Fake.Network("gone");
        var first = LatencyScenario.WithImprovement(gain: 6);
        await first.Optimizer.OptimizeAsync(network);

        // Same fingerprint inputs, but the property is simply not there any more.
        var controller = new FakeController
        {
            Detect = fingerprint => Fake.Capability(fingerprint) with { PowerManagement = [] },
        };
        var second = new LatencyScenario(controller, FakeProbe.Flat(controller), profiles: first.Profiles);

        var result = await second.Optimizer.OptimizeAsync(network);

        Assert.Empty(controller.Applied);
        Assert.Equal(LatencyOptimizationStatus.NoGain, result.Status);
    }
}
