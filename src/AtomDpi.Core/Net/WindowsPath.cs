namespace AtomDpi.Core.Net;

/// <summary>
/// Windows path helpers that do not depend on the host's directory separator.
/// </summary>
/// <remarks>
/// The paths handled here always come from the Windows kernel, so they are always
/// backslash separated. <see cref="Path.GetFileName(string)"/> only treats a
/// backslash as a separator when the process itself is running on Windows, which
/// makes it the wrong tool for parsing captured image paths - and makes the unit
/// tests that cover this logic unrunnable anywhere else.
/// </remarks>
public static class WindowsPath
{
    public static string FileName(string path)
    {
        var separator = path.LastIndexOfAny(['\\', '/']);
        return separator < 0 ? path : path[(separator + 1)..];
    }

    public static bool FileNameEquals(string path, string fileName)
        => string.Equals(FileName(path), fileName, StringComparison.OrdinalIgnoreCase);
}
