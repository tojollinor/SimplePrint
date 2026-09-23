using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SimplePrint.Common;

public static class PrivilegeHelper
{
    public static async Task RunPowerShellElevatedAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell konnte nicht gestartet werden.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Die administrative Aktion ist mit Fehlercode {process.ExitCode} fehlgeschlagen.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Die Administratorfreigabe wurde abgebrochen.", ex);
        }
    }
}
