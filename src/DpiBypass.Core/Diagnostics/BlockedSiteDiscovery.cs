using System.Collections.Concurrent;
using DpiBypass.Core.Config;
using DpiBypass.Core.Engine;

namespace DpiBypass.Core.Diagnostics;

/// <summary>
/// Notices sites this network filters, by measuring them rather than being told.
/// </summary>
/// <remarks>
/// <para>
/// A hostname the machine has just looked up is probed twice, in the background:
/// once with the engine passing packets through untouched, and once with the
/// working recipe applied. A site that fails the first and passes the second is
/// filtered here, so it is remembered and protected from then on.
/// </para>
/// <para>
/// Everything about this is deliberately timid. Probes are rate limited and run at
/// most two at a time, each hostname is only ever measured once an hour, and the
/// pass is skipped entirely unless a verified recipe is already in place - there is
/// no point measuring anything through a bypass that does not work. The cost of
/// being wrong is a domain being desynced unnecessarily, so the bar is "it only
/// worked with the bypass", never "it failed once".
/// </para>
/// </remarks>
public sealed class BlockedSiteDiscovery : IDisposable
{
    /// <summary>Do not re-measure the same hostname more often than this.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromHours(1);

    /// <summary>Let the user's own request go first.</summary>
    private static readonly TimeSpan ProbeDelay = TimeSpan.FromSeconds(2);

    private const int MaxTrackedHosts = 500;

    private readonly ConnectivityTester _tester;
    private readonly BypassEngine _engine;
    private readonly LearnedDomainStore _store;
    private readonly TargetMatcher _matcher;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _concurrency = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.OrdinalIgnoreCase);

    public BlockedSiteDiscovery(
        ConnectivityTester tester,
        BypassEngine engine,
        LearnedDomainStore store,
        TargetMatcher matcher,
        Action<string>? log = null)
    {
        _tester = tester;
        _engine = engine;
        _store = store;
        _matcher = matcher;
        _log = log;
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Raised after a new domain has been added to the protected set.</summary>
    public event Action<string>? DomainLearned;

    /// <summary>Queues a hostname for measurement. Returns immediately.</summary>
    public void Observe(string hostName, CancellationToken cancellationToken)
    {
        if (!Enabled || !ShouldConsider(hostName))
        {
            return;
        }

        _ = Task.Run(() => ProbeAsync(hostName, cancellationToken), cancellationToken);
    }

    internal bool ShouldConsider(string? hostName)
    {
        if (string.IsNullOrEmpty(hostName) || !hostName.Contains('.'))
        {
            return false;
        }

        // Already covered, or explicitly opted out of - nothing to learn either way.
        if (_matcher.IsBlockedSite(hostName) || _matcher.ExcludedDomains.Contains(hostName))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (_seen.TryGetValue(hostName, out var last) && now - last < Cooldown)
        {
            return false;
        }

        _seen[hostName] = now;
        PruneSeen(now);
        return true;
    }

    private void PruneSeen(DateTimeOffset now)
    {
        if (_seen.Count <= MaxTrackedHosts)
        {
            return;
        }

        foreach (var (host, stamp) in _seen)
        {
            if (now - stamp >= Cooldown)
            {
                _seen.TryRemove(host, out _);
            }
        }
    }

    private async Task ProbeAsync(string hostName, CancellationToken cancellationToken)
    {
        if (!await _concurrency.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            // Busy measuring something else; the hourly cooldown will let this one
            // through next time round rather than queueing work up behind the user.
            _seen.TryRemove(hostName, out _);
            return;
        }

        try
        {
            await Task.Delay(ProbeDelay, cancellationToken).ConfigureAwait(false);

            // Nothing to compare against until the tuner has settled on something.
            if (_engine.Strategy.IsPassthrough)
            {
                // The hostname was written off as seen before the probe ran. Leaving it
                // that way spends its hourly slot on a measurement that never happened -
                // and this is the state the app is in for the whole of the first sweep,
                // which is when most of a session's hostnames go past.
                _seen.TryRemove(hostName, out _);
                return;
            }

            // Control arm: this one hostname goes out untouched while every other
            // connection on the machine keeps its protection.
            _matcher.ProbePassthroughHost = hostName;
            var plain = await _tester.ProbeAsync(hostName, fetchHttp: false, cancellationToken).ConfigureAwait(false);
            _matcher.ProbePassthroughHost = null;

            // Only a failure that looks like filtering counts. A site that is simply
            // down, or that we cannot resolve, tells us nothing.
            if (plain.Success || !plain.LooksBlocked)
            {
                return;
            }

            // Treatment arm: the host is not in the protected set yet - that is the
            // whole question - so it has to be forced in for the measurement.
            _matcher.ProbeForcedHost = hostName;
            var bypassed = await _tester.ProbeAsync(hostName, fetchHttp: false, cancellationToken).ConfigureAwait(false);
            _matcher.ProbeForcedHost = null;

            if (!bypassed.Success)
            {
                return;
            }

            if (_store.Add(hostName))
            {
                _matcher.LearnedDomains.Add(hostName);
                _log?.Invoke(
                    $"Yeni engelli site bulundu: {hostName} — atlatma ile açıldı "
                        + $"({bypassed.Elapsed.TotalMilliseconds:F0} ms), listeye eklendi.");
                DomainLearned?.Invoke(hostName);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Site keşfi başarısız ({hostName}): {ex.Message}");
        }
        finally
        {
            // Whatever happened - including cancellation between the two arms - the
            // overrides must not outlive the probe.
            _matcher.ProbePassthroughHost = null;
            _matcher.ProbeForcedHost = null;
            _concurrency.Release();
        }
    }

    public void Dispose() => _concurrency.Dispose();
}
