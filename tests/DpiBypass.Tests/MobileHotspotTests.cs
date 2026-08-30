using DpiBypass.Core.Config;
using DpiBypass.Core.MobileHotspot;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Retiring only the hotspot TTL rewrite while preserving the Vodafone feature.
/// </summary>
/// <remarks>
/// The property being pinned is that no file can leave the retired packet rewrite on,
/// while reusable networks and unrelated preferences survive an idempotent migration.
/// </remarks>
public sealed class HotspotLegacyMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AConfigWithTheOldModeEnabledPreservesTheFeatureAndItsNetworks()
    {
        var settings = LegacySettings(enabled: true, networks: 3);

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.True(migration.Changed);
        Assert.True(migration.LegacyWasEnabled);
        Assert.Equal(3, migration.MigratedNetworks);

        Assert.False(settings.HotspotTtlFix);
        Assert.Empty(settings.HotspotTtlNetworks);
        Assert.True(settings.VodafoneModeEnabled);
        Assert.Equal(3, settings.VodafoneModeNetworks.Count);
        Assert.True(settings.HotspotDiagnostics);
        Assert.Equal(Now, settings.HotspotLegacyMigratedAt);
        Assert.Equal(Now, settings.VodafoneModeRestoredAt);
    }

    /// <summary>
    /// Somebody who had the mode switched off keeps their remembered networks without a
    /// feature they were not using being turned on for them.
    /// </summary>
    [Fact]
    public void RememberedNetworksAreMigratedWithoutEnablingAnythingNew()
    {
        var settings = LegacySettings(enabled: false, networks: 2);

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.True(migration.Changed);
        Assert.False(migration.LegacyWasEnabled);
        Assert.Empty(settings.HotspotTtlNetworks);
        Assert.Equal(2, settings.VodafoneModeNetworks.Count);
        Assert.False(settings.VodafoneModeEnabled);
        Assert.False(settings.HotspotDiagnostics);
    }

    [Fact]
    public void AFreshInstallIsLeftCompletelyAlone()
    {
        var settings = new AppSettings();

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.False(migration.Changed);
        Assert.Null(settings.HotspotLegacyMigratedAt);
        Assert.Null(settings.VodafoneModeRestoredAt);
        Assert.False(settings.VodafoneModeEnabled);
        Assert.False(settings.HotspotDiagnostics);
    }

    [Fact]
    public void RunningTheMigrationTwiceChangesNothingTheSecondTime()
    {
        var settings = LegacySettings(enabled: true, networks: 2);

        var first = HotspotLegacyMigration.Apply(settings, Now);
        var second = HotspotLegacyMigration.Apply(settings, Now + TimeSpan.FromDays(1));

        Assert.True(first.Changed);
        Assert.False(second.Changed);

        // The marker records when it was actually done, not the last time it was checked.
        Assert.Equal(Now, settings.HotspotLegacyMigratedAt);
        Assert.Equal(Now, settings.VodafoneModeRestoredAt);
        Assert.True(settings.VodafoneModeEnabled);
        Assert.Equal(2, settings.VodafoneModeNetworks.Count);
        Assert.True(settings.HotspotDiagnostics);
    }

    /// <summary>
    /// The exact reactivation the migration exists to prevent: a file that carries the
    /// marker and yet somehow has the switch back on.
    /// </summary>
    [Fact]
    public void AHandEditedFileCannotSwitchTheRetiredModeBackOn()
    {
        var settings = LegacySettings(enabled: true, networks: 0);
        settings.HotspotLegacyMigratedAt = Now - TimeSpan.FromDays(10);

        HotspotLegacyMigration.Apply(settings, Now);

        Assert.False(settings.HotspotTtlFix);
    }

    [Fact]
    public void CleanupIsNotGatedOnHavingBeenEnabledOnThisMachine()
    {
        var settings = new AppSettings { HotspotTtlFix = true };

        Assert.True(HotspotLegacyMigration.Apply(settings, Now).Changed);
        Assert.False(settings.HotspotTtlFix);
    }

    [Fact]
    public void APr11SettingsFileHasItsFeatureIdentityRestoredOnce()
    {
        var settings = new AppSettings
        {
            HotspotDiagnostics = true,
            HotspotLegacyMigratedAt = Now - TimeSpan.FromDays(1),
        };

        var first = HotspotLegacyMigration.Apply(settings, Now);
        settings.VodafoneModeEnabled = false;
        var second = HotspotLegacyMigration.Apply(settings, Now + TimeSpan.FromDays(1));

        Assert.True(first.Changed);
        Assert.True(first.VodafoneIdentityRestored);
        Assert.False(second.Changed);
        Assert.False(settings.VodafoneModeEnabled);
        Assert.Equal(Now, settings.VodafoneModeRestoredAt);
    }

    [Fact]
    public void ExistingSafeNetworkDetailsWinOverDuplicateLegacyData()
    {
        var settings = LegacySettings(enabled: true, networks: 1);
        settings.VodafoneModeNetworks.Add(new VodafoneModeNetwork
        {
            Key = "network-0",
            DisplayName = "current name",
            AdapterName = "current adapter",
        });

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.Equal(1, migration.MigratedNetworks);
        var network = Assert.Single(settings.VodafoneModeNetworks);
        Assert.Equal("current name", network.DisplayName);
        Assert.Equal("current adapter", network.AdapterName);
    }

    [Fact]
    public void MigrationDoesNotDiscardOlderValidNetworkRegistrations()
    {
        var settings = LegacySettings(enabled: true, networks: 12);

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.Equal(12, migration.MigratedNetworks);
        Assert.Equal(12, settings.VodafoneModeNetworks.Count);
        Assert.Equal("network-0", settings.VodafoneModeNetworks[0].Key);
        Assert.Equal("network-11", settings.VodafoneModeNetworks[^1].Key);
    }

    private static AppSettings LegacySettings(bool enabled, int networks)
    {
        var settings = new AppSettings { HotspotTtlFix = enabled };

        for (var index = 0; index < networks; index++)
        {
            settings.HotspotTtlNetworks.Add(new LegacyHotspotNetwork
            {
                Key = $"network-{index}",
                DisplayName = $"hotspot {index}",
                AdapterName = "Wi-Fi",
            });
        }

        return settings;
    }
}

