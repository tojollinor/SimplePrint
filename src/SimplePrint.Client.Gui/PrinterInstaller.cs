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

    public static Task InstallAsync(ClientPrinterMapping mapping)
    {
        var script = $@"
$port={PowerShellRunner.Quote(mapping.PortName)}
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$driver={PowerShellRunner.Quote(mapping.DriverName)}

$existing = Get-Printer -Name $printer -ErrorAction SilentlyContinue
if($existing -and $existing.PortName -ne $port) {{
  throw ('Der Drucker ' + $printer + ' existiert bereits und gehört nicht zu SimplePrint. Er wird nicht verändert.')
}}

if(-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {{
  Add-PrinterPort -Name $port -PrinterHostAddress '127.0.0.1' -PortNumber {mapping.LocalProxyPort} -SNMP 0
}}

if($existing) {{
  Set-Printer -Name $printer -DriverName $driver -PortName $port
}} else {{
  Add-Printer -Name $printer -DriverName $driver -PortName $port
}}
";
        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static Task RemoveAsync(ClientPrinterMapping mapping)
    {
        var script = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$port={PowerShellRunner.Quote(mapping.PortName)}

$existing = Get-Printer -Name $printer -ErrorAction SilentlyContinue
if($existing) {{
  if($existing.PortName -ne $port) {{
    throw ('Der Drucker ' + $printer + ' verwendet nicht den erwarteten SimplePrint-Port. Er wird aus Sicherheitsgründen nicht gelöscht.')
  }}
  Remove-Printer -Name $printer -ErrorAction Stop
}}

for($i=0; $i -lt 10; $i++) {{
  if(-not (Get-Printer -Name $printer -ErrorAction SilentlyContinue)) {{ break }}
  Start-Sleep -Milliseconds 250
}}

$portObject = Get-PrinterPort -Name $port -ErrorAction SilentlyContinue
if($portObject) {{
  try {{
    Remove-PrinterPort -Name $port -ErrorAction Stop
  }}
  catch {{
    # Die Queue ist bereits sicher entfernt. Ein noch kurz gesperrter Proxy-Port
    # darf den gesamten Entfernen-Vorgang nicht wieder als fehlgeschlagen markieren.
  }}
}}
";
        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static async Task<string> GetQuickDiagnosisAsync(ClientPrinterMapping mapping)
    {
        var script = $@"
$ErrorActionPreference='Continue'
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$port={PowerShellRunner.Quote(mapping.PortName)}
$proxyPort={mapping.LocalProxyPort}

'=== DIENST ==='
$svc = Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue
if($svc) {{ 'SimplePrintClient: ' + $svc.Status }} else {{ 'SimplePrintClient: NICHT INSTALLIERT' }}

'=== WINDOWS-DRUCKER ==='
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
if($p) {{
  'Queue: ' + $p.Name
  'Treiber: ' + $p.DriverName
  if($p.DriverName -like '*IPP Class Driver*') {
    'WARNUNG: Microsoft IPP Class Driver erkannt. Für SimplePrint RAW wird ein Hersteller-PCL6/PS-Treiber empfohlen.'
  }
  'Port: ' + $p.PortName
  'Status: ' + $p.PrinterStatus
  if($p.PortName -eq $port) {{ 'Queue-Zuordnung: OK' }} else {{ 'Queue-Zuordnung: FEHLER' }}
}} else {{
  'Queue: NICHT GEFUNDEN'
}}

'=== SIMPLEPRINT-PORT ==='
$pp = Get-PrinterPort -Name $port -ErrorAction SilentlyContinue
if($pp) {{
  'Port vorhanden: JA'
  'Ziel: ' + $pp.PrinterHostAddress + ':' + $pp.PortNumber
  'SNMP: ' + $pp.SNMPEnabled
}} else {{
  'Port vorhanden: NEIN'
}}

'=== LOKALER PROXY ==='
$listen = Get-NetTCPConnection -State Listen -LocalPort $proxyPort -ErrorAction SilentlyContinue
if($listen) {{ '127.0.0.1:' + $proxyPort + ' lauscht: JA' }} else {{ '127.0.0.1:' + $proxyPort + ' lauscht: NEIN' }}
";
        var r = await PowerShellRunner.RunAsync(script);
        return r.StdOut + Environment.NewLine + r.StdErr;
    }

    public static async Task<string> GetDiagnosticsAsync()
    {
        var script = @"
$ErrorActionPreference='Continue'
'=== SYSTEM ==='
Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion,OsBuildNumber,CsName | Format-List | Out-String
'=== NETWORK PROFILE ==='
Get-NetConnectionProfile | Format-Table Name,InterfaceAlias,NetworkCategory,IPv4Connectivity,IPv6Connectivity -AutoSize | Out-String
'=== CLIENT AGENT ==='
Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== SIMPLEPRINT PRINTERS ==='
Get-Printer | Where-Object { $_.PortName -Like 'SimplePrint_*' -or $_.Name -Like '* (SimplePrint)*' } | Format-Table Name,DriverName,PortName,PrinterStatus -AutoSize | Out-String
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
