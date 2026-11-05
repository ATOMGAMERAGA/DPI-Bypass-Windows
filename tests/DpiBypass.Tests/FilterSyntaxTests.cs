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
