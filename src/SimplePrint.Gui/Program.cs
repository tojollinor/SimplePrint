using SimplePrint.Common;

namespace SimplePrint.Gui;

internal static class Program
{
    public static bool StartInTray { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        AppPaths.Ensure();
        ApplicationConfiguration.Initialize();

        StartInTray = args.Any(x =>
            x.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        WindowsAppIdentity.Set("SimplePrint.Unified");
        Application.Run(new MainForm());
    }
}
