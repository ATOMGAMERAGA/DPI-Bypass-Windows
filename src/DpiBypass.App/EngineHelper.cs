using System.Diagnostics;
using DpiBypass.Core;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Logging;

namespace DpiBypass.App;

/// <summary>The elevated path has no window/tray/browser and owns the existing service.</summary>
internal static class EngineHelper
{
    public const string Switch = "--engine-helper";
    public static async Task<int> RunMaintenanceAsync(string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var child = await Task.Run(() => Process.Start(info)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MAINTENANCE_START_FAILED");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        return child.ExitCode;
    }
    public static async Task<ProtectionRpcClient> LaunchAsync(CancellationToken token)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("APP_PATH_UNAVAILABLE");
        var pipeName = "DpiBypass.Engine." + Guid.NewGuid().ToString("N");
        using var owner = Process.GetCurrentProcess();
        var start = owner.StartTime.ToUniversalTime().Ticks;
        // UAC belongs to the driver adapter, after the GUI can draw. It never elevates the GUI.
        var helper = await Task.Run(() => Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = $"{Switch} {pipeName} {Environment.ProcessId} {start}",
        }), token).ConfigureAwait(false) ?? throw new InvalidOperationException("HELPER_START_FAILED");
        var client = new ProtectionRpcClient(pipeName, new(helper.Id, executable));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                if (helper.HasExited) throw new InvalidOperationException("HELPER_START_FAILED");
                try
                {
                    var response = await client.SendAsync(new() { Id = Guid.NewGuid().ToString("N"), Method = "__snapshot" }, deadline.Token).ConfigureAwait(false);
                    if (response.Ok) return client;
                }
                catch (TimeoutException) { }
                catch (System.IO.IOException) { }
                await Task.Delay(200, deadline.Token).ConfigureAwait(false);
            }
            throw new TimeoutException("HELPER_START_TIMEOUT");
        }
        finally { helper.Dispose(); }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Elevation.IsElevated || args.Length != 4 || args[0] != Switch || !ProtectionProtocol.ValidPipeName(args[1])
            || !int.TryParse(args[2], out var pid) || !long.TryParse(args[3], out var started)) return 2;
        var executable = Environment.ProcessPath!;
        var peer = new ProtectionPeer(pid, executable);
        using var owner = Process.GetProcessById(pid);
        if (owner.StartTime.ToUniversalTime().Ticks != started || !ProtectionPipeServer.ValidateOwner(peer)) return 2;
        // The former GUI held a machine-wide instance lock. Its privileged owner
        // needs its own OS-held lock so takeover cannot overlap a dying engine.
        using var engineOwner = await AcquireOwnerAsync(owner).ConfigureAwait(false);
        await using var service = new ProtectionService();
        if (!service.Settings.StartEngineOnLaunch)
        {
            var recovery = new DnsConfigurator(AppPaths.StateDirectory, AppLog.InfoSink);
            if (recovery.HasPendingRestore) await recovery.RestoreAsync().ConfigureAwait(false);
        }
        using var dispatcher = new ProtectionBrokerDispatcher(service);
        var watchdogStarted = false;
        await using var pipe = new ProtectionPipeServer(args[1], peer, async (request, token) =>
        {
            if (request.Method == nameof(IProtectionController.StartAsync)
                && service.Settings.DnsMode != DnsMode.SystemDefault && !watchdogStarted)
            {
                if (!CommandLineTasks.TryStartDnsWatchdog()) return new ProtectionResponse { Error = "DNS_RECOVERY_UNAVAILABLE" };
                watchdogStarted = true;
            }
            return await dispatcher.HandleAsync(request, token).ConfigureAwait(false);
        });
        pipe.Start();
        // No reconnect can inherit a dead GUI's lease. On death, stop calls are
        // cancelled, then disposing this sole owner closes driver/DNS/NIC resources.
        await owner.WaitForExitAsync().ConfigureAwait(false);
        return 0;
    }
    private static async Task<System.IO.FileStream> AcquireOwnerAsync(Process owner)
    {
        var limit = Stopwatch.StartNew();
        while (limit.Elapsed < TimeSpan.FromSeconds(30) && !owner.HasExited)
        {
            try
            {
                return new System.IO.FileStream(System.IO.Path.Combine(AppPaths.StateDirectory, "engine-owner.lock"),
                    System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
            }
            catch (System.IO.IOException) { await Task.Delay(100).ConfigureAwait(false); }
        }
        throw new InvalidOperationException("ENGINE_ALREADY_OWNED");
    }
}
