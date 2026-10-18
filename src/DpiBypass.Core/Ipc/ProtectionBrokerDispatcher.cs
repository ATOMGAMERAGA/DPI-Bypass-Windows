using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Config;
using DpiBypass.Core.Network;

namespace DpiBypass.Core.Ipc;

/// <summary>Dispatches the fixed desktop contract; no implementation methods are exposed.</summary>
public sealed class ProtectionBrokerDispatcher : IDisposable
{
    private const int EventCapacity = 512;
    private static readonly JsonElement NullResult = JsonSerializer.SerializeToElement<object?>(null, ProtectionWireJson.Options);
    private static readonly IReadOnlyDictionary<string, MethodInfo[]> Operations = typeof(IProtectionController)
        .GetMethods().Where(method => !method.IsSpecialName && method.Name != nameof(IAsyncDisposable.DisposeAsync))
        .GroupBy(method => method.Name, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    private static readonly PropertyInfo[] SettingsProperties = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.GetIndexParameters().Length == 0
            && property.GetCustomAttribute<JsonIgnoreAttribute>() is null
            && property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true).ToArray();
    private readonly IProtectionController _service;
    private readonly SemaphoreSlim _requests = new(8, 8);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly Lock _cancellationGate = new();
    private readonly Dictionary<string, long> _cancelled = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _eventGate = new();
    private readonly Lock _settingsGate = new();
    private readonly Queue<ProtectionEvent> _events = new();
    private long _sequence;
    private int _disposed;

    public ProtectionBrokerDispatcher(IProtectionController service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        service.Changed += OnChanged;
        service.HostRewritten += OnHostRewritten;
        service.TuningProgress += OnTuningProgress;
        service.DomainLearned += OnDomainLearned;
        service.LatencyStageChanged += OnLatencyStageChanged;
        service.SaveStatusChanged += OnSaveStatusChanged;
    }

    public async Task<ProtectionResponse> HandleAsync(ProtectionRequest request, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return Failure("HELPER_STOPPED");
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128
            || request.AfterEvent < 0 || request.Arguments is null || request.Method is null)
            return Failure("INVALID_REQUEST");
        if (request.Settings is not null && request.Method != nameof(IProtectionController.SaveSettings))
            return Failure("SETTINGS_NOT_ALLOWED");

        // Cancellation and snapshots never wait behind a long-running operation.
        if (request.Method == "__cancel")
        {
            if (request.Arguments.Length != 1 || request.Arguments[0].ValueKind != JsonValueKind.String
                || request.Arguments[0].GetString() is not { Length: > 0 and <= 128 } target)
                return Failure("INVALID_ARGUMENTS");
            CancellationTokenSource? active;
            lock (_cancellationGate)
            {
                TrimCancelledRequests();
                if (!_running.TryGetValue(target, out active))
                {
                    _cancelled[target] = Environment.TickCount64 + 60_000;
                    if (_cancelled.Count > 256) _cancelled.Remove(_cancelled.MinBy(item => item.Value).Key);
                }
            }
            if (active is not null)
            {
                try { await active.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
            }
            return Respond(true, null, NullResult, request.AfterEvent);
        }
        if (request.Method == "__snapshot")
            return request.Arguments.Length == 0
                ? Respond(true, null, NullResult, request.AfterEvent) : Failure("INVALID_ARGUMENTS");

        if (!Operations.TryGetValue(request.Method, out var candidates)) return Failure("UNKNOWN_OPERATION");
        var matches = candidates.Where(method => method.GetParameters().Count(parameter =>
            parameter.ParameterType != typeof(CancellationToken)) == request.Arguments.Length).ToArray();
        if (matches.Length != 1) return Failure("INVALID_ARGUMENTS");
        if (!await _requests.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return Failure("HELPER_BUSY");

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        lifetime.CancelAfter(ProtectionProtocol.RequestDeadline);
        var registered = false;
        try
        {
            bool cancelled;
            lock (_cancellationGate)
            {
                TrimCancelledRequests();
                cancelled = _cancelled.Remove(request.Id);
                if (!cancelled && !(registered = _running.TryAdd(request.Id, lifetime))) return Failure("DUPLICATE_REQUEST");
            }
            if (cancelled) return Respond(false, "REQUEST_CANCELLED", NullResult, request.AfterEvent);
            lifetime.Token.ThrowIfCancellationRequested();
            var method = matches[0];
            var parameters = method.GetParameters();
            var values = new object?[parameters.Length];
            var argumentIndex = 0;
            for (var i = 0; i < parameters.Length; i++)
                values[i] = parameters[i].ParameterType == typeof(CancellationToken) ? lifetime.Token
                    : request.Arguments[argumentIndex++].Deserialize(parameters[i].ParameterType, ProtectionWireJson.Options);

            object? result;
            if (request.Method == nameof(IProtectionController.SaveSettings))
            {
                lock (_settingsGate)
                {
                    if (request.Settings is not null) CopySettings(request.Settings, _service.Settings);
                    result = method.Invoke(_service, values);
                }
            }
            else result = method.Invoke(_service, values);

            if (result is Task task)
            {
                await task.WaitAsync(lifetime.Token).ConfigureAwait(false);
                result = method.ReturnType.IsGenericType
                    ? method.ReturnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task) : null;
            }
            var element = result is null ? NullResult
                : JsonSerializer.SerializeToElement(result, result.GetType(), ProtectionWireJson.Options);
            return Respond(true, null, element, request.AfterEvent);
        }
        catch (Exception exception)
        {
            var actual = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            return Respond(false, actual switch
            {
                OperationCanceledException => "REQUEST_CANCELLED",
                JsonException or ArgumentException => "INVALID_ARGUMENTS",
                UnauthorizedAccessException => "ACCESS_DENIED",
                _ => "OPERATION_FAILED",
            }, NullResult, request.AfterEvent);
        }
        finally
        {
            if (registered) _running.TryRemove(request.Id, out _);
            _requests.Release();
        }
    }

