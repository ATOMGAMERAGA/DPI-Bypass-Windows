using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Logging;

namespace DpiBypass.Core.Diagnostics;

/// <summary>
/// One block of the report: a heading and its rows, all already masked.
/// </summary>
/// <remarks>
/// A row with nothing behind it carries the string "ölçülmedi" rather than a zero. A
/// report showing a zero for a probe that never ran would be worse than one that says
/// nothing at all, because a zero is a number somebody will reason about.
/// </remarks>
public sealed record ReportSection(string Title, IReadOnlyList<KeyValuePair<string, string>> Rows);

/// <summary>
/// A consistent picture of the app at one instant, ready to be written out.
/// </summary>
/// <remarks>
/// <para>
/// Built by reading state that already exists. Saving a report never starts a probe, a
/// load test or a connection change: a user asking "what does it look like right now"
/// must get an answer about right now, not about a machine the act of asking disturbed.
/// </para>
/// <para>
/// Every value has been through the redactor by the time it is here, so nothing further
/// down can leak one by forgetting to call it.
/// </para>
/// </remarks>
public sealed record DiagnosticSnapshot
{
    /// <summary>
    /// Bumped whenever the shape changes, so a report can be read by a build that did not
    /// write it.
    /// </summary>
    public const int SchemaVersion = 1;

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string AppVersion { get; init; }

    public required string OperatingSystem { get; init; }

    public required string Architecture { get; init; }

    public required bool Elevated { get; init; }

    public required bool RemoteSession { get; init; }

    /// <summary>
    /// Which run of the service the measurements below belong to.
    /// </summary>
    /// <remarks>
    /// Two numbers rather than one: the engine session says which start produced them, and
    /// the network alias says which link. A report that ran across a network change would
    /// otherwise put two different links' numbers under one heading.
    /// </remarks>
    public required long EngineSession { get; init; }

    public required string NetworkAlias { get; init; }

    public required IReadOnlyList<ReportSection> Sections { get; init; }

    /// <summary>The log tail, already masked and already trimmed to a size limit.</summary>
    public required IReadOnlyList<string> LogExcerpt { get; init; }

    public required int MaskedValues { get; init; }

    public required long DroppedLogLines { get; init; }
}

/// <summary>What a save attempt did.</summary>
public sealed record DiagnosticSaveResult(bool Saved, string? Path, string? Failure)
{
    public static DiagnosticSaveResult Ok(string path) => new(true, path, null);

    public static DiagnosticSaveResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// Writes a snapshot out as a small archive the user can attach to a message.
/// </summary>
/// <remarks>
/// <para>
/// Three entries: a summary anyone can read, a schema-versioned JSON anyone can parse,
/// and a bounded excerpt of the log. Deliberately not the raw settings files and not the
/// whole log directory - those are the two things that would make an archive convenient
/// to build and unsafe to send.
/// </para>
/// <para>
/// Nothing leaves the machine. There is no upload, and there is no telemetry behind this.
/// </para>
/// </remarks>
public static class DiagnosticReportWriter
{
    /// <summary>The most log text one report carries, as written.</summary>
    public const int MaxLogBytes = 512 * 1024;

    /// <summary>
    /// Room kept back for the "older lines skipped" marker.
    /// </summary>
    /// <remarks>
    /// The marker is written after the budget has been spent, so its own length has to be
    /// reserved up front or the file finishes just over the cap it advertises.
    /// </remarks>
    private const int SkipMarkerReserve = 128;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes the archive, and only names it once it is complete.
    /// </summary>
    /// <remarks>
    /// The archive is built beside its destination and moved into place at the end, so a
    /// cancelled or failed save never leaves a half written file for the user to send. A
    /// cancellation is reported as a cancellation rather than as an error: the user asked
    /// for it, and the app has no business colouring their own decision red.
    /// </remarks>
    public static async Task<DiagnosticSaveResult> WriteAsync(
        string path,
        DiagnosticSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);

        var partial = path + ".partial";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "ozet.txt", Summarise(snapshot), cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(
                    archive,
                    "tani.json",
                    JsonSerializer.Serialize(ToDocument(snapshot), Json),
                    cancellationToken).ConfigureAwait(false);
                await WriteEntryAsync(archive, "gunluk.txt", LogText(snapshot), cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            File.Move(partial, path, overwrite: true);
            return DiagnosticSaveResult.Ok(path);
        }
        catch (OperationCanceledException)
        {
            Cleanup(partial);
            return DiagnosticSaveResult.Failed("Kaydetme iptal edildi.");
        }
        catch (UnauthorizedAccessException ex)
        {
            Cleanup(partial);
            return DiagnosticSaveResult.Failed($"Bu konuma yazma izni yok: {ex.Message}");
        }
        catch (IOException ex)
        {
            Cleanup(partial);
            return DiagnosticSaveResult.Failed($"Dosya yazılamadı: {ex.Message}");
        }
        catch (Exception ex)
        {
            Cleanup(partial);
            return DiagnosticSaveResult.Failed($"Rapor oluşturulamadı: {ex.Message}");
        }
    }

