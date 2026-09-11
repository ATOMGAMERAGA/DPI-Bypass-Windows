namespace DpiBypass.Core.Diagnostics;

/// <summary>
/// When something that died on its own may be rebuilt, and how hard to keep trying.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog rebuilds three things that can stop without the process noticing: the
/// packet filter, the socket watcher behind process attribution, and the loopback DNS
/// listener. Each needs the same answer to the same two questions, and neither answer is
/// obvious. Retrying immediately and for ever turns a driver that will not open - a
/// Windows update mid-install, a policy that blocks the .sys - into a loop that reopens
/// it several times a second for the rest of the session. Giving up after a few goes
/// instead leaves the user unprotected until they think to restart the app, which for a
/// machine that was only briefly wedged is the worse answer of the two.
/// </para>
/// <para>
/// So: the first rebuild is immediate, each failure after that waits twice as long as
/// the last up to a ceiling, and nothing is ever abandoned. A part that comes back and
/// then stays up is forgiven - its history is forgotten once it has been healthy long
/// enough - so the delay describes how the machine is behaving now rather than what it
/// did an hour ago. A part that comes back and dies again keeps its place in the backoff,
/// which is what stops a flapping driver from being reopened every fifteen seconds.
/// </para>
/// <para>
/// It holds no timer and starts nothing. The caller ticks it, which is what makes the
/// whole policy testable without waiting for real time to pass.
/// </para>
/// </remarks>
public sealed class RecoverySchedule
{
    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _ceiling;
    private readonly TimeSpan _settled;

    private int _attempts;
    private DateTimeOffset? _nextAttempt;
    private DateTimeOffset? _healthySince;

    /// <param name="firstDelay">How long to wait after the first failed rebuild.</param>
    /// <param name="ceiling">The longest the wait is ever allowed to grow to.</param>
    /// <param name="settled">How long the part must stay up for its history to be dropped.</param>
    public RecoverySchedule(TimeSpan? firstDelay = null, TimeSpan? ceiling = null, TimeSpan? settled = null)
    {
        _firstDelay = firstDelay ?? TimeSpan.FromSeconds(15);
        _ceiling = ceiling ?? TimeSpan.FromMinutes(5);
        _settled = settled ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>Rebuilds tried since the last time the part settled.</summary>
    public int Attempts => _attempts;

    /// <summary>When the next rebuild may be tried, or null when one may be tried now.</summary>
    public DateTimeOffset? NextAttempt => _nextAttempt;

    /// <summary>True when a rebuild should be attempted at <paramref name="now"/>.</summary>
    public bool ShouldAttempt(DateTimeOffset now) => _nextAttempt is not { } next || now >= next;

    /// <summary>
    /// Records a rebuild that has just been tried, and returns how long the next one waits.
    /// </summary>
    /// <remarks>
    /// A rebuild that worked is still counted. Something that has to be rebuilt twice a
    /// minute is not healthy, and starting the backoff again from zero on every brief
    /// success is how a watchdog turns a flapping driver into a busy loop.
    /// </remarks>
    public TimeSpan RecordAttempt(DateTimeOffset now, bool succeeded)
    {
        _attempts++;
        _healthySince = succeeded ? now : null;

        var delay = DelayFor(_attempts);
        _nextAttempt = now + delay;
        return delay;
    }

    /// <summary>
    /// Tells the schedule the part is up. Returns true on the tick its history is dropped.
    /// </summary>
    public bool NoteHealthy(DateTimeOffset now)
    {
        if (_attempts == 0)
        {
            _healthySince = null;
            return false;
        }

        _healthySince ??= now;

        if (now - _healthySince.Value < _settled)
        {
            return false;
        }

        Reset();
        return true;
    }

    /// <summary>Forgets everything, as though the part had never failed.</summary>
    public void Reset()
    {
        _attempts = 0;
        _nextAttempt = null;
        _healthySince = null;
    }

    private TimeSpan DelayFor(int attempts)
    {
        // Shifted rather than multiplied, and clamped before the shift: doubling a
        // TimeSpan twenty times over is more than enough to reach any sane ceiling, and
        // stopping there is what keeps a long lived session from overflowing the ticks.
        var steps = Math.Clamp(attempts - 1, 0, 20);
        var ticks = _firstDelay.Ticks << steps;
        return ticks >= _ceiling.Ticks || ticks < 0 ? _ceiling : TimeSpan.FromTicks(ticks);
    }
}
