using System.Text.Json;

namespace SimplePrint.Common;

public sealed class PrinterRouteGuardResult
{
    public bool Safe { get; set; }
    public string PortName { get; set; } = "";
    public string Reason { get; set; } = "";
}

public static class PrinterRouteGuard
{
    public static async Task<PrinterRouteGuardResult> CheckAsync(string queueName)
    {
        var script = $@"
$ErrorActionPreference='Stop'
$q={PowerShellRunner.Quote(queueName)}
$p = Get-Printer -Name $q -ErrorAction SilentlyContinue

if(-not $p) {{
  [pscustomobject]@{{
    Safe=$false
    PortName=''
    Reason='Windows-Druckerwarteschlange nicht gefunden.'
  }} | ConvertTo-Json -Compress
  exit 0
}}

$portName = [string]$p.PortName
$port = Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue
$hostAddress = ''
$portNumber = $null

if($port) {{
  if($port.PSObject.Properties['PrinterHostAddress']) {{
    $hostAddress = [string]$port.PrinterHostAddress
  }}
  if($port.PSObject.Properties['PortNumber'] -and $port.PortNumber) {{
    $portNumber = [int]$port.PortNumber
  }}
}}

$isSimplePrintPort = $portName -like 'SimplePrint_*' -or $portName -like 'SimplePrintDirect_*'
$isLoopbackProxy = ($hostAddress -in @('127.0.0.1','::1','localhost')) -and
                   ($portNumber -ge 19100 -and $portNumber -le 19999)
$unsafe = $isSimplePrintPort -or $isLoopbackProxy

[pscustomobject]@{{
  Safe = -not $unsafe
  PortName = $portName
  Reason = if($unsafe) {{
    'Druckschleife verhindert: Die Serverqueue zeigt auf einen lokalen SimplePrint-Proxy (' +
    $portName + $(if($hostAddress){{', ' + $hostAddress + ':' + $portNumber}}else{{''}}) + ').'
  }} else {{
    ''
  }}
}} | ConvertTo-Json -Compress
";

        var response = await PowerShellRunner.RunAsync(script);

        if (response.ExitCode != 0)
        {
            return new PrinterRouteGuardResult
            {
                Safe = false,
                Reason = string.IsNullOrWhiteSpace(response.StdErr)
                    ? "Windows-Portzuordnung konnte nicht geprüft werden."
                    : response.StdErr.Trim()
            };
        }

        return JsonSerializer.Deserialize<PrinterRouteGuardResult>(
                   response.StdOut,
                   JsonStore.Options)
               ?? new PrinterRouteGuardResult
               {
                   Safe = false,
                   Reason = "Windows-Portzuordnung lieferte keine gültigen Daten."
               };
    }
}
