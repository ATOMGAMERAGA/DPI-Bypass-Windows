using DpiBypass.Core;
using DpiBypass.Core.Config;
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
