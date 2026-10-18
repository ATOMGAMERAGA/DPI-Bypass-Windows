using System.Reflection;
using System.Text.Json;
using DpiBypass.Core;
using DpiBypass.Core.Config;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

public sealed class ProtectionBrokerDispatcherTests
{
    [Theory]
    [InlineData("GetType")]
    [InlineData("MemberwiseClone")]
    [InlineData("DisposeAsync")]
    [InlineData("get_Settings")]
    [InlineData("add_Changed")]
    [InlineData("SocialExperiments")]
    [InlineData("UnknownMethod")]
    public async Task OnlyFixedInterfaceOperationsAreCallable(string method)
    {
        var (controller, fake) = ControllerProxy.Create();
        using var dispatcher = new ProtectionBrokerDispatcher(controller);
        var result = await dispatcher.HandleAsync(Request(method), default);
        Assert.False(result.Ok);
        Assert.Equal("UNKNOWN_OPERATION", result.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task ArgumentsAreExactTypedAndSettingsOnlyAccompanySave()
    {
        var (controller, fake) = ControllerProxy.Create();
        using var dispatcher = new ProtectionBrokerDispatcher(controller);
        Assert.Equal("INVALID_ARGUMENTS", (await dispatcher.HandleAsync(Request(nameof(controller.ApplyScope)), default)).Error);
        Assert.Equal("INVALID_ARGUMENTS", (await dispatcher.HandleAsync(Request(nameof(controller.ApplyScope), 42), default)).Error);
        Assert.Equal("INVALID_ARGUMENTS", (await dispatcher.HandleAsync(Request(nameof(controller.ApplyScope), "UnknownScope"), default)).Error);
        Assert.Equal("SETTINGS_NOT_ALLOWED", (await dispatcher.HandleAsync(Request("__snapshot") with { Settings = new() }, default)).Error);
        Assert.Empty(fake.Calls);

        var originalSettings = controller.Settings;
        originalSettings.Networks["helper-network"] = new NetworkProfile { Key = "helper-network", StrategyId = StrategyLibrary.Split2.Id };
        var incoming = new AppSettings { StartMinimised = false, ManualStrategyId = StrategyLibrary.Split2.Id };
        var saved = await dispatcher.HandleAsync(Request(nameof(controller.SaveSettings)) with { Settings = incoming }, default);
        Assert.True(saved.Ok);
        Assert.Same(originalSettings, controller.Settings);
        Assert.False(controller.Settings.StartMinimised);
        Assert.Equal(StrategyLibrary.Split2.Id, controller.Settings.ManualStrategyId);
        Assert.True(controller.Settings.Networks.ContainsKey("helper-network"));
        incoming.ExtraDomains.Add("later.example");
        Assert.DoesNotContain("later.example", controller.Settings.ExtraDomains);
        Assert.Equal([nameof(controller.SaveSettings)], fake.Calls);
    }

    [Fact]
    public async Task CancellationBypassesEightOccupiedRequestSlots()
    {
        var (controller, fake) = ControllerProxy.Create();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fake.Handlers[nameof(controller.ProbeAsync)] = arguments => WaitForCancellation((CancellationToken)arguments[1]!);
        async Task<ProbeResult> WaitForCancellation(CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == 8) allStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        }
        using var dispatcher = new ProtectionBrokerDispatcher(controller);
        var requests = Enumerable.Range(0, 8).Select(_ => Request(nameof(controller.ProbeAsync), "fixture.example")).ToArray();
        var pending = requests.Select(request => dispatcher.HandleAsync(request, default)).ToArray();
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("HELPER_BUSY", (await dispatcher.HandleAsync(Request(nameof(controller.ProbeAsync), "extra.example"), default)).Error);
        Assert.True((await dispatcher.HandleAsync(Request("__snapshot"), default)).Ok);
        foreach (var request in requests)
            Assert.True((await dispatcher.HandleAsync(Request("__cancel", request.Id), default)).Ok);
        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal("REQUEST_CANCELLED", result.Error));
    }

    [Fact]
    public async Task CancellationArrivingBeforeOriginalRequestPreventsInvocation()
    {
        var (controller, fake) = ControllerProxy.Create();
        using var dispatcher = new ProtectionBrokerDispatcher(controller);
        var original = Request(nameof(controller.ProbeAsync), "fixture.example");
        Assert.True((await dispatcher.HandleAsync(Request("__cancel", original.Id), default)).Ok);
        var response = await dispatcher.HandleAsync(original, default);
        Assert.False(response.Ok);
        Assert.Equal("REQUEST_CANCELLED", response.Error);
        Assert.Empty(fake.Calls);
        Assert.DoesNotContain(original.Id, JsonSerializer.Serialize(response, ProtectionWireJson.Options));
    }

