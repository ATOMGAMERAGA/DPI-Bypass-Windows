namespace DpiBypass.Core.Connection;

/// <summary>Where a connection attempt has got to, as the person watching it would say.</summary>
/// <remarks>
/// Named for what the user is waiting on rather than for what the engine is doing, because
/// this is what the main screen says out loud. The engine's own <c>ProtectionState</c> is
/// coarser - it has one "Starting" covering everything between the button and the first
/// verified request - and coarser is the wrong grain for a screen somebody is watching for
/// four seconds.
/// </remarks>
public enum ConnectionPhase
{
    /// <summary>Nothing running. The button offers to connect.</summary>
    Ready = 0,

    /// <summary>Working out which network this is and whether it is filtering anything.</summary>
    CheckingNetwork = 1,

    /// <summary>Measuring candidate profiles on the live engine.</summary>
    TryingProfiles = 2,

    /// <summary>A profile is installed; confirming a real request gets through with it.</summary>
    Verifying = 3,

    /// <summary>Connected and verified.</summary>
    Connected = 4,

    /// <summary>The user asked to stop an attempt that is still running.</summary>
    Cancelling = 5,

    /// <summary>Taking a working connection back down.</summary>
    Disconnecting = 6,

    /// <summary>The attempt ended without a working connection.</summary>
    Failed = 7,
}

/// <summary>One observable moment of the connection flow.</summary>
/// <param name="Phase">Which stage.</param>
/// <param name="Title">The headline, e.g. "Profiller deneniyor".</param>
/// <param name="Detail">One line under it, or empty. Never a percentage.</param>
/// <param name="Generation">
/// Which attempt this belongs to. Everything the UI shows carries it, so a screen can be
/// asked "is this still the attempt you are showing?" without guessing from the text.
/// </param>
public readonly record struct ConnectionSnapshot(
    ConnectionPhase Phase,
    string Title,
    string Detail,
    int Generation)
{
    /// <summary>True while something is running that the ring should be turning for.</summary>
    public bool IsWorking => Phase
        is ConnectionPhase.CheckingNetwork
        or ConnectionPhase.TryingProfiles
        or ConnectionPhase.Verifying
        or ConnectionPhase.Cancelling
        or ConnectionPhase.Disconnecting;

    /// <summary>True while the user can still call the attempt off.</summary>
    public bool CanCancel => Phase
        is ConnectionPhase.CheckingNetwork
        or ConnectionPhase.TryingProfiles
        or ConnectionPhase.Verifying;

    public bool IsConnected => Phase == ConnectionPhase.Connected;

    public bool IsFailed => Phase == ConnectionPhase.Failed;
}

/// <summary>
/// The connection flow as a state machine, with the guards that keep the screen honest.
/// </summary>
/// <remarks>
/// <para>
/// Three things go wrong on a screen like this and all three are ordering problems, not
/// drawing problems, so they are solved here rather than in XAML.
/// </para>
/// <para>
/// <b>A second click.</b> Pressing connect twice must start one attempt, not two: the
/// second press is answered by <see cref="TryBeginConnect"/> returning false, and the
/// caller does no work at all.
/// </para>
/// <para>
/// <b>A late answer.</b> Every stage a background task reports carries the generation it
/// belongs to. A measurement that finishes after the user cancelled and reconnected is
/// from a previous attempt, and putting "Profiller deneniyor" back on a screen that has
/// reached "Bağlı" is how a working connection comes to look broken. Stale generations are
/// dropped.
/// </para>
/// <para>
/// <b>A stage arriving out of order.</b> Even inside one attempt, two background steps can
/// report in either order. Phases have a rank, and a report that would move the screen
/// backwards is dropped - except for the ones that are genuinely allowed to interrupt
/// (cancelling, failing, finishing), which are terminal or near enough.
/// </para>
/// <para>
/// There is deliberately no percentage anywhere. The number of candidates is known but the
/// time each takes is not, and a bar that fills at a rate nobody can predict is a
/// guess presented as a measurement.
/// </para>
/// </remarks>
public sealed class ConnectionFlow
{
    private readonly Lock _gate = new();
    private ConnectionSnapshot _current = new(ConnectionPhase.Ready, ReadyTitle, string.Empty, 0);
    private int _generation;

    private const string ReadyTitle = "Hazır";

    public ConnectionSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised whenever the snapshot actually changed.</summary>
    /// <remarks>
    /// Raised outside the lock, because a handler that updates a UI and a lock held by a
    /// background thread reporting a stage is a deadlock waiting for the right timing.
    /// </remarks>
    public event Action<ConnectionSnapshot>? Changed;

    /// <summary>
    /// Starts an attempt, unless one is already running.
    /// </summary>
    /// <param name="generation">The attempt's number; pass it back to every <see cref="Report"/>.</param>
    /// <returns>False when a press should be ignored because an attempt is already under way.</returns>
    public bool TryBeginConnect(out int generation)
    {
        ConnectionSnapshot snapshot;

        lock (_gate)
        {
            if (_current.IsWorking)
            {
                generation = _current.Generation;
                return false;
            }

            // A new attempt, including a retry after a failure and a reconnect after a
            // disconnect. Connected is excluded: the button says "disconnect" there.
            if (_current.Phase == ConnectionPhase.Connected)
            {
                generation = _current.Generation;
                return false;
            }

            generation = ++_generation;
            snapshot = _current = new ConnectionSnapshot(
                ConnectionPhase.CheckingNetwork,
                TitleOf(ConnectionPhase.CheckingNetwork),
                string.Empty,
                generation);
        }

        Changed?.Invoke(snapshot);
        return true;
    }

