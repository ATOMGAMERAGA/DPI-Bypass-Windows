using DpiBypass.Core.Apps;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The hosts file layer, and the one thing it must never do: lose somebody's file.
/// </summary>
/// <remarks>
/// This writes to a file Windows itself reads and that a user may well have edited by
/// hand. Every test here is ultimately the same assertion from a different angle - what
/// was in the file before is still in the file after, unchanged, and turning the feature
/// off puts it back exactly as it was found.
/// </remarks>
public sealed class HostsFileBlocklistTests
{
    private const string Original =
        "# Copyright (c) 1993-2009 Microsoft Corp.\r\n"
        + "\r\n"
        + "127.0.0.1       localhost\r\n"
        + "::1             localhost\r\n"
        + "192.168.1.50    nas.local\r\n";

    private static readonly string[] Names = ["ads.overwolf.com", "tracking.overwolf.com"];

    [Fact]
    public void EnablingAddsAMarkedBlockAndLeavesEverythingElseAlone()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        File.WriteAllText(path, Original);

        var result = new HostsFileBlocklist(path).Apply(true, Names);
        var written = File.ReadAllText(path);

        Assert.True(result.Applied);
        Assert.True(result.Changed);
        Assert.StartsWith(Original, written, StringComparison.Ordinal);
        Assert.Contains(HostsFileBlocklist.BeginMarker, written, StringComparison.Ordinal);
        Assert.Contains(HostsFileBlocklist.EndMarker, written, StringComparison.Ordinal);

