namespace DpiBypass.Tests;

/// <summary>A scratch directory that removes itself, so a failing assert cannot leak one.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "dpibypass")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup only; the OS reclaims it.
        }
    }
}
