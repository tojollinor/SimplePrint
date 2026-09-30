using System.Runtime.InteropServices;

namespace SimplePrint.Gui;

/// <summary>
/// Liest den Dienststatus direkt über die Windows-Dienststeuerung statt über einen
/// PowerShell-Prozess (Millisekunden statt ca. einer Sekunde).
/// </summary>
internal static class ServiceStatusReader
{
    public const string Running = "Läuft";

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    public static string GetStatusText(string serviceName)
    {
        var manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
            return "Unbekannt";

        try
        {
            var service = OpenService(manager, serviceName, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST
                    ? "Nicht installiert"
                    : "Unbekannt";
            }

            try
            {
                if (!QueryServiceStatus(service, out var status))
                    return "Unbekannt";

                return status.dwCurrentState switch
                {
                    1 => "Beendet",
                    2 => "Startet",
                    3 => "Wird beendet",
                    4 => Running,
                    5 => "Wird fortgesetzt",
                    6 => "Wird angehalten",
                    7 => "Angehalten",
                    _ => "Unbekannt"
                };
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
