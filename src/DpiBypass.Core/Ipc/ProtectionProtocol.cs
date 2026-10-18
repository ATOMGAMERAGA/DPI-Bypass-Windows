using System.Text.Json;
using DpiBypass.Core.Config;

namespace DpiBypass.Core.Ipc;

public sealed record ProtectionRequest
{
    public required string Id { get; init; }
    public required string Method { get; init; }
    public JsonElement[] Arguments { get; init; } = [];
    public AppSettings? Settings { get; init; }
    public long AfterEvent { get; init; }
}
public sealed record ProtectionEvent(long Sequence, string Name, JsonElement[] Arguments);
public sealed record ProtectionResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public JsonElement Result { get; init; } = JsonSerializer.SerializeToElement<object?>(null);
    public ProtectionSnapshot? Snapshot { get; init; }
    public ProtectionEvent[] Events { get; init; } = [];
}

public static class ProtectionProtocol
{
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    // The existing deep latency lane has a 30 minute total budget. Alpha keeps its
    // independent 20 second deadline; cancellation uses a separate concurrent call.
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromMinutes(35);
    public static bool ValidPipeName(string value) => value.StartsWith("DpiBypass.Engine.", StringComparison.Ordinal)
        && value.Length == 49 && value[17..].All(Uri.IsHexDigit);
}
