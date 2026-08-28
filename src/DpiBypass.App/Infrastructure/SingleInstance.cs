using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using DpiBypass.Core;
using DpiBypass.Core.Startup;

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
/// The answer is three-valued rather than two, and that is the point. Silence used
/// to mean both "there is nobody there" and "the copy that is there has not
/// finished starting yet", and the second is the common case: an installation
/// launches the app and something launches it again a second later, while the first
/// copy is still loading the runtime. Treating that as death made the second launch
/// kill a perfectly healthy instance that was seconds from putting its window up -
/// the window appearing and vanishing again during an install was exactly this. So a
/// copy that is still coming up says so, from the moment it takes the lock, and a
/// launch that hears it waits instead of reaching for the kill.
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

    /// <summary>Answered instead of <see cref="AcknowledgeEventName"/> while still starting up.</summary>
    private const string StartingEventName = @"Global\DpiBypass.Starting";

    /// <summary>
    /// How long a launch waits for the running copy to confirm the window is up when
    /// the caller does not say. Long enough for a busy machine to schedule the UI
    /// thread, short enough that a user who double-clicked an icon is not left
    /// staring at nothing.
    /// </summary>
    private static readonly TimeSpan DefaultHandoverTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long one turn of <see cref="AwaitWindow"/> waits for a reply.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than the running copy's own deadline for raising its window
    /// (eight seconds, spent hopping onto a dispatcher that may be busy), because the
    /// two are the same wait seen from opposite ends. A turn that expires first
    /// reports no answer while the copy is in the middle of producing one - and no
    /// answer is what leads to a takeover, which is the whole failure this exists to
    /// prevent. Waiting past its deadline costs nothing: a reply ends the turn the
    /// moment it arrives.
    /// </remarks>
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The pause between asking a still-starting copy again.
    /// </summary>
    /// <remarks>
    /// "Still starting" comes back immediately - it is answered without touching the
    /// dispatcher - so without a pause here the loop would spin on the event for as
    /// long as the other copy takes to start, on a thread that is holding up a launch.
    /// </remarks>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _stopping = new();
    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private EventWaitHandle? _acknowledge;
    private EventWaitHandle? _starting;
    private Thread? _listener;
    private bool _ownsMarker;

    private SingleInstance(
        bool isPrimary,
        Mutex? mutex,
        EventWaitHandle? activate,
        EventWaitHandle? acknowledge,
        EventWaitHandle? starting)
    {
        IsPrimary = isPrimary;
        _mutex = mutex;
        _activate = activate;
        _acknowledge = acknowledge;
        _starting = starting;
    }

    /// <summary>True when this process owns the instance and should build the UI.</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>
    /// Raised on a background thread when another launch asks for the window.
    /// </summary>
    /// <remarks>
    /// The handler reports which of the three answers is true, and that answer is
    /// forwarded to the waiting launch: a dispatcher that is stuck reports failure and
    /// lets the other process take over, while one that simply has not built its window
    /// yet says so and is waited for. Until a handler is attached the listener answers
    /// <see cref="HandoverReply.Starting"/> on its own, which is what makes it safe -
    /// and necessary - to begin listening the instant the lock is taken.
    /// </remarks>
    public event Func<HandoverReply>? ActivationRequested;

    /// <summary>
    /// Where the primary copy records its process id, so a takeover can end that one
    /// process instead of sweeping every copy of the executable.
    /// </summary>
    private static string MarkerPath => Path.Combine(AppPaths.StateDirectory, "instance.pid");

    public static SingleInstance Acquire()
    {
        Mutex? mutex = null;

        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

            if (createdNew)
            {
                var instance = new SingleInstance(
                    isPrimary: true,
                    mutex,
                    OpenEvent(ActivateEventName, create: true),
                    OpenEvent(AcknowledgeEventName, create: true),
                    OpenEvent(StartingEventName, create: true));

                instance.PublishMarker();
                return instance;
            }

            mutex.Dispose();
            return new SingleInstance(
                isPrimary: false,
                mutex: null,
                OpenEvent(ActivateEventName, create: false),
                OpenEvent(AcknowledgeEventName, create: false),
                OpenEvent(StartingEventName, create: false));
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
                OpenEvent(AcknowledgeEventName, create: false),
                OpenEvent(StartingEventName, create: false));
        }
        catch (Exception)
        {
            // If the synchronisation objects cannot be created at all, running is a
            // better outcome than refusing to start.
            mutex?.Dispose();
            return new SingleInstance(isPrimary: true, mutex: null, activate: null, acknowledge: null, starting: null);
        }
    }

    /// <summary>
    /// Asks the running instance to show its window and reports what it said.
    /// </summary>
    /// <remarks>
    /// The timeout is the caller's to choose because the three answers are worth very
    /// different amounts. A short first wait keeps a shortcut feeling immediate;
    /// <see cref="HandoverReply.Starting"/> is a reason to come back rather than to
    /// conclude anything; and only <see cref="HandoverReply.NoAnswer"/> means nobody
    /// is serving this machine.
    /// </remarks>
    public HandoverReply SignalExistingInstance(TimeSpan? timeout = null)
    {
        if (_activate is null)
        {
            return HandoverReply.NoAnswer;
        }

        try
        {
            // Clear replies left behind by an earlier launch, or this one would accept
            // one as its own and misjudge the copy that never answered it.
            _acknowledge?.Reset();
            _starting?.Reset();

            if (!_activate.Set())
            {
                return HandoverReply.NoAnswer;
            }

            return WaitForReply(_acknowledge, _starting, timeout ?? DefaultHandoverTimeout);
        }
        catch (Exception)
        {
            return HandoverReply.NoAnswer;
        }
    }

    /// <summary>
    /// Keeps asking for the window while the running copy says it is still starting,
    /// giving up only when the budget runs out or the copy stops answering.
    /// </summary>
    /// <remarks>
    /// A cold first launch loads a self-contained runtime, reads its settings, builds
    /// the palette and the window, and registers a notification area icon before it
    /// can answer for a window - seconds on a healthy machine, longer on one that is
    /// busy installing the app that is starting. This is the wait that lets it finish.
    /// </remarks>
    public HandoverReply AwaitWindow(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        var result = HandoverReply.NoAnswer;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return result;
            }

            result = SignalExistingInstance(Shorter(remaining, ReplyWindow));

            // Either answer is final: the window is up, or there is nobody home.
            if (result is HandoverReply.WindowShown or HandoverReply.NoAnswer)
            {
                return result;
            }

            var pause = deadline - DateTime.UtcNow;
            if (pause <= TimeSpan.Zero)
            {
                return result;
            }

            Thread.Sleep(Shorter(pause, PollInterval));
        }
    }

    private static TimeSpan Shorter(TimeSpan left, TimeSpan right) => left < right ? left : right;

    /// <summary>
    /// Installer/readiness probe which asks the primary copy to put a real window on
    /// screen without ever acquiring the mutex itself. Acquiring here creates a race
    /// where the probe can become primary a millisecond before the application it is
    /// checking and make that application exit as the "second" copy.
    /// </summary>
    public static bool RequestVisibleWindow(TimeSpan timeout)
    {
        using var activate = OpenEvent(ActivateEventName, create: false);
        using var acknowledge = OpenEvent(AcknowledgeEventName, create: false);
        using var starting = OpenEvent(StartingEventName, create: false);
        if (activate is null || acknowledge is null)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;

        try
        {
            WindowActivation.AllowForegroundHandover();

            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                acknowledge.Reset();
                starting?.Reset();

                if (!activate.Set())
                {
                    return false;
                }

                var reply = WaitForReply(acknowledge, starting, Shorter(remaining, ReplyWindow));

                switch (reply)
                {
                    case HandoverReply.WindowShown:
                        return true;

                    // Still coming up: that is a running instance, so keep waiting for
                    // the window rather than reporting the installation as failed. The
                    // pause is what keeps that from being a spin on the event.
                    case HandoverReply.Starting:
                        var pause = deadline - DateTime.UtcNow;
                        if (pause <= TimeSpan.Zero)
                        {
                            return false;
                        }

                        Thread.Sleep(Shorter(pause, PollInterval));
                        continue;

                    default:
                        return false;
                }
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for whichever reply the running copy sends, and turns it into an answer.
    /// </summary>
    private static HandoverReply WaitForReply(
        EventWaitHandle? acknowledge,
        EventWaitHandle? starting,
        TimeSpan timeout)
    {
        if (acknowledge is null)
        {
            return HandoverReply.NoAnswer;
        }

        if (starting is null)
        {
            return acknowledge.WaitOne(timeout) ? HandoverReply.WindowShown : HandoverReply.NoAnswer;
        }

        // Order matters only in the tie: a copy that managed both in the same instant
        // has a window, which is the more useful of the two answers.
        var signalled = WaitHandle.WaitAny([acknowledge, starting], timeout);

        return signalled switch
        {
            0 => HandoverReply.WindowShown,
            1 => HandoverReply.Starting,
            _ => HandoverReply.NoAnswer,
        };
    }

    /// <summary>
    /// Promotes this process to the primary instance after the copy holding the lock
    /// failed to answer.
    /// </summary>
    /// <remarks>
    /// Exiting at this point is what left the user with no window, no tray icon and
    /// nothing to click - on every launch, until the machine was rebooted. Ending the
    /// unresponsive copy is safe precisely because it has just proved it is serving
    /// nobody: it was asked for a window, repeatedly, and never even claimed to be
    /// starting. This is the same thing the installer already does before it replaces
    /// the files, and it is restricted to processes running this exact executable.
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
            // Starting a second engine without the lock is not a visibility fix: two
            // WinDivert owners and two DNS proxies race over the whole machine. Leave
            // the caller to report the stuck copy instead of manufacturing a second
            // one that can take the connection down.
            log?.Invoke("Tek örnek kilidi bırakılamadı; ikinci motor başlatılmadı.");
            return false;
        }

        _activate ??= OpenEvent(ActivateEventName, create: true);
        _acknowledge ??= OpenEvent(AcknowledgeEventName, create: true);
        _starting ??= OpenEvent(StartingEventName, create: true);
        PublishMarker();
        return true;
    }

    /// <summary>Starts watching for activation requests. Primary instance only.</summary>
    /// <remarks>
    /// Called as soon as the lock is taken, long before there is a window to show.
    /// That is deliberate: a launch arriving during those seconds has to be told the
    /// app is on its way, and the only thing that can tell it is this thread.
    /// </remarks>
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

            HandoverReply result;
            try
            {
                // No handler yet means the application is between taking the lock and
                // building its window. That is the answer, not a failure.
                result = ActivationRequested?.Invoke() ?? HandoverReply.Starting;
            }
            catch (Exception)
            {
                result = HandoverReply.NoAnswer;
            }

            try
            {
                switch (result)
                {
                    case HandoverReply.WindowShown:
                        _acknowledge?.Set();
                        break;

                    case HandoverReply.Starting:
                        _starting?.Set();
                        break;

                    default:
                        // Staying quiet is deliberate: the launch that asked is waiting
                        // on a reply and will take over when neither arrives.
                        break;
                }
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
    /// Ends the copy that is holding the lock without answering. Never touches a
    /// different program that happens to share the name, never another user's session,
    /// and never this process.
    /// </summary>
    /// <remarks>
    /// The process to end is the one that recorded itself as the owner, not every copy
    /// of the executable that happens to be running. The same executable is also the
    /// installer's helper - it registers the logon task, restores DNS and answers the
    /// readiness probe - and sweeping the process name ended those jobs mid-flight
    /// while they were doing exactly what the installation asked of them. Only when
    /// there is no usable record of the owner does the old sweep run, because a lock
    /// held by a copy that left no trace still has to be recoverable.
    /// </remarks>
    private static void EndUnresponsiveInstances(Action<string>? log)
    {
        var owner = ReadMarker();
        if (owner is not null)
        {
            log?.Invoke($"Yanıt vermeyen kopya kapatılıyor (PID {owner.Id}).");
            EndProcess(owner);
            return;
        }

        log?.Invoke("Sahip kaydı okunamadı; bu oturumdaki kopyalar taranıyor.");

        var mine = CurrentSessionId();

        foreach (var process in FindSiblingProcesses())
        {
            try
            {
                // Another user's copy may be perfectly healthy and serving them.
                if (process.SessionId != mine)
                {
                    process.Dispose();
                    continue;
                }

                log?.Invoke($"Yanıt vermeyen kopya kapatılıyor (PID {process.Id}).");
                EndProcess(process);
            }
            catch (Exception)
            {
                // Already gone, or protected. The caller copes either way.
                process.Dispose();
            }
        }
    }

    /// <summary>Ends one process and disposes the handle. Never throws.</summary>
    private static void EndProcess(Process process)
    {
        try
        {
            process.Kill();
            process.WaitForExit(3000);
        }
        catch (Exception)
        {
            // Already gone, or protected.
        }
        finally
        {
            process.Dispose();
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

    /// <summary>Records this process as the owner of the instance lock.</summary>
    private void PublishMarker()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.StateDirectory);
            File.WriteAllText(MarkerPath, Environment.ProcessId.ToString());
            _ownsMarker = true;
        }
        catch (Exception)
        {
            // Without the record a takeover falls back to the process sweep, which is
            // the behaviour this replaced - never a reason to refuse to start.
        }
    }

    /// <summary>
    /// The live owner process, or null when the record is missing, stale, or points at
    /// something that is not this executable.
    /// </summary>
    private static Process? ReadMarker()
    {
        try
        {
            if (!File.Exists(MarkerPath) || !int.TryParse(File.ReadAllText(MarkerPath).Trim(), out var pid))
            {
                return null;
            }

            if (pid <= 0 || pid == Environment.ProcessId)
            {
                return null;
            }

            var process = Process.GetProcessById(pid);

            // A recycled process id belonging to something else entirely is exactly
            // what this check is for: never end a process we cannot identify as ours.
            var self = Environment.ProcessPath;
            var matches = !string.IsNullOrEmpty(self)
                && string.Equals(process.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase)
                && process.SessionId == CurrentSessionId();

            if (matches)
            {
                return process;
            }

            process.Dispose();
            return null;
        }
        catch (Exception)
        {
            // No record, gone already, or a process we may not open.
            return null;
        }
    }

    private void ClearMarker()
    {
        if (!_ownsMarker)
        {
            return;
        }

        try
        {
            // Only if it is still ours: a copy that took over after us owns it now.
            if (File.Exists(MarkerPath)
                && int.TryParse(File.ReadAllText(MarkerPath).Trim(), out var pid)
                && pid == Environment.ProcessId)
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception)
        {
            // A stale record is validated before it is acted on, so leaving it is safe.
        }
        finally
        {
            _ownsMarker = false;
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

        ClearMarker();

        _activate?.Dispose();
        _acknowledge?.Dispose();
        _starting?.Dispose();

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
