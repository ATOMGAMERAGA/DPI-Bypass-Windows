using System.Windows;

namespace DpiBypass.App;

/// <summary>
/// The process entry point, replacing the one WPF generates.
/// </summary>
/// <remarks>
/// <para>
/// It exists for one reason: the generated entry point has no error handling, and
/// two of the three things it does can fail before <see cref="App.OnStartup"/> - the
/// one place the app installs its own handlers - has ever run.
/// <c>InitializeComponent</c> loads the application resource dictionaries, and
/// constructing the <see cref="Application"/> is the first thing that touches the
/// WPF assemblies at all. A failure in either ends the process immediately: no
/// window, no notification area icon, no dialog, no log line, nothing written
/// anywhere. To the person who just double-clicked the shortcut, the application
/// started and vanished, and there is no evidence left to say why - which is
/// precisely the report this exists to make impossible.
/// </para>
/// <para>
/// So the whole of start-up is wrapped, including <see cref="Application.Run()"/>,
/// and anything that escapes is written to the crash log and put in front of the
/// user. A crash the user can read out is a crash that can be fixed.
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main()
    {
        try
        {
            var application = new App();
            application.InitializeComponent();
            return application.Run();
        }
        catch (Exception ex)
        {
            App.ReportFatal(ex);
            return 1;
        }
    }
}
