using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiBypass.Core.Ipc;

/// <summary>One command sent to the running instance.</summary>
public sealed record ControlRequest
{
    public required string Command { get; init; }

    public string? Argument { get; init; }
}

/// <summary>The answer. <see cref="Text"/> is what the command line prints.</summary>
public sealed record ControlResponse
{
    public bool Ok { get; init; }

    public string Text { get; init; } = string.Empty;

    public static ControlResponse Success(string text) => new() { Ok = true, Text = text };

    public static ControlResponse Failure(string text) => new() { Ok = false, Text = text };
}

public static class ControlProtocol
{
    /// <summary>
    /// The pipe the running instance listens on.
    /// </summary>
    /// <remarks>
    /// Local only, and both ends run elevated because the executable's manifest
    /// requires it - so no attempt is made to widen the pipe's default DACL. A
    /// non-administrator cannot start the client in the first place.
    /// </remarks>
    public const string PipeName = "DpiBypass.Control";

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Commands the running instance understands.</summary>
    public static class Commands
    {
        public const string Status = "status";
        public const string Test = "test";
        public const string Search = "search";
        public const string Enable = "enable";
        public const string Disable = "disable";
        public const string VodafoneOn = "vodafone.on";
        public const string VodafoneOff = "vodafone.off";
        public const string VodafoneStatus = "vodafone.status";
        public const string Domains = "domains";
    }
}
