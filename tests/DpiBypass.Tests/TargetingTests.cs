using System.Net.NetworkInformation;
using DpiBypass.Core.Config;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

public class TargetMatcherTests
{
    private const string DiscordExe = @"C:\Users\atom\AppData\Local\Discord\app-1.0.9042\Discord.exe";
    private const string ChromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string GameExe = @"D:\Games\Valorant\live\VALORANT.exe";

    [Theory]
    [InlineData("discord.com", true)]
    [InlineData("DISCORD.COM", true)]
    [InlineData("gateway.discord.gg", true)]
    [InlineData("cdn.discordapp.com", true)]
    [InlineData("latency.discord.media", true)]
    [InlineData("dis.gd", true)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void RecognisesDiscordHostnames(string host, bool expected)
        => Assert.Equal(expected, TargetMatcher.IsDiscordDomain(host));

    [Theory]
    [InlineData("notdiscord.com")]
    [InlineData("discord.com.evil.net")]
    [InlineData("mydiscord.com")]
    [InlineData("xdiscord.gg")]
    public void DoesNotFallForLookalikeHostnames(string host)
    {
        // Substring matching here would hand a bypass to any domain containing the
        // word, so matching is exact-or-proper-subdomain only.
        Assert.False(TargetMatcher.IsDiscordDomain(host));
    }

    [Fact]
    public void DiscordOnlyScopeCoversDiscordTrafficAndNothingElse()
    {
        var matcher = new TargetMatcher { Scope = ProtectionScope.DiscordOnly };

        Assert.True(matcher.ShouldProtect("discord.com", null));
        Assert.True(matcher.ShouldProtect(null, DiscordExe));
        Assert.False(matcher.ShouldProtect("youtube.com", ChromeExe));
        Assert.False(matcher.ShouldProtect(null, GameExe));
    }

    [Fact]
    public void BrowserScopeAddsBrowsersButStillSkipsOtherApps()
    {
        var matcher = new TargetMatcher { Scope = ProtectionScope.DiscordAndBrowsers };

        Assert.True(matcher.ShouldProtect("youtube.com", ChromeExe));
        Assert.True(matcher.ShouldProtect("discord.com", null));
        Assert.True(matcher.ShouldProtect(null, DiscordExe));
        Assert.False(matcher.ShouldProtect("riotgames.com", GameExe));
    }

    [Fact]
    public void SystemWideScopeCoversEverything()
    {
        var matcher = new TargetMatcher { Scope = ProtectionScope.Everything };

        Assert.True(matcher.ShouldProtect("riotgames.com", GameExe));
        Assert.True(matcher.ShouldProtect(null, null));
    }

    [Fact]
    public void ExclusionsWinEvenInSystemWideScope()
    {
        var matcher = new TargetMatcher { Scope = ProtectionScope.Everything };
        matcher.ExcludedDomains.Add("bank.com.tr");

        Assert.False(matcher.ShouldProtect("bank.com.tr", null));
        Assert.False(matcher.ShouldProtect("secure.bank.com.tr", null));
        Assert.True(matcher.ShouldProtect("other.com", null));
    }

    [Fact]
    public void ExtraDomainsAreCoveredInDiscordOnlyScope()
    {
        var matcher = new TargetMatcher { Scope = ProtectionScope.DiscordOnly };
        matcher.ExtraDomains.Add("wikipedia.org");

        Assert.True(matcher.ShouldProtect("tr.wikipedia.org", null));
        Assert.False(matcher.ShouldProtect("example.org", null));
    }

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Discord\app-1.0.9042\Discord.exe", true)]
    [InlineData(@"C:\Users\a\AppData\Local\DiscordCanary\app-1.0.1\DiscordCanary.exe", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Discord\Update.exe", true)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData(null, false)]
    public void IdentifiesDiscordExecutables(string? path, bool expected)
        => Assert.Equal(expected, TargetMatcher.IsDiscord(path));

    [Theory]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Yandex\YandexBrowser\Application\browser.exe", true)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    public void IdentifiesBrowsers(string path, bool expected) => Assert.Equal(expected, TargetMatcher.IsBrowser(path));

    [Fact]
    public void BrowserAndDiscordListsHaveNoBlankEntries()
    {
        Assert.All(TargetMatcher.BrowserExecutables, name => Assert.EndsWith(".exe", name));
        Assert.All(TargetMatcher.DiscordExecutables, name => Assert.EndsWith(".exe", name));
        Assert.All(TargetMatcher.DiscordDomains, domain => Assert.Contains('.', domain));
    }
}

public class IspCatalogTests
{
    [Fact]
    public void CatalogueMatchesTheOperatorListShownInTheUi()
    {
        var expected = new[]
        {
            "Türk Telekom (Mobil)",
            "Türk Telekom Evde İnternet",
            "Türk Telekom Hotspot",
            "Redbox (Türk Telekom)",
            "Turkcell (Mobil)",
            "Turkcell Superonline",
            "Superbox (Turkcell FWA)",
            "Turkcell Hotspot",
            "Vodafone (Mobil)",
            "Vodafone Evde İnternet",
            "Vodafone Hotspot",
            "TurkNet",
            "Diğer / Bilinmiyor",
        };

        Assert.Equal(expected, IspCatalog.All.Select(p => p.DisplayName).ToArray());
    }

    [Fact]
    public void ProfileIdsAreUniqueAndResolvable()
    {
        var ids = IspCatalog.All.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var profile in IspCatalog.All)
        {
            Assert.Same(profile, IspCatalog.ById(profile.Id));
        }
    }

    [Fact]
    public void UnknownIdFallsBackToTheGenericProfile()
        => Assert.Same(IspCatalog.Unknown, IspCatalog.ById("not-a-real-isp"));

    [Fact]
    public void EveryPreferredStrategyExistsInTheLibrary()
    {
        foreach (var profile in IspCatalog.All)
        {
            Assert.NotEmpty(profile.PreferredStrategies);

            foreach (var id in profile.PreferredStrategies)
            {
                Assert.NotNull(StrategyLibrary.Find(id));
            }
        }
    }

    [Fact]
    public void NoProfilePutsThePassthroughControlArmInItsCandidateList()
    {
        foreach (var profile in IspCatalog.All)
        {
            Assert.DoesNotContain(StrategyLibrary.Passthrough.Id, profile.PreferredStrategies);
        }
    }

    [Theory]
    [InlineData("host-88-240-1-1.ttnet.com.tr", "tt-home")]
    [InlineData("client.superonline.net", "superonline")]
    [InlineData("dsl.vodafone.net.tr", "vodafone-home")]
    [InlineData("static.turk.net", "turknet")]
    public void ReverseDnsIdentifiesTheOperator(string reverseDns, string expectedId)
    {
        var profile = IspCatalog.Match(reverseDns, asn: null, ssid: null, AccessKind.Fixed);
        Assert.Equal(expectedId, profile.Id);
    }

    [Fact]
    public void MoreSpecificReverseDnsWinsForMobile()
    {
        var profile = IspCatalog.Match("10-1-1-1.mobil.ttnet.com.tr", asn: null, ssid: null, AccessKind.Mobile);
        Assert.Equal("tt-mobile", profile.Id);
    }

    [Theory]
    [InlineData("Superbox_A1B2", "superbox")]
    [InlineData("TurkNet-5GHz", "turknet")]
    [InlineData("Redbox_Evim", "tt-redbox")]
    public void SsidAloneIsEnoughForTheFixedWirelessProducts(string ssid, string expectedId)
    {
        var profile = IspCatalog.Match(reverseDns: null, asn: null, ssid, AccessKind.FixedWireless);
        Assert.Equal(expectedId, profile.Id);
    }

    [Fact]
    public void AsnIsUsedWhenNothingElseIsAvailable()
    {
        var profile = IspCatalog.Match(reverseDns: null, asn: 12735, ssid: null, AccessKind.Fixed);
        Assert.Equal("turknet", profile.Id);
    }

    [Fact]
    public void NoSignalsAtAllYieldsTheUnknownProfile()
    {
        var profile = IspCatalog.Match(reverseDns: null, asn: null, ssid: null, AccessKind.Unknown);
        Assert.Same(IspCatalog.Unknown, profile);
    }

    [Fact]
    public void UnknownProfileStillOffersEveryActiveStrategy()
    {
        var expected = StrategyLibrary.All.Count(s => !s.IsPassthrough);
        Assert.Equal(expected, IspCatalog.Unknown.PreferredStrategies.Length);
    }
}

public class NetworkFingerprintTests
{
    [Fact]
    public void RenamingTheNetworkProducesADifferentKey()
    {
        var before = new NetworkFingerprint { Ssid = "atom", GatewayMac = "aa:bb:cc:dd:ee:ff" };
        var after = before with { Ssid = "atoms hotspot" };

        // This is the scenario the app is expected to handle: the user moves to a
        // differently named network and the cached recipe must not be reused.
        Assert.NotEqual(before.Key, after.Key);
    }

    [Fact]
    public void TheSameNetworkProducesAStableKey()
    {
        var a = new NetworkFingerprint { Ssid = "atom", GatewayMac = "aa:bb:cc:dd:ee:ff", AdapterType = NetworkInterfaceType.Wireless80211 };
        var b = new NetworkFingerprint { Ssid = "atom", GatewayMac = "aa:bb:cc:dd:ee:ff", AdapterType = NetworkInterfaceType.Wireless80211 };

        Assert.Equal(a.Key, b.Key);
        Assert.Equal(16, a.Key.Length);
    }

    [Fact]
    public void SameSsidOnADifferentRouterIsTreatedAsADifferentNetwork()
    {
        var home = new NetworkFingerprint { Ssid = "TP-LINK", GatewayMac = "11:22:33:44:55:66" };
        var elsewhere = new NetworkFingerprint { Ssid = "TP-LINK", GatewayMac = "aa:bb:cc:dd:ee:ff" };

        Assert.NotEqual(home.Key, elsewhere.Key);
    }

    [Fact]
    public void DisplayNamePrefersTheSsidThenTheAdapter()
    {
        Assert.Equal("atom", new NetworkFingerprint { Ssid = "atom" }.DisplayName);
        Assert.Equal("Ethernet", new NetworkFingerprint { AdapterName = "Ethernet" }.DisplayName);
        Assert.Equal("Mobil veri", new NetworkFingerprint { AdapterType = NetworkInterfaceType.Wwanpp }.DisplayName);
        Assert.Equal("Bilinmeyen ağ", new NetworkFingerprint().DisplayName);
    }

    [Fact]
    public void MobileBroadbandAdaptersAreClassifiedAsMobile()
    {
        Assert.Equal(AccessKind.Mobile, new NetworkFingerprint { AdapterType = NetworkInterfaceType.Wwanpp }.GuessAccessKind());
        Assert.Equal(AccessKind.Fixed, new NetworkFingerprint { AdapterType = NetworkInterfaceType.Ethernet }.GuessAccessKind());
    }

    [Fact]
    public void OperatorHotspotSsidsAreClassifiedAsHotspots()
    {
        var fingerprint = new NetworkFingerprint
        {
            Ssid = "TT Wi-Fi",
            AdapterType = NetworkInterfaceType.Wireless80211,
        };

        Assert.Equal(AccessKind.Hotspot, fingerprint.GuessAccessKind());
    }
}

public class ConfigStoreTests
{
    [Fact]
    public void SettingsAndNetworkProfilesRoundTripThroughDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dpibypass-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ConfigStore(
                Path.Combine(directory, "settings.json"),
                Path.Combine(directory, "networks.json"));

            var settings = new AppSettings
            {
                Scope = ProtectionScope.Everything,
                BlockQuicHandshakes = false,
                ManualStrategyId = "split-sni",
                ExtraDomains = ["example.com"],
            };
            settings.Networks["abc123"] = new NetworkProfile
            {
                Key = "abc123",
                DisplayName = "atom",
                StrategyId = "fake-ttl-split-sni",
                IspProfileId = "tt-home",
                SuccessCount = 3,
            };

            store.Save(settings);
            var loaded = store.Load();

            Assert.Equal(ProtectionScope.Everything, loaded.Scope);
            Assert.False(loaded.BlockQuicHandshakes);
            Assert.Equal("split-sni", loaded.ManualStrategyId);
            Assert.Equal(["example.com"], loaded.ExtraDomains);
            Assert.Single(loaded.Networks);
            Assert.Equal("fake-ttl-split-sni", loaded.Networks["abc123"].StrategyId);
            Assert.Equal(3, loaded.Networks["abc123"].SuccessCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DefaultsAreUsableWhenNothingHasBeenSavedYet()
    {
        var store = new ConfigStore(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"),
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        var settings = store.Load();

        Assert.Equal(ProtectionScope.DiscordAndBrowsers, settings.Scope);
        Assert.True(settings.StartWithWindows);
        Assert.Equal(DpiBypass.Core.Dns.DnsMode.EncryptedLoopback, settings.DnsMode);
        Assert.Empty(settings.Networks);
    }

    [Fact]
    public void ACorruptSettingsFileFallsBackToDefaultsInsteadOfThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dpibypass-bad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");

        try
        {
            var settings = new ConfigStore(path, path).Load();
            Assert.Equal(ProtectionScope.DiscordAndBrowsers, settings.Scope);

            // The unreadable file is kept rather than left to be overwritten by the
            // next save, so a user who hand-edited it can still get their settings back.
            Assert.True(File.Exists(path + ".bad"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bad");
        }
    }
}

public class ProtectionServiceDescriptionTests
{
    [Fact]
    public void ScopeDescriptionsAreFilledInForEveryValue()
    {
        foreach (var scope in Enum.GetValues<ProtectionScope>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DpiBypass.Core.ProtectionService.DescribeScope(scope)));
        }
    }

    [Fact]
    public void ProbeOutcomeDescriptionsAreFilledInForEveryValue()
    {
        foreach (var outcome in Enum.GetValues<DpiBypass.Core.Diagnostics.ProbeOutcome>())
        {
            Assert.False(string.IsNullOrWhiteSpace(DpiBypass.Core.ProtectionService.DescribeOutcome(outcome)));
        }
    }
}
