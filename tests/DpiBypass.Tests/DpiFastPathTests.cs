using DpiBypass.Core.Engine;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The architectural line between the DPI engine and everything else.
/// </summary>
/// <remarks>
/// <para>
/// The engine only ever sees the first data packet of a TCP connection, and only when it
/// is a TLS ClientHello or a plaintext HTTP request head. Everything else - every ACK,
/// every byte of payload after the handshake, all ordinary UDP, all ICMP - is never
/// copied to user mode at all, because the kernel filter excludes it. That is what makes
/// ping, voice and download throughput unaffected by this app being on.
/// </para>
/// <para>
/// It is also the property that quietly breaks first. Diverting a packet costs a copy
/// into user mode, a context switch, a queue and the risk of a drop, so a filter widened
/// for any reason - a feature that wants to see game traffic, a diagnostic that wants a
/// count - turns the latency feature into the thing making latency worse. These tests
/// exist so that widening has to be deliberate.
/// </para>
/// </remarks>
public sealed class DpiFastPathTests
{
    /// <summary>
    /// Subsystems that must never touch the packet path.
    /// </summary>
    /// <remarks>
    /// <c>Diagnostics</c> is deliberately absent: the strategy tuner and the blocked-site
    /// discovery are part of the DPI feature and drive the engine on purpose. These two
    /// are the ones that exist alongside it and must stay off it.
    /// </remarks>
    private static readonly string[] NonDivertingSubsystems = ["Network", "MobileHotspot"];

    private static readonly string[] DivertMarkers = ["WinDivert", "BypassEngine", "TcpFilterLadder"];

    /// <summary>
    /// The one file outside the engine allowed to name WinDivert, and why.
    /// </summary>
    /// <remarks>
    /// Discovering which server a UDP game is talking to has no other answer on Windows:
    /// the UDP table reports a socket's local address and nothing else. WinDivert's FLOW
    /// layer reports the five-tuple and the process id for flows as they are created, and
    /// the documentation requires that layer to be opened <c>SNIFF | RECV_ONLY</c> - a
    /// handle that cannot block, modify or inject anything, on a layer that carries events
    /// rather than packets. The packet path is untouched, which
    /// <see cref="TheFlowObserverOnlyEverListens"/> holds it to line by line.
    /// </remarks>
    private const string FlowObserverFile = "ProcessFlowObserver.cs";