    [Fact]
    public async Task SnapshotAndBoundedEventsRoundTripAndDisposeUnsubscribes()
    {
        var (controller, fake) = ControllerProxy.Create();
        var stats = new EngineStatistics { LastHost = "fixture.example", StartedAt = DateTimeOffset.UtcNow };
        stats.AddSegments(7);
        fake.Properties[nameof(controller.Stats)] = stats;
        using var dispatcher = new ProtectionBrokerDispatcher(controller);
        for (var i = 0; i < 600; i++) fake.Raise(nameof(controller.DomainLearned), "fixture" + i + ".example");
        var response = await dispatcher.HandleAsync(Request("__snapshot"), default);
        var restored = JsonSerializer.Deserialize<ProtectionResponse>(
            JsonSerializer.Serialize(response, ProtectionWireJson.Options), ProtectionWireJson.Options)!;
        Assert.True(restored.Ok);
        Assert.Equal(512, restored.Events.Length);
        Assert.Equal(7, restored.Snapshot!.Stats!.SegmentsSent);
        Assert.Equal("fixture88.example", restored.Events[0].Arguments[0].GetString());
        Assert.True(restored.Events.Zip(restored.Events.Skip(1), (a, b) => a.Sequence < b.Sequence).All(value => value));
        Assert.True(restored.Snapshot.Sequence > restored.Events[^1].Sequence);
        var next = await dispatcher.HandleAsync(Request("__snapshot") with { AfterEvent = restored.Snapshot.Sequence }, default);
        Assert.Empty(next.Events);
        Assert.True(next.Snapshot!.Sequence > restored.Snapshot.Sequence);
        dispatcher.Dispose();
        Assert.True(fake.Events.Values.All(handler => handler is null));
    }

    [Fact]
    public async Task ImplementationExceptionsNeverReturnPathsOrNetworkTargets()
    {
        var (controller, fake) = ControllerProxy.Create();
        fake.Handlers[nameof(controller.SaveSettings)] = _ => throw new IOException("secret C:/private/file https://example.test/");
        using var dispatcher = new ProtectionBrokerDispatcher(controller);
        var response = await dispatcher.HandleAsync(Request(nameof(controller.SaveSettings)), default);
        Assert.False(response.Ok);
        Assert.Equal("OPERATION_FAILED", response.Error);
    }

    private static ProtectionRequest Request(string method, params object[] arguments) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Method = method,
        Arguments = arguments.Select(argument => JsonSerializer.SerializeToElement(argument, argument.GetType(), ProtectionWireJson.Options)).ToArray(),
    };

    public class ControllerProxy : DispatchProxy
    {
        public Dictionary<string, object?> Properties { get; } = new()
        {
            [nameof(IProtectionController.Settings)] = new AppSettings(),
            [nameof(IProtectionController.Network)] = new NetworkFingerprint(),
            [nameof(IProtectionController.Isp)] = IspCatalog.Unknown,
            [nameof(IProtectionController.Strategy)] = StrategyLibrary.Default,
            [nameof(IProtectionController.LearnedDomains)] = Array.Empty<string>(),
        };
        public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new();
        public Dictionary<string, Delegate?> Events { get; } = new();
        public List<string> Calls { get; } = [];

        public static (IProtectionController Controller, ControllerProxy Fake) Create()
        {
            var controller = DispatchProxy.Create<IProtectionController, ControllerProxy>();
            return (controller, (ControllerProxy)controller);
        }

        public void Raise(string name, params object?[] arguments)
        {
            if (Events.TryGetValue(name, out var handler)) handler?.DynamicInvoke(arguments);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments)
        {
            var method = targetMethod!;
            var args = arguments ?? [];
            if (method.Name.StartsWith("get_", StringComparison.Ordinal))
                return Properties.GetValueOrDefault(method.Name[4..])
                    ?? (method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null);
            if (method.Name.StartsWith("add_", StringComparison.Ordinal))
            {
                var name = method.Name[4..]; Events[name] = Delegate.Combine(Events.GetValueOrDefault(name), (Delegate?)args[0]); return null;
            }
            if (method.Name.StartsWith("remove_", StringComparison.Ordinal))
            {
                var name = method.Name[7..]; Events[name] = Delegate.Remove(Events.GetValueOrDefault(name), (Delegate?)args[0]); return null;
            }
            // These read-only projections are now included in every GUI snapshot.
            // Keep Calls as the record of requested operations rather than reads.
            if (!Handlers.ContainsKey(method.Name))
            {
                if (method.Name == nameof(IProtectionController.ProtectedDomains)) return Array.Empty<string>();
                if (method.Name == nameof(IProtectionController.DescribeLunarAdBlock)) return new LunarAdBlockState(false, false, false, 0, "");
            }
            lock (Calls) Calls.Add(method.Name);
            return Handlers.TryGetValue(method.Name, out var handler) ? handler(args) : null;
        }
    }
}
