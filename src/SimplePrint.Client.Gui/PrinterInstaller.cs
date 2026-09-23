using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

internal static class PrinterInstaller
{
    public static async Task<List<string>> GetDriverNamesAsync()
    {
        var r = await PowerShellRunner.RunAsync("ConvertTo-Json -InputObject @(Get-PrinterDriver | Select-Object -ExpandProperty Name) -Compress");
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
        return JsonSerializer.Deserialize<List<string>>(r.StdOut, JsonStore.Options) ?? [];
    }

    public static async Task InstallAsync(ClientPrinterMapping mapping)
    {
        var script = $@"
$ErrorActionPreference='Stop'
$port={PowerShellRunner.Quote(mapping.PortName)}
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$driver={PowerShellRunner.Quote(mapping.DriverName)}
if(-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {{
  Add-PrinterPort -Name $port -PrinterHostAddress '127.0.0.1' -PortNumber {mapping.LocalProxyPort}
}}
if(Get-Printer -Name $printer -ErrorAction SilentlyContinue) {{
  Set-Printer -Name $printer -DriverName $driver -PortName $port
}} else {{
  Add-Printer -Name $printer -DriverName $driver -PortName $port
}}
";
        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
    }

    public static async Task RemoveAsync(ClientPrinterMapping mapping)
    {
        var script = $@"
$ErrorActionPreference='Continue'
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$port={PowerShellRunner.Quote(mapping.PortName)}
Remove-Printer -Name $printer -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Remove-PrinterPort -Name $port -ErrorAction SilentlyContinue
";
        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
    }

    public static async Task<string> GetDiagnosticsAsync()
    {
        var script = @"
$ErrorActionPreference='Continue'
'=== SYSTEM ==='
Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion,OsBuildNumber,CsName | Format-List | Out-String
'=== CLIENT AGENT ==='
Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== SIMPLEPRINT PRINTERS ==='
Get-Printer | Where-Object PortName -Like 'SimplePrint_*' | Format-Table Name,DriverName,PortName,PrinterStatus -AutoSize | Out-String
'=== PORTS ==='
Get-PrinterPort | Where-Object Name -Like 'SimplePrint_*' | Format-Table Name,PrinterHostAddress,PortNumber,SNMPEnabled -AutoSize | Out-String
";
        var r = await PowerShellRunner.RunAsync(script);
        return r.StdOut + Environment.NewLine + r.StdErr;
    }

    public static void PrintTestPage(string printerName)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /k /n \"{printerName.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