    [Fact]
    public void TheLatencyAndHotspotSubsystemsCannotDivertAPacket()
    {
        var offenders = new List<string>();

        foreach (var subsystem in NonDivertingSubsystems)
        {
            var directory = Path.Combine(RepoFiles.CoreProjectDirectory, subsystem);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                if (Path.GetFileName(file) == FlowObserverFile)
                {
                    continue;
                }

                foreach (var marker in DivertMarkers)
                {
                    // A comment saying the subsystem does not divert is the point, not a
                    // violation of it; only real code is judged.
                    if (CodeLines(text).Any(line => line.Contains(marker, StringComparison.Ordinal)))
                    {
                        offenders.Add($"{subsystem}/{Path.GetFileName(file)}: {marker}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The flow observer listens and can do nothing else, enforced line by line.
    /// </summary>
    /// <remarks>
    /// It is the only thing outside the engine holding a WinDivert handle, so the rules
    /// that keep it off the packet path are checked rather than trusted: the FLOW layer
    /// and no other, sniffing and receiving and nothing else, and no send path at all.
    /// A handle opened any other way could drop a game's packets.
    /// </remarks>
    [Fact]
    public void TheFlowObserverOnlyEverListens()
    {
        var file = Directory
            .EnumerateFiles(RepoFiles.CoreProjectDirectory, FlowObserverFile, SearchOption.AllDirectories)
            .Single();

        var code = CodeLines(File.ReadAllText(file)).ToArray();
        var opens = code.Where(line => line.Contains("WinDivertHandle.Open", StringComparison.Ordinal)).ToArray();

        var open = Assert.Single(opens);
        Assert.Contains("WinDivertLayer.Flow", open, StringComparison.Ordinal);
        Assert.Contains("WinDivertFlags.Sniff | WinDivertFlags.RecvOnly", open, StringComparison.Ordinal);

        Assert.DoesNotContain(code, line => line.Contains("WinDivertLayer.Network", StringComparison.Ordinal));
        Assert.DoesNotContain(code, line => line.Contains("WinDivertFlags.Drop", StringComparison.Ordinal));
        Assert.DoesNotContain(code, line => line.Contains(".Send(", StringComparison.Ordinal));
        Assert.DoesNotContain(code, line => line.Contains("CalculateChecksums", StringComparison.Ordinal));
    }

    /// <summary>
    /// Four handles, and the reason for each one. A fifth means somebody put another
    /// subsystem on the packet path.
    /// </summary>
    /// <summary>
    /// The list of things allowed to hold a WinDivert handle, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HotspotTtlFix</c> is on it deliberately, and it is the one entry that costs
    /// something. Rewriting the TTL of outgoing packets cannot be done from user mode
    /// without seeing them, so while Vodafone Sınırsız Modu is active every outbound
    /// packet on that one adapter is copied to user mode and back - which is why the rule
    /// is scoped to a single adapter index and to networks the user registered by hand,
    /// and why it comes down the moment either stops being true.
    /// </para>
    /// <para>
    /// It does not touch the engine's fast path: it is a separate handle at a lower
    /// priority with its own filter, and the guard keeps it away from the low-TTL decoy
    /// packets the strategies depend on.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyFourNamedSubsystemsOpenADivertHandle()
    {
        var callers = new List<string>();

        foreach (var file in Directory.EnumerateFiles(RepoFiles.CoreProjectDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file) == "WinDivertHandle.cs")
            {
                continue;
            }

            callers.AddRange(CodeLines(File.ReadAllText(file))
                .Where(line => line.Contains("WinDivertHandle.Open", StringComparison.Ordinal))
                .Select(_ => Path.GetFileName(file)));
        }

        Assert.Equal(
            [
                "BypassEngine.cs",
                "BypassEngine.cs",
                "HotspotTtlFix.cs",
                "ProcessFlowObserver.cs",
                "ProcessPortMap.cs",
            ],
            callers.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NoKernelFilterEverMatchesIcmp()
        => Assert.All(AllFilters(), filter => Assert.DoesNotContain("icmp", filter, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheTcpFilterLadderNeverMatchesUdp()
        => Assert.All(
            BypassEngine.TcpFilterLadder,
            filter => Assert.DoesNotContain("udp", filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every filter is outbound and destination-port bound, so inbound traffic and every
    /// port a game actually uses are never even looked at.
    /// </summary>
    [Fact]
    public void EveryFilterIsOutboundAndPortBound()
    {
        Assert.All(AllFilters(), filter =>
        {
            Assert.StartsWith("outbound and", filter, StringComparison.Ordinal);
            Assert.Contains("DstPort == 443", filter, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Ordinary UDP is what games use. The only UDP the engine looks at is a QUIC
    /// Initial on 443, identified by the first payload byte in the kernel, so an
    /// established QUIC session - and every game protocol - is never diverted.
    /// </summary>
    [Fact]
    public void OnlyQuicInitialPacketsAreTakenFromTheUdpPath()
    {
        var quic = BypassEngine.QuicFilterLadder[0];

        Assert.Contains("udp.DstPort == 443", quic, StringComparison.Ordinal);
        Assert.Contains("udp.PayloadLength > 32", quic, StringComparison.Ordinal);

        // Long header, Initial type: 0xC0-0xCF for QUIC v1 and 0xD0-0xDF for v2. A short
        // header (0x40-0x7F), which is what an established session sends, cannot match.
        Assert.Contains("udp.Payload[0] >= 0xC0", quic, StringComparison.Ordinal);
        Assert.Contains("udp.Payload[0] <= 0xDF", quic, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ACK carries no payload, and the narrow filter needs payload bytes before it
    /// matches anything, so a pure ACK never leaves the kernel.
    /// </summary>
    [Fact]
    public void AnAckWithoutPayloadCannotMatchAnyTcpFilter()
        => Assert.All(
            BypassEngine.TcpFilterLadder,
            filter => Assert.Contains("tcp.PayloadLength", filter, StringComparison.Ordinal));

    /// <summary>
    /// The preferred filter goes further: payload alone is not enough, the first bytes
    /// have to look like a handshake, so established HTTP and HTTPS payload stays in the
    /// kernel too.
    /// </summary>
    [Fact]
    public void EstablishedPayloadOnlyReachesUserModeOnTheCompatibilityFallbacks()
    {
        var ladder = BypassEngine.TcpFilterLadder;

        Assert.Contains("tcp.Payload[0] == 0x16", ladder[0], StringComparison.Ordinal);
        Assert.Contains("tcp.Payload[5] == 0x01", ladder[0], StringComparison.Ordinal);

        // The fallbacks exist only for WinDivert builds that reject payload indexing, so
        // they are allowed to be broader - but they must stay last.
        Assert.All(ladder.Skip(1), filter => Assert.DoesNotContain("tcp.Payload[", filter, StringComparison.Ordinal));
    }

    private static IEnumerable<string> AllFilters()
        => BypassEngine.TcpFilterLadder.Concat(BypassEngine.QuicFilterLadder);

    /// <summary>Source lines with comments and string-free directives stripped.</summary>
    private static IEnumerable<string> CodeLines(string text) => text
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
            && !line.StartsWith("///", StringComparison.Ordinal)
            && !line.StartsWith("*", StringComparison.Ordinal)
            && !line.StartsWith("/*", StringComparison.Ordinal));
}
