using System.Text;

namespace DpiBypass.Core.Apps;

/// <summary>What happened the last time the hosts file was brought into line.</summary>
/// <param name="Applied">True when the file now says what the setting asked for.</param>
/// <param name="Changed">True when bytes were actually written.</param>
/// <param name="Detail">A sentence for the user, or empty when there is nothing to say.</param>
public readonly record struct HostsBlocklistResult(bool Applied, bool Changed, string Detail)
{
    public static HostsBlocklistResult Unchanged { get; } = new(true, false, string.Empty);

    public static HostsBlocklistResult Failed(string detail) => new(false, false, detail);
}

/// <summary>
/// Keeps one delimited block of sinkhole entries in the machine's hosts file.
/// </summary>
/// <remarks>
/// <para>
/// The hosts file is the second half of the advertisement block, and it exists because
/// the first half - the loopback resolver - only answers while protection is running and
/// only for clients that ask it. A name in the hosts file is refused by the Windows
/// resolver itself, before any server is consulted, for every process on the machine and
/// whether this application is running or not. It costs nothing to have: the resolver
/// reads the file once and keeps it in memory, so nothing on the network path grows by a
/// single packet or a single microsecond.
/// </para>
/// <para>
/// Everything this writes lives between two marker lines, and nothing outside them is
/// ever touched - not reordered, not reindented, not re-line-ended. Removing the block
/// puts the file back exactly as it was found. A file that already says the right thing
/// is not rewritten at all, so a machine that starts the app twice a day does not
/// accumulate a write per start.
/// </para>
/// </remarks>
public sealed class HostsFileBlocklist
{
    public const string BeginMarker = "# >>> DPI Bypass - Lunar Client reklam engeli >>>";

    public const string EndMarker = "# <<< DPI Bypass - Lunar Client reklam engeli <<<";

    private const string Explanation =
        "# Bu blok DPI Bypass tarafindan yonetilir. Ayarlardaki anahtari kapatmak veya "
        + "uygulamayi kaldirmak bu satirlari siler.";

    /// <summary>Windows accepts either family here; both are written so neither is left open.</summary>
    private const string SinkholeV4 = "0.0.0.0";

    private const string SinkholeV6 = "::";

    /// <summary>
    /// Serialises the read-modify-write, because two of them can be asked for at once.
    /// </summary>
    /// <remarks>
    /// The user's switch and a start re-asserting the block are separate paths that can
    /// land together. Neither would corrupt the file - each writes a complete desired
    /// text - but one would fail on a sharing violation and report the block as not
    /// applied when it is about to be.
    /// </remarks>
    private readonly Lock _gate = new();

    private readonly string _path;

    public HostsFileBlocklist(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>The hosts file Windows itself reads.</summary>
    /// <remarks>
    /// Built from the system directory rather than hard coded to <c>C:\Windows</c>, which
    /// is not where every installation puts it.
    /// </remarks>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers",
        "etc",
        "hosts");

    /// <summary>The file this instance keeps in line. Named so it does not hide System.IO.Path.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Adds or removes the managed block so the file matches <paramref name="enabled"/>.
    /// </summary>
    /// <remarks>
    /// Failure is reported rather than thrown. Writing here needs administrator rights and
    /// can be refused by security software, and neither is a reason to fail the setting:
    /// the resolver half of the block is unaffected, so the feature degrades instead of
    /// breaking, and the caller has a sentence to show for why.
    /// </remarks>
    public HostsBlocklistResult Apply(bool enabled, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        lock (_gate)
        {
            return ApplyCore(enabled, names);
        }
    }

