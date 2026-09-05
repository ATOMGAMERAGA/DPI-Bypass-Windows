using DpiBypass.Core.Config;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Network;
using DpiBypass.Core.Vodafone;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The TTL rewrite itself: the guard, the kernel filter and the arithmetic.
/// </summary>
public class HotspotTtlFixTests
{
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

    /// <summary>
    /// Windows and Linux describe the same behaviour, so they use the same numbers.
    /// </summary>
    /// <remarks>
    /// The Linux build keeps these in <c>src/dpibypass/constants.py</c> as
    /// <c>VODAFONE_TTL_GUARD</c>, <c>VODAFONE_TTL_VALUE</c> and
    /// <c>VODAFONE_MAX_NETWORKS</c>. A value that drifted on one platform would not be a
    /// platform difference; it would be a bug on whichever side moved.
    /// </remarks>
    [Fact]
    public void TheConstantsMatchTheLinuxBuild()
    {
        Assert.Equal(32, TtlFixSettings.DefaultGuard);
        Assert.Equal(65, TtlFixSettings.DefaultTimeToLive);
        Assert.Equal(10, TtlFixSettings.MaxNetworks);
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

    /// <summary>
    /// A stored number is corrected rather than allowed to disable the feature silently.
    /// </summary>
    [Theory]
    [InlineData(0, TtlFixSettings.DefaultTimeToLive)]
    [InlineData(32, TtlFixSettings.DefaultTimeToLive)]
    [InlineData(256, TtlFixSettings.DefaultTimeToLive)]
    [InlineData(-1, TtlFixSettings.DefaultTimeToLive)]
    [InlineData(33, 33)]
    [InlineData(255, 255)]
    public void AnUnusableStoredTtlBecomesTheDefault(int stored, int expected)
        => Assert.Equal(expected, TtlFixSettings.CoerceTimeToLive(stored));

    /// <summary>A value out of range is refused with the reason, not clamped into range.</summary>
    [Fact]
    public void ValidatingATtlDoesNotWrapAroundIntoALegalValue()
    {
        Assert.Throws<TtlFixException>(() => TtlFixSettings.ValidateTimeToLive(321));
        Assert.Throws<TtlFixException>(() => TtlFixSettings.ValidateTimeToLive(0));
        TtlFixSettings.ValidateTimeToLive(65);
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

    /// <summary>
    /// The filter never sees inbound traffic, so nothing arriving is copied to user mode.
    /// </summary>
    [Fact]
    public void TheFilterOnlyEverMatchesOutboundTraffic()
    {
        var filter = HotspotTtlFix.BuildFilter(3, TtlFixSettings.Default);

        Assert.StartsWith("outbound and", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("inbound", filter, StringComparison.Ordinal);
    }

    /// <summary>
    /// A decoy packet under the guard leaves exactly as the engine built it.
    /// </summary>
    /// <remarks>
    /// The kernel filter already excludes these, so this is the second of the two
    /// guards. Both are cheap and the failure they prevent is silent: every fake-packet
    /// strategy would keep reporting success while the decoys reached the server.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(TtlFixSettings.DefaultGuard - 1)]
    public void ADecoyPacketUnderTheGuardIsNotTouched(byte ttl)
    {
        Span<byte> packet = stackalloc byte[20];
        packet[8] = ttl;

        Assert.Null(HotspotTtlFix.Rewrite(packet, offset: 8, TtlFixSettings.Default));
        Assert.Equal(ttl, packet[8]);
    }

    /// <summary>An ordinary packet is rewritten, and says what was there before.</summary>
    /// <remarks>
    /// The previous value is what lets the loop put the packet back when the checksum
    /// cannot be recomputed, rather than sending one the next hop discards.
    /// </remarks>
    [Fact]
    public void AnOrdinaryPacketIsRewrittenAndReportsWhatItReplaced()
    {
        Span<byte> packet = stackalloc byte[20];
        packet[8] = 128;

        var change = HotspotTtlFix.Rewrite(packet, offset: 8, TtlFixSettings.Default);

        Assert.Equal((8, (byte)128), change);
        Assert.Equal(TtlFixSettings.DefaultTimeToLive, packet[8]);
    }

    /// <summary>A packet already carrying our value is left alone.</summary>
    /// <remarks>
    /// Re-injected packets come back around through the same handle. Rewriting a value
    /// to itself would count a rewrite and redo a checksum on every pass forever.
    /// </remarks>
    [Fact]
    public void APacketAlreadyCarryingOurValueIsLeftAlone()
    {
        Span<byte> packet = stackalloc byte[20];
        packet[8] = TtlFixSettings.DefaultTimeToLive;

        Assert.Null(HotspotTtlFix.Rewrite(packet, offset: 8, TtlFixSettings.Default));
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
        settings.RememberVodafoneNetwork(new NetworkFingerprint { Ssid = "atom" });

        Assert.True(settings.VodafoneNetworkRegistered(new NetworkFingerprint { Ssid = "atom" }));
        Assert.False(settings.VodafoneNetworkRegistered(new NetworkFingerprint { Ssid = "ev-wifi" }));
    }

    [Fact]
    public void TheOldestNetworkIsDroppedOnceTheListIsFull()
    {
        var settings = new AppSettings();

        for (var i = 0; i < TtlFixSettings.MaxNetworks + 5; i++)
        {
            settings.RememberVodafoneNetwork($"key-{i}", $"net {i}", "Wi-Fi", $"net {i}");
        }

        Assert.Equal(TtlFixSettings.MaxNetworks, settings.VodafoneModeNetworks.Count);
        Assert.False(settings.VodafoneNetworkRegistered("key-0"));
        Assert.True(settings.VodafoneNetworkRegistered($"key-{TtlFixSettings.MaxNetworks + 4}"));
    }

    [Fact]
    public void ForgettingANetworkReportsWhetherItWasThere()
    {
        var settings = new AppSettings();
        settings.RememberVodafoneNetwork("key", "net", "Wi-Fi");

        Assert.True(settings.ForgetVodafoneNetwork("key"));
        Assert.False(settings.ForgetVodafoneNetwork("key"));
    }

    [Fact]
    public void AnEmptyKeyIsNeverRegistered()
    {
        var settings = new AppSettings();
        settings.RememberVodafoneNetwork(string.Empty, "net", "Wi-Fi");

        Assert.Empty(settings.VodafoneModeNetworks);
        Assert.False(settings.VodafoneNetworkRegistered(string.Empty));
    }
}
