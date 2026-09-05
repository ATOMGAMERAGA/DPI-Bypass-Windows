using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Core.Vodafone;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Vodafone Sınırsız Modu recognising the network the user actually saved.
/// </summary>
/// <remarks>
/// The reported defect was that the mode never noticed the registered hotspot: the card
/// said "kayıtlı değil" against the very network that had just been saved, nothing ran
/// by itself, and every check had to be started by hand. Two separate causes, both
/// pinned here - a fingerprint key that changes every hotspot session, and a network
/// identity that was only ever read while the DPI engine was running.
/// </remarks>
public sealed class VodafoneModeTests
{
    /// <summary>
    /// A phone hotspot is a different key every session and the same network every time.
    /// </summary>
    /// <remarks>
    /// <see cref="NetworkFingerprint.Key"/> hashes the access point's MAC address, and
    /// Android and iOS both hand out a fresh randomised one whenever the hotspot is
    /// switched off and on. Matching on the key alone therefore lost the registration on
    /// the next connection, which is precisely the connection this feature is for.
    /// </remarks>
    [Fact]
    public void AHotspotThatRotatesItsMacIsStillTheSameSavedNetwork()
    {
        var settings = new AppSettings();
        var monday = new NetworkFingerprint
        {
            Ssid = "atom",
            Bssid = "aa:bb:cc:dd:ee:01",
            AdapterName = "Wi-Fi",
        };
        var tuesday = monday with { Bssid = "aa:bb:cc:dd:ee:02" };

        settings.RememberVodafoneNetwork(monday);

        Assert.NotEqual(monday.Key, tuesday.Key);
        Assert.True(settings.VodafoneNetworkRegistered(tuesday));
        Assert.True(ProtectionService.ShouldRunHotspotDiagnostics(
            new AppSettings
            {
                VodafoneModeEnabled = true,
                HotspotDiagnostics = true,
                VodafoneModeNetworks = settings.VodafoneModeNetworks,
            },
            tuesday));
    }

    /// <summary>Recognising a network by name updates the entry to this session.</summary>
    [Fact]
    public void RecognisingANetworkByNameAdoptsItsCurrentIdentity()
    {
        var settings = new AppSettings();
        var first = new NetworkFingerprint { Ssid = "atom", Bssid = "aa:bb:cc:dd:ee:01" };
        var second = first with { Bssid = "aa:bb:cc:dd:ee:02", AdapterName = "Wi-Fi 2" };

        settings.RememberVodafoneNetwork(first);

        Assert.True(settings.RefreshVodafoneNetworkIdentity(second));
        Assert.Equal(second.Key, Assert.Single(settings.VodafoneModeNetworks).Key);

        // Idempotent: the second pass has nothing to write, so nothing is saved.
        Assert.False(settings.RefreshVodafoneNetworkIdentity(second));
    }

    /// <summary>Two different names stay two different networks.</summary>
    [Fact]
    public void ADifferentNetworkIsStillNotAMatch()
    {
        var settings = new AppSettings();
        settings.RememberVodafoneNetwork(new NetworkFingerprint { Ssid = "atom" });

        Assert.False(settings.VodafoneNetworkRegistered(new NetworkFingerprint { Ssid = "cafe-wifi" }));

        // A link with no name has nothing to match on, so it falls back to the key only.
        Assert.False(settings.VodafoneNetworkRegistered(new NetworkFingerprint { AdapterName = "Ethernet" }));
    }

    /// <summary>Saving the same hotspot again replaces its row rather than adding one.</summary>
    [Fact]
    public void SavingTheSameHotspotTwiceDoesNotFillTheListWithSessions()
    {
        var settings = new AppSettings();

        for (var session = 0; session < 5; session++)
        {
            settings.RememberVodafoneNetwork(new NetworkFingerprint
            {
                Ssid = "atom",
                Bssid = $"aa:bb:cc:dd:ee:0{session}",
            });
        }

        var entry = Assert.Single(settings.VodafoneModeNetworks);
        Assert.Equal("atom", entry.Ssid);
    }

    /// <summary>
    /// Settings written before the name was stored still match once we are on them.
    /// </summary>
    [Fact]
    public void AnEntryFromAnOlderBuildMatchesOnItsDisplayName()
    {
        var settings = new AppSettings
        {
            VodafoneModeNetworks =
            [
                new VodafoneModeNetwork { Key = "0123456789abcdef", DisplayName = "atom" },
            ],
        };

        Assert.True(settings.VodafoneNetworkRegistered(new NetworkFingerprint { Ssid = "atom" }));
    }

