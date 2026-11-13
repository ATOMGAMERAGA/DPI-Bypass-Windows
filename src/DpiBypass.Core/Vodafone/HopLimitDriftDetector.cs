namespace DpiBypass.Core.Vodafone;

/// <summary>
/// Tells the TTL rule when Windows has dropped the adapter's native hop limit.
/// </summary>
/// <remarks>
/// <para>
/// With the hop limit set on the adapter (<see cref="NativeHopLimit"/>), ordinary packets
/// already leave at the right value and the kernel filter lets them pass without a trip
/// through user mode. So a steady stream of ordinary packets that <i>do</i> need their TTL
/// rewritten means one thing: the stack is sending at its default again - the adapter was
/// restarted, the hotspot reconnected, a game booster's optimiser toggled a property. From
/// that moment every game packet is diverted and re-injected, which is what ExitLag's WFP
/// routing does not survive.
/// </para>
/// <para>
/// Packets another driver injected (impostors) are not counted: a booster that builds its
/// own IP headers keeps its own TTL whatever the adapter says, and re-applying the native
/// value could never quieten it.
/// </para>
/// <para>
/// A re-apply runs PowerShell, so it is rate-limited: nothing in the first
/// <see cref="InitialBackoff"/> after the rule goes up (the native change is still in
/// flight), and if the flood does not stop the wait doubles up to <see cref="MaxBackoff"/>.
/// A quiet window resets it. Single-threaded by design: only the rule's worker calls it.
/// </para>
/// </remarks>
internal sealed class HopLimitDriftDetector
{
    /// <summary>Rewrites of ordinary packets inside one window that count as a flood.</summary>
    internal const int Threshold = 64;

    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    private long _windowStart;
    private int _count;
    private long _notBefore;
    private long _backoff = InitialBackoff.Ticks;

    public HopLimitDriftDetector(long nowTicks) => Reset(nowTicks);

    /// <summary>Starts over, as when the rule is (re)applied.</summary>
    public void Reset(long nowTicks)
    {
        _windowStart = nowTicks;
        _count = 0;
        _backoff = InitialBackoff.Ticks;
        _notBefore = nowTicks + _backoff;
    }

    /// <summary>Records one rewritten ordinary packet.</summary>
    /// <returns>True when the native hop limit should be applied again now.</returns>
    public bool NoteRewrite(long nowTicks)
    {
        if (nowTicks - _windowStart >= Window.Ticks)
        {
            if (_count < Threshold)
            {
                // A quiet window: whatever was flooding has stopped, so the next drift
                // gets a prompt answer again.
                _backoff = InitialBackoff.Ticks;
            }

            _windowStart = nowTicks;
            _count = 0;
        }

        if (++_count < Threshold || nowTicks < _notBefore)
        {
            return false;
        }

        _notBefore = nowTicks + _backoff;
        _backoff = Math.Min(_backoff * 2, MaxBackoff.Ticks);
        _windowStart = nowTicks;
        _count = 0;
        return true;
    }
}
