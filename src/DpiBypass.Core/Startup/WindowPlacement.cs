namespace DpiBypass.Core.Startup;

/// <summary>
/// A screen rectangle in device pixels, with no dependency on WPF or Win32 so the
/// geometry can be reasoned about - and tested - away from a real desktop.
/// </summary>
public readonly record struct WindowRect(double Left, double Top, double Width, double Height)
{
    public static readonly WindowRect Empty = new(0, 0, 0, 0);

    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public double CentreX => Left + (Width / 2);

    public double CentreY => Top + (Height / 2);

    public static WindowRect FromEdges(double left, double top, double right, double bottom)
        => new(left, top, right - left, bottom - top);

    public WindowRect Intersect(WindowRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right <= left || bottom <= top ? Empty : FromEdges(left, top, right, bottom);
    }

    public override string ToString() => $"{Left:0}×{Top:0} {Width:0}x{Height:0}";
}

/// <summary>
/// Decides whether a window is somewhere the user could actually reach it, and where
/// to put it when it is not.
/// </summary>
/// <remarks>
/// <para>
/// A window can be shown, unminimised, uncloaked and completely inaccessible, because
/// the coordinates it was given belong to a display that is no longer there. The
/// laptop that was docked to two monitors when the app last ran, the resolution that
/// changed, the Remote Desktop session that ended - each leaves the desktop smaller
/// than the rectangle the window occupies, and Windows does not move it back. From
/// the user's chair that is a process with a taskbar button and no window, which is
/// indistinguishable from every other way this app can fail to open.
/// </para>
/// <para>
/// The threshold is deliberately more than a single pixel of overlap. A window with
/// four pixels of its edge on screen is on screen by any arithmetic and unusable by
/// any human, so "reachable" means enough of it is there to grab and drag.
/// </para>
/// </remarks>
public static class WindowPlacement
{
    /// <summary>How much of the window has to be on a work area for it to count.</summary>
    public const double MinimumVisibleWidth = 120;

    /// <summary>Enough to include the title bar, which is the handle for everything else.</summary>
    public const double MinimumVisibleHeight = 40;

    /// <summary>
    /// Whether enough of <paramref name="window"/> lands on one of the work areas.
    /// </summary>
    /// <remarks>
    /// An empty work area list means the monitors could not be enumerated, and a
    /// window that cannot be judged is left alone: moving one on a guess is how a
    /// perfectly placed window ends up somewhere the user did not put it.
    /// </remarks>
    public static bool IsReachable(WindowRect window, IReadOnlyList<WindowRect> workAreas)
    {
        if (workAreas is null || workAreas.Count == 0)
        {
            return true;
        }

        if (window.IsEmpty)
        {
            return false;
        }

        var neededWidth = Math.Min(MinimumVisibleWidth, window.Width);
        var neededHeight = Math.Min(MinimumVisibleHeight, window.Height);

        foreach (var area in workAreas)
        {
            var overlap = window.Intersect(area);
            if (overlap.Width >= neededWidth && overlap.Height >= neededHeight)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the rectangle the window should occupy so it is reachable, keeping it
    /// exactly where it is when it already is.
    /// </summary>
    /// <remarks>
    /// The window keeps its size unless the chosen work area is too small for it, and
    /// it is moved by the smallest amount that gets it back - not recentred. A window
    /// the user has positioned is a window the user positioned, and shoving it to the
    /// middle of the screen every time something is checked is its own bug.
    /// </remarks>
    public static WindowRect MoveOnScreen(WindowRect window, IReadOnlyList<WindowRect> workAreas)
    {
        if (workAreas is null || workAreas.Count == 0 || IsReachable(window, workAreas))
        {
            return window;
        }

        var target = NearestArea(window, workAreas);

        var width = Math.Min(window.Width, target.Width);
        var height = Math.Min(window.Height, target.Height);

        if (width <= 0 || height <= 0)
        {
            // A work area we cannot fit anything into: centre on it and let the caller's
            // minimum size win.
            return new WindowRect(target.CentreX, target.CentreY, window.Width, window.Height);
        }

        var left = Math.Clamp(window.Left, target.Left, target.Right - width);
        var top = Math.Clamp(window.Top, target.Top, target.Bottom - height);

        return new WindowRect(left, top, width, height);
    }

    /// <summary>The work area with the most of the window on it, or the closest one.</summary>
    private static WindowRect NearestArea(WindowRect window, IReadOnlyList<WindowRect> workAreas)
    {
        var best = workAreas[0];
        var bestOverlap = -1d;
        var bestDistance = double.MaxValue;

        foreach (var area in workAreas)
        {
            var overlap = window.Intersect(area);
            var covered = overlap.Width * overlap.Height;

            if (covered > bestOverlap)
            {
                best = area;
                bestOverlap = covered;
                bestDistance = Distance(window, area);
                continue;
            }

            if (covered == bestOverlap)
            {
                var distance = Distance(window, area);
                if (distance < bestDistance)
                {
                    best = area;
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    private static double Distance(WindowRect window, WindowRect area)
    {
        var dx = window.CentreX - area.CentreX;
        var dy = window.CentreY - area.CentreY;
        return (dx * dx) + (dy * dy);
    }
}
