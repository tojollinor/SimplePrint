using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        AppPaths.Ensure();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
