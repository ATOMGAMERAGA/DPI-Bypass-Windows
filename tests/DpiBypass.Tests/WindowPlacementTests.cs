using DpiBypass.Core.Startup;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Whether a window is somewhere the user could reach it, and where to put it when it
/// is not.
/// </summary>
/// <remarks>
/// A window keeps the coordinates it was given even after the display those
/// coordinates belonged to has gone: the laptop left its dock, the second monitor was
/// unplugged, the resolution changed, the Remote Desktop session ended. Windows does
/// not move it back, and from the user's chair the result is a live process with a
/// taskbar button and no window - the same symptom as every other way this app can
/// fail to open, which is why it is checked rather than assumed.
/// </remarks>
public sealed class WindowPlacementTests
{
    private static readonly WindowRect Primary = new(0, 0, 1920, 1040);

    /// <summary>A second display to the left, as a docked laptop usually has.</summary>
    private static readonly WindowRect Secondary = new(-1920, 0, 1920, 1040);

    [Fact]
    public void AWindowInTheMiddleOfTheScreenIsReachable()
    {
        Assert.True(WindowPlacement.IsReachable(new WindowRect(420, 130, 1080, 780), [Primary]));
    }

    [Fact]
    public void AWindowOnASecondMonitorIsReachable()
    {
        Assert.True(WindowPlacement.IsReachable(new WindowRect(-1500, 100, 1080, 780), [Primary, Secondary]));
    }

    [Fact]
    public void AWindowOnAMonitorThatIsNoLongerThereIsNot()
    {
        // The docked laptop, undocked: the rectangle is unchanged and the display it
        // referred to is gone.
        Assert.False(WindowPlacement.IsReachable(new WindowRect(-1500, 100, 1080, 780), [Primary]));
    }

    [Fact]
    public void ASliverOfWindowOnScreenDoesNotCount()
    {
        // Six pixels of edge is on screen by arithmetic and unusable by any human.
        Assert.False(WindowPlacement.IsReachable(new WindowRect(1914, 300, 1080, 780), [Primary]));
    }

    [Fact]
    public void AWindowSmallerThanTheThresholdStillCountsWhenItIsFullyOnScreen()
    {
        var tiny = new WindowRect(40, 40, 60, 30);

        Assert.True(WindowPlacement.IsReachable(tiny, [Primary]));
    }

    [Fact]
    public void AWindowIsLeftAloneWhenTheMonitorsCannotBeRead()
    {
        // Judging a window against nothing and moving it on the strength of that is how
        // a perfectly placed window ends up somewhere the user did not put it.
        var window = new WindowRect(-4000, -4000, 1080, 780);

        Assert.True(WindowPlacement.IsReachable(window, []));
        Assert.Equal(window, WindowPlacement.MoveOnScreen(window, []));
    }

    [Fact]
    public void AReachableWindowIsNeverMoved()
    {
        var window = new WindowRect(420, 130, 1080, 780);

        Assert.Equal(window, WindowPlacement.MoveOnScreen(window, [Primary, Secondary]));
    }

    [Fact]
    public void AWindowOffEveryMonitorIsBroughtBackOntoOne()
    {
        var window = new WindowRect(-4200, -3000, 1080, 780);

        var moved = WindowPlacement.MoveOnScreen(window, [Primary]);

        Assert.True(WindowPlacement.IsReachable(moved, [Primary]));
        Assert.Equal(window.Width, moved.Width);
        Assert.Equal(window.Height, moved.Height);
    }

    [Fact]
    public void AWindowIsMovedTheSmallestDistanceThatWorks()
    {
        // Recentring a window the user positioned is its own bug, so a window that has
        // slid off one edge comes back to that edge rather than to the middle.
        var window = new WindowRect(1900, 200, 1080, 780);

        var moved = WindowPlacement.MoveOnScreen(window, [Primary]);

        Assert.Equal(Primary.Right - window.Width, moved.Left);
        Assert.Equal(200, moved.Top);
    }

    [Fact]
    public void AWindowTooLargeForTheRemainingDisplayIsShrunkToFit()
    {
        // The 4K monitor is gone and only a 1280x720 laptop panel is left.
        var small = new WindowRect(0, 0, 1280, 680);
        var window = new WindowRect(3000, 1600, 1920, 1200);

        var moved = WindowPlacement.MoveOnScreen(window, [small]);

        Assert.True(WindowPlacement.IsReachable(moved, [small]));
        Assert.True(moved.Width <= small.Width);
        Assert.True(moved.Height <= small.Height);
    }

    [Fact]
    public void TheNearestRemainingMonitorIsChosen()
    {
        var window = new WindowRect(-3600, 200, 400, 300);

        var moved = WindowPlacement.MoveOnScreen(window, [Primary, Secondary]);

        Assert.True(WindowPlacement.IsReachable(moved, [Secondary]));
    }

    [Fact]
    public void AnEmptyWindowRectangleIsNeverReachable()
    {
        Assert.False(WindowPlacement.IsReachable(WindowRect.Empty, [Primary]));
    }

    [Fact]
    public void RectanglesIntersectTheWayScreenCoordinatesDo()
    {
        var overlap = new WindowRect(-100, -100, 300, 300).Intersect(Primary);

        Assert.Equal(new WindowRect(0, 0, 200, 200), overlap);
        Assert.True(new WindowRect(-500, 0, 100, 100).Intersect(Primary).IsEmpty);
    }
}
