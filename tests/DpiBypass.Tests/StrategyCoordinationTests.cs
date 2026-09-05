using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The races that used to be reachable through the engine's shared strategy field.
/// </summary>
/// <remarks>
/// Every one of these is driven with a task the test releases by hand rather than with a
/// sleep: the point is which write wins, and a test that decides that with a timer proves
/// nothing on a loaded build agent. The handover budget is set to a few milliseconds so a
/// superseded run that is deliberately still blocked cannot hold the new one up.
/// </remarks>
public sealed class StrategyCoordinationTests
{
    private static readonly TimeSpan Handover = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The engine, plus a record of everything written to it and whether two runs were
    /// ever inside a write at the same time.
    /// </summary>
    private sealed class FakeEngine
    {
        private readonly Lock _gate = new();
        private readonly List<string> _writes = [];

        public BypassStrategy Strategy { get; private set; } = StrategyLibrary.Default;

        public IReadOnlyList<string> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public void Write(BypassStrategy strategy)
        {
            lock (_gate)
            {
                Strategy = strategy;
                _writes.Add(strategy.Id);
            }
        }
    }

    private static StrategyCoordinator Coordinator(FakeEngine engine, List<string>? log = null)
        => new(
            () => engine.Strategy,
            engine.Write,
            log is null ? null : line => { lock (log) { log.Add(line); } },
            handoverTimeout: Handover);

    /// <summary>
    /// The headline race: a sweep started on network A finishes after the machine has
    /// already settled on B, and must not touch B's engine or B's profile.
    /// </summary>
    [Fact]
    public async Task ASlowRunOnTheOldNetworkWritesNothingOnceTheMachineHasMoved()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        var slowRunReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlowRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StrategyLease? staleLease = null;

        var slow = coordinator.RunAsync(
            StrategyWorkKind.Automatic,
            "A ağı taraması",
            async (lease, _) =>
            {
                staleLease = lease;
                slowRunReached.SetResult();
                await releaseSlowRun.Task.ConfigureAwait(false);

                // Deliberately ignores its cancellation, the way a probe wedged in a
                // kernel call would. Being stale, not being cancelled, is what stops it.
                lease.TryWrite(StrategyLibrary.SplitSni);
            });

        await slowRunReached.Task.WaitAsync(Patience);

        // The machine moves. Everything the old run is about to do is now about a link
        // nobody is on.
        Assert.True(coordinator.AdoptNetwork("network-b"));

        await coordinator.RunAsync(
            StrategyWorkKind.Automatic,
            "B ağı taraması",
            (lease, _) =>
            {
                Assert.True(lease.TryWrite(StrategyLibrary.Disorder2));
                return Task.CompletedTask;
            }).WaitAsync(Patience);

        Assert.Equal(StrategyLibrary.Disorder2, engine.Strategy);

        releaseSlowRun.SetResult();
        await slow.WaitAsync(Patience);

