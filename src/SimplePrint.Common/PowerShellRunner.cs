using System.Diagnostics;
using System.Text;

namespace SimplePrint.Common;

public static class PowerShellRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string script,
        bool hidden = true,
        TimeSpan? timeout = null)
    {
        var wrappedScript =
            "$ProgressPreference='SilentlyContinue';$InformationPreference='SilentlyContinue';" +
            "try{[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)}catch{};" +
            "$OutputEncoding=New-Object System.Text.UTF8Encoding($false);" +
            Environment.NewLine +
            script;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrappedScript));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = hidden,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("PowerShell konnte nicht gestartet werden.");

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        var limit = timeout ?? DefaultTimeout;
        using var cts = new CancellationTokenSource(limit);

        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            await Task.WhenAny(
                Task.WhenAll(stdout, stderr),
                Task.Delay(TimeSpan.FromSeconds(2)));

            var partialOut = stdout.IsCompletedSuccessfully ? stdout.Result : "";
            var partialErr = stderr.IsCompletedSuccessfully ? stderr.Result : "";

            return (
                -1,
                partialOut,
                (partialErr + Environment.NewLine +
                 $"Zeitüberschreitung: Die Windows-Aktion wurde nach {limit.TotalSeconds:0} s abgebrochen.").Trim());
        }

        var output = await stdout;
        var error = await stderr;

        // Endet PowerShell mit einem Fehlercode, ohne etwas auf stderr auszugeben,
        // wäre die Ursache sonst nirgends erkennbar (z. B. im Dienst-Log).
        if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(error))
        {
            error = $"Die PowerShell-Aktion endete mit Exitcode {p.ExitCode}.";

            if (!string.IsNullOrWhiteSpace(output))
                error += " Ausgabe: " + output.Trim();
        }

        return (p.ExitCode, output, error);
    }

    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
