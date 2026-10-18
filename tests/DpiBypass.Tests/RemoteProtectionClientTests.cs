using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Ipc;
using Xunit;

namespace DpiBypass.Tests;

public sealed class RemoteProtectionClientTests
{
    [Fact]
    public async Task RealFacadeKeepsSettingsIdentityAndUnsavedEditsWhileImportingHelperState()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var harness = new ClientHarness();
        var settings = new AppSettings { StartMinimised = false };
        await using var client = harness.Client(settings);
        settings.ManualStrategyId = StrategyLibrary.Split2.Id; // edited after baseline, before first connection
        harness.Controller.Settings.StartMinimised = true;
        var stats = new EngineStatistics { LastHost = "fixture.example" }; stats.AddSegments(5);
        harness.Fake.Properties[nameof(IProtectionController.Stats)] = stats;
        harness.Fake.Properties[nameof(IProtectionController.State)] = ProtectionState.Running;

        await client.ProbeAsync("fixture.example");

        Assert.Same(settings, client.Settings);
        Assert.True(client.Settings.StartMinimised);
        Assert.Equal(StrategyLibrary.Split2.Id, client.Settings.ManualStrategyId);
        Assert.Equal(5, client.Stats!.SegmentsSent);
        Assert.Equal("fixture.example", client.Stats.LastHost);
        Assert.Equal(ProtectionState.Running, client.State);
        var socialPreferences = client.Settings.SocialAccess;
        client.SaveSettings();
        await client.ProbeAsync("fixture.example"); // subsequent commands await the queued save
        Assert.Same(settings, client.Settings);
        Assert.Same(socialPreferences, client.Settings.SocialAccess);
        Assert.Equal(StrategyLibrary.Split2.Id, harness.Controller.Settings.ManualStrategyId);
    }

    [Fact]
    public async Task RealFacadeRoutesMutationAndDeliversEachDomainEventOnce()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var harness = new ClientHarness();
        harness.Fake.Handlers[nameof(IProtectionController.ApplyQuicSetting)] = arguments =>
        {
            harness.Controller.Settings.BlockQuicHandshakes = (bool)arguments[0]!;
            harness.Fake.Raise(nameof(IProtectionController.Changed));
            return null;
        };
        harness.Fake.Handlers[nameof(IProtectionController.AddDomain)] = arguments =>
        {
            var host = (string)arguments[0]!;
            harness.Controller.Settings.ExtraDomains.Add(host);
            harness.Fake.Raise(nameof(IProtectionController.DomainLearned), host);
            return true;
        };
        harness.Fake.Handlers[nameof(IProtectionController.VerifyAllAsync)] = _ =>
            Task.FromResult<IReadOnlyList<(string Host, ProbeResult Result)>>(
                [("fixture.example", new(ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(3)))]);
        await using var client = harness.Client(new AppSettings());
        var domains = new List<string>();
        client.DomainLearned += domain => { lock (domains) domains.Add(domain); };

        client.ApplyQuicSetting(false);
        Assert.False(client.Settings.BlockQuicHandshakes);
        Assert.True(client.AddDomain("added.example"));
        var results = await client.VerifyAllAsync();
        await client.ProbeAsync("fixture.example"); // overlapping snapshot/event replies must not replay it

        Assert.Equal("fixture.example", results[0].Host);
        Assert.Equal(ProbeOutcome.Reachable, results[0].Result.Outcome);
        lock (domains) Assert.Equal(["added.example"], domains);
        Assert.Contains("added.example", harness.Controller.Settings.ExtraDomains);
        lock (harness.Fake.Calls)
        {
            Assert.Contains(nameof(IProtectionController.ApplyQuicSetting), harness.Fake.Calls);
            Assert.Contains(nameof(IProtectionController.AddDomain), harness.Fake.Calls);
        }
    }

    [Fact]
    public async Task ConnectionFailureIsReportedWithoutConstructingLocalProtection()
    {
        var attempts = 0;
        var settings = new AppSettings();
        await using var client = new RemoteProtectionClient(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<ProtectionRpcClient>(new InvalidOperationException("FIXTURE_HELPER_UNAVAILABLE"));
        }, settings);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync());
        Assert.Equal("FIXTURE_HELPER_UNAVAILABLE", error.Message);
        Assert.Equal(1, attempts);
        Assert.Equal(ProtectionState.Stopped, client.State);
        Assert.Null(client.Stats);
        Assert.Same(settings, client.Settings);
    }

    [Fact]
    public async Task CancellationFromFacadeReachesRunningHelperOperation()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var harness = new ClientHarness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Fake.Handlers[nameof(IProtectionController.ProbeAsync)] = arguments => WaitForCancellation((CancellationToken)arguments[1]!);
        async Task<ProbeResult> WaitForCancellation(CancellationToken token)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("Unreachable");
        }
        await using var client = harness.Client(new AppSettings());
        using var requestCancellation = new CancellationTokenSource();
        var operation = client.ProbeAsync("fixture.example", requestCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueuedAppearanceSavePreservesProfilesLearnedByHelperWhileConnecting()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var harness = new ClientHarness();
        var connectionRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new AppSettings();
        await using var client = new RemoteProtectionClient(async token =>
        {
            connectionRequested.TrySetResult();
            await allowConnection.Task.WaitAsync(token);
            return harness.Rpc();
        }, settings);
        settings.Appearance = AppearanceMode.Mica;
        client.SaveSettings();
        await connectionRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Controller.Settings.Networks["new-network"] = new NetworkProfile
        {
            Key = "new-network", StrategyId = StrategyLibrary.Split2.Id,
        };
        allowConnection.TrySetResult();
        await client.ProbeAsync("fixture.example");
        Assert.Equal(AppearanceMode.Mica, harness.Controller.Settings.Appearance);
        Assert.True(harness.Controller.Settings.Networks.ContainsKey("new-network"));
    }

    private sealed class ClientHarness : IAsyncDisposable
    {
        public IProtectionController Controller { get; }
        public ProtectionBrokerDispatcherTests.ControllerProxy Fake { get; }
        private readonly ProtectionBrokerDispatcher _dispatcher;
        private readonly ProtectionPipeServer _server;
        private readonly string _name = "DpiBypass.Engine." + Guid.NewGuid().ToString("N");
        private readonly ProtectionPeer _peer = new(Environment.ProcessId, Environment.ProcessPath!);

        public ClientHarness()
        {
            (Controller, Fake) = ProtectionBrokerDispatcherTests.ControllerProxy.Create();
            Fake.Handlers[nameof(IProtectionController.ProbeAsync)] = _ =>
                Task.FromResult(new ProbeResult(ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(2)));
            _dispatcher = new ProtectionBrokerDispatcher(Controller);
            _server = new ProtectionPipeServer(_name, _peer, _dispatcher.HandleAsync);
            _server.Start();
        }

        public RemoteProtectionClient Client(AppSettings settings)
            => new(_ => Task.FromResult(Rpc()), settings);
        public ProtectionRpcClient Rpc() => new(_name, _peer, false);

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync();
            _dispatcher.Dispose();
        }
    }
}