    private HostsBlocklistResult ApplyCore(bool enabled, IReadOnlyList<string> names)
    {
        try
        {
            var exists = File.Exists(_path);
            if (!exists && !enabled)
            {
                // No file and nothing to add: the machine is already in the asked-for
                // state, and creating a hosts file to say so would be a change for
                // nothing.
                return HostsBlocklistResult.Unchanged;
            }

            var original = exists ? File.ReadAllText(_path) : string.Empty;
            var stripped = RemoveBlock(original, names);
            var desired = enabled ? AppendBlock(stripped, names) : stripped;

            if (string.Equals(desired, original, StringComparison.Ordinal))
            {
                return HostsBlocklistResult.Unchanged;
            }

            // Written in place rather than through a temporary file and a rename: the
            // hosts file has an access control list of its own, and replacing the file
            // would replace that with whatever the new one inherited.
            File.WriteAllText(_path, desired, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Read back, because a write that silently did not land is exactly the
            // failure this feature cannot notice any other way.
            if (!string.Equals(File.ReadAllText(_path), desired, StringComparison.Ordinal))
            {
                return HostsBlocklistResult.Failed(
                    "Hosts dosyası yazıldı ama içeriği doğrulanamadı; güvenlik yazılımı engelliyor olabilir.");
            }

            return new HostsBlocklistResult(true, true, string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return HostsBlocklistResult.Failed(
                "Hosts dosyasına yazılamadı: uygulamayı yönetici olarak çalıştırın.");
        }
        catch (IOException ex)
        {
            return HostsBlocklistResult.Failed($"Hosts dosyasına yazılamadı: {ex.Message}");
        }
        catch (System.Security.SecurityException)
        {
            return HostsBlocklistResult.Failed(
                "Hosts dosyasına erişim reddedildi: uygulamayı yönetici olarak çalıştırın.");
        }
    }

    /// <summary>Whether the managed block is in the file right now.</summary>
    public bool IsApplied()
    {
        try
        {
            return File.Exists(_path)
                && File.ReadAllText(_path).Contains(BeginMarker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            // Unreadable is not the same as applied, and this only feeds a status line.
            return false;
        }
    }

    /// <summary>
    /// Cuts every managed block out of the text, leaving the rest byte for byte.
    /// </summary>
    /// <remarks>
    /// A loop rather than a single pass, because two copies of the block in one file is a
    /// state a crash between the write and the read-back could leave behind, and leaving
    /// one of them would mean the switch could never be turned off again.
    /// </remarks>
    private static string RemoveBlock(string text, IReadOnlyList<string> names)
    {
        while (true)
        {
            var marker = text.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                return text;
            }

            var start = StartOfLine(text, marker);
            var end = text.IndexOf(EndMarker, marker, StringComparison.Ordinal);

            int cut;
            if (end >= 0)
            {
                cut = SkipLineEnding(text, end + EndMarker.Length);
            }
            else
            {
                // The closing marker is missing, so the block was cut short by something
                // that stopped mid-write. Only the lines this class would itself have
                // written are taken, which leaves anything a person put after them alone.
                cut = EndOfOwnEntries(text, SkipLineEnding(text, marker + BeginMarker.Length), names);
            }

            text = string.Concat(text.AsSpan(0, start), text.AsSpan(cut));
        }
    }

    private static string AppendBlock(string text, IReadOnlyList<string> names)
    {
        var builder = new StringBuilder(text);

        // One line ending before the block and none spare, so switching the feature on
        // and off repeatedly cannot leave a growing gap at the end of the file.
        if (builder.Length > 0 && builder[^1] is not ('\n' or '\r'))
        {
            builder.Append("\r\n");
        }

        builder.Append(BeginMarker).Append("\r\n");
        builder.Append(Explanation).Append("\r\n");

        foreach (var name in names)
        {
            builder.Append(SinkholeV4).Append(' ').Append(name).Append("\r\n");
            builder.Append(SinkholeV6).Append(' ').Append(name).Append("\r\n");
        }

        builder.Append(EndMarker).Append("\r\n");
        return builder.ToString();
    }

    private static int StartOfLine(string text, int index)
    {
        if (index <= 0)
        {
            return 0;
        }

        var newline = text.LastIndexOf('\n', index - 1);
        return newline < 0 ? 0 : newline + 1;
    }

    private static int SkipLineEnding(string text, int index)
    {
        if (index < text.Length && text[index] == '\r')
        {
            index++;
        }

        if (index < text.Length && text[index] == '\n')
        {
            index++;
        }

        return index;
    }

    private static int EndOfOwnEntries(string text, int index, IReadOnlyList<string> names)
    {
        while (index < text.Length)
        {
            var lineEnd = text.IndexOf('\n', index);
            var line = lineEnd < 0 ? text[index..] : text[index..lineEnd];

            if (!IsOwnLine(line.TrimEnd('\r'), names))
            {
                return index;
            }

            index = lineEnd < 0 ? text.Length : lineEnd + 1;
        }

        return index;
    }

    private static bool IsOwnLine(string line, IReadOnlyList<string> names)
    {
        if (string.Equals(line, Explanation, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var name in names)
        {
            if (string.Equals(line, $"{SinkholeV4} {name}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(line, $"{SinkholeV6} {name}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
