using DpiBypass.Core.Config;
using System.Buffers.Binary;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Vodafone;
using Xunit;

namespace DpiBypass.Tests;

public class HotspotTtlFixTests
{
    [Theory]
    [InlineData(443)]
    [InlineData(25565)]
    public void TtlRewritePreservesTransportIncludingDeliberatelyBadChecksums(int port)
    {
        var packet = PacketFactory.BuildIPv4Tcp([0, 1, 2, 3], destinationPort: (ushort)port);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(36), 0xBAD1);
        var transport = packet[20..];

        Assert.True(HotspotTtlFix.TryFix(packet, TtlFixSettings.Default, out var changed));
        Assert.True(changed);
        Assert.Equal(65, packet[8]);
        Assert.Equal(transport, packet[20..]);

        uint sum = 0;
        for (var i = 0; i < 20; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i));
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        Assert.Equal(0xFFFFu, sum);
    }

    [Fact]
    public void ExpiringDecoyIsForwardedByteForByte()
    {
        var packet = PacketFactory.BuildIPv4Tcp([1, 2, 3], ttl: 5);
        var original = packet.ToArray();
        Assert.True(HotspotTtlFix.TryFix(packet, TtlFixSettings.Default, out var changed));
        Assert.False(changed);
        Assert.Equal(original, packet);
    }

    [Fact]
    public void IPv6HopLimitRewritePreservesTransportAndExtensions()
    {
        var packet = PacketFactory.BuildIPv6Tcp([1, 2, 3], destinationOptions: true);
        var original = packet.ToArray();
        Assert.True(HotspotTtlFix.TryFix(packet, new TtlFixSettings { DropIPv6 = false }, out var changed));
        Assert.True(changed);
        original[7] = 65;
        Assert.Equal(original, packet);
    }

    /// <summary>
    /// The invariant that keeps the two features from destroying each other.
    /// </summary>
    /// <remarks>
    /// The decoy strategies work precisely because their packets expire in the
    /// operator's network before reaching the server. If the hotspot TTL fix rewrote
    /// those to 65 they would arrive, the server would see a ClientHello for
    /// www.google.com on a discord.com connection, and every fake-packet recipe would
    /// silently stop working. So the guard must sit above the highest TTL any
    /// strategy in the library uses - and this test fails the build if someone ever
    /// adds one that crosses it.
    /// </remarks>
    [Fact]
    public void TheGuardSitsAboveEveryDecoyTtlInTheLibrary()
    {
        var highest = StrategyLibrary.All
            .Where(s => s.Fake == FakeMode.ExpiredTtl)
            .Select(s => (int)s.FakeTtl)
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(highest > 0, "no expired-TTL strategy found; this test would pass vacuously");
        Assert.True(
            highest < TtlFixSettings.DefaultGuard,
            $"a strategy uses TTL {highest}, which the TTL fix guard ({TtlFixSettings.DefaultGuard}) would rewrite");
    }

    [Fact]
    public void TheDefaultTtlLeavesSixtyFourAfterOneHop()
    {
        // The whole trick: the phone decrements once, so the operator sees its own
        // traffic's value.
        Assert.Equal(64, TtlFixSettings.DefaultTimeToLive - 1);
    }

    [Fact]
    public void ATtlBelowTheGuardIsRefused()
    {
        var settings = new TtlFixSettings { TimeToLive = 10 };
        var error = Assert.Throws<TtlFixException>(settings.Validate);

        Assert.Contains("TTL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultSettingsValidate()
    {
        TtlFixSettings.Default.Validate();
        new TtlFixSettings { TimeToLive = 255 }.Validate();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheKernelFilterIsScopedToOneAdapterAndCarriesTheGuard(bool dropIPv6)
    {
        var filter = HotspotTtlFix.BuildFilter(17, new TtlFixSettings { DropIPv6 = dropIPv6 });

        Assert.Contains("ifIdx == 17", filter, StringComparison.Ordinal);
        Assert.Contains("outbound", filter, StringComparison.Ordinal);
        Assert.Contains($"ip.TTL >= {TtlFixSettings.DefaultGuard}", filter, StringComparison.Ordinal);

        if (dropIPv6)
        {
            Assert.DoesNotContain("HopLimit", filter, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"ipv6.HopLimit >= {TtlFixSettings.DefaultGuard}", filter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ApplyingWithoutAnAdapterFails()
    {
        using var fix = new HotspotTtlFix();
        Assert.Throws<TtlFixException>(() => fix.Apply(0, TtlFixSettings.Default));
    }

    // --- the per network registration the mode is gated on ---------------------

    [Fact]
    public void AModeSwitchedOnForOneNetworkDoesNotFollowYouToAnother()
    {
        var settings = new AppSettings();
        settings.RememberHotspotNetwork("phone-key", "atoms hotspot", "Wi-Fi");

        Assert.True(settings.HotspotNetworkRegistered("phone-key"));
        Assert.False(settings.HotspotNetworkRegistered("home-key"));
    }

    [Fact]
    public void RegisteringTheSameNetworkTwiceDoesNotDuplicateIt()
    {
        var settings = new AppSettings();
        settings.RememberHotspotNetwork("key", "first name", "Wi-Fi");
        settings.RememberHotspotNetwork("key", "renamed", "Wi-Fi");

        Assert.Single(settings.HotspotTtlNetworks);
        Assert.Equal("renamed", settings.HotspotTtlNetworks[0].DisplayName);
    }

    [Fact]
    public void TheOldestNetworkIsDroppedOnceTheListIsFull()
    {
        var settings = new AppSettings();

        for (var i = 0; i < TtlFixSettings.MaxNetworks + 5; i++)
        {
            settings.RememberHotspotNetwork($"key-{i}", $"net {i}", "Wi-Fi");
        }

        Assert.Equal(TtlFixSettings.MaxNetworks, settings.HotspotTtlNetworks.Count);
        Assert.False(settings.HotspotNetworkRegistered("key-0"));
        Assert.True(settings.HotspotNetworkRegistered($"key-{TtlFixSettings.MaxNetworks + 4}"));
    }

    [Fact]
    public void ForgettingANetworkReportsWhetherItWasThere()
    {
        var settings = new AppSettings();
        settings.RememberHotspotNetwork("key", "net", "Wi-Fi");

        Assert.True(settings.ForgetHotspotNetwork("key"));
        Assert.False(settings.ForgetHotspotNetwork("key"));
    }

    [Fact]
    public void AnEmptyKeyIsNeverRegistered()
    {
        var settings = new AppSettings();
        settings.RememberHotspotNetwork(string.Empty, "net", "Wi-Fi");

        Assert.Empty(settings.HotspotTtlNetworks);
        Assert.False(settings.HotspotNetworkRegistered(string.Empty));
    }
}