    /// <summary>
    /// The card leads with "Aktif" once the rewrite is running on a saved network.
    /// </summary>
    /// <remarks>
    /// The word is the whole of what the user was looking for and could not find. It is
    /// deliberately not shown for an unregistered network: "on, but not here" is a
    /// different state and saying "active" would be a claim about a network the mode is
    /// doing nothing with. It is equally not shown when the rule failed to install -
    /// see <see cref="VodafoneRewriteCardTests"/>.
    /// </remarks>
    [Fact]
    public void ARegisteredNetworkReadsAsActive()
    {
        var active = HotspotStatusView.From(Status(registeredHere: true));
        var elsewhere = HotspotStatusView.From(Status(registeredHere: false));

        Assert.StartsWith("Aktif · atom", active.Headline, StringComparison.Ordinal);
        Assert.Equal("ok", active.Severity);
        Assert.True(active.RewriteActive);

        Assert.StartsWith("Açık · atom", elsewhere.Headline, StringComparison.Ordinal);
        Assert.Contains("kayıtlı değil", elsewhere.Headline, StringComparison.Ordinal);
        Assert.False(elsewhere.RewriteActive);
    }

    [Fact]
    public void TheModeBeingOffStillReadsAsOff()
    {
        var view = HotspotStatusView.From(Status(registeredHere: true, enabled: false));

        Assert.Equal("Kapalı", view.Headline);
        Assert.Equal("off", view.Severity);
    }

    /// <summary>
    /// Moving onto a saved network runs the checks even with protection switched off.
    /// </summary>
    /// <remarks>
    /// This is the second half of the reported defect. The network watch used to be
    /// built by the engine and torn down with it, and the transition handler returned
    /// early on the engine's cancelled token - so with protection stopped the app did
    /// not know which network it was on, said the saved hotspot was unregistered, and
    /// ran nothing until the user pressed every button on the card by hand.
    /// </remarks>
    [Fact]
    public async Task ArrivingOnASavedNetworkRunsTheChecksWithTheEngineStopped()
    {
        using var directory = new TempDirectory();
        var diagnostics = new RecordingHotspotDiagnostics();
        var rule = new FakeTtlFix();
        await using var service = new ProtectionService(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            hotspotDiagnostics: diagnostics,
            ttlFix: rule);

        var monday = new NetworkFingerprint
        {
            Ssid = "atom",
            Bssid = "aa:bb:cc:dd:ee:01",
            AdapterName = "Wi-Fi",
            InterfaceIndex = 7,
        };
        service.EnableVodafoneMode(monday);

        Assert.Equal(ProtectionState.Stopped, service.State);

        // The same hotspot, a session later, under a randomised access point MAC.
        var tuesday = monday with { Bssid = "aa:bb:cc:dd:ee:02" };
        service.OnNetworkChanged(tuesday);

        Assert.True(await diagnostics.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(service.HotspotStatus.RegisteredHere);
        Assert.Equal(tuesday.Key, Assert.Single(service.Settings.VodafoneModeNetworks).Key);
        Assert.StartsWith("Aktif · atom", service.HotspotView.Headline, StringComparison.Ordinal);

        // And the rule is on the adapter, which is the half of "aktif" that the previous
        // build did not have at all.
        Assert.True(rule.IsActive);
        Assert.Equal(7, rule.InterfaceIndex);
    }

    /// <summary>A network nobody saved is still not one of the user's own.</summary>
    [Fact]
    public async Task ArrivingSomewhereElseDoesNotClaimTheModeIsActive()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = new ProtectionService(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            hotspotDiagnostics: new RecordingHotspotDiagnostics(),
            ttlFix: rule);

        service.EnableVodafoneMode(new NetworkFingerprint
        {
            Ssid = "atom",
            AdapterName = "Wi-Fi",
            InterfaceIndex = 7,
        });

        service.OnNetworkChanged(new NetworkFingerprint
        {
            Ssid = "cafe-wifi",
            AdapterName = "Wi-Fi",
            InterfaceIndex = 8,
        });

        Assert.False(service.HotspotStatus.RegisteredHere);
        Assert.Contains("kayıtlı değil", service.HotspotView.Headline, StringComparison.Ordinal);

        // The rule was scoped to adapter 7 and the machine is on adapter 8. Leaving it up
        // would rewrite the TTL of every packet on a network nobody registered.
        Assert.False(rule.IsActive);
        Assert.Equal(1, rule.Clears);
        Assert.Equal([7], rule.AppliedTo);
    }

