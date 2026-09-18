using DpiBypass.Core.Connection;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The ordering guarantees the connection screen is built on.
/// </summary>
/// <remarks>
/// Every one of these is a way a progress screen lies: a second press starting a second
/// attempt, a measurement from an abandoned attempt putting an old stage back, a stage
/// arriving out of order and walking the screen backwards. None of them is visible in a
/// screenshot and all of them are visible to the person waiting.
/// </remarks>
public sealed class ConnectionFlowTests
{
    [Fact]
    public void ItStartsAtRestWithNothingRunning()
    {
        var flow = new ConnectionFlow();

        Assert.Equal(ConnectionPhase.Ready, flow.Current.Phase);
        Assert.False(flow.Current.IsWorking);
        Assert.False(flow.Current.CanCancel);
    }

    [Fact]
    public void ConnectingWalksTheStagesInOrder()
    {
        var flow = new ConnectionFlow();
        var seen = new List<ConnectionPhase>();
        flow.Changed += snapshot => seen.Add(snapshot.Phase);

        Assert.True(flow.TryBeginConnect(out var generation));
        Assert.True(flow.Report(generation, ConnectionPhase.TryingProfiles));
        Assert.True(flow.Report(generation, ConnectionPhase.Verifying));
        Assert.True(flow.Report(generation, ConnectionPhase.Connected));

        Assert.Equal(
            [
                ConnectionPhase.CheckingNetwork,
                ConnectionPhase.TryingProfiles,
                ConnectionPhase.Verifying,
                ConnectionPhase.Connected,
            ],
            seen);

        Assert.True(flow.Current.IsConnected);
        Assert.False(flow.Current.IsWorking);
    }

    /// <summary>A second press must start one attempt, not two.</summary>
    [Fact]
    public void PressingConnectAgainWhileItIsWorkingDoesNothing()
    {
        var flow = new ConnectionFlow();
        Assert.True(flow.TryBeginConnect(out var first));

        var changes = 0;
        flow.Changed += _ => changes++;

        Assert.False(flow.TryBeginConnect(out var second));
        Assert.Equal(first, second);
        Assert.Equal(0, changes);
        Assert.Equal(ConnectionPhase.CheckingNetwork, flow.Current.Phase);
    }

