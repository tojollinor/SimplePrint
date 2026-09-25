using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

internal static class Program
{
    public static bool StartInTray { get; private set; }
    public static string? UpdateSuccessVersion { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        AppPaths.Ensure();
        ApplicationConfiguration.Initialize();

        if (HasArgument(args, "--bootstrap-update"))
        {
            RunUpdateBootstrap();
            return;
        }

        if (TryGetArgumentValue(args, "--apply-update", out var requestPath))
        {
            Application.Run(new UpdateProgressForm(requestPath));
            return;
        }

        UpdateSuccessVersion = TryGetArgumentValue(args, "--update-success", out var version)
            ? version
            : null;

        StartInTray =
            string.IsNullOrWhiteSpace(UpdateSuccessVersion) &&
            HasArgument(args, "--tray");

        WindowsAppIdentity.Set("SimplePrint.Server.0_2_0");
        Application.Run(new MainForm());
    }

    private static void RunUpdateBootstrap()
    {
        try
        {
            var current = typeof(Program).Assembly.GetName().Version
                ?? new Version(0, 0, 0, 0);

            var prepared = GitHubUpdateService.PrepareDetachedUpdateHostAsync(
                    current,
                    SimplePrintComponent.Server,
                    Application.ExecutablePath)
                .GetAwaiter()
                .GetResult();

            GitHubUpdateService.LaunchDetachedUpdateHost(prepared);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "SimplePrint Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static bool HasArgument(string[] args, string name) =>
        args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetArgumentValue(
        string[] args,
        string name,
        out string value)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = args[i + 1];
            return true;
        }

        value = "";
        return false;
    }
}