    private static void CopySettings(AppSettings source, AppSettings destination)
    {
        var clone = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source, ProtectionWireJson.Options), ProtectionWireJson.Options)
            ?? throw new JsonException("Settings are required.");
        foreach (var property in SettingsProperties) property.SetValue(destination, property.GetValue(clone));
    }

    private void TrimCancelledRequests()
    {
        var now = Environment.TickCount64;
        foreach (var id in _cancelled.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
            _cancelled.Remove(id);
    }

    private ProtectionResponse Respond(bool ok, string? error, JsonElement result, long afterEvent)
    {
        lock (_eventGate)
        {
            return new ProtectionResponse
            {
                Ok = ok, Error = error, Result = result,
                Snapshot = ProtectionSnapshot.Capture(_service, ++_sequence),
                Events = _events.Where(item => item.Sequence > afterEvent).ToArray(),
            };
        }
    }

    private static ProtectionResponse Failure(string error)
        => new() { Ok = false, Error = error, Result = NullResult, Events = [] };

    private void Enqueue(string name, params object?[] arguments)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var values = arguments.Select(argument => argument is null ? NullResult
            : JsonSerializer.SerializeToElement(argument, argument.GetType(), ProtectionWireJson.Options)).ToArray();
        lock (_eventGate)
        {
            _events.Enqueue(new ProtectionEvent(++_sequence, name, values));
            while (_events.Count > EventCapacity) _events.Dequeue();
        }
    }

    private void OnChanged() => Enqueue(nameof(IProtectionController.Changed));
    private void OnHostRewritten(string host, string strategy) => Enqueue(nameof(IProtectionController.HostRewritten), host, strategy);
    private void OnTuningProgress(string strategy, int current, int total) => Enqueue(nameof(IProtectionController.TuningProgress), strategy, current, total);
    private void OnDomainLearned(string host) => Enqueue(nameof(IProtectionController.DomainLearned), host);
    private void OnLatencyStageChanged(LoadedLaneProgress progress) => Enqueue(nameof(IProtectionController.LatencyStageChanged), progress);
    private void OnSaveStatusChanged(ConfigSaveResult? result) => Enqueue(nameof(IProtectionController.SaveStatusChanged), result);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _service.Changed -= OnChanged;
        _service.HostRewritten -= OnHostRewritten;
        _service.TuningProgress -= OnTuningProgress;
        _service.DomainLearned -= OnDomainLearned;
        _service.LatencyStageChanged -= OnLatencyStageChanged;
        _service.SaveStatusChanged -= OnSaveStatusChanged;
        _stopping.Cancel();
        // Active calls retain their linked tokens until they finish. Do not dispose
        // the semaphore while their finally blocks still need to release it.
    }
}