    private sealed class RecordingHotspotDiagnostics : IMobileHotspotDiagnostics
    {
        public TaskCompletionSource<bool> Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HotspotDiagnosticResult> RunAsync(
            NetworkFingerprint network,
            CancellationToken cancellationToken = default)
        {
            var result = new HotspotDiagnosticResult
            {
                NetworkName = network.DisplayName,
                AdapterName = network.AdapterName ?? "-",
                HasIpv4 = true,
                HasIpv6 = false,
                Ipv4Works = true,
                Ipv6Works = false,
                DnsWorks = true,
                AddressKind = HotspotAddressKind.Private,
                VpnAdapterActive = false,
            };

            Ran.TrySetResult(true);
            return Task.FromResult(result);
        }
    }

    private static HotspotStatus Status(
        bool registeredHere,
        bool enabled = true,
        bool? ttlActive = null) => new(
        VodafoneModeEnabled: enabled,
        DiagnosticsEnabled: true,
        RegisteredHere: registeredHere,
        RegisteredNetworks: 1,
        NetworkName: "atom",
        AdapterName: "Wi-Fi",
        LegacyCleanedAt: null,
        LastResult: null,
        TtlActive: ttlActive ?? (enabled && registeredHere));
}

/// <summary>
/// The rule the mode exists to install, and every decision about when it is up.
/// </summary>
/// <remarks>
/// The Windows build spent a release with this feature's switch wired to nothing but a
/// read-only diagnostic pass, so "Vodafone Sınırsız Modu açık" meant the app was checking
/// the connection and changing nothing about it - while the Linux build was rewriting the
/// TTL all along. These pin the mechanism back to the switch, and pin the conditions the
/// rule must never outlive.
/// </remarks>
public sealed class VodafoneRewriteTests
{
    private static readonly NetworkFingerprint Hotspot = new()
    {
        Ssid = "atom",
        Bssid = "aa:bb:cc:dd:ee:01",
        AdapterName = "Wi-Fi",
        InterfaceIndex = 7,
    };

