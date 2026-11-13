using System.Globalization;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Vodafone;

/// <summary>
/// Sets the hop limit Windows itself writes on packets leaving one adapter.
/// </summary>
/// <remarks>
/// Abstracted so the reconciliation in <see cref="NativeHopLimit"/> can be tested
/// without PowerShell or administrator rights.
/// </remarks>
public interface IHopLimitBackend
{
    /// <summary>Reads the current value for each family (null where unreadable), then writes <paramref name="hopLimit"/>.</summary>
    Task<HopLimitSnapshot?> ApplyAsync(int interfaceIndex, byte hopLimit, CancellationToken cancellationToken);

    /// <summary>Puts back what <see cref="ApplyAsync"/> found.</summary>
    Task RestoreAsync(HopLimitSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>What an adapter's hop limits were before the mode changed them.</summary>
public sealed record HopLimitSnapshot(int InterfaceIndex, uint? IPv4, uint? IPv6);

/// <summary>
/// Gives Vodafone Sınırsız Modu's TTL to the network stack instead of to a packet filter.
/// </summary>
/// <remarks>
/// <para>
/// The TTL rule used to work by diverting every outbound packet on the hotspot adapter
/// through WinDivert, rewriting it in user mode and injecting it again. That includes
/// every packet of every game. ExitLag in its default WFP mode routes a game by
/// classifying that same traffic in the kernel, and a packet WinDivert has taken out and
/// re-injected arrives at its callout as "injected by another driver" - which it does
/// not route. Valorant's connection went dead the moment both were running, while the
/// rest of the PC was fine.
/// </para>
/// <para>
/// Windows can do the rewrite itself: an adapter's CurrentHopLimit is the TTL the stack
/// puts on every packet it sends there. Set in the active store only, so it never
/// outlives a reboot, and put back when the rule is removed. Once it is in place every
/// ordinary packet already leaves at the right value, the WinDivert filter (which skips
/// packets already carrying it) diverts almost nothing, and game traffic never leaves the
/// kernel path. The filter stays as the safety net for software that picks its own TTL
/// and for the moments before the stack has taken the new value.
/// </para>
/// <para>
/// Changes are applied on a background worker that always converges on the latest wanted
/// state, so a quick remove-and-reapply (network change, settings edit) costs nothing.
/// </para>
/// </remarks>
public sealed class NativeHopLimit : IDisposable
{
    private readonly IHopLimitBackend _backend;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();

    private (int Index, byte HopLimit) _wanted;
    private (int Index, byte HopLimit) _applied;
    private HopLimitSnapshot? _original;
    private Task _work = Task.CompletedTask;
    private bool _reassert;
    private bool _disposed;

    public NativeHopLimit(IHopLimitBackend? backend = null, Action<string>? log = null)
    {
        _backend = backend ?? new PowerShellHopLimitBackend();
        _log = log;
    }

    /// <summary>The adapter and value the stack currently carries, or (0, 0).</summary>
    public (int Index, byte HopLimit) Applied
    {
        get
        {
            lock (_gate)
            {
                return _applied;
            }
        }
    }

    /// <summary>Asks for <paramref name="hopLimit"/> on <paramref name="interfaceIndex"/>; 0 removes it.</summary>
    public Task Set(int interfaceIndex, byte hopLimit)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _wanted = interfaceIndex > 0 ? (interfaceIndex, hopLimit) : (0, (byte)0);
            _work = _work.ContinueWith(_ => ReconcileAsync(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _work;
        }
    }

    public Task Clear() => Set(0, 0);

    /// <summary>
    /// Writes the wanted value again even though it was applied once already.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value lives in the active store, and Windows rebuilds that store whenever the
    /// adapter restarts: a phone hotspot dropping and coming back, a driver reset, a
    /// "network optimisation" in a game booster that toggles an adapter property. Every
    /// one of those quietly puts the adapter back on 128 while this class still believes
    /// 65 is in force - and from then on the TTL rule is diverting every game packet
    /// again, which is exactly the state that took Valorant down under ExitLag's WFP
    /// routing. The rule notices the flood and calls this.
    /// </para>
    /// <para>
    /// The original values are not read again: what is on the adapter now is the reset,
    /// not what the user had, and it is the first snapshot that gets put back.
    /// </para>
    /// </remarks>
    public Task Reassert()
    {
        lock (_gate)
        {
            if (_disposed || _wanted.Index == 0)
            {
                return Task.CompletedTask;
            }

            _reassert = true;
            _work = _work.ContinueWith(_ => ReconcileAsync(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return _work;
        }
    }

    private async Task ReconcileAsync()
    {
        (int Index, byte HopLimit) wanted;
        (int Index, byte HopLimit) applied;
        HopLimitSnapshot? original;
        bool reassert;
        lock (_gate)
        {
            wanted = _wanted;
            applied = _applied;
            original = _original;
            reassert = _reassert && wanted.Index != 0;
            _reassert = false;
        }

        if (wanted == applied && !reassert)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Moving to another adapter (or off entirely): put the old one back first.
            if (original is not null && (wanted.Index != applied.Index || wanted.Index == 0))
            {
                await _backend.RestoreAsync(original, timeout.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _original = null;
                    _applied = (0, (byte)0);
                }

                _log?.Invoke($"Vodafone: adapter {original.InterfaceIndex} hop limit restored.");
                original = null;
            }

            if (wanted.Index == 0)
            {
                return;
            }

            var snapshot = await _backend.ApplyAsync(wanted.Index, wanted.HopLimit, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // Keep the very first values seen: a second apply on the same adapter
                // would otherwise record our own value as the "original".
                _original = original ?? snapshot;
                _applied = snapshot is null ? (0, (byte)0) : wanted;
            }

            _log?.Invoke(snapshot is null
                ? $"Vodafone: adapter {wanted.Index} hop limit could not be set natively; the packet filter keeps rewriting."
                : reassert && wanted == applied
                    ? $"Vodafone: adapter {wanted.Index} had lost its TTL {wanted.HopLimit} (adapter restarted?); set again natively."
                    : $"Vodafone: adapter {wanted.Index} sends at TTL {wanted.HopLimit} natively; game traffic is no longer diverted.");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Vodafone: native hop limit change failed ({ex.Message}); the packet filter keeps rewriting.");
        }
    }

    public void Dispose()
    {
        Task work;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _wanted = (0, (byte)0);
            _work = _work.ContinueWith(_ => ReconcileAsync(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            work = _work;
            _disposed = true;
        }

        try
        {
            // The app is going away; give the restore a bounded chance to finish.
            work.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Active-store values are gone after a reboot anyway.
        }
    }
}

/// <summary>Get-/Set-NetIPInterface, active store only, so nothing survives a reboot.</summary>
internal sealed class PowerShellHopLimitBackend : IHopLimitBackend
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(25);

    public async Task<HopLimitSnapshot?> ApplyAsync(int interfaceIndex, byte hopLimit, CancellationToken cancellationToken)
    {
        // Values are integers we produced, so they are formatted straight into the script.
        var script = string.Create(CultureInfo.InvariantCulture, $$"""
            $out = @()
            foreach ($family in 'IPv4','IPv6') {
              try {
                $i = Get-NetIPInterface -InterfaceIndex {{interfaceIndex}} -AddressFamily $family -PolicyStore ActiveStore -ErrorAction Stop
                $before = [uint32]$i.CurrentHopLimit
                Set-NetIPInterface -InterfaceIndex {{interfaceIndex}} -AddressFamily $family -CurrentHopLimit {{hopLimit}} -PolicyStore ActiveStore -ErrorAction Stop
                $out += "$family=$before"
              } catch { }
            }
            $out -join ';'
            """);

        var result = await ProcessRunner.PowerShellAsync(script, Budget, cancellationToken).ConfigureAwait(false);
        return result.Success ? Parse(interfaceIndex, result.StandardOutput) : null;
    }

    public async Task RestoreAsync(HopLimitSnapshot snapshot, CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        // Zero reads as "not set on the adapter"; Windows' own default for both
        // families is 128, which is what such an adapter was sending at.
        if (snapshot.IPv4 is { } v4)
        {
            v4 = v4 is > 0 and <= 255 ? v4 : 128;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"try {{ Set-NetIPInterface -InterfaceIndex {snapshot.InterfaceIndex} -AddressFamily IPv4 -CurrentHopLimit {v4} -PolicyStore ActiveStore -ErrorAction Stop }} catch {{ }}"));
        }

        if (snapshot.IPv6 is { } v6)
        {
            v6 = v6 is > 0 and <= 255 ? v6 : 128;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"try {{ Set-NetIPInterface -InterfaceIndex {snapshot.InterfaceIndex} -AddressFamily IPv6 -CurrentHopLimit {v6} -PolicyStore ActiveStore -ErrorAction Stop }} catch {{ }}"));
        }

        if (lines.Count == 0)
        {
            return;
        }

        await ProcessRunner.PowerShellAsync(string.Join(Environment.NewLine, lines), Budget, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>"IPv4=128;IPv6=64" → snapshot; null when neither family was changed.</summary>
    internal static HopLimitSnapshot? Parse(int interfaceIndex, string output)
    {
        uint? v4 = null;
        uint? v6 = null;
        foreach (var part in output.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2 || !uint.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            if (pieces[0] == "IPv4")
            {
                v4 = value;
            }
            else if (pieces[0] == "IPv6")
            {
                v6 = value;
            }
        }

        return v4 is null && v6 is null ? null : new HopLimitSnapshot(interfaceIndex, v4, v6);
    }
}
