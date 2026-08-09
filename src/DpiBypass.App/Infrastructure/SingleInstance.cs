using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace DpiBypass.App.Infrastructure;

/// <summary>
/// Keeps one copy of the app running and gives later launches a way to say
/// "show yourself" instead of dying quietly.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between the app working and the app appearing to be
/// broken. The logon task starts the engine minimised to the tray, so by the time
/// the user clicks the Start menu entry an instance is already running. Without a
/// hand-off, that click starts a process which sees the mutex, exits, and shows the
/// user nothing at all - indistinguishable from the app failing to launch.
/// </para>
/// <para>
/// A named event is used rather than a window message or a pipe because both ends
/// may be running elevated while the shell that launched them is not, and an event
/// with an explicit DACL is the simplest object that crosses that boundary
/// reliably.
/// </para>
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\DpiBypass.SingleInstance";
    private const string ActivateEventName = @"Global\DpiBypass.Activate";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _listener;

    private SingleInstance(bool isPrimary, Mutex? mutex, EventWaitHandle? activate)
    {
        IsPrimary = isPrimary;
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>True when this process owns the instance and should build the UI.</summary>
    public bool IsPrimary { get; }

    /// <summary>Raised on a background thread when another launch asks for the window.</summary>
    public event Action? ActivationRequested;

    public static SingleInstance Acquire()
    {
        Mutex? mutex = null;

        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

            if (createdNew)
            {
                return new SingleInstance(isPrimary: true, mutex, OpenActivationEvent(create: true));
            }

            mutex.Dispose();
            return new SingleInstance(isPrimary: false, mutex: null, OpenActivationEvent(create: false));
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but belongs to a context we may not open: another
            // instance is definitely running.
            mutex?.Dispose();
            return new SingleInstance(isPrimary: false, mutex: null, OpenActivationEvent(create: false));
        }
        catch (Exception)
        {
            // If the synchronisation objects cannot be created at all, running is a
            // better outcome than refusing to start.
            return new SingleInstance(isPrimary: true, mutex, activate: null);
        }
    }

    /// <summary>Asks the running instance to show its window. Returns false if nobody answered.</summary>
    public bool SignalExistingInstance()
    {
        try
        {
            return _activate?.Set() == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Starts watching for activation requests. Primary instance only.</summary>
    public void BeginListening()
    {
        if (!IsPrimary || _activate is null || _listener is not null)
        {
            return;
        }

        _listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "DpiBypass.Activation",
        };

        _listener.Start();
    }

    private void Listen()
    {
        var handles = new WaitHandle[] { _activate!, _stopping.Token.WaitHandle };

        while (!_stopping.IsCancellationRequested)
        {
            int signalled;
            try
            {
                signalled = WaitHandle.WaitAny(handles);
            }
            catch (Exception)
            {
                return;
            }

            if (signalled != 0)
            {
                return;
            }

            ActivationRequested?.Invoke();
        }
    }

    /// <summary>
    /// An auto-reset event every logged-on user may open, so a shortcut launched
    /// without elevation can still reach the elevated instance.
    /// </summary>
    private static EventWaitHandle? OpenActivationEvent(bool create)
    {
        try
        {
            if (!create)
            {
                return EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing) ? existing : null;
            }

            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
                AccessControlType.Allow));

            return EventWaitHandleAcl.TryOpenExisting(
                ActivateEventName,
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
                out var opened)
                ? opened
                : EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ActivateEventName, out _, security);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();

        try
        {
            _listener?.Join(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Shutting down anyway.
        }

        _activate?.Dispose();

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (Exception)
            {
                // Not owned, or already gone.
            }

            _mutex.Dispose();
        }

        _stopping.Dispose();
    }
}
