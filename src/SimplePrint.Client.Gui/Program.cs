using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

internal static class Program
{
    public static bool StartInTray { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        StartInTray = args.Any(x => x.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        WindowsAppIdentity.Set("SimplePrint.Client.0_2_0");
        AppPaths.Ensure();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