    /// <summary>The plain text half, which is what most people will actually read.</summary>
    public static string Summarise(DiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Invariant throughout. The report is a file somebody sends on, and a decimal
        // comma or a thousands dot that depends on the machine that wrote it makes two
        // reports of the same fault impossible to compare - and makes the JSON half
        // ambiguous to anything that parses it.
        var text = new StringBuilder();
        text.AppendLine("DPI Bypass · tanı raporu");
        text.AppendLine($"Şema sürümü      : {DiagnosticSnapshot.SchemaVersion}");
        text.AppendLine(FormattableString.Invariant($"Oluşturulma      : {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}"));
        text.AppendLine($"Uygulama sürümü  : {snapshot.AppVersion}");
        text.AppendLine($"İşletim sistemi  : {snapshot.OperatingSystem} ({snapshot.Architecture})");
        text.AppendLine($"Yönetici hakları : {(snapshot.Elevated ? "var" : "yok")}");
        text.AppendLine($"Uzak oturum      : {(snapshot.RemoteSession ? "evet" : "hayır")}");
        text.AppendLine($"Motor oturumu    : {snapshot.EngineSession}");
        text.AppendLine($"Ağ               : {snapshot.NetworkAlias}");
        text.AppendLine();

        foreach (var section in snapshot.Sections)
        {
            text.AppendLine($"— {section.Title} —");
            foreach (var (key, value) in section.Rows)
            {
                text.AppendLine($"  {key,-28}: {value}");
            }

            text.AppendLine();
        }

        text.AppendLine(
            $"Bu rapor yerelde oluşturuldu ve hiçbir yere gönderilmedi. {snapshot.MaskedValues} tanımlayıcı "
            + "değer (ağ adı, donanım ve IP adresleri, kullanıcı yolları) takma adlarla değiştirildi.");

        if (snapshot.DroppedLogLines > 0)
        {
            text.AppendLine(
                    FormattableString.Invariant(
                    $"Uyarı: yoğunluk nedeniyle {snapshot.DroppedLogLines} günlük satırı yazılamadı; ")
                + "aşağıdaki kesit eksik olabilir.");
        }

        return text.ToString();
    }

    private static object ToDocument(DiagnosticSnapshot snapshot) => new
    {
        schema = DiagnosticSnapshot.SchemaVersion,
        generatedAt = snapshot.GeneratedAt,
        app = new { version = snapshot.AppVersion },
        system = new
        {
            operatingSystem = snapshot.OperatingSystem,
            architecture = snapshot.Architecture,
            elevated = snapshot.Elevated,
            remoteSession = snapshot.RemoteSession,
        },
        session = new { engine = snapshot.EngineSession, network = snapshot.NetworkAlias },
        sections = snapshot.Sections.Select(section => new
        {
            title = section.Title,
            values = section.Rows.ToDictionary(row => row.Key, row => row.Value),
        }),
        privacy = new
        {
            maskedValues = snapshot.MaskedValues,
            droppedLogLines = snapshot.DroppedLogLines,
            uploaded = false,
        },
    };

    private static string LogText(DiagnosticSnapshot snapshot)
    {
        var text = new StringBuilder();
        var bytes = 0;

        // Newest first, so the cap takes the oldest lines rather than the ones describing
        // whatever the user is reporting.
        // Counted as it will be written, not as it would be on the machine that wrote the
        // code: AppendLine emits Environment.NewLine, which is two bytes on Windows and one
        // elsewhere. Budgeting one byte a line put the excerpt about six kilobytes over its
        // own cap on Windows - which is the only platform this application runs on.
        var newline = Encoding.UTF8.GetByteCount(Environment.NewLine);
        var budget = MaxLogBytes - SkipMarkerReserve;

        var kept = new List<string>();
        for (var i = snapshot.LogExcerpt.Count - 1; i >= 0; i--)
        {
            var line = snapshot.LogExcerpt[i];
            var size = Encoding.UTF8.GetByteCount(line) + newline;
            if (bytes + size > budget)
            {
                text.AppendLine($"[… {i + 1} eski satır boyut sınırı nedeniyle atlandı …]");
                break;
            }

            bytes += size;
            kept.Add(line);
        }

        kept.Reverse();
        foreach (var line in kept)
        {
            text.AppendLine(line);
        }

        return text.Length == 0 ? "ölçülmedi: bu oturumda günlük kaydı yok." : text.ToString();
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static void Cleanup(string partial)
    {
        try
        {
            File.Delete(partial);
        }
        catch (Exception)
        {
            // The destination was never written, which is what matters. A leftover
            // ".partial" is not something the user can mistake for a finished report.
        }
    }

    /// <summary>The tail of the in-memory log, masked line by line.</summary>
    /// <remarks>
    /// The in-memory buffer rather than the log directory. Attaching the whole directory
    /// would be days of history from every network the machine has been on, and none of it
    /// through the redactor.
    /// </remarks>
    public static IReadOnlyList<string> MaskedLogTail(DiagnosticRedactor redactor, IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(entries);

        return [.. entries.Select(entry => redactor.Redact(entry.ToString()))];
    }
}