    /// <summary>Switching the mode on installs the rewrite, with the arithmetic that matters.</summary>
    [Fact]
    public async Task SwitchingTheModeOnRewritesOutgoingPacketsOnThisAdapter()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);

        Assert.True(rule.IsActive);
        Assert.Equal(7, rule.InterfaceIndex);

        // 65 out, one decrement in the phone, 64 at the operator: the value the phone's
        // own traffic carries. This is the whole trick and it is the same number the
        // Linux build sends.
        Assert.Equal(65, rule.Settings.TimeToLive);
        Assert.Equal(64, rule.Settings.TimeToLive - 1);
        Assert.True(rule.Settings.DropIPv6);

        var status = service.HotspotStatus;
        Assert.True(status.TtlActive);
        Assert.Equal(65, status.TtlValue);
    }

    /// <summary>Switching it off takes the rule down, not just the label.</summary>
    [Fact]
    public async Task SwitchingTheModeOffRemovesTheRule()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);
        service.DisableVodafoneMode();

        Assert.False(rule.IsActive);
        Assert.Equal(1, rule.Clears);
        Assert.False(service.HotspotStatus.TtlActive);
    }

    /// <summary>Forgetting the network under us un-gates the rule immediately.</summary>
    /// <remarks>
    /// Waiting for the next network change would leave the TTL of every packet rewritten
    /// on a connection the user has just said they do not want this on.
    /// </remarks>
    [Fact]
    public async Task ForgettingTheNetworkWeAreOnRemovesTheRuleAtOnce()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);
        service.ForgetVodafoneNetwork(Assert.Single(service.Settings.VodafoneModeNetworks).Key);

        Assert.False(rule.IsActive);
        Assert.True(service.Settings.VodafoneModeEnabled);
    }

    /// <summary>Registering the network we are on brings the rule up without a reconnect.</summary>
    [Fact]
    public async Task RememberingThisNetworkStartsTheRewriteWithoutAReconnect()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);
        service.OnNetworkChanged(Hotspot with { Ssid = "cafe-wifi", InterfaceIndex = 8 });
        Assert.False(rule.IsActive);

        service.Settings.RememberVodafoneNetwork(service.Network);
        service.OnNetworkChanged(service.Network);

        Assert.True(rule.IsActive);
        Assert.Equal(8, rule.InterfaceIndex);
    }

    /// <summary>
    /// The same rule on the same adapter is left alone rather than reinstalled.
    /// </summary>
    /// <remarks>
    /// Re-applying closes and reopens the driver handle, and packets leaving in that gap
    /// carry the TTL the rule exists to correct. Nothing that merely re-reads the state -
    /// a status poll, a redundant transition - may cost that.
    /// </remarks>
    [Fact]
    public async Task AnUnchangedRuleIsNotReinstalled()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);
        var installs = rule.Applies;

        // The same network again - the poll and the OS event both raise this - and a
        // read of the card, which is what the UI does on every tick.
        service.OnNetworkChanged(Hotspot);
        _ = service.HotspotView;
        _ = service.HotspotStatus;

        Assert.Equal(installs, rule.Applies);
        Assert.Equal(0, rule.Clears);
        Assert.True(rule.IsActive);
    }

    /// <summary>
    /// A rule that could not be installed is reported, not shown as working.
    /// </summary>
    /// <remarks>
    /// On Windows this needs administrator rights and the WinDivert driver, and neither is
    /// visible from the switch. A card that says "Aktif" while nothing is rewriting is the
    /// exact experience this whole change is about.
    /// </remarks>
    [Fact]
    public async Task ARuleThatCannotBeInstalledIsReportedRatherThanClaimed()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix((_, _) => new TtlFixException("sürücü açılamadı"));
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);

        var view = service.HotspotView;
        Assert.False(view.RewriteActive);
        Assert.DoesNotContain("Aktif", view.Headline, StringComparison.Ordinal);
        Assert.Contains("sürücü açılamadı", view.Headline, StringComparison.Ordinal);
        Assert.Equal("attention", view.Severity);
        Assert.Equal("sürücü açılamadı", service.HotspotStatus.TtlFailure);
    }

    /// <summary>The mode is not tied to the DPI engine, on Windows any more than on Linux.</summary>
    [Fact]
    public async Task TheRuleRunsWithProtectionStopped()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);

        Assert.Equal(ProtectionState.Stopped, service.State);
        Assert.True(rule.IsActive);
    }

    /// <summary>Nothing is left rewriting after the service is gone.</summary>
    [Fact]
    public async Task DisposingTheServiceTakesTheRuleDown()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();

        await using (var service = Build(directory, rule))
        {
            service.EnableVodafoneMode(Hotspot);
            Assert.True(rule.IsActive);
        }

        Assert.False(rule.IsActive);
    }

    /// <summary>
    /// A TTL at or below the guard is refused before it can reach the driver.
    /// </summary>
    /// <remarks>
    /// It would rewrite the engine's own low-TTL decoy packets, and every fake-packet
    /// strategy in the library would stop working with nothing reporting a failure.
    /// </remarks>
    [Fact]
    public async Task ATtlUnderTheGuardIsRefused()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);

        Assert.Throws<TtlFixException>(() => service.SetVodafoneTtl(TtlFixSettings.DefaultGuard));
        Assert.Throws<TtlFixException>(() => service.SetVodafoneTtl(256));
        Assert.Equal(TtlFixSettings.DefaultTimeToLive, service.Settings.VodafoneTtl);
        Assert.Equal(TtlFixSettings.DefaultTimeToLive, rule.Settings.TimeToLive);
    }

    /// <summary>Changing the TTL or the IPv6 option re-applies the rule with it.</summary>
    [Fact]
    public async Task ChangingTheSettingsReinstallsTheRule()
    {
        using var directory = new TempDirectory();
        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        service.EnableVodafoneMode(Hotspot);
        service.SetVodafoneTtl(128);
        Assert.Equal(128, rule.Settings.TimeToLive);

        service.SetVodafoneDropIPv6(false);
        Assert.False(rule.Settings.DropIPv6);
        Assert.Equal(128, rule.Settings.TimeToLive);
    }

    /// <summary>
    /// A settings file written by the build that had the rewrite comes back with it on.
    /// </summary>
    /// <remarks>
    /// The user's own TTL and IPv6 choices come with it. Losing them on upgrade would be
    /// a silent change to what leaves the machine.
    /// </remarks>
    [Fact]
    public async Task ALegacySettingsFileComesBackWithTheModeAndItsTuning()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("settings.json"), """
            {
              "HotspotTtlFix": true,
              "HotspotTtlValue": 128,
              "HotspotDropIPv6": false,
              "HotspotTtlNetworks": [ { "Key": "abc", "DisplayName": "atom", "AdapterName": "Wi-Fi" } ]
            }
            """);

        var rule = new FakeTtlFix();
        await using var service = Build(directory, rule);

        Assert.True(service.Settings.VodafoneModeEnabled);
        Assert.Equal(128, service.Settings.VodafoneTtl);
        Assert.False(service.Settings.VodafoneDropIPv6);
        Assert.Single(service.Settings.VodafoneModeNetworks);
        Assert.False(service.Settings.HotspotTtlFix);
    }

    /// <summary>A hand-edited TTL that would break the engine becomes the default.</summary>
    [Fact]
    public async Task AnUnusableStoredTtlFallsBackToTheDefault()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("settings.json"), """{"VodafoneTtl": 4}""");

        await using var service = Build(directory, new FakeTtlFix());

        Assert.Equal(TtlFixSettings.DefaultTimeToLive, service.Settings.VodafoneTtl);
    }

    private static ProtectionService Build(TempDirectory directory, IHotspotTtlFix rule)
        => new(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            hotspotDiagnostics: new StubHotspotDiagnostics(),
            ttlFix: rule);

    private sealed class StubHotspotDiagnostics : IMobileHotspotDiagnostics
    {
        public Task<HotspotDiagnosticResult> RunAsync(
            NetworkFingerprint network,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new HotspotDiagnosticResult
            {
                NetworkName = network.DisplayName,
                AdapterName = network.AdapterName ?? "-",
                HasIpv4 = true,
                HasIpv6 = false,
                Ipv4Works = true,
                Ipv6Works = false,
                DnsWorks = true,
                AddressKind = HotspotAddressKind.Private,
                VpnAdapterActive = false,
            });
    }
}

