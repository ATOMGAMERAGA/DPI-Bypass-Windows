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
    public const int MaxRequestBytes = 16 * 1024;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The pipe the running instance listens on.
    /// </summary>
    /// <remarks>
    /// Local only. The server also restricts request size and deadlines because
    /// pipe clients may be buggy or hostile even on the same machine.
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

        /// <summary>Read-only checks on a tethered or mobile connection.</summary>
        public const string HotspotDiagnose = "hotspot.diagnose";

        public const string HotspotStatus = "hotspot.status";

        /// <summary>Removes anything an older build's hotspot TTL mode left behind.</summary>
        public const string HotspotCleanup = "hotspot.cleanup";

        // Compatibility names retained from the original Vodafone feature. They now
        // control the safe diagnostics/compatibility mode; none installs the retired TTL
        // rewrite. The generic hotspot commands remain aliases for diagnostic operations.
        public const string VodafoneOn = "vodafone.on";
        public const string VodafoneOff = "vodafone.off";
        public const string VodafoneStatus = "vodafone.status";
        public const string LatencyOn = "latency.on";
        public const string LatencyOff = "latency.off";
        public const string LatencyStatus = "latency.status";
        public const string LatencyRestore = "latency.restore";

        /// <summary>The full status as stable JSON, for scripts and support requests.</summary>
        public const string LatencyStatusJson = "latency.status.json";

        /// <summary>Idle latency to the chosen target; changes nothing.</summary>
        public const string LatencyQuickTest = "latency.quick";

        /// <summary>Latency while the user's own transfer is running, plus Traffic Guard.</summary>
        public const string LatencyDeepTest = "latency.deep";

        /// <summary>Measure again from scratch, ignoring the saved answer.</summary>
        public const string LatencyRetest = "latency.retest";

        /// <summary>The last full report, exactly as the status panel shows it.</summary>
        public const string LatencyReport = "latency.report";

        /// <summary>Forget every saved per-network latency result.</summary>
        public const string LatencyProfilesClear = "latency.profiles.clear";

        /// <summary>Set the measurement target; the argument is host, host:port or a URI form.</summary>
        public const string LatencyTarget = "latency.target";
        public const string Domains = "domains";
    }
}