/// <summary>The migration reaching every code path that loads settings.</summary>
public sealed class HotspotConfigMigrationTests
{
    [Fact]
    public void ASettingsFileFromAnOlderBuildLoadsWithTheRetiredModeOff()
    {
        using var directory = new TempDirectory();
        var settingsPath = directory.File("settings.json");

        File.WriteAllText(settingsPath, """
            {
              "HotspotTtlFix": true,
              "HotspotTtlValue": 65,
              "HotspotDropIPv6": true,
              "HotspotTtlNetworks": [
                { "Key": "abc123", "DisplayName": "atom hotspot", "AdapterName": "Wi-Fi" }
              ]
            }
            """);

        var settings = new ConfigStore(settingsPath, directory.File("networks.json")).Load();

        Assert.False(settings.HotspotTtlFix);
        Assert.Empty(settings.HotspotTtlNetworks);
        Assert.Null(settings.HotspotTtlValue);
        Assert.Null(settings.HotspotDropIPv6);
        var preserved = Assert.Single(settings.VodafoneModeNetworks);
        Assert.Equal("abc123", preserved.Key);
        Assert.Equal("atom hotspot", preserved.DisplayName);
        Assert.True(settings.VodafoneModeEnabled);
        Assert.True(settings.HotspotDiagnostics);
        Assert.NotNull(settings.HotspotLegacyMigratedAt);
    }

