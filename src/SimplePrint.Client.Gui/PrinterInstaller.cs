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

internal sealed class ReusableDirectPrinter
{
    public string Name { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string PortName { get; set; } = "";
}

internal static class PrinterInstaller
{
    public static async Task<List<string>> GetDriverNamesAsync()
    {
        var r = await PowerShellRunner.RunAsync("ConvertTo-Json -InputObject @(Get-PrinterDriver | Select-Object -ExpandProperty Name) -Compress");
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
        return JsonSerializer.Deserialize<List<string>>(r.StdOut, JsonStore.Options) ?? [];
    }

    public static async Task<ReusableDirectPrinter?> FindReusableDirectPrinterAsync(
        string transportMode,
        string directAddress,
        string deviceUuid,
        string serverPortName,
        string preferredName)
    {
        var script = $@"
$transport={PowerShellRunner.Quote(transportMode)}
$targetAddress={PowerShellRunner.Quote(directAddress)}
$targetUuid={PowerShellRunner.Quote(deviceUuid)}
$serverPortName={PowerShellRunner.Quote(serverPortName)}
$preferredName={PowerShellRunner.Quote(preferredName)}

function Normalize-Uuid([string]$value) {{
  if([string]::IsNullOrWhiteSpace($value)) {{ return '' }}
  $v = $value.Trim()
  if($v.StartsWith('urn:uuid:', [System.StringComparison]::OrdinalIgnoreCase)) {{
    $v = $v.Substring(9)
  }}
  $g = [Guid]::Empty
  if([Guid]::TryParse($v, [ref]$g)) {{ return $g.ToString('D') }}
  return $v.ToLowerInvariant()
}}

$normalizedTargetUuid = Normalize-Uuid $targetUuid
$matches = @()

Get-Printer | ForEach-Object {{
  $p = $_
  $port = Get-PrinterPort -Name $p.PortName -ErrorAction SilentlyContinue

  $deviceUrl = ''
  $localUuid = ''
  $hostAddress = ''

  if($port) {{
    if($port.PSObject.Properties['DeviceURL']) {{ $deviceUrl = [string]$port.DeviceURL }}
    if($port.PSObject.Properties['DeviceUUID']) {{ $localUuid = [string]$port.DeviceUUID }}
    if($port.PSObject.Properties['PrinterHostAddress']) {{ $hostAddress = [string]$port.PrinterHostAddress }}
  }}

  $isWsd = ([string]$p.PortName).StartsWith('WSD-',[System.StringComparison]::OrdinalIgnoreCase)
  if($isWsd -and [string]::IsNullOrWhiteSpace($localUuid)) {{
    $wsdKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\' + [string]$p.PortName
    if(Test-Path -LiteralPath $wsdKey) {{
      $wsd = Get-ItemProperty -LiteralPath $wsdKey -ErrorAction SilentlyContinue
      if($wsd) {{
        if($wsd.PSObject.Properties['Printer UUID']) {{
          $localUuid = [string]$wsd.'Printer UUID'
        }}
        if([string]::IsNullOrWhiteSpace($localUuid)) {{
          foreach($property in $wsd.PSObject.Properties) {{
            $value = [string]$property.Value
            if($value -match '(?i)urn:uuid:[0-9a-f]{{8}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{4}}-[0-9a-f]{{12}}') {{
              $localUuid = $Matches[0]
              break
            }}
          }}
        }}
      }}
    }}
  }}

  $sameDevice = $false
  if($transport -eq 'Wsd') {{
    $samePort =
      -not [string]::IsNullOrWhiteSpace($serverPortName) -and
      ([string]$p.PortName).Equals($serverPortName,[System.StringComparison]::OrdinalIgnoreCase)

    $sameUuid =
      -not [string]::IsNullOrWhiteSpace($normalizedTargetUuid) -and
      ((Normalize-Uuid $localUuid) -eq $normalizedTargetUuid)

    $sameDevice = $samePort -or $sameUuid
  }}
  elseif($transport -eq 'Ipp' -and -not [string]::IsNullOrWhiteSpace($targetAddress)) {{
    $sameDevice =
      ([string]$deviceUrl).Equals($targetAddress,[System.StringComparison]::OrdinalIgnoreCase) -or
      ([string]$hostAddress).Equals($targetAddress,[System.StringComparison]::OrdinalIgnoreCase)
  }}

  if($sameDevice) {{
    $matches += [pscustomobject]@{{
      Name = [string]$p.Name
      DriverName = [string]$p.DriverName
      PortName = [string]$p.PortName
      Preferred = ([string]$p.Name).Equals($preferredName,[System.StringComparison]::OrdinalIgnoreCase)
    }}
  }}
}}

$selected = $matches | Sort-Object Preferred -Descending | Select-Object -First 1
if($selected) {{ $selected | ConvertTo-Json -Compress }}
";

        var result = await PowerShellRunner.RunAsync(script);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? "Vorhandene direkte Windows-Druckerqueues konnten nicht geprüft werden."
                    : result.StdErr.Trim());

