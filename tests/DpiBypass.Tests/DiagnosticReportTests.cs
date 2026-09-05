using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Logging;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// A report is written to be sent to somebody, so what it must never contain is the
/// part with teeth.
/// </summary>
public sealed class DiagnosticRedactionTests
{
    [Fact]
    public void TheSameValueGetsTheSameAliasEverywhere()
    {
        var redactor = new DiagnosticRedactor();

        var first = redactor.Alias(RedactionKind.Network, "Ev-WiFi-5G");
        var again = redactor.Alias(RedactionKind.Network, "Ev-WiFi-5G");
        var other = redactor.Alias(RedactionKind.Network, "Komsu-WiFi");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.DoesNotContain("Ev-WiFi", first!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The alias must carry nothing derived from the value.
    /// </summary>
    /// <remarks>
    /// Hashing an SSID is not anonymisation: the plausible space is small enough that
    /// anyone holding the report can hash their candidates and compare. An ordinal in
    /// order of first appearance carries nothing to attack, which is what this pins.
    /// </remarks>
    [Fact]
    public void AliasesAreOrdinalsRatherThanAnythingDerivedFromTheValue()
    {
        var a = new DiagnosticRedactor();
        var b = new DiagnosticRedactor();

        // Registered in a different order, so an alias derived from the value would match
        // across the two and an ordinal cannot.
        Assert.Equal("ag-1", a.Alias(RedactionKind.Network, "TurkTelekom_ABC1"));
        Assert.Equal("ag-2", a.Alias(RedactionKind.Network, "Vodafone_Net_9"));

        Assert.Equal("ag-1", b.Alias(RedactionKind.Network, "Vodafone_Net_9"));
        Assert.Equal("ag-2", b.Alias(RedactionKind.Network, "TurkTelekom_ABC1"));
    }

    [Theory]
    [InlineData("Bağlanılamadı: 192.168.1.14 yanıt vermiyor", "192.168.1.14")]
    [InlineData("gateway 2001:db8:85a3::8a2e:370:7334 unreachable", "2001:db8:85a3::8a2e:370:7334")]
    [InlineData("adapter a4:5e:60:c1:22:0f went down", "a4:5e:60:c1:22:0f")]
    [InlineData(@"Could not open C:\Users\aysegul\AppData\Roaming\state.json", "aysegul")]
    [InlineData("System.Net.Sockets.SocketException: 203.0.113.9:443", "203.0.113.9")]
    public void FreeTextLosesWhateverIdentifiesTheMachine(string text, string secret)
    {
        var redactor = new DiagnosticRedactor();

        var masked = redactor.Redact(text);

        Assert.DoesNotContain(secret, masked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A log line's clock is not an address, and turning it into one would make the
    /// excerpt unreadable while protecting nobody.
    /// </summary>
    [Fact]
    public void ATimestampIsNotMistakenForAnAddress()
    {
        var redactor = new DiagnosticRedactor();

        var masked = redactor.Redact("14:59:54 [I] Koruma etkin");

        Assert.Contains("14:59:54", masked);
    }

    /// <summary>The app's own constants stay legible: they say nothing about the user.</summary>
    [Theory]
    [InlineData("DNS proxy listening on 127.0.0.1:53")]
    [InlineData("Cloudflare (1.1.1.1) answered")]
    public void TheAppsOwnAddressesAreLeftAlone(string text)
    {
        var redactor = new DiagnosticRedactor();

        Assert.Equal(text, redactor.Redact(text));
    }

    /// <summary>
    /// A registered value is masked in free text too, not only where it was registered.
    /// </summary>
    [Fact]
    public void ARegisteredNetworkNameIsMaskedInsideLogLinesAndExceptions()
    {
        var redactor = new DiagnosticRedactor();
        redactor.Register(RedactionKind.Network, "Ahmetin-iPhone");

        var masked = redactor.Redact("Ağ değişti: 'Ahmetin-iPhone' · InvalidOperationException: Ahmetin-iPhone yok");

        Assert.DoesNotContain("Ahmetin-iPhone", masked, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, masked.Split("ag-1").Length - 1);
    }

    [Fact]
    public void NothingToMaskStaysNothingRatherThanBecomingAnAlias()
    {
        var redactor = new DiagnosticRedactor();

        Assert.Null(redactor.Alias(RedactionKind.Network, null));
        Assert.Null(redactor.Alias(RedactionKind.Network, "   "));
        Assert.Equal(string.Empty, redactor.Redact(null));
    }
}

/// <summary>
/// What ends up in the archive, and what happens when writing it goes wrong.
/// </summary>
public sealed class DiagnosticReportWriterTests
{
    private static DiagnosticSnapshot Snapshot(
        IReadOnlyList<string>? log = null,
        long droppedLogLines = 0) => new()
        {
            GeneratedAt = DateTimeOffset.Parse("2026-09-05T14:00:00+03:00"),
            AppVersion = "1.0.0.42",
            OperatingSystem = "Microsoft Windows NT 10.0.26100.0",
            Architecture = "X64",
            Elevated = true,
            RemoteSession = false,
            EngineSession = 3,
            NetworkAlias = "ag-1",
            Sections =
            [
                new("Koruma", [new("Durum", "Running"), new("Son doğrulama", "ölçülmedi")]),
                new("Gecikme", [new("Boştayken (sonra)", "ölçülmedi")]),
            ],
            LogExcerpt = log ?? ["14:00:00 [I] Koruma etkin"],
            MaskedValues = 4,
            DroppedLogLines = droppedLogLines,
        };

    [Fact]
    public async Task TheArchiveHoldsASummaryASchemaVersionedDocumentAndALogExcerpt()
    {
        using var directory = new TempDirectory();
        var path = directory.File("tani.zip");

        var result = await DiagnosticReportWriter.WriteAsync(path, Snapshot());

        Assert.True(result.Saved);
        Assert.Equal(path, result.Path);

        using var archive = ZipFile.OpenRead(path);
        Assert.Equal(["gunluk.txt", "ozet.txt", "tani.json"], archive.Entries.Select(e => e.FullName).Order());

        var json = JsonDocument.Parse(Read(archive, "tani.json"));
        Assert.Equal(DiagnosticSnapshot.SchemaVersion, json.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("session").GetProperty("engine").GetInt64());
        Assert.Equal("ag-1", json.RootElement.GetProperty("session").GetProperty("network").GetString());
        Assert.False(json.RootElement.GetProperty("privacy").GetProperty("uploaded").GetBoolean());

        var summary = Read(archive, "ozet.txt");
        Assert.Contains("1.0.0.42", summary);
        Assert.Contains("Microsoft Windows NT", summary);
        Assert.Contains("ölçülmedi", summary);
        Assert.Contains("hiçbir yere gönderilmedi", summary);
    }

    /// <summary>The excerpt is capped, and it is the newest lines that survive the cap.</summary>
    [Fact]
    public async Task ALargeLogIsTrimmedFromTheOldEnd()
    {
        using var directory = new TempDirectory();
        var path = directory.File("tani.zip");
        var lines = Enumerable.Range(0, 40_000).Select(i => $"14:00:00 [I] satır {i} {new string('x', 60)}").ToArray();

        var result = await DiagnosticReportWriter.WriteAsync(path, Snapshot(log: lines));

        Assert.True(result.Saved);
        using var archive = ZipFile.OpenRead(path);
        var log = Read(archive, "gunluk.txt");

        Assert.True(
            Encoding.UTF8.GetByteCount(log) <= DiagnosticReportWriter.MaxLogBytes + 1024,
            $"the excerpt was {Encoding.UTF8.GetByteCount(log)} bytes");
        Assert.Contains("satır 39999", log);
        Assert.DoesNotContain("satır 0 ", log);
        Assert.Contains("boyut sınırı nedeniyle atlandı", log);
    }

    [Fact]
    public async Task ANoisyLogSaysWhatItCouldNotWriteDown()
    {
        using var directory = new TempDirectory();
        var path = directory.File("tani.zip");

        await DiagnosticReportWriter.WriteAsync(path, Snapshot(droppedLogLines: 1234));

        using var archive = ZipFile.OpenRead(path);
        Assert.Contains("1234 günlük satırı yazılamadı", Read(archive, "ozet.txt"));
    }

    /// <summary>A cancelled save leaves no file for the user to send by mistake.</summary>
    [Fact]
    public async Task ACancelledSaveLeavesNothingBehindAndSaysItWasCancelled()
    {
        using var directory = new TempDirectory();
        var path = directory.File("tani.zip");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await DiagnosticReportWriter.WriteAsync(path, Snapshot(), cancellation.Token);

        Assert.False(result.Saved);
        Assert.Contains("iptal", result.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    /// <summary>A destination that cannot be written is reported, not half written.</summary>
    [Fact]
    public async Task ADestinationTheSystemRefusesIsReportedAndLeavesNoPartialFile()
    {
        using var directory = new TempDirectory();
        var path = directory.File("tani.zip");

        // A directory on the working name is a write the OS refuses on every platform,
        // including for the elevated process this app always is.
        Directory.CreateDirectory(path + ".partial");

        var result = await DiagnosticReportWriter.WriteAsync(path, Snapshot());

        Assert.False(result.Saved);
        Assert.Null(result.Path);
        Assert.NotNull(result.Failure);
        Assert.False(File.Exists(path));
    }

    /// <summary>An empty log says so rather than shipping an empty file.</summary>
    [Fact]
    public async Task AnEmptyLogSaysItWasNotMeasured()
    {
        using var directory = new TempDirectory();
        var path = directory.File("tani.zip");

        await DiagnosticReportWriter.WriteAsync(path, Snapshot(log: []));

        using var archive = ZipFile.OpenRead(path);
        Assert.Contains("ölçülmedi", Read(archive, "gunluk.txt"));
    }

    /// <summary>
    /// The log excerpt goes through the same masking as everything else.
    /// </summary>
    [Fact]
    public void TheLogTailIsMaskedLineByLine()
    {
        var redactor = new DiagnosticRedactor();
        redactor.Register(RedactionKind.Network, "Ev-WiFi");

        var masked = DiagnosticReportWriter.MaskedLogTail(
            redactor,
            [
                new(DateTimeOffset.Now, LogLevel.Info, "Ağ değişti: Ev-WiFi"),
                new(DateTimeOffset.Now, LogLevel.Error, "192.168.1.1 yanıt vermedi"),
            ]);

        Assert.All(masked, line => Assert.DoesNotContain("Ev-WiFi", line, StringComparison.OrdinalIgnoreCase));
        Assert.All(masked, line => Assert.DoesNotContain("192.168.1.1", line, StringComparison.Ordinal));
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