    /// <summary>
    /// Unknown fields from the retired mode are simply dropped by the serializer, and a
    /// file that no longer parses at all must not resurrect anything either.
    /// </summary>
    [Fact]
    public void ARetiredFieldLeftInTheFileDoesNotSurviveASaveAndReload()
    {
        using var directory = new TempDirectory();
        var store = new ConfigStore(directory.File("settings.json"), directory.File("networks.json"));

        File.WriteAllText(directory.File("settings.json"), """{"HotspotTtlFix": true}""");

        var settings = store.Load();
        store.Save(settings);

        var reloaded = store.Load();

        Assert.False(reloaded.HotspotTtlFix);
        Assert.DoesNotContain("\"HotspotTtlFix\": true", File.ReadAllText(directory.File("settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyPacketOptionsAloneAreRemovedWithoutChangingOtherPreferences()
    {
        using var directory = new TempDirectory();
        var settingsPath = directory.File("settings.json");
        File.WriteAllText(settingsPath, """
            {
              "HotspotTtlValue": 65,
              "HotspotDropIPv6": true,
              "StartEngineOnLaunch": false,
              "ManualIspProfileId": "vodafone-mobile"
            }
            """);

        var store = new ConfigStore(settingsPath, directory.File("networks.json"));
        var settings = store.Load();
        Assert.True(settings.LegacyHotspotCleaned);
        store.Save(settings);

        var onDisk = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("HotspotTtlValue", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("HotspotDropIPv6", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"StartEngineOnLaunch\": false", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"ManualIspProfileId\": \"vodafone-mobile\"", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullNetworkListDoesNotThrowOnLoad()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("settings.json"), """{"HotspotTtlNetworks": null}""");

        var settings = new ConfigStore(directory.File("settings.json"), directory.File("networks.json")).Load();

        Assert.Empty(settings.HotspotTtlNetworks);
    }

    [Fact]
    public void MigrationPreservesUnrelatedPreferences()
    {
        using var directory = new TempDirectory();
        var settingsPath = directory.File("settings.json");
        File.WriteAllText(settingsPath, """
            {
              "HotspotTtlFix": true,
              "HotspotTtlNetworks": [
                { "Key": "vodafone", "DisplayName": "phone", "AdapterName": "Wi-Fi" }
              ],
              "Scope": "Everything",
              "DnsMode": "SystemDefault",
              "StartEngineOnLaunch": false,
              "StartMinimised": false,
              "ExtraDomains": ["example.test"]
            }
            """);

        var store = new ConfigStore(settingsPath, directory.File("networks.json"));
        var settings = store.Load();
        store.Save(settings);
        var reloaded = store.Load();

        Assert.Equal(Core.Engine.ProtectionScope.Everything, reloaded.Scope);
        Assert.Equal(Core.Dns.DnsMode.SystemDefault, reloaded.DnsMode);
        Assert.False(reloaded.StartEngineOnLaunch);
        Assert.False(reloaded.StartMinimised);
        Assert.Equal(["example.test"], reloaded.ExtraDomains);
        Assert.Single(reloaded.VodafoneModeNetworks);
    }

    [Fact]
    public void AFreshInstallDoesNotGetTheDiagnosticsSwitchedOnForIt()
    {
        using var directory = new TempDirectory();

        var settings = new ConfigStore(directory.File("settings.json"), directory.File("networks.json")).Load();

        Assert.False(settings.HotspotDiagnostics);
        Assert.Null(settings.HotspotLegacyMigratedAt);
    }
}

/// <summary>The diagnostics result: what it says, and what it refuses to say.</summary>
public sealed class HotspotDiagnosticResultTests
{
    /// <summary>
    /// The one thing the app must never guess at. TTL, SSID, carrier name, APN and
    /// address range are all things an operator sets for its own reasons, and none of
    /// them establishes what somebody's subscription includes.
    /// </summary>
    [Fact]
    public void PlanEntitlementIsAlwaysReportedAsUnknown()
    {
        Assert.Equal("Bilinmiyor", HotspotDiagnosticResult.PlanEntitlement);
        Assert.Contains("Plan / hotspot hakkı: Bilinmiyor", Result().ToReport(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCarrierIsPrintedAsUnknownRatherThanGuessed()
        => Assert.Contains("Operatör      : Bilinmiyor", Result().ToReport(), StringComparison.Ordinal);

    [Fact]
    public void WorkingIpv4WithoutIpv6CountsAsHavingInternet()
    {
        var result = Result() with { Ipv4Works = true, HasIpv6 = false, Ipv6Works = false };

        Assert.True(result.HasInternet);
    }

    [Fact]
    public void AnAddressWithoutTrafficIsReportedAsSuch()
    {
        var report = (Result() with { HasIpv4 = true, Ipv4Works = false }).ToReport();

        Assert.Contains("IPv4          : adres var ama trafik geçmiyor", report, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAddressAtAllIsDistinguishedFromNoTraffic()
    {
        var report = (Result() with { HasIpv6 = false, Ipv6Works = false }).ToReport();

        Assert.Contains("IPv6          : adres yok", report, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedAddressSpaceIsReportedWithoutClaimingUpstreamCarrierNat()
    {
        var report = (Result() with { AddressKind = HotspotAddressKind.SharedAddressSpace }).ToReport();

        Assert.Contains("100.64/10", report, StringComparison.Ordinal);
        Assert.DoesNotContain("operatör NAT'ı", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AReportWithoutMeasurementsOmitsThemRatherThanPrintingZeros()
    {
        var report = (Result() with { MedianRttMs = null, P95RttMs = null, PacketLossPercent = null }).ToReport();

        Assert.DoesNotContain("Gecikme", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnMtuBelowFifteenHundredIsSpelledOutInBytes()
    {
        var report = (Result() with { LargestUnfragmentedPayload = 1432, MtuLooksReduced = true }).ToReport();

        Assert.Contains("üst sınır 1460 bayt", report, StringComparison.Ordinal);
    }

    private static HotspotDiagnosticResult Result() => new()
    {
        NetworkName = "atom hotspot",
        AdapterName = "Wi-Fi",
        HasIpv4 = true,
        HasIpv6 = true,
        Ipv4Works = true,
        Ipv6Works = true,
        DnsWorks = true,
        MedianRttMs = 42,
        P95RttMs = 61,
        PacketLossPercent = 0,
        AddressKind = HotspotAddressKind.Private,
        VpnAdapterActive = false,
    };
}

/// <summary>
/// What the readings are turned into. No I/O here: the gathering is separate from the
/// judging, and this is the judging.
/// </summary>
public sealed class HotspotFindingsTests
{
    [Theory]
    [InlineData("100.64.0.1", HotspotAddressKind.SharedAddressSpace)]
    [InlineData("100.127.255.254", HotspotAddressKind.SharedAddressSpace)]
    [InlineData("100.63.255.255", HotspotAddressKind.Public)]
    [InlineData("100.128.0.1", HotspotAddressKind.Public)]
    [InlineData("192.168.1.42", HotspotAddressKind.Private)]
    [InlineData("10.0.0.7", HotspotAddressKind.Private)]
    [InlineData("172.16.3.9", HotspotAddressKind.Private)]
    [InlineData("172.32.3.9", HotspotAddressKind.Public)]
    [InlineData("169.254.10.1", HotspotAddressKind.Private)]
    [InlineData("93.184.216.34", HotspotAddressKind.Public)]
    public void AddressesAreClassifiedByRangeAlone(string address, HotspotAddressKind expected)
        => Assert.Equal(expected, MobileHotspotDiagnostics.Classify(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void MultipleIpv4AddressClassesAreSummarizedDeterministically()
    {
        System.Net.IPAddress[] addresses =
        [
            System.Net.IPAddress.Parse("192.168.1.20"),
            System.Net.IPAddress.Parse("100.64.2.5"),
        ];

        Assert.Equal(HotspotAddressKind.Mixed, MobileHotspotDiagnostics.SummarizeAddressKinds(addresses));
        Assert.Equal(HotspotAddressKind.Mixed, MobileHotspotDiagnostics.SummarizeAddressKinds(addresses.Reverse()));
        Assert.Equal(
            HotspotAddressKind.Private,
            MobileHotspotDiagnostics.SummarizeAddressKinds(
            [
                System.Net.IPAddress.Parse("192.168.1.20"),
                System.Net.IPAddress.Parse("10.0.0.4"),
            ]));
    }

    [Fact]
    public void SharedAddressSpaceFindingDoesNotClaimItProvesCarrierCgnat()
    {
        var findings = MobileHotspotDiagnostics.Findings(
            Working() with { AddressKind = HotspotAddressKind.SharedAddressSpace });

        Assert.Contains(findings, finding => finding.Contains("tek başına kanıtlamaz", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, finding => finding.Contains("arkasındasınız", StringComparison.Ordinal));
    }

    [Fact]
    public void NoConnectivityAtAllLeadsWithThatAndSuggestsRestartingTheShare()
    {
        var result = Working() with { Ipv4Works = false, Ipv6Works = false };

        Assert.Contains(
            MobileHotspotDiagnostics.Findings(result),
            finding => finding.Contains("internet erişimi yok", StringComparison.Ordinal));
        Assert.Contains("paylaşımı kapatıp açın", MobileHotspotDiagnostics.Remediation(result)!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Most mobile shares hand out no IPv6 at all, and saying so without saying it is
    /// fine would turn a normal connection into a scary report.
    /// </summary>
    [Fact]
    public void MissingIpv6IsReportedAsNormalRatherThanAsAFault()
    {
        var findings = MobileHotspotDiagnostics.Findings(Working() with { HasIpv6 = false, Ipv6Works = false });

        Assert.Contains(findings, finding => finding.Contains("olağandır", StringComparison.Ordinal));
        Assert.Null(MobileHotspotDiagnostics.Remediation(Working() with { HasIpv6 = false, Ipv6Works = false }));
    }

    [Fact]
    public void AnIpv6AddressWithoutIpv6TrafficIsCalledOut()
    {
        var findings = MobileHotspotDiagnostics.Findings(Working() with { HasIpv6 = true, Ipv6Works = false });

        Assert.Contains(findings, finding => finding.Contains("IPv6 trafiği geçmiyor", StringComparison.Ordinal));
    }

    [Fact]
    public void ADnsFailureIsSeparatedFromAConnectivityFailure()
    {
        var result = Working() with { DnsWorks = false };

        Assert.Contains(
            MobileHotspotDiagnostics.Findings(result),
            finding => finding.Contains("Ad çözümleme", StringComparison.Ordinal));
        Assert.Contains("DNS", MobileHotspotDiagnostics.Remediation(result)!, StringComparison.Ordinal);
    }

    [Fact]
    public void AReducedMtuExplainsTheSymptomWithoutBlindlyPrescribingFourteenHundred()
    {
        var result = Working() with { MtuLooksReduced = true, LargestUnfragmentedPayload = 1432 };

        Assert.Contains(
            MobileHotspotDiagnostics.Findings(result),
            finding => finding.Contains("yarım yüklenirse", StringComparison.Ordinal));
        Assert.Contains("MTU", MobileHotspotDiagnostics.Remediation(result)!, StringComparison.Ordinal);
        Assert.DoesNotContain("1400", MobileHotspotDiagnostics.Remediation(result)!, StringComparison.Ordinal);
    }

    /// <summary>An unmeasurable MTU says nothing; operators drop large ICMP all the time.</summary>
    [Fact]
    public void AnUnmeasurableMtuProducesNoFinding()
    {
        var findings = MobileHotspotDiagnostics.Findings(Working() with { MtuLooksReduced = null });

        Assert.DoesNotContain(findings, finding => finding.Contains("1500", StringComparison.Ordinal));
    }

    [Fact]
    public void AnActiveVpnIsFlaggedBecauseItChangesWhatWasMeasured()
        => Assert.Contains(
            MobileHotspotDiagnostics.Findings(Working() with { VpnAdapterActive = true }),
            finding => finding.Contains("VPN", StringComparison.Ordinal));

    [Fact]
    public void AHealthyConnectionNeedsNoRemediation()
        => Assert.Null(MobileHotspotDiagnostics.Remediation(Working()));

    [Fact]
    public void LossAboveTwoPercentIsWorthMentioning()
    {
        Assert.Contains(
            MobileHotspotDiagnostics.Findings(Working() with { PacketLossPercent = 6 }),
            finding => finding.Contains("Paket kaybı", StringComparison.Ordinal));

        Assert.DoesNotContain(
            MobileHotspotDiagnostics.Findings(Working() with { PacketLossPercent = 1 }),
            finding => finding.Contains("Paket kaybı", StringComparison.Ordinal));
    }

    private static HotspotDiagnosticResult Working() => new()
    {
        NetworkName = "atom hotspot",
        AdapterName = "Wi-Fi",
        HasIpv4 = true,
        HasIpv6 = true,
        Ipv4Works = true,
        Ipv6Works = true,
        DnsWorks = true,
        MedianRttMs = 42,
        P95RttMs = 61,
        PacketLossPercent = 0,
        AddressKind = HotspotAddressKind.Private,
        VpnAdapterActive = false,
    };
}

public sealed class HotspotMtuProbeTests
{
    [Fact]
    public async Task AFullSizeSuccessReportsEthernetMtuWithoutSearching()
    {
        var calls = new List<int>();

        var result = await MobileHotspotDiagnostics.FindLargestUnfragmentedPayloadAsync((payload, _) =>
        {
            calls.Add(payload);
            return Task.FromResult<bool?>(true);
        });

        Assert.Equal(1472, result.Largest);
        Assert.False(result.Reduced);
        Assert.Equal([1472], calls);
    }

    [Fact]
    public async Task BinarySearchFindsTheActualLargestSuccessfulPayload()
    {
        const int largestWorkingPayload = 1432;

        var result = await MobileHotspotDiagnostics.FindLargestUnfragmentedPayloadAsync(
            (payload, _) => Task.FromResult<bool?>(payload <= largestWorkingPayload));

        Assert.Equal(largestWorkingPayload, result.Largest);
        Assert.True(result.Reduced);
    }

    [Fact]
    public async Task FilteredOrContradictoryIcmpIsInconclusive()
    {
        var fullFiltered = await MobileHotspotDiagnostics.FindLargestUnfragmentedPayloadAsync(
            (_, _) => Task.FromResult<bool?>(null));
        var midProbe = 0;
        var filteredDuringSearch = await MobileHotspotDiagnostics.FindLargestUnfragmentedPayloadAsync((payload, _) =>
        {
            if (payload == 1472)
            {
                return Task.FromResult<bool?>(false);
            }

            if (payload == 1200)
            {
                return Task.FromResult<bool?>(true);
            }

            midProbe = payload;
            return Task.FromResult<bool?>(null);
        });

        Assert.Null(fullFiltered.Largest);
        Assert.Null(fullFiltered.Reduced);
        Assert.True(midProbe > 1200);
        Assert.Null(filteredDuringSearch.Largest);
        Assert.Null(filteredDuringSearch.Reduced);
    }
}

public sealed class HotspotVpnDetectionTests
{
    [Theory]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Tunnel, "Ethernet", "ordinary", false)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ppp, "WAN Miniport", "PPP", false)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "WireGuard Tunnel", "Wintun Userspace Tunnel", true)]
    [InlineData(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "Local Area Connection", "TAP-Windows Adapter V9", true)]
    public void ActiveTunnelTypesAndHighConfidenceAdapterNamesAreRecognized(
        System.Net.NetworkInformation.NetworkInterfaceType type,
        string name,
        string description,
        bool hasAddress)
        => Assert.True(MobileHotspotDiagnostics.LooksLikeActiveTunnel(
            System.Net.NetworkInformation.OperationalStatus.Up,
            type,
            name,
            description,
            hasAddress));

    [Fact]
    public void AGenericActiveVirtualEthernetAdapterIsNotAutomaticallyCalledAVpn()
        => Assert.False(MobileHotspotDiagnostics.LooksLikeActiveTunnel(
            System.Net.NetworkInformation.OperationalStatus.Up,
            System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            "Hyper-V Virtual Ethernet Adapter",
            "Microsoft Virtual Ethernet Adapter",
            hasUsableAddress: true));

    [Fact]
    public void ARecognizableButInactiveOrUnaddressedAdapterIsNotCalledActive()
    {
        Assert.False(MobileHotspotDiagnostics.LooksLikeActiveTunnel(
            System.Net.NetworkInformation.OperationalStatus.Down,
            System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            "WireGuard",
            "Wintun",
            hasUsableAddress: true));
        Assert.False(MobileHotspotDiagnostics.LooksLikeActiveTunnel(
            System.Net.NetworkInformation.OperationalStatus.Up,
            System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
            "WireGuard",
            "Wintun",
            hasUsableAddress: false));
    }
}

/// <summary>The restored name and both CLI spellings remain wired to safe commands.</summary>
public sealed class VodafoneCompatibilitySurfaceTests
{
    [Fact]
    public void VodafoneCliVerbStillSupportsStatusOnOffDiagnoseAndCleanup()
    {
        var source = File.ReadAllText(RepoFiles.Find("src", "DpiBypass.App", "CommandLineTasks.cs"));

        Assert.Contains("case \"vodafone\"", source, StringComparison.Ordinal);
        Assert.Contains("ControlProtocol.Commands.VodafoneOn", source, StringComparison.Ordinal);
        Assert.Contains("ControlProtocol.Commands.VodafoneOff", source, StringComparison.Ordinal);
        Assert.Contains("ControlProtocol.Commands.VodafoneStatus", source, StringComparison.Ordinal);
        Assert.Contains("ControlProtocol.Commands.HotspotDiagnose", source, StringComparison.Ordinal);
        Assert.Contains("ControlProtocol.Commands.HotspotCleanup", source, StringComparison.Ordinal);
        Assert.Contains("DpiBypass.exe vodafone [on|off]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeVodafoneModeDoesNotRestoreTheDeletedPacketRewriteClasses()
    {
        var root = new DirectoryInfo(RepoFiles.Find("src", "DpiBypass.Core", "ProtectionService.cs")).Parent!;
        var oldNamespace = Path.Combine(root.FullName, "Vodafone");
        var service = File.ReadAllText(Path.Combine(root.FullName, "ProtectionService.cs"));

        Assert.False(File.Exists(Path.Combine(oldNamespace, "HotspotTtlFix.cs")));
        Assert.False(File.Exists(Path.Combine(oldNamespace, "TtlFixSettings.cs")));
        Assert.DoesNotContain("ApplyTtlFix", service, StringComparison.Ordinal);
        Assert.DoesNotContain("HotspotTtlFix _", service, StringComparison.Ordinal);
        Assert.DoesNotContain("HotspotTtlValue", service, StringComparison.Ordinal);
        Assert.DoesNotContain("HotspotDropIPv6", service, StringComparison.Ordinal);
    }
}
