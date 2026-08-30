using DpiBypass.Core.Config;
using DpiBypass.Core.MobileHotspot;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Retiring the hotspot TTL rewrite from a settings file.
/// </summary>
/// <remarks>
/// The property being pinned is that no file - old, hand-edited, restored from a backup -
/// can leave the retired mode switched on, and that running the cleanup again never does
/// anything a second time.
/// </remarks>
public sealed class HotspotLegacyMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AConfigWithTheOldModeEnabledIsCleanedAndTheReplacementSwitchedOn()
    {
        var settings = LegacySettings(enabled: true, networks: 3);

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.True(migration.Changed);
        Assert.True(migration.LegacyWasEnabled);
        Assert.Equal(3, migration.ClearedNetworks);

        Assert.False(settings.HotspotTtlFix);
        Assert.Empty(settings.HotspotTtlNetworks);
        Assert.True(settings.HotspotDiagnostics);
        Assert.Equal(Now, settings.HotspotLegacyMigratedAt);
    }

    /// <summary>
    /// Somebody who had the mode switched off but still had networks remembered gets the
    /// list cleared without a feature they never used being turned on for them.
    /// </summary>
    [Fact]
    public void RememberedNetworksAreClearedWithoutEnablingAnythingNew()
    {
        var settings = LegacySettings(enabled: false, networks: 2);

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.True(migration.Changed);
        Assert.False(migration.LegacyWasEnabled);
        Assert.Empty(settings.HotspotTtlNetworks);
        Assert.False(settings.HotspotDiagnostics);
    }

    [Fact]
    public void AFreshInstallIsLeftCompletelyAlone()
    {
        var settings = new AppSettings();

        var migration = HotspotLegacyMigration.Apply(settings, Now);

        Assert.False(migration.Changed);
        Assert.Null(settings.HotspotLegacyMigratedAt);
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
    public void ANullNetworkListDoesNotThrowOnLoad()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(directory.File("settings.json"), """{"HotspotTtlNetworks": null}""");

        var settings = new ConfigStore(directory.File("settings.json"), directory.File("networks.json")).Load();

        Assert.Empty(settings.HotspotTtlNetworks);
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
    public void CarrierGradeNatIsReportedAsInformationRatherThanAFault()
    {
        var report = (Result() with { AddressKind = HotspotAddressKind.CarrierGradeNat }).ToReport();

        Assert.Contains("100.64/10", report, StringComparison.Ordinal);
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
        var report = (Result() with { LargestUnfragmentedPayload = 1372, MtuLooksReduced = true }).ToReport();

        Assert.Contains("1400 bayta kadar", report, StringComparison.Ordinal);
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
    [InlineData("100.64.0.1", HotspotAddressKind.CarrierGradeNat)]
    [InlineData("100.127.255.254", HotspotAddressKind.CarrierGradeNat)]
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
    public void AReducedMtuExplainsTheSymptomAndSuggestsALowerValue()
    {
        var result = Working() with { MtuLooksReduced = true, LargestUnfragmentedPayload = 1372 };

        Assert.Contains(
            MobileHotspotDiagnostics.Findings(result),
            finding => finding.Contains("yarım yüklenirse", StringComparison.Ordinal));
        Assert.Contains("MTU", MobileHotspotDiagnostics.Remediation(result)!, StringComparison.Ordinal);
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