        if (string.IsNullOrWhiteSpace(result.StdOut))
            return null;

        return JsonSerializer.Deserialize<ReusableDirectPrinter>(
            result.StdOut,
            JsonStore.Options);
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

    private static async Task InstallWindowsShareAsync(ClientPrinterMapping mapping)
    {
        var shareScript = $@"
$ErrorActionPreference='Stop'
$connection={PowerShellRunner.Quote(mapping.DirectAddress)}
if([string]::IsNullOrWhiteSpace($connection) -or -not $connection.StartsWith('\\')) {{
  throw 'Die Windows-Druckerfreigabe ist ungültig.'
}}

$existing = Get-Printer -Name $connection -ErrorAction SilentlyContinue
if(-not $existing) {{
  Add-Printer -ConnectionName $connection -ErrorAction Stop
  Start-Sleep -Milliseconds 800
  $existing = Get-Printer -Name $connection -ErrorAction SilentlyContinue
}}

if(-not $existing) {{
  throw ('Windows konnte die Server-Druckerfreigabe nicht verbinden: ' + $connection)
}}
";

        var normal = await PowerShellRunner.RunAsync(shareScript);
        if (normal.ExitCode == 0)
            return;

        var detail = string.Join(
            Environment.NewLine,
            new[] { normal.StdErr, normal.StdOut }
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            .Trim();

        var credentialError =
            detail.Contains("0x8007052e", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("alternative Benutzeranmeldeinformationen", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Logon failure", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Anmeldefehler", StringComparison.OrdinalIgnoreCase);

        if (credentialError)
        {
            throw new InvalidOperationException(
                "Die Windows-Druckerfreigabe des SimplePrint-Servers verlangt Netzwerk-Anmeldedaten. " +
                "Das ist kein lokales Administratorproblem und würde durch eine weitere UAC-Abfrage nicht behoben. " +
                "SimplePrint hat deshalb keinen unnötigen zweiten Admin-Prompt geöffnet.\r\n\r\n" +
                detail);
        }

        var elevationLikelyRequired =
            detail.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Administrator", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("elevation", StringComparison.OrdinalIgnoreCase);

        if (elevationLikelyRequired)
        {
            await PrivilegeHelper.RunPowerShellElevatedAsync(shareScript);
            return;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? "Die Windows-Druckerfreigabe konnte nicht verbunden werden."
                : detail);
    }

    public static Task InstallAsync(ClientPrinterMapping mapping)
    {
        if (string.Equals(
                mapping.TransportMode,
                PrinterTransport.WindowsShare,
                StringComparison.OrdinalIgnoreCase))
        {
            return InstallWindowsShareAsync(mapping);
        }

        if (PrinterTransport.IsDirect(mapping.TransportMode))
        {
            if (mapping.UseExistingQueue)
            {
                var verifyExistingScript = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
if(-not (Get-Printer -Name $printer -ErrorAction SilentlyContinue)) {{
  throw ('Die übernommene Windows-Druckerqueue ' + $printer + ' wurde nicht gefunden.')
}}
";
                return PrivilegeHelper.RunPowerShellElevatedAsync(verifyExistingScript);
            }

            var directScript = $@"
$printer={PowerShellRunner.Quote(mapping.LocalPrinterName)}
$address={PowerShellRunner.Quote(mapping.DirectAddress)}
$deviceUuid={PowerShellRunner.Quote(mapping.DeviceUuid)}
$driver={PowerShellRunner.Quote(mapping.DriverName)}
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
    function Normalize-Uuid([string]$value) {{
      if([string]::IsNullOrWhiteSpace($value)) {{ return '' }}
      $v = $value.Trim()
      if($v.StartsWith('urn:uuid:', [System.StringComparison]::OrdinalIgnoreCase)) {{
        $v = $v.Substring(9)
      }}

      $g = [Guid]::Empty
      if([Guid]::TryParse($v, [ref]$g)) {{ return $g.ToString('D') }}
      return $v.ToLowerInvariant()
    }}

    function Find-MatchingWsdPort([string]$targetUuid) {{
      $normalizedTarget = Normalize-Uuid $targetUuid
      if([string]::IsNullOrWhiteSpace($normalizedTarget)) {{ return $null }}

      foreach($port in @(Get-PrinterPort -ErrorAction SilentlyContinue | Where-Object Name -Like 'WSD-*')) {{
        $localUuid = ''
        if($port.PSObject.Properties['DeviceUUID']) {{
          $localUuid = [string]$port.DeviceUUID
        }}

        if([string]::IsNullOrWhiteSpace($localUuid)) {{
          $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\' + [string]$port.Name
          if(Test-Path -LiteralPath $key) {{
            $wsd = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
            if($wsd -and $wsd.PSObject.Properties['Printer UUID']) {{
              $localUuid = [string]$wsd.'Printer UUID'
            }}
          }}
        }}

        if((Normalize-Uuid $localUuid) -eq $normalizedTarget) {{
          return [string]$port.Name
        }}
      }}

      return $null
    }}

    function Try-CreateFromKnownPort([string]$targetUuid) {{
      $knownPort = Find-MatchingWsdPort $targetUuid
      if([string]::IsNullOrWhiteSpace($knownPort)) {{ return $false }}

      if(-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {{
        Add-PrinterDriver -Name $driver -ErrorAction SilentlyContinue
      }}

      try {{
        Add-Printer -Name $printer -DriverName $driver -PortName $knownPort -Comment $tag -ErrorAction Stop
        return $true
      }}
      catch {{
        return $false
      }}
    }}

    $rawUuid = $deviceUuid.Trim()
    $uuidText = Normalize-Uuid $rawUuid
    $uuidGuid = [Guid]::Empty
    if(-not [Guid]::TryParse($uuidText, [ref]$uuidGuid)) {{
      throw ('Die vom Server gelieferte WSD-DeviceUUID ist ungültig: ' + $deviceUuid)
    }}

    function Ensure-WsdDiscoveryPrerequisites {{
      $publicProfiles = @(
        Get-NetConnectionProfile -ErrorAction SilentlyContinue |
          Where-Object {{ $_.IPv4Connectivity -ne 'Disconnected' -and $_.NetworkCategory -eq 'Public' }}
      )

      if($publicProfiles.Count -gt 0) {{
        throw (
          'Die aktive Windows-Netzwerkverbindung ist als Öffentlich eingestuft. ' +
          'WSD-Erkennung wird von SimplePrint aus Sicherheitsgründen nur in privaten oder Domänennetzwerken automatisch freigeschaltet.')
      }}

      $fd = Get-Service -Name fdPHost -ErrorAction SilentlyContinue
      if($fd -and $fd.Status -ne 'Running') {{
        Start-Service -Name fdPHost -ErrorAction Stop
      }}

      $wsdRules = @(
        'SimplePrint-WSD-Discovery-In',
        'SimplePrint-WSD-Events-In',
        'SimplePrint-WSD-EventsSecure-In'
      )

      foreach($rule in $wsdRules) {{
        Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
      }}

      New-NetFirewallRule -Name 'SimplePrint-WSD-Discovery-In' -DisplayName 'SimplePrint WSD Discovery' -Direction Inbound -Action Allow -Enabled True -Protocol UDP -LocalPort 3702 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
      New-NetFirewallRule -Name 'SimplePrint-WSD-Events-In' -DisplayName 'SimplePrint WSD Events' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort 5357 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
      New-NetFirewallRule -Name 'SimplePrint-WSD-EventsSecure-In' -DisplayName 'SimplePrint WSD Events Secure' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort 5358 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null

      try {{ & pnputil.exe /scan-devices | Out-Null }} catch {{}}
    }}

    $created = Try-CreateFromKnownPort $rawUuid

    if(-not $created) {{
      Ensure-WsdDiscoveryPrerequisites
      Start-Sleep -Milliseconds 1800
      $created = Try-CreateFromKnownPort $rawUuid
    }}

    $candidates = @(
      $uuidGuid.ToString(),
      ('urn:uuid:' + $uuidGuid.ToString())
    ) | Select-Object -Unique

    $lastError = $null

    if(-not $created) {{
      for($round = 0; $round -lt 3 -and -not $created; $round++) {{
        foreach($candidate in $candidates) {{
          try {{
            Add-Printer -Name $printer -DeviceUUID $candidate -Comment $tag -ErrorAction Stop
            $lastError = $null
            $created = $true
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

        if(-not $created) {{
          Start-Sleep -Milliseconds 1800
          $created = Try-CreateFromKnownPort $rawUuid
        }}
      }}
    }}

    if(-not $created) {{
      $detail = if($lastError) {{ $lastError.Exception.Message }} else {{ 'Keine passende lokale WSD-Gegenstelle gefunden.' }}
      throw (
        'Windows konnte den WSD-Drucker nicht über die vom Server ermittelte DeviceUUID finden. ' +
        'DeviceUUID: ' + $uuidGuid.ToString() + '. ' +
        'SimplePrint hat den Function-Discovery-Dienst gestartet, die für WSD benötigten ' +
        'Firewallports 3702/UDP sowie 5357-5358/TCP für Privat/Domäne + LocalSubnet freigeschaltet, ' +
        'die Geräteerkennung neu angestoßen und vorhandene WSD-Ports geprüft. ' +
        'Der Drucker muss vom Client im selben Netzwerk per WSD erreichbar sein. Details: ' + $detail)
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
        if (string.Equals(
                mapping.TransportMode,
                PrinterTransport.WindowsShare,
                StringComparison.OrdinalIgnoreCase))
        {
            var shareScript = $@"
$connection={PowerShellRunner.Quote(mapping.DirectAddress)}
$p = Get-Printer -Name $connection -ErrorAction SilentlyContinue
if($p) {{
  Remove-Printer -Name $connection -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 400
}}

if(Get-Printer -Name $connection -ErrorAction SilentlyContinue) {{
  & rundll32.exe printui.dll,PrintUIEntry /dn /n $connection
  Start-Sleep -Milliseconds 400
}}

if(Get-Printer -Name $connection -ErrorAction SilentlyContinue) {{
  throw ('Die verbundene Server-Druckerqueue konnte nicht entfernt werden: ' + $connection)
}}
";
            return PrivilegeHelper.RunPowerShellElevatedAsync(shareScript);
        }

        if (PrinterTransport.IsDirect(mapping.TransportMode))
        {
            if (mapping.UseExistingQueue)
                return Task.CompletedTask;

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
$adopted={(mapping.UseExistingQueue ? "$true" : "$false")}
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
           '; Queue-Typ=' + $(if($adopted){{'vorhandene Windows-Queue übernommen'}}else{{'von SimplePrint angelegt'}}) +
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
'Queue-Typ: {(mapping.UseExistingQueue ? "vorhandene Windows-Queue übernommen" : "von SimplePrint angelegt")}'
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