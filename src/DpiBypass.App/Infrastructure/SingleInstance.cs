using System.Diagnostics;
using System.IO;
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
/// The hand-off is acknowledged, not fire-and-forget. A named object outlives the
/// usefulness of the process holding it: a copy that is wedged, or one Windows has
/// not finished tearing down, still owns the mutex and still drains the activation
/// event while showing the user nothing. Waiting for the running copy to confirm it
/// really put a window on screen is what separates "it is already open" from "it is
/// stuck", and the two need opposite responses.
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
    private const string AcknowledgeEventName = @"Global\DpiBypass.Activated";

    /// <summary>
    /// How long a launch waits for the running copy to confirm the window is up.
    /// Long enough for a busy machine to schedule the UI thread, short enough that a
    /// user who double-clicked an icon is not left staring at nothing.
    /// </summary>
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private EventWaitHandle? _acknowledge;
    private Thread? _listener;

    private SingleInstance(bool isPrimary, Mutex? mutex, EventWaitHandle? activate, EventWaitHandle? acknowledge)
    {
        IsPrimary = isPrimary;
        _mutex = mutex;
        _activate = activate;
        _acknowledge = acknowledge;
    }

    /// <summary>True when this process owns the instance and should build the UI.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>
    /// Raised on a background thread when another launch asks for the window.
    /// </summary>
    /// <remarks>
    /// The handler returns true only once the window is genuinely on screen. That
    /// answer is forwarded to the waiting launch, so a dispatcher that is stuck
    /// reports failure and lets the other process take over rather than swallowing
    /// the request and leaving the user with nothing.
    /// </remarks>
    public event Func<bool>? ActivationRequested;

    public static SingleInstance Acquire()
    {
        Mutex? mutex = null;

        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

            if (createdNew)
            {
                return new SingleInstance(
                    isPrimary: true,
                    mutex,
                    OpenEvent(ActivateEventName, create: true),
                    OpenEvent(AcknowledgeEventName, create: true));
            }

            mutex.Dispose();
            return new SingleInstance(
                isPrimary: false,
                mutex: null,
                OpenEvent(ActivateEventName, create: false),
                OpenEvent(AcknowledgeEventName, create: false));
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but belongs to a context we may not open: another
            // instance is definitely running.
            mutex?.Dispose();
            return new SingleInstance(
                isPrimary: false,
                mutex: null,
                OpenEvent(ActivateEventName, create: false),
                OpenEvent(AcknowledgeEventName, create: false));
        }
        catch (Exception)
        {
            // If the synchronisation objects cannot be created at all, running is a
            // better outcome than refusing to start.
            mutex?.Dispose();
            return new SingleInstance(isPrimary: true, mutex: null, activate: null, acknowledge: null);
        }
    }

    /// <summary>
    /// Asks the running instance to show its window and waits for it to confirm.
    /// Returns false when nobody answered, which means the lock is held by a copy
    /// that is not serving anyone.
    /// </summary>
    public bool SignalExistingInstance()
    {
        if (_activate is null)
        {
            return false;
        }

        try
        {
            // Clear a reply left behind by an earlier launch, or this one would accept
            // it as its own and conclude a wedged instance is healthy.
            _acknowledge?.Reset();

            if (!_activate.Set())
            {
                return false;
            }

            return _acknowledge is not null && _acknowledge.WaitOne(HandoverTimeout);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Promotes this process to the primary instance after the copy holding the lock
    /// failed to answer.
    /// </summary>
    /// <remarks>
    /// Exiting at this point is what left the user with no window, no tray icon and
    /// nothing to click - on every launch, until the machine was rebooted. Ending the
    /// unresponsive copy is safe precisely because it has just proved it is serving
    /// nobody: it was asked for a window and never produced one. This is the same
    /// thing the installer already does before it replaces the files, and it is
    /// restricted to processes running this exact executable.
    /// </remarks>
    public bool TryTakeOver(Action<string>? log = null)
    {
        EndUnresponsiveInstances(log);

        // The kernel keeps the mutex alive until the last handle is closed, which
        // trails process exit slightly, so give it a few tries rather than one.
        for (var attempt = 0; attempt < 10 && !IsPrimary; attempt++)
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                if (createdNew)
                {
                    _mutex = mutex;
                    IsPrimary = true;
                    break;
                }

                mutex.Dispose();
            }
            catch (Exception)
            {
                // Try again; if it never works we still carry on below.
            }

            Thread.Sleep(200);
        }

        if (!IsPrimary)
        {
            // Something still holds the lock and still will not talk to us. Putting a
            // usable window in front of the user beats honouring a lock whose owner
            // has already failed to answer twice.
            log?.Invoke("Tek örnek kilidi bırakılmadı; pencere yine de açılıyor.");
            IsPrimary = true;
        }

        _activate ??= OpenEvent(ActivateEventName, create: true);
        _acknowledge ??= OpenEvent(AcknowledgeEventName, create: true);
        return true;
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

            var shown = false;
            try
            {
                shown = ActivationRequested?.Invoke() ?? false;
            }
            catch (Exception)
            {
                shown = false;
            }

            if (!shown)
            {
                // Staying quiet is deliberate: the launch that asked is waiting on the
                // acknowledgement and will take over when it does not arrive.
                continue;
            }

            try
            {
                _acknowledge?.Set();
            }
            catch (Exception)
            {
                // The other side falls back to taking over, which is still correct.
            }
        }
    }

    /// <summary>Where the copies of this executable that are already running live.</summary>
    /// <param name="OnThisDesktop">At least one is in this Windows session.</param>
    /// <param name="OnAnotherDesktop">At least one is in a different Windows session.</param>
    public readonly record struct InstanceLocations(bool OnThisDesktop, bool OnAnotherDesktop);

    /// <summary>
    /// Finds out whose desktop the running copies are on.
    /// </summary>
    /// <remarks>
    /// The engine is a machine-wide packet filter, so the instance lock has to be
    /// machine-wide too - which means a launch here can be answered by a copy running
    /// on somebody else's desktop after a user switch. That copy dutifully raises its
    /// window where this user cannot see it and reports success, and this launch exits
    /// without a word: the shortcut appears to do nothing, for ever. Detecting it is
    /// the only way to say something useful instead.
    /// </remarks>
    public static InstanceLocations LocateInstances()
    {
        var mine = CurrentSessionId();
        var here = false;
        var elsewhere = false;

        foreach (var process in FindSiblingProcesses())
        {
            try
            {
                if (process.SessionId == mine)
                {
                    here = true;
                }
                else
                {
                    elsewhere = true;
                }
            }
            catch (Exception)
            {
                // A copy we cannot read is assumed to be ours, which keeps the normal
                // handover in charge rather than putting a dialog in front of it.
                here = true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return new InstanceLocations(here, elsewhere);
    }

    /// <summary>
    /// Ends copies of this exact executable that are holding the lock without
    /// answering. Never touches a different program that happens to share the name,
    /// never another user's session, and never this process.
    /// </summary>
    private static void EndUnresponsiveInstances(Action<string>? log)
    {
        var mine = CurrentSessionId();

        foreach (var process in FindSiblingProcesses())
        {
            try
            {
                // A copy that started moments ago is a sibling still finding its feet -
                // a command line verb, or the elevated relaunch of another shortcut -
                // not the wedged instance this is recovering from.
                if (DateTime.Now - process.StartTime < TimeSpan.FromSeconds(10))
                {
                    continue;
                }

                // Another user's copy may be perfectly healthy and serving them.
                if (process.SessionId != mine)
                {
                    continue;
                }

                log?.Invoke($"Yanıt vermeyen kopya kapatılıyor (PID {process.Id}).");
                process.Kill();
                process.WaitForExit(3000);
            }
            catch (Exception)
            {
                // Already gone, or protected. The caller copes either way.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Every other process running this exact executable. Caller disposes.</summary>
    private static IEnumerable<Process> FindSiblingProcesses()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
        {
            yield break;
        }

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(self));
        }
        catch (Exception)
        {
            yield break;
        }

        var current = Environment.ProcessId;

        foreach (var process in candidates)
        {
            var match = false;

            try
            {
                match = process.Id != current
                    && string.Equals(process.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // A process we cannot read is not one we are going to act on.
            }

            if (match)
            {
                yield return process;
            }
            else
            {
                process.Dispose();
            }
        }
    }

    private static int CurrentSessionId()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// An auto-reset event every logged-on user may open, so a shortcut launched
    /// without elevation can still reach the elevated instance.
    /// </summary>
    private static EventWaitHandle? OpenEvent(string name, bool create)
    {
        try
        {
            if (!create)
            {
                return EventWaitHandle.TryOpenExisting(name, out var existing) ? existing : null;
            }

            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
                AccessControlType.Allow));

            return EventWaitHandleAcl.TryOpenExisting(
                name,
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
                out var opened)
                ? opened
                : EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
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
        _acknowledge?.Dispose();

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
