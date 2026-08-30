using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The parts of the service that decide what the tuner is asked to do. Everything
/// here runs without a driver or a network, because that is the point: choosing the
/// operator profile is a decision, not an I/O operation.
/// </summary>
public sealed class ProtectionServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly ProtectionService _service;

    public ProtectionServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"dpibypass-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var store = new ConfigStore(
            Path.Combine(_directory, "settings.json"),
            Path.Combine(_directory, "networks.json"));

        _service = new ProtectionService(store, new LearnedDomainStore(Path.Combine(_directory, "learned.json")));
    }

    [Fact]
    public void ForcingAnOperatorIsReflectedStraightAway()
    {
        _service.ApplyManualIsp(IspCatalog.TurkTelekomHome.Id);

        Assert.Equal(IspCatalog.TurkTelekomHome.Id, _service.Isp.Id);
        Assert.Equal(IspCatalog.TurkTelekomHome.Id, _service.Settings.ManualIspProfileId);
        Assert.NotNull(_service.Detection);
        Assert.False(_service.Detection!.WasAutomatic);
    }

    /// <summary>
    /// Going back to "Otomatik algıla" has to drop the forced answer. Keeping it meant
    /// the status line went on naming the operator the user had just deselected, and -
    /// worse - the next sweep was still ordered by that operator's strategy list.
    /// </summary>
    [Fact]
    public void ChoosingAutomaticDetectionDropsThePreviouslyForcedOperator()
    {
        _service.ApplyManualIsp(IspCatalog.VodafoneMobile.Id);
        Assert.Equal(IspCatalog.VodafoneMobile.Id, _service.Isp.Id);

        _service.ApplyManualIsp(null);

        Assert.Null(_service.Settings.ManualIspProfileId);
        Assert.Null(_service.Detection);
        Assert.Equal(IspCatalog.Unknown.Id, _service.Isp.Id);
    }

    [Fact]
    public void TheOperatorChoiceIsPersisted()
    {
        _service.ApplyManualIsp(IspCatalog.Superonline.Id);

        var reloaded = new ConfigStore(
            Path.Combine(_directory, "settings.json"),
            Path.Combine(_directory, "networks.json")).Load();

        Assert.Equal(IspCatalog.Superonline.Id, reloaded.ManualIspProfileId);
    }

    /// <summary>
    /// The neutral profile still has to be able to drive a sweep: it is what the tuner
    /// is handed between deselecting an operator and detection finishing.
    /// </summary>
    [Fact]
    public void TheUnknownProfileStillOffersEveryStrategy()
    {
        Assert.NotEmpty(IspCatalog.Unknown.PreferredStrategies);
        Assert.DoesNotContain("passthrough", IspCatalog.Unknown.PreferredStrategies);
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

/// <summary>
/// The service side of the mobile hotspot feature: cleanup that can be called at any
/// time, from anywhere, twice.
/// </summary>
public sealed class ProtectionServiceHotspotTests : IDisposable
{
    private readonly TempDirectory _directory = new("dpibypass-hotspot");

    [Fact]
    public async Task ALegacyConfigIsCleanedAndPersistedWhenTheServiceIsBuilt()
    {
        File.WriteAllText(SettingsPath, """
            {
              "HotspotTtlFix": true,
              "HotspotTtlNetworks": [ { "Key": "abc", "DisplayName": "phone", "AdapterName": "Wi-Fi" } ]
            }
            """);

        await using var service = Build();

        Assert.False(service.Settings.HotspotTtlFix);
        Assert.True(service.Settings.HotspotDiagnostics);

        // On disk, not only in memory: the next process to read this file, including the
        // uninstaller, must see the cleaned version.
        var onDisk = File.ReadAllText(SettingsPath);
        Assert.Contains("\"HotspotTtlFix\": false", onDisk, StringComparison.Ordinal);
        Assert.Contains("\"HotspotDiagnostics\": true", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupCanBeCalledTwiceAndOnAMachineThatNeverHadTheOldMode()
    {
        await using var service = Build();

        var first = service.CleanUpLegacyHotspotConfiguration();
        var second = service.CleanUpLegacyHotspotConfiguration();

        Assert.False(first.Changed);
        Assert.False(second.Changed);
        Assert.False(service.Settings.HotspotTtlFix);
    }

    [Fact]
    public async Task CleanupReportsWhatItActuallyRemoved()
    {
        File.WriteAllText(SettingsPath, """{"HotspotTtlFix": true}""");
        await using var service = Build();

        // The constructor already migrated, so an explicit cleanup finds nothing left -
        // which is the point: there is no second copy of the state to go stale.
        Assert.False(service.CleanUpLegacyHotspotConfiguration().Changed);
        Assert.False(service.Settings.HotspotTtlFix);
    }

    [Fact]
    public async Task RunningTheDiagnosticsRemembersTheResultForTheUi()
    {
        var diagnostics = new FakeHotspotDiagnostics();
        await using var service = Build(diagnostics);

        Assert.Null(service.HotspotStatus.LastResult);

        var result = await service.RunHotspotDiagnosticsAsync();

        Assert.Same(result, service.HotspotStatus.LastResult);
        Assert.Equal(1, diagnostics.Runs);
    }

    [Fact]
    public async Task TheDiagnosticsSwitchIsPersisted()
    {
        await using var service = Build();

        service.SetHotspotDiagnostics(true);

        Assert.True(new ConfigStore(SettingsPath, ProfilesPath).Load().HotspotDiagnostics);
    }

    [Fact]
    public async Task SettingTheSwitchToWhatItAlreadyIsDoesNotWriteAnything()
    {
        await using var service = Build();
        service.SetHotspotDiagnostics(true);

        var written = File.GetLastWriteTimeUtc(SettingsPath);
        service.SetHotspotDiagnostics(true);

        Assert.Equal(written, File.GetLastWriteTimeUtc(SettingsPath));
    }

    private string SettingsPath => Path.Combine(_directory.Path, "settings.json");

    private string ProfilesPath => Path.Combine(_directory.Path, "networks.json");

    private ProtectionService Build(IMobileHotspotDiagnostics? diagnostics = null) => new(
        new ConfigStore(SettingsPath, ProfilesPath),
        new LearnedDomainStore(Path.Combine(_directory.Path, "learned.json")),
        hotspotDiagnostics: diagnostics ?? new FakeHotspotDiagnostics());

    public void Dispose() => _directory.Dispose();

    private sealed class FakeHotspotDiagnostics : IMobileHotspotDiagnostics
    {
        public int Runs { get; private set; }

        public Task<HotspotDiagnosticResult> RunAsync(
            NetworkFingerprint network,
            CancellationToken cancellationToken = default)
        {
            Runs++;

            return Task.FromResult(new HotspotDiagnosticResult
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
}
