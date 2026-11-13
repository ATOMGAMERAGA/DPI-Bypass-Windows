using System.Runtime.InteropServices;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Interop;
using DpiBypass.Core.Vodafone;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Every kernel filter the app builds, checked by WinDivert's own parser.
/// </summary>
/// <remarks>
/// A filter the driver rejects is a feature that silently does not start, and string
/// tests cannot tell. This runs wherever the WinDivert payload has been fetched next to
/// the repository (the Windows build does that before the tests) and is a no-op elsewhere.
/// </remarks>
public sealed class FilterSyntaxTests
{
    [Fact]
    public void EveryFilterCompilesInTheRealParser()
    {
        if (!OperatingSystem.IsWindows() || !TryLoadWinDivert())
        {
            return;
        }

        var settings = new[]
        {
            TtlFixSettings.Default,
            new TtlFixSettings { DropIPv6 = false },
        };

        var filters = BypassEngine.TcpFilterLadder
            .Concat(BypassEngine.QuicFilterLadder)
            .Concat(settings.SelectMany(s => new[]
            {
                HotspotTtlFix.BuildFilter(7, s),
                HotspotTtlFix.BuildFilter(7, s, includeMinecraft: false),
            }));

        foreach (var filter in filters)
        {
            Assert.True(
                WinDivertHandle.TryCompileFilter(filter, WinDivertLayer.Network, out var error),
                $"{error}: {filter}");
        }
    }

    /// <summary>
    /// The TTL rule's Minecraft clause, judged by WinDivert's own evaluator on real packets.
    /// </summary>
    /// <remarks>
    /// Everything the clause takes leaves the kernel path and comes back re-injected, and
    /// a packet re-injected by another driver is one a game booster's WFP routing does not
    /// handle. So a TTL-correct packet must match only when it really is a join handshake,
    /// not whenever byte 1 or 2 happens to be zero.
    /// </remarks>
    [Fact]
    public void TheMinecraftClauseTakesJoinHandshakesAndNothingElse()
    {
        if (!OperatingSystem.IsWindows() || !TryLoadWinDivert())
        {
            return;
        }

        var filter = HotspotTtlFix.BuildFilter(7, TtlFixSettings.Default);
        var ttl = TtlFixSettings.DefaultTimeToLive;

        // Short (one-byte length) and long (two-byte length) handshakes.
        Assert.True(Matches(filter, MinecraftJoin("play.example.test"), 25565, ttl));
        Assert.True(Matches(filter, MinecraftJoin(new string('a', 200)), 25565, ttl));

        // SMB / NetBIOS session header, and a big-endian length prefix.
        Assert.False(Matches(filter, [0x00, 0x00, 0x00, 0x54, 0xFE, 0x53, 0x4D, 0x42, 0x40, 0x00], 445, ttl));
        Assert.False(Matches(filter, [0x00, 0x00, 0x00, 0x20, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06], 7777, ttl));

        // TLS application data on a non-web port (Riot chat and similar).
        Assert.False(Matches(filter, [0x17, 0x03, 0x03, 0x00, 0x20, 1, 2, 3, 4, 5], 5223, ttl));

        // Anything at the wrong TTL is still taken, whatever it carries.
        Assert.True(Matches(filter, [0x00, 0x00, 0x00, 0x20, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06], 7777, 128));
    }

    private static bool Matches(string filter, byte[] payload, ushort port, byte ttl)
    {
        var packet = PacketFactory.BuildIPv4Tcp(payload, destinationPort: port, ttl: ttl);
        var address = new WinDivertAddress { Layer = WinDivertLayer.Network, Outbound = true, IfIdx = 7 };
        return EvalFilter(filter, packet, (uint)packet.Length, ref address);
    }

    private static byte[] MinecraftJoin(string host)
    {
        static void VarInt(List<byte> bytes, int value)
        {
            do
            {
                var part = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value == 0 ? part : (byte)(part | 0x80));
            } while (value != 0);
        }

        var name = System.Text.Encoding.UTF8.GetBytes(host);
        var frame = new List<byte> { 0 };
        VarInt(frame, 767);
        VarInt(frame, name.Length);
        frame.AddRange(name);
        frame.AddRange([0x63, 0xDD]);
        VarInt(frame, 2);
        var wire = new List<byte>();
        VarInt(wire, frame.Count);
        wire.AddRange(frame);
        return [.. wire];
    }

    [DllImport("WinDivert.dll", EntryPoint = "WinDivertHelperEvalFilter", CharSet = CharSet.Ansi, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvalFilter(string filter, [In] byte[] packet, uint length, ref WinDivertAddress address);

    private static bool TryLoadWinDivert()
    {
        try
        {
            var core = RepoFiles.CoreProjectDirectory;
            var dll = Path.GetFullPath(Path.Combine(core, "..", "..", "third-party", "windivert", "WinDivert.dll"));
            if (!File.Exists(dll))
            {
                return false;
            }

            // Loaded by full path first, so the DllImport of "WinDivert.dll" binds to it.
            NativeLibrary.Load(dll);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
