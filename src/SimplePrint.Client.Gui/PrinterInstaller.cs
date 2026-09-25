using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Client.Gui;

internal sealed class LocalPrinterReadiness
{
    public bool Ready { get; set; }
    public string Detail { get; set; } = "";
    public List<string> Problems { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

internal static class PrinterInstaller
{
    public static async Task<List<string>> GetDriverNamesAsync()
    {
        var r = await PowerShellRunner.RunAsync("ConvertTo-Json -InputObject @(Get-PrinterDriver | Select-Object -ExpandProperty Name) -Compress");
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
        return JsonSerializer.Deserialize<List<string>>(r.StdOut, JsonStore.Options) ?? [];
    }

    public static async Task<bool> EnsureDriverInstalledAsync(string driverName)
    {
        var installed = await GetDriverNamesAsync();
        if (installed.Any(x => x.Equals(driverName, StringComparison.OrdinalIgnoreCase)))
            return true;

        var script = $@"
$driver={PowerShellRunner.Quote(driverName)}
Add-PrinterDriver -Name $driver -ErrorAction Stop
";

        await PrivilegeHelper.RunPowerShellElevatedAsync(script);

        installed = await GetDriverNamesAsync();
        return installed.Any(x => x.Equals(driverName, StringComparison.OrdinalIgnoreCase));
    }

    public static Task InstallAsync(ClientPrinterMapping mapping)
    {
        if (PrinterTransport.IsDirect(mapping.TransportMode))
        {
            var directScript = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$address={PowerShellRunner.Quote(mapping.DirectAddress)}
$deviceUuid={PowerShellRunner.Quote(mapping.DeviceUuid)}
$tag={PowerShellRunner.Quote($"SimplePrint:{mapping.ServerId:N}:{mapping.PrinterId:N}")}

$existing = Get-Printer -Name $printer -ErrorAction SilentlyContinue
if($existing) {{
  throw ('Der Drucker ' + $printer + ' existiert bereits. Er wird aus Sicherheitsgründen nicht verändert.')
}}

if({(string.Equals(mapping.TransportMode, PrinterTransport.Ipp, StringComparison.OrdinalIgnoreCase) ? "$true" : "$false")}) {{
  if([string]::IsNullOrWhiteSpace($address)) {{
    throw 'Für den direkten IPP-Druck wurde keine Geräteadresse übermittelt.'
  }}
  Add-Printer -Name $printer -IppURL $address -Comment $tag -ErrorAction Stop
}} else {{
  if(-not [string]::IsNullOrWhiteSpace($address)) {{
    Add-Printer -Name $printer -DeviceURL $address -Comment $tag -ErrorAction Stop
  }}
  elseif(-not [string]::IsNullOrWhiteSpace($deviceUuid)) {{
    $rawUuid = $deviceUuid.Trim()
    $uuidText = $rawUuid
    if($uuidText.StartsWith('urn:uuid:', [System.StringComparison]::OrdinalIgnoreCase)) {{
      $uuidText = $uuidText.Substring(9)
    }}

    $uuidGuid = [Guid]::Empty
    if(-not [Guid]::TryParse($uuidText, [ref]$uuidGuid)) {{
      throw ('Die vom Server gelieferte WSD-DeviceUUID ist ungültig: ' + $deviceUuid)
    }}

    $candidates = @()
    if($rawUuid.StartsWith('urn:uuid:', [System.StringComparison]::OrdinalIgnoreCase)) {{
      $candidates += $rawUuid
      $candidates += $uuidGuid.ToString()
    }} else {{
      $candidates += $uuidGuid.ToString()
      $candidates += ('urn:uuid:' + $uuidGuid.ToString())
    }}

    $lastError = $null
    foreach($candidate in ($candidates | Select-Object -Unique)) {{
      try {{
        Add-Printer -Name $printer -DeviceUUID $candidate -Comment $tag -ErrorAction Stop
        $lastError = $null
        break
      }}
      catch {{
        $lastError = $_
        $partial = Get-Printer -Name $printer -ErrorAction SilentlyContinue
        if($partial) {{
          Remove-Printer -Name $printer -ErrorAction SilentlyContinue
          Start-Sleep -Milliseconds 300
        }}
      }}
    }}

    if($lastError) {{
      throw ('Windows konnte den WSD-Drucker mit der ermittelten DeviceUUID nicht anlegen. ' + $lastError.Exception.Message)
    }}
  }}
  else {{
    throw 'Für den direkten WSD-Druck wurden weder DeviceURL noch DeviceUUID übermittelt.'
  }}
}}

if(-not (Get-Printer -Name $printer -ErrorAction SilentlyContinue)) {{
  throw 'Windows hat die direkte Druckerqueue nicht angelegt.'
}}
";
            return PrivilegeHelper.RunPowerShellElevatedAsync(directScript);
        }

        var script = $@"
$port={PowerShellRunner.Quote(mapping.PortName)}
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$driver={PowerShellRunner.Quote(mapping.DriverName)}

$existing = Get-Printer -Name $printer -ErrorAction SilentlyContinue
if($existing -and $existing.PortName -ne $port) {{
  throw ('Der Drucker ' + $printer + ' existiert bereits und gehört nicht zu SimplePrint. Er wird nicht verändert.')
}}

if(-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) {{
  Add-PrinterPort -Name $port -PrinterHostAddress '127.0.0.1' -PortNumber {mapping.LocalProxyPort}
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
        if (PrinterTransport.IsDirect(mapping.TransportMode))
        {
            var tag = $"SimplePrint:{mapping.ServerId:N}:{mapping.PrinterId:N}";
            var directScript = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$tag={PowerShellRunner.Quote(tag)}
$existing = Get-Printer -Name $printer -ErrorAction SilentlyContinue
if($existing) {{
  if([string]$existing.Comment -ne $tag) {{
    throw ('Der direkte Drucker ' + $printer + ' trägt nicht die erwartete SimplePrint-Kennung. Er wird aus Sicherheitsgründen nicht gelöscht.')
  }}
  Remove-Printer -Name $printer -ErrorAction Stop
}}
";
            return PrivilegeHelper.RunPowerShellElevatedAsync(directScript);
        }

        var script = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$port={PowerShellRunner.Quote(mapping.PortName)}

$existing = Get-Printer -Name $printer -ErrorAction SilentlyContinue
$removedManagedQueue = $false

if($existing -and $existing.PortName -eq $port) {{
  Remove-Printer -Name $printer -ErrorAction Stop
  $removedManagedQueue = $true
}}
elseif($existing) {{
  # Die gespeicherte SimplePrint-Zuordnung ist veraltet, die vorhandene Windows-Queue
  # zeigt inzwischen auf einen anderen Port (z. B. die echte WSD-/IPP-Queue auf einem
  # Rechner, der zugleich Server und Client ist). Diese fremde/physische Queue niemals
  # löschen. Nur die veralteten SimplePrint-Artefakte bereinigen.
}}

if($removedManagedQueue) {{
  for($i=0; $i -lt 10; $i++) {{
    if(-not (Get-Printer -Name $printer -ErrorAction SilentlyContinue)) {{ break }}
    Start-Sleep -Milliseconds 250
  }}
}}

$portObject = Get-PrinterPort -Name $port -ErrorAction SilentlyContinue
if($portObject) {{
  try {{
    Remove-PrinterPort -Name $port -ErrorAction Stop
  }}
  catch {{
    # Ein noch kurz gesperrter oder bereits fremd referenzierter Port darf die
    # sichere Migration nicht abbrechen. Die physische Windows-Queue bleibt unangetastet.
  }}
}}
";
        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static async Task<LocalPrinterReadiness> GetLocalReadinessAsync(
        ClientPrinterMapping mapping)
    {
        if (PrinterTransport.IsDirect(mapping.TransportMode))
        {
            var directScript = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$address={PowerShellRunner.Quote(mapping.DirectAddress)}
$deviceUuid={PowerShellRunner.Quote(mapping.DeviceUuid)}
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
$problems = @()
if(-not $p) {{ $problems += 'Direkte Windows-Druckerqueue fehlt.' }}

$target = if(-not [string]::IsNullOrWhiteSpace($address)) {{
  $address
}} elseif(-not [string]::IsNullOrWhiteSpace($deviceUuid)) {{
  'UUID ' + $deviceUuid
}} else {{
  'nicht verfügbar'
}}

[pscustomobject]@{{
  Ready = ($problems.Count -eq 0)
  Detail = 'Modus={mapping.TransportMode}; Queue=' + $(if($p){{'vorhanden'}}else{{'fehlt'}}) +
           '; Ziel=' + $target
  Problems = @($problems)
  Warnings = @($problems)
}} | ConvertTo-Json -Compress
";

            var directResult = await PowerShellRunner.RunAsync(directScript);
            if (directResult.ExitCode != 0)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(directResult.StdErr)
                        ? "Direkte Druckbereitschaft konnte nicht geprüft werden."
                        : directResult.StdErr.Trim());

            return JsonSerializer.Deserialize<LocalPrinterReadiness>(
                       directResult.StdOut,
                       JsonStore.Options)
                   ?? new LocalPrinterReadiness
                   {
                       Ready = false,
                       Detail = "Keine lokalen Statusdaten erhalten."
                   };
        }

        var script = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$port={PowerShellRunner.Quote(mapping.PortName)}
$proxyPort={mapping.LocalProxyPort}

$svc = Get-Service -Name SimplePrintClient -ErrorAction SilentlyContinue
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
$pp = Get-PrinterPort -Name $port -ErrorAction SilentlyContinue
$listen = Get-NetTCPConnection -State Listen -LocalPort $proxyPort -ErrorAction SilentlyContinue
$queuePortMatches = ($p -and $p.PortName -eq $port)

$problems = @()
$warnings = @()

if(-not $svc -or [string]$svc.Status -ne 'Running') {{ $problems += 'SimplePrint Client-Agent läuft nicht.' }}
if(-not $p) {{ $problems += 'Windows-Druckerqueue fehlt.' }}
elseif($p.PortName -ne $port) {{ $problems += 'Windows-Drucker verwendet nicht den erwarteten SimplePrint-Port.' }}

if(-not $pp) {{ $problems += 'SimplePrint-Druckerport fehlt.' }}
if(-not $listen) {{ $problems += 'Lokaler SimplePrint-Proxy lauscht nicht auf Port ' + $proxyPort + '.' }}

if($p -and ([string]$p.DriverName -match 'Class Driver|Type1 Class|Type 1 Class|Microsoft IPP')) {{
  $warnings += 'Class-Treiber erkannt. Für den Tunnel muss exakt derselbe Treiber wie am Server verwendet werden.'
}}

[pscustomobject]@{{
  Ready = ($problems.Count -eq 0)
  Detail = 'Dienst=' + $(if($svc){{[string]$svc.Status}}else{{'fehlt'}}) +
           '; Queue=' + $(if($p){{'vorhanden'}}else{{'fehlt'}}) +
           '; Queue-Zuordnung=' + $(if($queuePortMatches){{'korrekt'}}elseif($p){{'falsch'}}else{{'nicht prüfbar'}}) +
           '; Port=' + $(if($pp){{'vorhanden'}}else{{'fehlt'}}) +
           '; Proxy=' + $(if($listen){{'lauscht'}}else{{'nicht aktiv'}})
  Problems = @($problems)
  Warnings = @($problems + $warnings)
}} | ConvertTo-Json -Compress
";

        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(r.StdErr)
                    ? "Lokale Druckbereitschaft konnte nicht geprüft werden."
                    : r.StdErr.Trim());

        return JsonSerializer.Deserialize<LocalPrinterReadiness>(
                   r.StdOut,
                   JsonStore.Options)
               ?? new LocalPrinterReadiness
               {
                   Ready = false,
                   Detail = "Keine lokalen Statusdaten erhalten."
               };
    }

    public static async Task<string> GetQuickDiagnosisAsync(ClientPrinterMapping mapping)
    {
        if (PrinterTransport.IsDirect(mapping.TransportMode))
        {
            var directScript = $@"
$ErrorActionPreference='Continue'
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}

'=== DIREKTDRUCK ==='
'Modus: {mapping.TransportMode}'
'Ziel: ' + $(if(-not [string]::IsNullOrWhiteSpace({PowerShellRunner.Quote(mapping.DirectAddress)})){{{PowerShellRunner.Quote(mapping.DirectAddress)}}}else{{'UUID ' + {PowerShellRunner.Quote(mapping.DeviceUuid)}}})
$p = Get-Printer -Name $printer -ErrorAction SilentlyContinue
if($p) {{
  'Queue: ' + $p.Name
  'Treiber: ' + $p.DriverName
  'Port: ' + $p.PortName
  'Status: ' + $p.PrinterStatus
}} else {{
  'Queue: NICHT GEFUNDEN'
}}
";
            var directResult = await PowerShellRunner.RunAsync(directScript);
            return directResult.StdOut + Environment.NewLine + directResult.StdErr;
        }

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
  if([string]$p.DriverName -match 'Class Driver|Type1 Class|Type 1 Class|Microsoft IPP') {{
    'HINWEIS: Class-Treiber erkannt. Für den Tunnel muss exakt derselbe Treiber wie am Server verwendet werden.'
  }}
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