    /// <summary>Starts taking a working connection down, unless something is already running.</summary>
    public bool TryBeginDisconnect(out int generation)
    {
        ConnectionSnapshot snapshot;

        lock (_gate)
        {
            if (_current.Phase != ConnectionPhase.Connected)
            {
                generation = _current.Generation;
                return false;
            }

            generation = ++_generation;
            snapshot = _current = new ConnectionSnapshot(
                ConnectionPhase.Disconnecting,
                TitleOf(ConnectionPhase.Disconnecting),
                string.Empty,
                generation);
        }

        Changed?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Asks the running attempt to stop. Does nothing when there is nothing to stop.
    /// </summary>
    /// <remarks>
    /// The generation is bumped as well as the phase, so every measurement still in flight
    /// is stale from this instant - the cancellation takes effect on the screen
    /// immediately even though the work behind it unwinds at its own pace.
    /// </remarks>
    public bool TryCancel(out int generation)
    {
        ConnectionSnapshot snapshot;

        lock (_gate)
        {
            if (!_current.CanCancel)
            {
                generation = _current.Generation;
                return false;
            }

            generation = ++_generation;
            snapshot = _current = new ConnectionSnapshot(
                ConnectionPhase.Cancelling,
                TitleOf(ConnectionPhase.Cancelling),
                string.Empty,
                generation);
        }

        Changed?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Reports where an attempt has got to.
    /// </summary>
    /// <param name="generation">The attempt this belongs to, from <see cref="TryBeginConnect"/>.</param>
    /// <returns>False when the report was dropped as stale or out of order.</returns>
    public bool Report(int generation, ConnectionPhase phase, string detail = "")
        => Report(generation, phase, TitleOf(phase), detail);

    /// <summary>Reports a stage with a headline of its own, for a failure's own wording.</summary>
    public bool Report(int generation, ConnectionPhase phase, string title, string detail)
    {
        ConnectionSnapshot snapshot;

        lock (_gate)
        {
            if (generation != _generation)
            {
                // From an attempt that has been superseded. Whatever it measured describes
                // a connection nobody is waiting for any more.
                return false;
            }

            if (!MayMoveTo(_current.Phase, phase))
            {
                return false;
            }

            var next = new ConnectionSnapshot(phase, title, detail, generation);
            if (next == _current)
            {
                return false;
            }

            snapshot = _current = next;
        }

        Changed?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Puts the flow back to rest: after a cancellation has unwound, or a disconnect has
    /// finished. Accepted from any phase, because "nothing is running" is always true
    /// enough to say.
    /// </summary>
    public void Settle(int generation, string detail = "")
    {
        ConnectionSnapshot snapshot;

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            var next = new ConnectionSnapshot(ConnectionPhase.Ready, ReadyTitle, detail, generation);
            if (next == _current)
            {
                return;
            }

            snapshot = _current = next;
        }

        Changed?.Invoke(snapshot);
    }

    /// <summary>
    /// Forces the flow to match something outside it - the engine coming up on its own at
    /// launch, or dropping while nobody was pressing anything.
    /// </summary>
    /// <remarks>
    /// Bumps the generation, so an attempt that was in flight when the world changed
    /// underneath it cannot report over the top of the new truth. Used for the events the
    /// user did not cause, which is why it has no guard: there is no "out of order" when
    /// the engine has simply told us what it is.
    /// </remarks>
    public void Adopt(ConnectionPhase phase, string detail = "")
    {
        ConnectionSnapshot snapshot;

        lock (_gate)
        {
            var next = new ConnectionSnapshot(phase, TitleOf(phase), detail, ++_generation);
            snapshot = _current = next;
        }

        Changed?.Invoke(snapshot);
    }

    /// <summary>The Turkish headline for each stage, in the app's voice.</summary>
    public static string TitleOf(ConnectionPhase phase) => phase switch
    {
        ConnectionPhase.CheckingNetwork => "Ağ kontrol ediliyor",
        ConnectionPhase.TryingProfiles => "Profiller deneniyor",
        ConnectionPhase.Verifying => "Bağlantı doğrulanıyor",
        ConnectionPhase.Connected => "Bağlı",
        ConnectionPhase.Cancelling => "İptal ediliyor",
        ConnectionPhase.Disconnecting => "Bağlantı kesiliyor",
        ConnectionPhase.Failed => "Bağlanamadı",
        _ => ReadyTitle,
    };

    /// <summary>
    /// Whether a stage may follow the one on screen.
    /// </summary>
    /// <remarks>
    /// Progress stages never move backwards inside one attempt, but they do repeat: the
    /// candidate counter under "Profiller deneniyor" changes without the stage changing,
    /// and that is news. A report identical to what is on screen is dropped by the caller's
    /// equality check rather than here. The three stages that may arrive at any point are
    /// the ones that end an attempt: a cancellation the user asked for, a failure, and -
    /// because a connection that is already working must be allowed to say so however late
    /// the earlier stage's report was queued - reaching Connected.
    /// </remarks>
    private static bool MayMoveTo(ConnectionPhase from, ConnectionPhase to)
    {
        if (to is ConnectionPhase.Cancelling or ConnectionPhase.Failed or ConnectionPhase.Connected)
        {
            return true;
        }

        if (from is ConnectionPhase.Cancelling or ConnectionPhase.Disconnecting)
        {
            // Both are on their way to rest; Settle is what ends them.
            return false;
        }

        return Rank(to) >= Rank(from);
    }

    private static int Rank(ConnectionPhase phase) => phase switch
    {
        ConnectionPhase.Ready => 0,
        ConnectionPhase.CheckingNetwork => 1,
        ConnectionPhase.TryingProfiles => 2,
        ConnectionPhase.Verifying => 3,
        ConnectionPhase.Connected => 4,
        _ => 5,
    };
}
