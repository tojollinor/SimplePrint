using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

internal static class WinPrinterHelper
{
    public static async Task<List<LocalPrinterInfo>> GetPrintersAsync()
    {
        const string script = "ConvertTo-Json -InputObject @(Get-Printer | Select-Object Name,DriverName,PortName,PrinterStatus) -Compress";
        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
        return JsonSerializer.Deserialize<List<LocalPrinterInfo>>(r.StdOut, JsonStore.Options) ?? [];
    }

    public static async Task<string> GetDiagnosticsAsync()
    {
        var script = @"
$ErrorActionPreference='Continue'
'=== SYSTEM ==='
Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion,OsBuildNumber,CsName | Format-List | Out-String
'=== SERVICE ==='
Get-Service -Name SimplePrintServer -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== PRINTERS ==='
Get-Printer | Format-Table Name,DriverName,PortName,PrinterStatus -AutoSize | Out-String
'=== FIREWALL ==='
Get-NetFirewallRule -DisplayName 'SimplePrint*' -ErrorAction SilentlyContinue | Select-Object DisplayName,Enabled,Profile,Direction,Action | Format-Table -AutoSize | Out-String
'=== PORTS ==='
Get-NetTCPConnection -LocalPort 45881 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String
Get-NetUDPEndpoint -LocalPort 45880 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String
";
        var r = await PowerShellRunner.RunAsync(script);
        return r.StdOut + Environment.NewLine + r.StdErr;
    }

    public static async Task RepairFirewallAsync()
    {
        var script = @"
$ErrorActionPreference='Stop'
Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'SimplePrint Discovery' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45880 -Profile Private,Domain | Out-Null
New-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45881 -Profile Private,Domain | Out-Null
";
        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
    }

    public static void PrintTestPage(string printerName)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /k /n {QuoteArg(printerName)}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string QuoteArg(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