        foreach (var name in Names)
        {
            Assert.Contains($"0.0.0.0 {name}\r\n", written, StringComparison.Ordinal);
            Assert.Contains($":: {name}\r\n", written, StringComparison.Ordinal);
        }
    }

    /// <summary>Turning it off gives back the file that was there, byte for byte.</summary>
    [Fact]
    public void DisablingRestoresTheFileExactly()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        File.WriteAllText(path, Original);
        var blocklist = new HostsFileBlocklist(path);

        blocklist.Apply(true, Names);
        var removal = blocklist.Apply(false, Names);

        Assert.True(removal.Applied);
        Assert.True(removal.Changed);
        Assert.Equal(Original, File.ReadAllText(path));
        Assert.False(blocklist.IsApplied());
    }

    /// <summary>
    /// Switching it on and off repeatedly leaves the file where it started every time.
    /// </summary>
    /// <remarks>
    /// The failure this catches is a block that grows a blank line per cycle, which is how
    /// a hosts file quietly turns into a thousand empty lines over a few months of use.
    /// </remarks>
    [Fact]
    public void RepeatedCyclesNeitherGrowNorShrinkTheFile()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        File.WriteAllText(path, Original);
        var blocklist = new HostsFileBlocklist(path);

        string? enabled = null;

        for (var cycle = 0; cycle < 5; cycle++)
        {
            blocklist.Apply(true, Names);
            enabled ??= File.ReadAllText(path);
            Assert.Equal(enabled, File.ReadAllText(path));

            blocklist.Apply(false, Names);
            Assert.Equal(Original, File.ReadAllText(path));
        }
    }

    /// <summary>Applying what is already there writes nothing.</summary>
    /// <remarks>
    /// Start-up re-asserts the block, so without this every launch would be a write to a
    /// system file - and every one of those is a chance for security software to take an
    /// interest, for no gain at all.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyingTheSameStateTwiceIsNotAWrite(bool enabled)
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        File.WriteAllText(path, Original);
        var blocklist = new HostsFileBlocklist(path);

        blocklist.Apply(enabled, Names);
        var stamp = File.GetLastWriteTimeUtc(path);
        var second = blocklist.Apply(enabled, Names);

        Assert.True(second.Applied);
        Assert.False(second.Changed);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(enabled, blocklist.IsApplied());
    }

    /// <summary>
    /// A block cut short by a crash is cleaned up without taking the next line with it.
    /// </summary>
    /// <remarks>
    /// Only the lines this class writes are removed, so a user entry immediately after a
    /// half written block survives - which is the whole reason removal does not simply
    /// delete everything from the marker to the end of the file.
    /// </remarks>
    [Fact]
    public void AHalfWrittenBlockIsRemovedWithoutEatingWhatFollowsIt()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        File.WriteAllText(
            path,
            Original
            + HostsFileBlocklist.BeginMarker + "\r\n"
            + "0.0.0.0 ads.overwolf.com\r\n"
            + ":: ads.overwolf.com\r\n"
            + "10.0.0.9        printer.local\r\n");

        var result = new HostsFileBlocklist(path).Apply(false, Names);

        Assert.True(result.Applied);
        Assert.Equal(Original + "10.0.0.9        printer.local\r\n", File.ReadAllText(path));
    }

    /// <summary>Two blocks - one crash away from possible - are both taken out.</summary>
    [Fact]
    public void EveryCopyOfTheBlockIsRemoved()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        var blocklist = new HostsFileBlocklist(path);

        File.WriteAllText(path, Original);
        blocklist.Apply(true, Names);
        var doubled = File.ReadAllText(path);
        File.WriteAllText(path, doubled + doubled[Original.Length..]);

        blocklist.Apply(false, Names);

        Assert.Equal(Original, File.ReadAllText(path));
        Assert.DoesNotContain(HostsFileBlocklist.BeginMarker, File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>A file that is not there is not created just to say the block is off.</summary>
    [Fact]
    public void NoFileAndNothingToAddIsNotAChange()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");

        var result = new HostsFileBlocklist(path).Apply(false, Names);

        Assert.True(result.Applied);
        Assert.False(result.Changed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AMissingFileIsCreatedWhenTheBlockIsAskedFor()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");

        var result = new HostsFileBlocklist(path).Apply(true, Names);

        Assert.True(result.Applied);
        Assert.True(File.Exists(path));
        Assert.StartsWith(HostsFileBlocklist.BeginMarker, File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>A file with Unix line endings keeps them outside the block.</summary>
    [Fact]
    public void LineEndingsOutsideTheBlockAreNotRewritten()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        const string unix = "127.0.0.1 localhost\n192.168.1.50 nas.local\n";
        File.WriteAllText(path, unix);
        var blocklist = new HostsFileBlocklist(path);

        blocklist.Apply(true, Names);
        Assert.StartsWith(unix, File.ReadAllText(path), StringComparison.Ordinal);

        blocklist.Apply(false, Names);
        Assert.Equal(unix, File.ReadAllText(path));
    }

    /// <summary>
    /// A file carrying bytes that are not ASCII comes back with those bytes intact.
    /// </summary>
    /// <remarks>
    /// A byte order mark, a comment in another script, or a byte sequence that is not
    /// valid UTF-8 at all. None of them is this class's to normalise, and a hosts file
    /// that came back subtly re-encoded would be a change nobody asked for to a file
    /// Windows reads on every lookup.
    /// </remarks>
    [Fact]
    public void BytesOutsideTheBlockSurviveWhateverTheyAre()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");

        byte[] awkward =
        [
            0xEF, 0xBB, 0xBF,                                     // a UTF-8 byte order mark
            .. "127.0.0.1 localhost\r\n# "u8.ToArray(),
            0xC3, 0xBC, 0xC3, 0xA7,                               // "üç" in UTF-8
            0x0D, 0x0A,
            0xFF, 0xFE,                                           // and two bytes that are not
            0x0D, 0x0A,
        ];

        File.WriteAllBytes(path, awkward);
        var blocklist = new HostsFileBlocklist(path);

        blocklist.Apply(true, Names);
        Assert.Equal(awkward, File.ReadAllBytes(path).Take(awkward.Length));

        blocklist.Apply(false, Names);
        Assert.Equal(awkward, File.ReadAllBytes(path));
    }

    /// <summary>The real list is what actually gets written, and all of it does.</summary>
    [Fact]
    public void TheShippedListIsWrittenInFull()
    {
        using var directory = new TempDirectory("hosts");
        var path = directory.File("hosts");
        File.WriteAllText(path, Original);

        new HostsFileBlocklist(path).Apply(true, LunarAdBlock.HostsFileNames);
        var written = File.ReadAllText(path);

        Assert.All(LunarAdBlock.HostsFileNames, name =>
            Assert.Contains($"0.0.0.0 {name}\r\n", written, StringComparison.Ordinal));
    }
}