        Assert.Equal(StrategyLibrary.Disorder2, engine.Strategy);
        Assert.False(staleLease!.IsCurrent);
        Assert.DoesNotContain(StrategyLibrary.SplitSni.Id, engine.Writes);
        Assert.True(coordinator.SupersededWrites > 0);
    }

    /// <summary>
    /// A superseded run keeps its own network key, so a profile write from it can be
    /// refused rather than filed under the network the machine has since moved to.
    /// </summary>
    [Fact]
    public async Task AStaleLeaseStillKnowsWhichNetworkItWasMeasuring()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        StrategyLease? captured = null;
        await coordinator.RunAsync(
            StrategyWorkKind.Automatic,
            "A",
            (lease, _) =>
            {
                captured = lease;
                return Task.CompletedTask;
            }).WaitAsync(Patience);

        Assert.Equal("network-a", captured!.NetworkKey);
        Assert.True(captured.IsCurrent);

        coordinator.AdoptNetwork("network-b");

        Assert.Equal("network-a", captured.NetworkKey);
        Assert.False(captured.IsCurrent);
    }

    /// <summary>
    /// The user pressing re-tune while the timer's own sweep is running must produce one
    /// writer, not two interleaved ones.
    /// </summary>
    [Fact]
    public async Task AManualRunSupersedesTheAutomaticSweepInFlight()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        var automaticReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAutomatic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var automaticWasCancelled = false;

        var automatic = coordinator.RunAsync(
            StrategyWorkKind.Automatic,
            "düzenli denetim",
            async (lease, token) =>
            {
                automaticReached.SetResult();
                await releaseAutomatic.Task.ConfigureAwait(false);
                automaticWasCancelled = token.IsCancellationRequested;

                // The sweep's own restore on the way out, which used to be able to undo
                // whatever the manual run had just installed.
                lease.TryWrite(StrategyLibrary.Passthrough);
            });

        await automaticReached.Task.WaitAsync(Patience);

        await coordinator.RunAsync(
            StrategyWorkKind.Manual,
            "elle yeniden ayarlama",
            (lease, _) =>
            {
                Assert.True(lease.TryWrite(StrategyLibrary.MultiSplitSni));
                return Task.CompletedTask;
            }).WaitAsync(Patience);

        releaseAutomatic.SetResult();
        await automatic.WaitAsync(Patience);

        Assert.True(automaticWasCancelled);
        Assert.Equal(StrategyLibrary.MultiSplitSni, engine.Strategy);
        Assert.DoesNotContain(StrategyLibrary.Passthrough.Id, engine.Writes);
    }

    /// <summary>
    /// Choosing a recipe by hand is a write like any other, and outranks the sweep it
    /// interrupts - including that sweep's restore.
    /// </summary>
    [Fact]
    public async Task PickingARecipeByHandOutranksARunningSweep()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var sweep = coordinator.RunAsync(
            StrategyWorkKind.Automatic,
            "tarama",
            async (lease, _) =>
            {
                reached.SetResult();
                await release.Task.ConfigureAwait(false);
                lease.TryWrite(StrategyLibrary.Passthrough);
            });

        await reached.Task.WaitAsync(Patience);
        Assert.True(coordinator.ApplyImmediate("kullanıcı seçimi", StrategyLibrary.OobSplitSni));

        release.SetResult();
        await sweep.WaitAsync(Patience);

        Assert.Equal(StrategyLibrary.OobSplitSni, engine.Strategy);
    }

    /// <summary>
    /// Several automatic requests arriving at once - the start-up pass, the timer, a
    /// notification burst - are one sweep, not four queued behind each other.
    /// </summary>
    [Fact]
    public async Task ConcurrentAutomaticRequestsForOneNetworkRunTheWorkOnce()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        var runs = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunAsync(
            StrategyWorkKind.Automatic,
            "ilk başlatma",
            async (_, _) =>
            {
                Interlocked.Increment(ref runs);
                reached.SetResult();
                await release.Task.ConfigureAwait(false);
            });

        await reached.Task.WaitAsync(Patience);

        var joiners = Enumerable.Range(0, 8)
            .Select(i => coordinator.RunAsync(
                StrategyWorkKind.Automatic,
                $"düzenli denetim {i}",
                (_, _) =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                }))
            .ToArray();

        release.SetResult();
        await Task.WhenAll([first, .. joiners]).WaitAsync(Patience);

        Assert.Equal(1, runs);
    }

    /// <summary>
    /// Coalescing must never wait on a lock the run it is joining needs to finish.
    /// </summary>
    /// <remarks>
    /// A superseded run reports the cancellation it was given, which is the honest answer
    /// and not what this is testing: the property here is that every one of these settles
    /// at all, rather than a joiner and the run it joined waiting on each other.
    /// </remarks>
    [Fact]
    public async Task CoalescingManyRequestsNeverDeadlocks()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        var requests = Enumerable.Range(0, 64)
            .Select(i => Task.Run(() => Settled(coordinator.RunAsync(
                i % 8 == 0 ? StrategyWorkKind.Manual : StrategyWorkKind.Automatic,
                $"istek {i}",
                async (lease, token) =>
                {
                    await Task.Yield();
                    if (!token.IsCancellationRequested)
                    {
                        lease.TryWrite(StrategyLibrary.Split2);
                    }
                }))))
            .ToArray();

        await Task.WhenAll(requests).WaitAsync(Patience);
    }

    /// <summary>Awaits a run, treating "you were superseded" as a normal outcome.</summary>
    private static async Task Settled(Task run)
    {
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A stop invalidates leases immediately, so work still unwinding cannot write to the
    /// engine the next start builds.
    /// </summary>
    [Fact]
    public async Task WorkFromTheStoppedSessionCannotWriteToTheEngineThatReplacesIt()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var old = coordinator.RunAsync(
            StrategyWorkKind.Automatic,
            "eski oturum",
            async (lease, _) =>
            {
                reached.SetResult();
                await release.Task.ConfigureAwait(false);
                lease.TryWrite(StrategyLibrary.FakeTtl8Split2);
            });

        await reached.Task.WaitAsync(Patience);

        // Stop, then start again - the quick stop/start the user does from the window.
        coordinator.EndSession();
        coordinator.BeginSession("network-a");
        Assert.True(coordinator.ApplyImmediate("yeni oturum", StrategyLibrary.SplitSni));

        release.SetResult();
        await old.WaitAsync(Patience);

        Assert.Equal(StrategyLibrary.SplitSni, engine.Strategy);
        Assert.DoesNotContain(StrategyLibrary.FakeTtl8Split2.Id, engine.Writes);
    }

    /// <summary>With no engine there is nothing to write to, and nothing is queued for later.</summary>
    [Fact]
    public void WithNoEngineAnImmediateApplyIsRefusedRatherThanRemembered()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);

        Assert.False(coordinator.ApplyImmediate("motor yok", StrategyLibrary.SplitSni));
        Assert.Equal(StrategyLibrary.Default, engine.Strategy);
    }

    /// <summary>
    /// Two runs must never be inside the work at the same time, however they arrived.
    /// </summary>
    [Fact]
    public async Task OnlyOneRunIsInsideTheWorkAtATime()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine, log: null);
        coordinator.BeginSession("network-a");

        var inside = 0;
        var overlapped = false;

        var runs = Enumerable.Range(0, 24)
            .Select(i => Task.Run(() => Settled(coordinator.RunAsync(
                StrategyWorkKind.Manual,
                $"elle {i}",
                async (_, _) =>
                {
                    if (Interlocked.Increment(ref inside) > 1)
                    {
                        overlapped = true;
                    }

                    await Task.Delay(1).ConfigureAwait(false);
                    Interlocked.Decrement(ref inside);
                }))))
            .ToArray();

        await Task.WhenAll(runs).WaitAsync(Patience);

        Assert.False(overlapped);
    }

    /// <summary>
    /// A run whose caller cancels does not leave the coordinator wedged for the next one.
    /// </summary>
    [Fact]
    public async Task ACancelledRunReleasesTheCoordinatorForTheNextOne()
    {
        var engine = new FakeEngine();
        using var coordinator = Coordinator(engine);
        coordinator.BeginSession("network-a");

        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cancelled = coordinator.RunAsync(
            StrategyWorkKind.Manual,
            "iptal edilecek",
            async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            },
            caller.Token);

        await started.Task.WaitAsync(Patience);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience));

        await coordinator.RunAsync(
            StrategyWorkKind.Manual,
            "sonraki",
            (lease, _) =>
            {
                Assert.True(lease.TryWrite(StrategyLibrary.Disorder2));
                return Task.CompletedTask;
            }).WaitAsync(Patience);

        Assert.Equal(StrategyLibrary.Disorder2, engine.Strategy);
    }
}

