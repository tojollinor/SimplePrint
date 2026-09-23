using System.Runtime.InteropServices;

namespace SimplePrint.Common;

public static class WindowsAppIdentity
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    public static void Set(string appId)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _ = SetCurrentProcessExplicitAppUserModelID(appId); }
        catch { }
    }
}
