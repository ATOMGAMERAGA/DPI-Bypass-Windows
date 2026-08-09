using System.Text.Json;
using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Ipc;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The domain list as the user manipulates it, driven through the service rather
/// than the matcher so persistence and the protected set are checked together.
/// </summary>
public sealed class DomainManagementTests : IDisposable
{
    private readonly string _directory;
    private readonly ProtectionService _service;

    public DomainManagementTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"dpibypass-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var store = new ConfigStore(
            Path.Combine(_directory, "settings.json"),
            Path.Combine(_directory, "networks.json"));

        _service = new ProtectionService(store, new LearnedDomainStore(Path.Combine(_directory, "learned.json")));
    }

    [Fact]
    public void TheShippedListIsProtectedOutOfTheBox()
    {
        var domains = _service.ProtectedDomains();

        Assert.Contains("discord.com", domains);
        Assert.Contains("roblox.com", domains);
        Assert.Equal(BlockedSiteCatalog.All.Length, domains.Count);
    }

    [Fact]
    public void AddingADomainNormalisesAndPersistsIt()
    {
        Assert.True(_service.AddDomain("  Example.COM. "));

        Assert.Contains("example.com", _service.ProtectedDomains());
        Assert.Contains("example.com", _service.Settings.ExtraDomains);
    }

    [Fact]
    public void AddingTheSameDomainTwiceIsRefused()
    {
        Assert.True(_service.AddDomain("example.com"));
        Assert.False(_service.AddDomain("EXAMPLE.COM"));
        Assert.Single(_service.Settings.ExtraDomains);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    public void AValueThatIsNotADomainIsRefused(string value)
    {
        Assert.False(_service.AddDomain(value));
        Assert.Empty(_service.Settings.ExtraDomains);
    }

    [Fact]
    public void RemovingAManualDomainDropsItFromTheList()
    {
        _service.AddDomain("example.com");

        Assert.True(_service.RemoveDomain("example.com"));
        Assert.DoesNotContain("example.com", _service.ProtectedDomains());
    }

    /// <summary>
    /// A shipped entry cannot be deleted, so opting out has to be recorded as an
    /// exclusion - otherwise the next start would bring it straight back.
    /// </summary>
    [Fact]
    public void RemovingAShippedDomainExcludesItAndSurvivesAReload()
    {
        Assert.True(_service.RemoveDomain("instagram.com"));

        Assert.DoesNotContain("instagram.com", _service.ProtectedDomains());
        Assert.Contains("instagram.com", _service.Settings.ExcludedDomains);

        var reloaded = new ConfigStore(
            Path.Combine(_directory, "settings.json"),
            Path.Combine(_directory, "networks.json")).Load();

        Assert.Contains("instagram.com", reloaded.ExcludedDomains);
    }

    [Fact]
    public void AnExcludedDomainCanBeRestored()
    {
        _service.RemoveDomain("instagram.com");

        Assert.True(_service.RestoreDomain("instagram.com"));
        Assert.False(_service.RestoreDomain("instagram.com"));
        Assert.Contains("instagram.com", _service.ProtectedDomains());
    }

    /// <summary>Re-adding a domain by hand must clear a previous opt-out.</summary>
    [Fact]
    public void AddingBackAnExcludedDomainClearsTheExclusion()
    {
        _service.RemoveDomain("instagram.com");
        Assert.True(_service.AddDomain("instagram.com"));

        Assert.DoesNotContain("instagram.com", _service.Settings.ExcludedDomains);
        Assert.Contains("instagram.com", _service.ProtectedDomains());
    }

    [Fact]
    public void TheProtectedListIsSortedAndFreeOfDuplicates()
    {
        _service.AddDomain("aaa.example");
        _service.AddDomain("zzz.example");

        var domains = _service.ProtectedDomains();

        Assert.Equal(domains.Distinct(StringComparer.OrdinalIgnoreCase).Count(), domains.Count);
        Assert.Equal(domains.Order(StringComparer.Ordinal), domains);
    }

    [Fact]
    public void RecheckIntervalIsClampedAtZeroAndPersisted()
    {
        _service.ApplyRecheckInterval(-5);
        Assert.Equal(0, _service.Settings.RecheckIntervalSeconds);

        _service.ApplyRecheckInterval(900);
        Assert.Equal(900, _service.Settings.RecheckIntervalSeconds);
    }

    // --- control protocol -----------------------------------------------------

    [Fact]
    public void AControlRequestSurvivesARoundTrip()
    {
        var request = new ControlRequest { Command = ControlProtocol.Commands.Test, Argument = "discord.com" };
        var json = JsonSerializer.Serialize(request, ControlProtocol.Json);

        var decoded = JsonSerializer.Deserialize<ControlRequest>(json, ControlProtocol.Json);

        Assert.Equal(request, decoded);
    }

    [Fact]
    public void TheStrategyListingCoversEveryStrategy()
    {
        var text = ControlCommands.DescribeStrategies();

        foreach (var strategy in StrategyLibrary.All)
        {
            Assert.Contains(strategy.Id, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheOperatorListingCoversEveryProfile()
    {
        var text = ControlCommands.DescribeIsps();

        foreach (var profile in Core.Network.IspCatalog.All)
        {
            Assert.Contains(profile.Id, text, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup only.
        }
    }
}