/// <summary>
/// What the sweep itself installs, and - just as importantly - what it puts back.
/// </summary>
public sealed class StrategySweepTests
{
    private sealed class ScriptedProbe : IConnectivityProbe
    {
        private readonly Func<int, ProbeResult> _answer;
        private int _calls;

        public ScriptedProbe(Func<int, ProbeResult> answer) => _answer = answer;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ProbeResult> ProbeAsync(string host, bool fetchHttp = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_answer(Interlocked.Increment(ref _calls)));
        }
    }

    private static ProbeResult Blocked => new(ProbeOutcome.HandshakeReset, TimeSpan.FromMilliseconds(20));

    private static ProbeResult Reachable(int ms) => new(ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// The writer a superseded sweep holds refuses every write, including the restore in
    /// its <c>finally</c> - which is the write that used to undo the live run's winner.
    /// </summary>
    [Fact]
    public async Task ASupersededSweepInstallsNothingAtAll()
    {
        var engine = new FakeEngineWriter { Strategy = StrategyLibrary.Disorder2 };
        var writer = new RevocableWriter(engine);
        var probe = new ScriptedProbe(_ => Blocked);
        var tuner = new StrategyTuner(probe);

        writer.Revoked = true;
        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown, checkUnfilteredFirst: true);

        Assert.Null(result.Winner);
        Assert.Equal(StrategyLibrary.Disorder2, engine.Strategy);
        Assert.Empty(engine.Writes);
        Assert.Equal(0, probe.Calls);
    }

    /// <summary>
    /// A sweep revoked half way through leaves the engine on whatever the live run has
    /// since installed, rather than on the candidate it happened to be measuring.
    /// </summary>
    [Fact]
    public async Task ASweepRevokedMidFlightDoesNotPutItsOwnCandidateBack()
    {
        var engine = new FakeEngineWriter { Strategy = StrategyLibrary.Default };
        var writer = new RevocableWriter(engine);
        var probe = new ScriptedProbe(call =>
        {
            if (call >= 2)
            {
                // The network changed while the second candidate was being measured.
                writer.Revoked = true;
            }

            return Blocked;
        });

        var tuner = new StrategyTuner(probe);
        await tuner.FindBestAsync(writer, IspCatalog.Unknown, checkUnfilteredFirst: true);

        // Whatever the live run installed in the meantime.
        engine.Strategy = StrategyLibrary.MultiSplitSni;
        Assert.DoesNotContain(StrategyLibrary.Passthrough.Id, engine.Writes.Skip(1));
        Assert.Equal(StrategyLibrary.MultiSplitSni, engine.Strategy);
    }

    /// <summary>
    /// A sweep that finds nothing puts back the strategy the session was already on.
    /// </summary>
    [Fact]
    public async Task ASweepThatFindsNothingRestoresWhatTheSessionWasUsing()
    {
        var engine = new FakeEngineWriter { Strategy = StrategyLibrary.FakeTtl6SplitSni };
        var writer = new RevocableWriter(engine);
        var tuner = new StrategyTuner(new ScriptedProbe(_ => Blocked));

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown, checkUnfilteredFirst: true);

        Assert.Null(result.Winner);
        Assert.Equal(StrategyLibrary.FakeTtl6SplitSni, engine.Strategy);
    }

    /// <summary>A cancelled sweep restores too, rather than leaving the last candidate on.</summary>
    [Fact]
    public async Task ACancelledSweepRestoresWhatTheSessionWasUsing()
    {
        var engine = new FakeEngineWriter { Strategy = StrategyLibrary.FakeBadSeqSplitSni };
        var writer = new RevocableWriter(engine);
        using var cancellation = new CancellationTokenSource();
        var tuner = new StrategyTuner(new ScriptedProbe(call =>
        {
            if (call >= 2)
            {
                cancellation.Cancel();
            }

            return Blocked;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tuner.FindBestAsync(writer, IspCatalog.Unknown, checkUnfilteredFirst: true, cancellation.Token));

        Assert.Equal(StrategyLibrary.FakeBadSeqSplitSni, engine.Strategy);
    }

    /// <summary>The fastest of the candidates that worked wins, and it is what stays installed.</summary>
    [Fact]
    public async Task TheFastestWorkingCandidateIsTheOneLeftInstalled()
    {
        var engine = new FakeEngineWriter { Strategy = StrategyLibrary.Default };
        var writer = new RevocableWriter(engine);

        // Both control attempts fail - the network really is filtering - and then each
        // candidate succeeds first time with a different cost.
        var latencies = new[] { 90, 40, 70 };
        var tuner = new StrategyTuner(new ScriptedProbe(call => call <= 2
            ? Blocked
            : Reachable(latencies[Math.Min(call - 3, latencies.Length - 1)])));

        var result = await tuner.FindBestAsync(writer, IspCatalog.Unknown, checkUnfilteredFirst: true);

        Assert.NotNull(result.Winner);
        Assert.Equal(result.Winner, engine.Strategy);
        Assert.Equal(40, result.Trials.Where(t => t.Success).Min(t => t.Result.Elapsed).TotalMilliseconds);
    }

    private sealed class FakeEngineWriter
    {
        private readonly List<string> _writes = [];

        public BypassStrategy Strategy { get; set; } = StrategyLibrary.Default;

        public IReadOnlyList<string> Writes => _writes;

        public void Write(BypassStrategy strategy)
        {
            Strategy = strategy;
            _writes.Add(strategy.Id);
        }
    }

    private sealed class RevocableWriter : IStrategyWriter
    {
        private readonly FakeEngineWriter _engine;

        public RevocableWriter(FakeEngineWriter engine) => _engine = engine;

        public bool Revoked { get; set; }

        public BypassStrategy Current => _engine.Strategy;

        public bool IsCurrent => !Revoked;

        public bool TryWrite(BypassStrategy strategy)
        {
            if (Revoked)
            {
                return false;
            }

            _engine.Write(strategy);
            return true;
        }
    }
}
