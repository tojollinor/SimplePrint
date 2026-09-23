using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SimplePrint.Common;

public static class PrivilegeHelper
{
    public static async Task RunPowerShellElevatedAsync(string script)
    {
        var token = Guid.NewGuid().ToString("N");
        var scriptPath = Path.Combine(Path.GetTempPath(), $"SimplePrint-{token}.ps1");
        var errorPath = Path.Combine(Path.GetTempPath(), $"SimplePrint-{token}.error.txt");

        var wrapper = $@"
$ErrorActionPreference='Stop'
try {{
{script}
  exit 0
}}
catch {{
  $_ | Out-String | Set-Content -LiteralPath {PowerShellRunner.Quote(errorPath)} -Encoding UTF8
  exit 1
}}
";
        await File.WriteAllTextAsync(scriptPath, wrapper, new UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell konnte nicht gestartet werden.");
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var details = File.Exists(errorPath)
                        ? (await File.ReadAllTextAsync(errorPath)).Trim()
                        : $"Fehlercode {process.ExitCode}";
                    throw new InvalidOperationException($"Die administrative Aktion ist fehlgeschlagen.\r\n\r\n{details}");
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("Die Administratorfreigabe wurde abgebrochen.", ex);
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            try { File.Delete(errorPath); } catch { }
        }
    }
}