/// <summary>How the card describes the rewrite, in each of the states it can be in.</summary>
public sealed class VodafoneRewriteCardTests
{
    [Fact]
    public void AnInstalledRuleSaysWhatItIsDoingAndHowManyPacketsItHasTouched()
    {
        var view = HotspotStatusView.From(Status(ttlActive: true, rewritten: 12_345));

        Assert.True(view.RewriteActive);
        Assert.Equal("ok", view.RewriteSeverity);
        Assert.Contains("TTL 65", view.RewriteLine, StringComparison.Ordinal);
        Assert.Contains("12", view.RewriteLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleThatFailedIsNotDressedUpAsOneThatIsRunning()
    {
        var view = HotspotStatusView.From(Status(ttlActive: false, failure: "yönetici hakkı yok"));

        Assert.False(view.RewriteActive);
        Assert.Equal("attention", view.RewriteSeverity);
        Assert.Contains("yönetici hakkı yok", view.RewriteLine, StringComparison.Ordinal);
        Assert.Contains("yönetici hakkı yok", view.Suggestion!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnregisteredNetworkIsNotAFailure()
    {
        var view = HotspotStatusView.From(Status(ttlActive: false, registeredHere: false));

        Assert.Equal("info", view.RewriteSeverity);
        Assert.Contains("kayıtlı değil", view.RewriteLine, StringComparison.Ordinal);
    }

    /// <summary>The rewrite row is in the details whether or not a check has ever run.</summary>
    [Fact]
    public void TheRewriteRowIsAlwaysAvailable()
    {
        var view = HotspotStatusView.From(Status(ttlActive: true));
        var card = Assert.Single(view.TechnicalDetails, detail => detail.Title == "TTL düzeltmesi");

        Assert.Equal(HotspotCheckState.Ok, card.State);
        Assert.Contains("64", card.Detail!, StringComparison.Ordinal);
    }

    private static HotspotStatus Status(
        bool ttlActive,
        bool registeredHere = true,
        long rewritten = 0,
        string? failure = null) => new(
        VodafoneModeEnabled: true,
        DiagnosticsEnabled: true,
        RegisteredHere: registeredHere,
        RegisteredNetworks: 1,
        NetworkName: "atom",
        AdapterName: "Wi-Fi",
        LegacyCleanedAt: null,
        LastResult: null,
        TtlActive: ttlActive,
        TtlValue: TtlFixSettings.DefaultTimeToLive,
        RewrittenPackets: rewritten,
        TtlFailure: failure);
}
