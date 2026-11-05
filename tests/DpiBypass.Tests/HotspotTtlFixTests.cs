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

        // Packets the stack already sends at the right value stay in the kernel: with
        // the adapter's hop limit set natively, game traffic is never diverted.
        Assert.Contains($"ip.TTL != {TtlFixSettings.DefaultTimeToLive}", filter, StringComparison.Ordinal);

        // This lower-priority handle must still see packets injected by the DPI engine;
        // the engine itself is the boundary that excludes packets injected upstream.
        Assert.DoesNotContain("impostor", filter, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// A packet a game booster's driver injected loses one hop when WinDivert re-injects
    /// it, so it is written one higher to still reach the operator at 64.
    /// </summary>
    [Fact]
    public void APacketABoosterInjectedIsWrittenOneHigherToCancelTheReinjectionHop()
    {
        Span<byte> packet = stackalloc byte[20];
        packet[8] = 128;

        var change = HotspotTtlFix.Rewrite(packet, offset: 8, TtlFixSettings.Default, impostor: true);

        Assert.Equal((8, (byte)128), change);
        Assert.Equal(TtlFixSettings.DefaultTimeToLive + 1, packet[8]);

        // The decrement WinDivert applies, then the phone's: 64 on the wire.
        Assert.Equal(64, packet[8] - 1 - 1);
    }

    [Fact]
    public void ABoosterPacketStillRespectsTheDecoyGuard()
    {
        Span<byte> packet = stackalloc byte[20];
        packet[8] = 5;

        Assert.Null(HotspotTtlFix.Rewrite(packet, offset: 8, TtlFixSettings.Default, impostor: true));
        Assert.Equal(5, packet[8]);
    }

    [Fact]
    public void TheCompensatedTtlNeverWrapsPast255()
        => Assert.Equal(255, HotspotTtlFix.TargetTimeToLive(new TtlFixSettings { TimeToLive = 255 }, impostor: true));

    [Fact]
    public void TryFixCompensatesBoosterPacketsOnBothFamilies()
    {
        using var fix = new HotspotTtlFix();

        var v4 = new byte[20];
        v4[0] = 0x45;
        v4[8] = 128;
        Assert.True(fix.TryFix(v4, out _, impostor: true));
        Assert.Equal(TtlFixSettings.DefaultTimeToLive + 1, v4[8]);

        var ordinary = new byte[20];
        ordinary[0] = 0x45;
        ordinary[8] = 128;
        Assert.True(fix.TryFix(ordinary, out _));
        Assert.Equal(TtlFixSettings.DefaultTimeToLive, ordinary[8]);
    }

    /// <summary>
    /// A Minecraft join handshake already at the right TTL must still reach the splitter,
    /// and the clause must not pull HTTPS into user mode.
    /// </summary>
    [Fact]
    public void MinecraftHandshakesAreStillDivertedWhenTheTtlIsAlreadyRight()
    {
        var filter = HotspotTtlFix.BuildFilter(5, TtlFixSettings.Default);
        Assert.Contains("tcp.Payload[1] == 0", filter, StringComparison.Ordinal);
        Assert.Contains("tcp.DstPort != 443", filter, StringComparison.Ordinal);

        var fallback = HotspotTtlFix.BuildFilter(5, TtlFixSettings.Default, includeMinecraft: false);
        Assert.DoesNotContain("Payload", fallback, StringComparison.Ordinal);
        Assert.Contains($"ip.TTL != {TtlFixSettings.DefaultTimeToLive}", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNativeHopLimitIsAppliedAndPutBack()
    {
        var backend = new FakeHopLimitBackend();
        using var native = new NativeHopLimit(backend);

        await native.Set(12, 65);
        Assert.Equal((12, (byte)65), native.Applied);
        Assert.Equal([(12, (byte)65)], backend.Applied);

        await native.Clear();
        Assert.Equal((0, (byte)0), native.Applied);
        var restored = Assert.Single(backend.Restored);
        Assert.Equal(new HopLimitSnapshot(12, 128, 64), restored);
    }

    /// <summary>A remove-and-reapply on the same adapter costs no PowerShell run.</summary>
    [Fact]
    public async Task AQuickClearAndReapplyConvergesWithoutChurn()
    {
        var backend = new FakeHopLimitBackend();
        using var native = new NativeHopLimit(backend);
        await native.Set(12, 65);

        _ = native.Clear();
        await native.Set(12, 65);

        Assert.Single(backend.Applied);
        Assert.Empty(backend.Restored);
    }

    [Fact]
    public async Task MovingAdaptersRestoresTheOldOneFirst()
    {
        var backend = new FakeHopLimitBackend();
        using var native = new NativeHopLimit(backend);
        await native.Set(12, 65);
        await native.Set(14, 65);

        Assert.Equal(12, Assert.Single(backend.Restored).InterfaceIndex);
        Assert.Equal((14, (byte)65), native.Applied);
    }

    [Fact]
    public async Task ARefusedNativeChangeLeavesTheFilterInCharge()
    {
        var backend = new FakeHopLimitBackend { Refuse = true };
        using var native = new NativeHopLimit(backend);
        await native.Set(12, 65);
        Assert.Equal((0, (byte)0), native.Applied);
    }

    [Theory]
    [InlineData("IPv4=128;IPv6=64", 128u, 64u)]
    [InlineData("IPv4=128\r\n", 128u, null)]
    [InlineData("IPv6=255", null, 255u)]
    public void HopLimitOutputIsParsed(string output, uint? v4, uint? v6)
    {
        var snapshot = PowerShellHopLimitBackend.Parse(3, output);
        Assert.Equal(new HopLimitSnapshot(3, v4, v6), snapshot);
    }

    [Fact]
    public void EmptyHopLimitOutputMeansNothingWasChanged()
        => Assert.Null(PowerShellHopLimitBackend.Parse(3, "  "));

    private sealed class FakeHopLimitBackend : IHopLimitBackend
    {
        public bool Refuse { get; init; }
        public List<(int, byte)> Applied { get; } = [];
        public List<HopLimitSnapshot> Restored { get; } = [];

        public Task<HopLimitSnapshot?> ApplyAsync(int interfaceIndex, byte hopLimit, CancellationToken cancellationToken)
        {
            if (Refuse)
            {
                return Task.FromResult<HopLimitSnapshot?>(null);
            }

            Applied.Add((interfaceIndex, hopLimit));
            return Task.FromResult<HopLimitSnapshot?>(new HopLimitSnapshot(interfaceIndex, 128, 64));
        }

        public Task RestoreAsync(HopLimitSnapshot snapshot, CancellationToken cancellationToken)
        {
            Restored.Add(snapshot);
            return Task.CompletedTask;
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
