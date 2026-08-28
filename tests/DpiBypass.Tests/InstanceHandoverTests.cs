using DpiBypass.Core.Startup;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The decision a second launch makes about the copy already running.
/// </summary>
/// <remarks>
/// Both ways of getting this wrong end with the user having no application, so both
/// are pinned here. Answering "still starting" with a takeover kills a healthy
/// instance seconds before it would have shown its window - which is what an
/// installation that launches the app, followed by anything that launches it again,
/// produced: a window that appeared and vanished. Answering silence with an
/// indefinite wait leaves a wedged copy holding the lock for ever, and every later
/// launch showing nothing.
/// </remarks>
public sealed class InstanceHandoverTests
{
    [Fact]
    public void AWindowOnScreenEndsTheLaunch()
    {
        Assert.Equal(
            LaunchAction.Exit,
            InstanceHandover.Decide(HandoverReply.WindowShown, startupBudgetSpent: false));
    }

    [Fact]
    public void AWindowOnScreenEndsTheLaunchEvenAfterALongWait()
    {
        // The budget is about how long to keep waiting, never about whether an answer
        // that arrived counts.
        Assert.Equal(
            LaunchAction.Exit,
            InstanceHandover.Decide(HandoverReply.WindowShown, startupBudgetSpent: true));
    }

    [Fact]
    public void ACopyThatIsStillStartingIsWaitedForRatherThanEnded()
    {
        // The regression: this must never be ProbeLiveness, which is the road to
        // killing an instance that is seconds from putting its window up.
        Assert.Equal(
            LaunchAction.WaitForStartup,
            InstanceHandover.Decide(HandoverReply.Starting, startupBudgetSpent: false));
    }

    [Fact]
    public void ACopyStillStartingAfterTheWholeBudgetIsNoLongerStarting()
    {
        // The other half: waiting has to end, or a copy stuck before it ever builds a
        // window holds the lock for the life of the session.
        Assert.Equal(
            LaunchAction.ProbeLiveness,
            InstanceHandover.Decide(HandoverReply.Starting, startupBudgetSpent: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SilenceIsCheckedRatherThanActedOnDirectly(bool budgetSpent)
    {
        // Never Exit - that is the launch disappearing with nothing on screen - and
        // never a takeover without the liveness probe that stands between "quiet" and
        // "dead".
        Assert.Equal(
            LaunchAction.ProbeLiveness,
            InstanceHandover.Decide(HandoverReply.NoAnswer, budgetSpent));
    }

    [Fact]
    public void WaitingIsOnlyEverTheAnswerForACopyThatSaidItWasStarting()
    {
        foreach (var reply in Enum.GetValues<HandoverReply>())
        {
            foreach (var spent in new[] { false, true })
            {
                if (InstanceHandover.Decide(reply, spent) == LaunchAction.WaitForStartup)
                {
                    Assert.Equal(HandoverReply.Starting, reply);
                    Assert.False(spent);
                }
            }
        }
    }
}
