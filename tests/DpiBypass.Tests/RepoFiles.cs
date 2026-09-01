namespace DpiBypass.Tests;

/// <summary>Locates repository source files from the test output directory.</summary>
internal static class RepoFiles
{
    public static string MainWindowXaml => Find("src", "DpiBypass.App", "MainWindow.xaml");

    public static string SharedThemeXaml => Find("src", "DpiBypass.App", "Theme", "Shared.xaml");

    public static string MainViewModel => Find("src", "DpiBypass.App", "ViewModels", "MainViewModel.cs");

    public static string CoreProjectDirectory => Find("src", "DpiBypass.Core", "DpiBypass.Core.csproj") is { } project
        ? Path.GetDirectoryName(project)!
        : throw new Xunit.Sdk.XunitException("Could not locate the core project.");

    public static string Find(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = Path.Combine(relativeParts);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException($"Could not locate {relative} above {AppContext.BaseDirectory}.");
    }
}
