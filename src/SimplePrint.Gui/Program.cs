using SimplePrint.Common;

namespace SimplePrint.Gui;

internal static class Program
{
    private const string InstanceMutexName = @"Local\SimplePrint.Gui.Unified";
    private const string ShowEventName = @"Local\SimplePrint.Gui.Unified.Show";

    private static EventWaitHandle? _showEvent;
    private static RegisteredWaitHandle? _showRegistration;

    public static bool StartInTray { get; private set; }
    public static string? UpdateSuccessVersion { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        AppPaths.Ensure();
        ApplicationConfiguration.Initialize();

        // Unerwartete Fehler in Ereignishandlern zeigen eine Meldung, statt die
        // gesamte Oberfläche zu beenden.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnexpectedError(e.Exception);

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

        UpdateSuccessVersion =
            TryGetArgumentValue(args, "--update-success", out var version)
                ? version
                : null;

        StartInTray =
            string.IsNullOrWhiteSpace(UpdateSuccessVersion) &&
            HasArgument(args, "--tray");

        using var instanceMutex = new Mutex(false, InstanceMutexName);
        var ownsMutex = TryAcquire(
            instanceMutex,
            string.IsNullOrWhiteSpace(UpdateSuccessVersion)
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(15));

        if (!ownsMutex)
        {
            // Es läuft bereits eine Oberfläche: diese nach vorne holen,
            // statt eine zweite Instanz zu starten.
            if (!StartInTray)
                SignalExistingInstance();

            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            WindowsAppIdentity.Set("SimplePrint.Unified");

            // Fragt am Server, ob ein Client einen Druckertreiber erhalten darf.
            using var driverRequests = new DriverRequestWatcher();
            driverRequests.Start();

            Application.Run(new MainForm());
        }
        finally
        {
            _showRegistration?.Unregister(null);
            _showEvent?.Dispose();

            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch
            {
            }
        }
    }

    public static void RegisterShowRequestHandler(Action handler)
    {
        if (_showEvent is null)
            return;

        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => handler(),
            null,
            Timeout.Infinite,
            false);
    }

    private static bool TryAcquire(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // Die vorherige Instanz wurde hart beendet; der Mutex gehört jetzt uns.
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(ShowEventName);
            existing.Set();
        }
        catch
        {
        }
    }

    private static void ShowUnexpectedError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "Ein unerwarteter Fehler ist aufgetreten. SimplePrint läuft weiter." +
                Environment.NewLine + Environment.NewLine + ex.Message,
                "SimplePrint",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch
        {
        }
    }

    private static void RunUpdateBootstrap()
    {
        try
        {
            var current = typeof(Program).Assembly.GetName().Version
                ?? new Version(0, 0, 0, 0);

            var prepared = GitHubUpdateService.PrepareDetachedUpdateHostAsync(
                    current,
                    SimplePrintComponent.Unified,
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
        args.Any(x =>
            x.Equals(name, StringComparison.OrdinalIgnoreCase));

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
