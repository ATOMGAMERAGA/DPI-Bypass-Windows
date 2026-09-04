using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
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
    /// The card leads with "Aktif" once the mode is on and this is a saved network.
    /// </summary>
    /// <remarks>
    /// The word is the whole of what the user was looking for and could not find. It is
    /// deliberately not shown for an unregistered network: "on, but not here" is a
    /// different state and saying "active" would be a claim about a network the mode is
    /// doing nothing with.
    /// </remarks>
    [Fact]
    public void ARegisteredNetworkReadsAsActive()
    {
        var active = HotspotStatusView.From(Status(registeredHere: true));
        var elsewhere = HotspotStatusView.From(Status(registeredHere: false));

        Assert.StartsWith("Aktif · atom", active.Headline, StringComparison.Ordinal);
        Assert.Equal("ok", active.Severity);

        Assert.StartsWith("Açık · atom", elsewhere.Headline, StringComparison.Ordinal);
        Assert.Contains("kayıtlı değil", elsewhere.Headline, StringComparison.Ordinal);
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
        await using var service = new ProtectionService(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            hotspotDiagnostics: diagnostics);

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
    }

    /// <summary>A network nobody saved is still not one of the user's own.</summary>
    [Fact]
    public async Task ArrivingSomewhereElseDoesNotClaimTheModeIsActive()
    {
        using var directory = new TempDirectory();
        await using var service = new ProtectionService(
            new ConfigStore(directory.File("settings.json"), directory.File("networks.json")),
            new LearnedDomainStore(directory.File("learned.json")),
            hotspotDiagnostics: new RecordingHotspotDiagnostics());

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

    private static HotspotStatus Status(bool registeredHere, bool enabled = true) => new(
        VodafoneModeEnabled: enabled,
        DiagnosticsEnabled: true,
        RegisteredHere: registeredHere,
        RegisteredNetworks: 1,
        NetworkName: "atom",
        AdapterName: "Wi-Fi",
        LegacyCleanedAt: null,
        LastResult: null);
}