    [Fact]
    public void PressingConnectWhileConnectedDoesNothing()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var generation);
        flow.Report(generation, ConnectionPhase.Connected);

        Assert.False(flow.TryBeginConnect(out _));
        Assert.Equal(ConnectionPhase.Connected, flow.Current.Phase);
    }

    /// <summary>
    /// The core of it: work from a cancelled attempt cannot reach the screen.
    /// </summary>
    [Fact]
    public void AStageFromASupersededAttemptIsDropped()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var abandoned);
        flow.TryCancel(out _);
        flow.Settle(flow.Current.Generation);

        flow.TryBeginConnect(out var current);
        flow.Report(current, ConnectionPhase.Verifying);

        // The measurement the first attempt started finally comes back.
        Assert.False(flow.Report(abandoned, ConnectionPhase.TryingProfiles));
        Assert.Equal(ConnectionPhase.Verifying, flow.Current.Phase);
    }

    [Fact]
    public void ASuccessFromASupersededAttemptCannotClaimTheScreen()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var abandoned);
        flow.TryCancel(out _);

        Assert.False(flow.Report(abandoned, ConnectionPhase.Connected));
        Assert.Equal(ConnectionPhase.Cancelling, flow.Current.Phase);
    }

    [Fact]
    public void AStageThatWouldWalkTheScreenBackwardsIsDropped()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var generation);
        flow.Report(generation, ConnectionPhase.Verifying);

        Assert.False(flow.Report(generation, ConnectionPhase.TryingProfiles));
        Assert.False(flow.Report(generation, ConnectionPhase.CheckingNetwork));
        Assert.Equal(ConnectionPhase.Verifying, flow.Current.Phase);
    }

    /// <summary>
    /// A connection that is working says so however late the earlier stage's report was.
    /// </summary>
    [Fact]
    public void ReachingConnectedIsAcceptedFromAnyStage()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var generation);

        Assert.True(flow.Report(generation, ConnectionPhase.Connected));
        Assert.Equal(ConnectionPhase.Connected, flow.Current.Phase);
    }

    [Fact]
    public void AFailureIsAcceptedFromAnyStage()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var generation);
        flow.Report(generation, ConnectionPhase.Verifying);

        Assert.True(flow.Report(generation, ConnectionPhase.Failed, "Ağ bulunamadı."));
        Assert.True(flow.Current.IsFailed);
        Assert.Equal("Ağ bulunamadı.", flow.Current.Detail);
    }

    [Fact]
    public void RetryingAfterAFailureStartsAFreshAttempt()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var failed);
        flow.Report(failed, ConnectionPhase.Failed, "…");

        Assert.True(flow.TryBeginConnect(out var retry));
        Assert.NotEqual(failed, retry);
        Assert.Equal(ConnectionPhase.CheckingNetwork, flow.Current.Phase);

        // And the failed attempt's leftovers still cannot reach the screen.
        Assert.False(flow.Report(failed, ConnectionPhase.Connected));
    }

    [Fact]
    public void CancelOnlyAppliesWhileThereIsSomethingToCancel()
    {
        var flow = new ConnectionFlow();
        Assert.False(flow.TryCancel(out _));

        flow.TryBeginConnect(out var generation);
        Assert.True(flow.TryCancel(out _));

        // Twice is once.
        Assert.False(flow.TryCancel(out _));

        flow.Settle(flow.Current.Generation);
        Assert.Equal(ConnectionPhase.Ready, flow.Current.Phase);
        Assert.False(flow.Report(generation, ConnectionPhase.Connected));
    }

    [Fact]
    public void ConnectedCanBeTakenBackDownButOnlyFromConnected()
    {
        var flow = new ConnectionFlow();
        Assert.False(flow.TryBeginDisconnect(out _));

        flow.TryBeginConnect(out var generation);
        Assert.False(flow.TryBeginDisconnect(out _));

        flow.Report(generation, ConnectionPhase.Connected);
        Assert.True(flow.TryBeginDisconnect(out var stopping));
        Assert.Equal(ConnectionPhase.Disconnecting, flow.Current.Phase);
        Assert.False(flow.Current.CanCancel);

        flow.Settle(stopping);
        Assert.Equal(ConnectionPhase.Ready, flow.Current.Phase);
    }

    /// <summary>
    /// The engine coming up or dropping on its own overrides whatever the screen thought.
    /// </summary>
    [Fact]
    public void AdoptingAnOutsideStateSupersedesAnythingInFlight()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var inFlight);

        flow.Adopt(ConnectionPhase.Connected, "Koruma etkin");

        Assert.Equal(ConnectionPhase.Connected, flow.Current.Phase);
        Assert.False(flow.Report(inFlight, ConnectionPhase.Failed, "…"));
    }

    [Fact]
    public void TheWorkingAndCancellableFlagsMatchTheStage()
    {
        var working = new[]
        {
            ConnectionPhase.CheckingNetwork,
            ConnectionPhase.TryingProfiles,
            ConnectionPhase.Verifying,
            ConnectionPhase.Cancelling,
            ConnectionPhase.Disconnecting,
        };

        foreach (var phase in Enum.GetValues<ConnectionPhase>())
        {
            var snapshot = new ConnectionSnapshot(phase, ConnectionFlow.TitleOf(phase), string.Empty, 1);
            Assert.Equal(working.Contains(phase), snapshot.IsWorking);
        }

        foreach (var phase in new[] { ConnectionPhase.Cancelling, ConnectionPhase.Disconnecting })
        {
            var snapshot = new ConnectionSnapshot(phase, ConnectionFlow.TitleOf(phase), string.Empty, 1);

            // Turning, but past the point where calling it off means anything.
            Assert.True(snapshot.IsWorking);
            Assert.False(snapshot.CanCancel);
        }
    }

    /// <summary>Every stage says something; an empty headline is a blank screen.</summary>
    [Fact]
    public void EveryStageHasAHeadline()
    {
        foreach (var phase in Enum.GetValues<ConnectionPhase>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ConnectionFlow.TitleOf(phase)));
        }
    }

    /// <summary>A repeat of the stage already on screen raises nothing.</summary>
    [Fact]
    public void RepeatingTheSameStageDoesNotChurnTheScreen()
    {
        var flow = new ConnectionFlow();
        flow.TryBeginConnect(out var generation);
        flow.Report(generation, ConnectionPhase.TryingProfiles, "1/6");

        var changes = 0;
        flow.Changed += _ => changes++;

        Assert.False(flow.Report(generation, ConnectionPhase.TryingProfiles, "1/6"));
        Assert.Equal(0, changes);

        // A different detail on the same stage is still news: it is the candidate counter.
        Assert.True(flow.Report(generation, ConnectionPhase.TryingProfiles, "2/6"));
        Assert.Equal(1, changes);
    }

    /// <summary>
    /// Sixty-four interleaved presses, cancels and late reports leave exactly one attempt
    /// owning the screen, and it is the last one started.
    /// </summary>
    [Fact]
    public void ConcurrentPressesAndLateReportsLeaveOneConsistentState()
    {
        var flow = new ConnectionFlow();
        var stale = new List<int>();

        for (var i = 0; i < 64; i++)
        {
            if (flow.TryBeginConnect(out var generation))
            {
                stale.Add(generation);
                flow.Report(generation, ConnectionPhase.TryingProfiles);
                flow.TryCancel(out _);
                flow.Settle(flow.Current.Generation);
            }
        }

        Assert.True(flow.TryBeginConnect(out var live));
        flow.Report(live, ConnectionPhase.Connected);

        foreach (var generation in stale)
        {
            Assert.False(flow.Report(generation, ConnectionPhase.Failed, "…"));
        }

        Assert.Equal(ConnectionPhase.Connected, flow.Current.Phase);
    }
}
