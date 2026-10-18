using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.SocialAccess;
using Xunit;

namespace DpiBypass.Tests;

public sealed class ExperimentStrategyControlTests
{
    [Fact]
    public async Task RetryKeepsOneLeaseAndRecoveryCanReleaseByJournalOperation()
    {
        var current = StrategyLibrary.Default;
        using var coordinator = new StrategyCoordinator(() => current, value => current = value);
        coordinator.BeginSession("network");
        var control = new LocalExperimentStrategyControl(coordinator);

        Assert.True(control.TryAcquire("trial", current.Id));
        Assert.True(control.TryAcquire("trial", current.Id));
        Assert.False(control.TryAcquire("another-trial", current.Id));
        Assert.True(control.TryWrite("trial", StrategyLibrary.Split2.Id));
        Assert.True(control.TryWrite("trial", StrategyLibrary.Split2.Id));
        Assert.False(coordinator.ApplyImmediate("normal tuner", StrategyLibrary.Disorder2));

        // A reconstructed handler has the operation in its journal, rather than a
        // process-local StrategyLease reference.
        var recoveredHandler = new EngineStrategyHandler(control, StrategyLibrary.Split2,
            () => true, () => true, _ => true);
        Assert.True(await recoveredHandler.RollbackAsync(new UndoRecord
        {
            OperationId = "trial", Before = StrategyLibrary.Default.Id, Installed = StrategyLibrary.Split2.Id,
        }, CancellationToken.None));
        Assert.Equal(StrategyLibrary.Default, current);
        Assert.True(control.TryRelease("trial", StrategyLibrary.Default.Id, StrategyLibrary.Split2.Id));
        Assert.True(coordinator.ApplyImmediate("normal tuner", StrategyLibrary.Disorder2));
    }

    [Fact]
    public void RollbackCannotReplaceConcurrentStrategyOrUseDifferentJournalValues()
    {
        var current = StrategyLibrary.Default;
        using var coordinator = new StrategyCoordinator(() => current, value => current = value);
        coordinator.BeginSession("network");
        var control = new LocalExperimentStrategyControl(coordinator);
        Assert.True(control.TryAcquire("trial", current.Id));
        Assert.True(control.TryWrite("trial", StrategyLibrary.Split2.Id));
        Assert.False(control.TryWrite("trial", StrategyLibrary.Disorder2.Id));
        Assert.False(control.TryRelease("trial", StrategyLibrary.Disorder2.Id, StrategyLibrary.Split2.Id));
        Assert.False(control.TryRelease("trial", StrategyLibrary.Default.Id, StrategyLibrary.Disorder2.Id));

        current = StrategyLibrary.Disorder2;
        Assert.False(control.TryRelease("trial", StrategyLibrary.Default.Id, StrategyLibrary.Split2.Id));
        Assert.Equal(StrategyLibrary.Disorder2, current);
        current = StrategyLibrary.Split2;
        Assert.True(control.TryRelease("trial", StrategyLibrary.Default.Id, StrategyLibrary.Split2.Id));
    }

    [Fact]
    public void StaleOperationCannotChangeReplacementEngine()
    {
        var current = StrategyLibrary.Default;
        using var coordinator = new StrategyCoordinator(() => current, value => current = value);
        coordinator.BeginSession("network-a");
        var control = new LocalExperimentStrategyControl(coordinator);
        Assert.True(control.TryAcquire("old", current.Id));
        Assert.True(control.TryWrite("old", StrategyLibrary.Split2.Id));
        coordinator.BeginSession("network-b");
        current = StrategyLibrary.Disorder2;

        Assert.False(control.TryWrite("old", StrategyLibrary.Split2.Id));
        Assert.True(control.TryRelease("old", StrategyLibrary.Default.Id, StrategyLibrary.Split2.Id));
        Assert.Equal(StrategyLibrary.Disorder2, current);
        Assert.True(control.TryAcquire("new", current.Id));
        Assert.False(control.TryWrite("new", "unknown-strategy"));
    }
}
