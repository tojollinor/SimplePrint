using System.Diagnostics;
using System.Text;

namespace SimplePrint.Common;

public static class PowerShellRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string script, bool hidden = true)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = hidden,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell konnte nicht gestartet werden.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }

    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
