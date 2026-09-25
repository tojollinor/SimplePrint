using System.Diagnostics;
using System.Text.Json;
using SimplePrint.Common;

namespace SimplePrint.Server.Gui;

internal static class WinPrinterHelper
{
    public static async Task<List<LocalPrinterInfo>> GetPrintersAsync()
    {
        const string script = """
$items = @(Get-Printer | ForEach-Object {
  $p = $_
  $port = Get-PrinterPort -Name $p.PortName -ErrorAction SilentlyContinue

  $deviceUrl = ''
  $deviceUuid = ''
  $hostAddress = ''
  $portNumber = $null

  if($port) {
    if($port.PSObject.Properties['DeviceURL']) { $deviceUrl = [string]$port.DeviceURL }
    if($port.PSObject.Properties['DeviceUUID']) { $deviceUuid = [string]$port.DeviceUUID }
    if($port.PSObject.Properties['PrinterHostAddress']) { $hostAddress = [string]$port.PrinterHostAddress }
    if($port.PSObject.Properties['PortNumber'] -and $port.PortNumber) { $portNumber = [int]$port.PortNumber }
  }

  $isWsdPort = ([string]$p.PortName).StartsWith('WSD-',[System.StringComparison]::OrdinalIgnoreCase)

  if($isWsdPort -and
     ([string]::IsNullOrWhiteSpace($deviceUrl) -or [string]::IsNullOrWhiteSpace($deviceUuid))) {
    $wsdKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\' + [string]$p.PortName

    if(Test-Path -LiteralPath $wsdKey) {
      $wsd = Get-ItemProperty -LiteralPath $wsdKey -ErrorAction SilentlyContinue

      if($wsd) {
        if([string]::IsNullOrWhiteSpace($deviceUuid) -and $wsd.PSObject.Properties['Printer UUID']) {
          $deviceUuid = [string]$wsd.'Printer UUID'
        }

        foreach($property in $wsd.PSObject.Properties) {
          if($property.Name -match '^PS(Path|ParentPath|ChildName|Drive|Provider)
  $directAddress = ''

  if([string]$p.DriverName -match 'Microsoft IPP Class Driver') {
    if($isWsdPort -and
       (-not [string]::IsNullOrWhiteSpace($deviceUrl) -or
        -not [string]::IsNullOrWhiteSpace($deviceUuid))) {
      # WSD ports do not always expose DeviceURL through Get-PrinterPort.
      # DeviceUUID is an equally valid Windows discovery target.
      $transportMode = 'Wsd'
      $directAddress = $deviceUrl
    }
    elseif(-not [string]::IsNullOrWhiteSpace($deviceUrl)) {
      $directAddress = $deviceUrl
      # IPP directed discovery may expose http/https as well as ipp/ipps URLs.
      $transportMode = 'Ipp'
    }
    elseif(-not [string]::IsNullOrWhiteSpace($hostAddress) -and
           $hostAddress -notin @('127.0.0.1','::1','localhost')) {
      $directAddress = $hostAddress
      $transportMode = 'Ipp'
    }
  }

  [pscustomobject]@{
    Name = [string]$p.Name
    DriverName = [string]$p.DriverName
    PortName = [string]$p.PortName
    PrinterStatus = [string]$p.PrinterStatus
    TransportMode = $transportMode
    DirectAddress = $directAddress
    DeviceUuid = $deviceUuid
    PrinterHostAddress = $hostAddress
    PortNumber = $portNumber
  }
})

ConvertTo-Json -InputObject $items -Compress
""";

        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);

        return JsonSerializer.Deserialize<List<LocalPrinterInfo>>(
                   r.StdOut,
                   JsonStore.Options)
               ?? [];
    }

    public static bool IsUnsafeSimplePrintLoop(LocalPrinterInfo printer) =>
        PrinterTransport.IsSimplePrintPort(printer.PortName) ||
        PrinterTransport.IsLoopbackProxy(printer.PrinterHostAddress, printer.PortNumber);

    public static async Task<(FirewallRuleStatus Discovery, FirewallRuleStatus Gateway)> GetFirewallStateAsync()
    {
        const string script = @"
function Get-SimplePrintRuleState($name,$display,$protocol,$port) {
  $r = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
  if(-not $r) {
    $r = Get-NetFirewallRule -DisplayName $display -ErrorAction SilentlyContinue | Select-Object -First 1
  }

  if(-not $r) {
    return [pscustomobject]@{
      Name=$display; Exists=$false; Enabled=$false; Correct=$false; Detail='Regel fehlt'
    }
  }

  $pf = $r | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
  $af = $r | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue
  $profile = [string]$r.Profile
  $remote = @($af.RemoteAddress) -join ','

  $enabled = ([string]$r.Enabled -eq 'True')
  $profileOk = ($profile -match 'Private') -and ($profile -match 'Domain') -and ($profile -notmatch 'Public')
  $protocolText = [string]$pf.Protocol
  $protocolOk = ($protocolText -eq $protocol) -or
                ($protocol -eq 'UDP' -and $protocolText -eq '17') -or
                ($protocol -eq 'TCP' -and $protocolText -eq '6')
  $portOk = $protocolOk -and ([string]$pf.LocalPort -eq [string]$port)
  $addressOk = $remote -match 'LocalSubnet'
  $correct = $enabled -and ([string]$r.Direction -eq 'Inbound') -and
             ([string]$r.Action -eq 'Allow') -and $profileOk -and $portOk -and $addressOk

  $detail = 'Enabled=' + $enabled +
            '; Profile=' + $profile +
            '; Protocol=' + $protocolText +
            '; Port=' + [string]$pf.LocalPort +
            '; Remote=' + $remote

  [pscustomobject]@{
    Name=$display; Exists=$true; Enabled=$enabled; Correct=$correct; Detail=$detail
  }
}

@(
  Get-SimplePrintRuleState 'SimplePrint-Discovery' 'SimplePrint Discovery' 'UDP' 45880
  Get-SimplePrintRuleState 'SimplePrint-Gateway' 'SimplePrint Print Gateway' 'TCP' 45881
) | ConvertTo-Json -Compress
";

        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(r.StdErr)
                    ? "Firewallstatus konnte nicht gelesen werden."
                    : r.StdErr.Trim());

        var states = JsonSerializer.Deserialize<List<FirewallRuleStatus>>(
                         r.StdOut,
                         JsonStore.Options)
                     ?? [];

        FirewallRuleStatus Find(string name) =>
            states.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? new FirewallRuleStatus { Name = name, Detail = "Keine Statusdaten" };

        return (
            Find("SimplePrint Discovery"),
            Find("SimplePrint Print Gateway"));
    }

    public static Task ApplyFirewallAsync()
    {
        const string script = """
$ErrorActionPreference='Stop'

Get-NetFirewallRule -Name 'SimplePrint-Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -Name 'SimplePrint-Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

New-NetFirewallRule -Name 'SimplePrint-Discovery' -DisplayName 'SimplePrint Discovery' -Description 'SimplePrint Server Discovery im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol UDP -LocalPort 45880 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name 'SimplePrint-Gateway' -DisplayName 'SimplePrint Print Gateway' -Description 'SimplePrint RAW Print Gateway im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort 45881 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null

function Assert-Rule($name,$protocol,$port) {
  $r = Get-NetFirewallRule -Name $name -ErrorAction Stop
  $pf = $r | Get-NetFirewallPortFilter
  $af = $r | Get-NetFirewallAddressFilter

  if([string]$r.Enabled -ne 'True') { throw "$name ist nicht aktiviert." }
  if([string]$r.Direction -ne 'Inbound') { throw "$name hat eine falsche Richtung." }
  if([string]$r.Action -ne 'Allow') { throw "$name erlaubt den Verkehr nicht." }

  $profile = [string]$r.Profile
  if($profile -notmatch 'Private' -or $profile -notmatch 'Domain' -or $profile -match 'Public') {
    throw "$name hat ein falsches Firewallprofil: $profile"
  }

  if([string]$pf.LocalPort -ne [string]$port) {
    throw "$name hat einen falschen Port: $($pf.LocalPort)"
  }

  $remote = @($af.RemoteAddress) -join ','
  if($remote -notmatch 'LocalSubnet') {
    throw "$name ist nicht auf LocalSubnet beschränkt."
  }
}

Assert-Rule 'SimplePrint-Discovery' 'UDP' 45880
Assert-Rule 'SimplePrint-Gateway' 'TCP' 45881

$private = Get-NetFirewallProfile -Profile Private -ErrorAction SilentlyContinue
$domain = Get-NetFirewallProfile -Profile Domain -ErrorAction SilentlyContinue

if(($private -and [string]$private.AllowLocalFirewallRules -eq 'False') -and
   ($domain -and [string]$domain.AllowLocalFirewallRules -eq 'False')) {
  throw 'Windows/Gruppenrichtlinie blockiert lokale Firewallregeln für Privat und Domäne.'
}
""";

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static Task RemoveFirewallAsync()
    {
        const string script = @"
$ErrorActionPreference='Stop'
Get-NetFirewallRule -Name 'SimplePrint-Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -Name 'SimplePrint-Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
";

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static async Task RestartServerServiceAsync()
    {
        const string script = @"
$ErrorActionPreference='Stop'
$svc = Get-Service -Name SimplePrintServer -ErrorAction Stop

if($svc.Status -ne 'Stopped') {
  Restart-Service -Name SimplePrintServer -Force -ErrorAction Stop
} else {
  Start-Service -Name SimplePrintServer -ErrorAction Stop
}

(Get-Service -Name SimplePrintServer -ErrorAction Stop).WaitForStatus('Running',[TimeSpan]::FromSeconds(12))

if((Get-Service -Name SimplePrintServer).Status -ne 'Running') {
  throw 'SimplePrintServer konnte nicht gestartet werden.'
}
";

        await PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static Task<PrinterHealthStatus> ProbePrinterAsync(
        string printerName,
        Guid printerId = default,
        CancellationToken ct = default)
        => PrinterHealthProbe.ProbeAsync(printerName, printerId, ct);

    public static void OpenQueue(string printerName)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /o /n {QuoteArg(printerName)}",
            UseShellExecute = true
        });
    }

    public static async Task<string> GetDiagnosticsAsync()
    {
        var script = @"
$ErrorActionPreference='Continue'
'=== SYSTEM ==='
Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion,OsBuildNumber,CsName | Format-List | Out-String
'=== SERVICE ==='
Get-Service -Name SimplePrintServer -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== SPOOLER ==='
Get-Service -Name Spooler -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== NETWORK PROFILE ==='
Get-NetConnectionProfile | Format-Table Name,InterfaceAlias,NetworkCategory,IPv4Connectivity -AutoSize | Out-String
'=== PRINTERS ==='
Get-Printer | Format-Table Name,DriverName,PortName,PrinterStatus,JobCount -AutoSize | Out-String
'=== PRINTER PORTS ==='
Get-PrinterPort | Select-Object Name,PrinterHostAddress,PortNumber,DeviceURL,DeviceUUID,SNMPEnabled,SNMPCommunity | Format-Table -AutoSize | Out-String
'=== WSD PORT REGISTRY ==='
$wsdRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports'
if(Test-Path -LiteralPath $wsdRoot) {
  Get-ChildItem -LiteralPath $wsdRoot -ErrorAction SilentlyContinue | ForEach-Object {
    '--- ' + $_.PSChildName + ' ---'
    Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue |
      Select-Object * -ExcludeProperty PSPath,PSParentPath,PSChildName,PSDrive,PSProvider |
      Format-List | Out-String
  }
} else {
  'WSD-Port-Registrypfad nicht vorhanden.'
}
'=== FIREWALL RULES ==='
Get-NetFirewallRule -DisplayName 'SimplePrint*' -ErrorAction SilentlyContinue | Select-Object Name,DisplayName,Enabled,Profile,Direction,Action | Format-Table -AutoSize | Out-String
'=== FIREWALL PORT FILTERS ==='
Get-NetFirewallRule -DisplayName 'SimplePrint*' -ErrorAction SilentlyContinue | Get-NetFirewallPortFilter | Format-Table Protocol,LocalPort,RemotePort -AutoSize | Out-String
'=== FIREWALL ADDRESS FILTERS ==='
Get-NetFirewallRule -DisplayName 'SimplePrint*' -ErrorAction SilentlyContinue | Get-NetFirewallAddressFilter | Format-Table LocalAddress,RemoteAddress -AutoSize | Out-String
'=== FIREWALL PROFILES ==='
Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultInboundAction,AllowInboundRules,AllowLocalFirewallRules | Format-Table -AutoSize | Out-String
'=== LISTENERS ==='
Get-NetTCPConnection -LocalPort 45881 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String
Get-NetUDPEndpoint -LocalPort 45880 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String
";

        var r = await PowerShellRunner.RunAsync(script);
        return r.StdOut + Environment.NewLine + r.StdErr;
    }

    public static void PrintTestPage(string printerName)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /k /n {QuoteArg(printerName)}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string QuoteArg(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";
}) { continue }

          $value = [string]$property.Value
          if([string]::IsNullOrWhiteSpace($value)) { continue }

          if([string]::IsNullOrWhiteSpace($deviceUuid) -and
             $value -match '(?i)urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}') {
            $deviceUuid = $Matches[0]
          }

          if([string]::IsNullOrWhiteSpace($deviceUrl) -and
             $value -match '^(?i)(https?|ipps?)://') {
            $deviceUrl = $value
          }
        }
      }
    }
  }

  $transportMode = 'Tunnel'
  $directAddress = ''

  if([string]$p.DriverName -match 'Microsoft IPP Class Driver') {
    $isWsdPort = ([string]$p.PortName).StartsWith('WSD-',[System.StringComparison]::OrdinalIgnoreCase)

    if($isWsdPort -and
       (-not [string]::IsNullOrWhiteSpace($deviceUrl) -or
        -not [string]::IsNullOrWhiteSpace($deviceUuid))) {
      # WSD ports do not always expose DeviceURL through Get-PrinterPort.
      # DeviceUUID is an equally valid Windows discovery target.
      $transportMode = 'Wsd'
      $directAddress = $deviceUrl
    }
    elseif(-not [string]::IsNullOrWhiteSpace($deviceUrl)) {
      $directAddress = $deviceUrl
      # IPP directed discovery may expose http/https as well as ipp/ipps URLs.
      $transportMode = 'Ipp'
    }
    elseif(-not [string]::IsNullOrWhiteSpace($hostAddress) -and
           $hostAddress -notin @('127.0.0.1','::1','localhost')) {
      $directAddress = $hostAddress
      $transportMode = 'Ipp'
    }
  }

  [pscustomobject]@{
    Name = [string]$p.Name
    DriverName = [string]$p.DriverName
    PortName = [string]$p.PortName
    PrinterStatus = [string]$p.PrinterStatus
    TransportMode = $transportMode
    DirectAddress = $directAddress
    DeviceUuid = $deviceUuid
    PrinterHostAddress = $hostAddress
    PortNumber = $portNumber
  }
})

ConvertTo-Json -InputObject $items -Compress
""";

        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);

        return JsonSerializer.Deserialize<List<LocalPrinterInfo>>(
                   r.StdOut,
                   JsonStore.Options)
               ?? [];
    }

    public static bool IsUnsafeSimplePrintLoop(LocalPrinterInfo printer) =>
        PrinterTransport.IsSimplePrintPort(printer.PortName) ||
        PrinterTransport.IsLoopbackProxy(printer.PrinterHostAddress, printer.PortNumber);

    public static async Task<(FirewallRuleStatus Discovery, FirewallRuleStatus Gateway)> GetFirewallStateAsync()
    {
        const string script = @"
function Get-SimplePrintRuleState($name,$display,$protocol,$port) {
  $r = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
  if(-not $r) {
    $r = Get-NetFirewallRule -DisplayName $display -ErrorAction SilentlyContinue | Select-Object -First 1
  }

  if(-not $r) {
    return [pscustomobject]@{
      Name=$display; Exists=$false; Enabled=$false; Correct=$false; Detail='Regel fehlt'
    }
  }

  $pf = $r | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
  $af = $r | Get-NetFirewallAddressFilter -ErrorAction SilentlyContinue
  $profile = [string]$r.Profile
  $remote = @($af.RemoteAddress) -join ','

  $enabled = ([string]$r.Enabled -eq 'True')
  $profileOk = ($profile -match 'Private') -and ($profile -match 'Domain') -and ($profile -notmatch 'Public')
  $protocolText = [string]$pf.Protocol
  $protocolOk = ($protocolText -eq $protocol) -or
                ($protocol -eq 'UDP' -and $protocolText -eq '17') -or
                ($protocol -eq 'TCP' -and $protocolText -eq '6')
  $portOk = $protocolOk -and ([string]$pf.LocalPort -eq [string]$port)
  $addressOk = $remote -match 'LocalSubnet'
  $correct = $enabled -and ([string]$r.Direction -eq 'Inbound') -and
             ([string]$r.Action -eq 'Allow') -and $profileOk -and $portOk -and $addressOk

  $detail = 'Enabled=' + $enabled +
            '; Profile=' + $profile +
            '; Protocol=' + $protocolText +
            '; Port=' + [string]$pf.LocalPort +
            '; Remote=' + $remote

  [pscustomobject]@{
    Name=$display; Exists=$true; Enabled=$enabled; Correct=$correct; Detail=$detail
  }
}

@(
  Get-SimplePrintRuleState 'SimplePrint-Discovery' 'SimplePrint Discovery' 'UDP' 45880
  Get-SimplePrintRuleState 'SimplePrint-Gateway' 'SimplePrint Print Gateway' 'TCP' 45881
) | ConvertTo-Json -Compress
";

        var r = await PowerShellRunner.RunAsync(script);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(r.StdErr)
                    ? "Firewallstatus konnte nicht gelesen werden."
                    : r.StdErr.Trim());

        var states = JsonSerializer.Deserialize<List<FirewallRuleStatus>>(
                         r.StdOut,
                         JsonStore.Options)
                     ?? [];

        FirewallRuleStatus Find(string name) =>
            states.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? new FirewallRuleStatus { Name = name, Detail = "Keine Statusdaten" };

        return (
            Find("SimplePrint Discovery"),
            Find("SimplePrint Print Gateway"));
    }

    public static Task ApplyFirewallAsync()
    {
        const string script = """
$ErrorActionPreference='Stop'

Get-NetFirewallRule -Name 'SimplePrint-Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -Name 'SimplePrint-Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

New-NetFirewallRule -Name 'SimplePrint-Discovery' -DisplayName 'SimplePrint Discovery' -Description 'SimplePrint Server Discovery im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol UDP -LocalPort 45880 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name 'SimplePrint-Gateway' -DisplayName 'SimplePrint Print Gateway' -Description 'SimplePrint RAW Print Gateway im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort 45881 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null

function Assert-Rule($name,$protocol,$port) {
  $r = Get-NetFirewallRule -Name $name -ErrorAction Stop
  $pf = $r | Get-NetFirewallPortFilter
  $af = $r | Get-NetFirewallAddressFilter

  if([string]$r.Enabled -ne 'True') { throw "$name ist nicht aktiviert." }
  if([string]$r.Direction -ne 'Inbound') { throw "$name hat eine falsche Richtung." }
  if([string]$r.Action -ne 'Allow') { throw "$name erlaubt den Verkehr nicht." }

  $profile = [string]$r.Profile
  if($profile -notmatch 'Private' -or $profile -notmatch 'Domain' -or $profile -match 'Public') {
    throw "$name hat ein falsches Firewallprofil: $profile"
  }

  if([string]$pf.LocalPort -ne [string]$port) {
    throw "$name hat einen falschen Port: $($pf.LocalPort)"
  }

  $remote = @($af.RemoteAddress) -join ','
  if($remote -notmatch 'LocalSubnet') {
    throw "$name ist nicht auf LocalSubnet beschränkt."
  }
}

Assert-Rule 'SimplePrint-Discovery' 'UDP' 45880
Assert-Rule 'SimplePrint-Gateway' 'TCP' 45881

$private = Get-NetFirewallProfile -Profile Private -ErrorAction SilentlyContinue
$domain = Get-NetFirewallProfile -Profile Domain -ErrorAction SilentlyContinue

if(($private -and [string]$private.AllowLocalFirewallRules -eq 'False') -and
   ($domain -and [string]$domain.AllowLocalFirewallRules -eq 'False')) {
  throw 'Windows/Gruppenrichtlinie blockiert lokale Firewallregeln für Privat und Domäne.'
}
""";

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static Task RemoveFirewallAsync()
    {
        const string script = @"
$ErrorActionPreference='Stop'
Get-NetFirewallRule -Name 'SimplePrint-Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -Name 'SimplePrint-Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
";

        return PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static async Task RestartServerServiceAsync()
    {
        const string script = @"
$ErrorActionPreference='Stop'
$svc = Get-Service -Name SimplePrintServer -ErrorAction Stop

if($svc.Status -ne 'Stopped') {
  Restart-Service -Name SimplePrintServer -Force -ErrorAction Stop
} else {
  Start-Service -Name SimplePrintServer -ErrorAction Stop
}

(Get-Service -Name SimplePrintServer -ErrorAction Stop).WaitForStatus('Running',[TimeSpan]::FromSeconds(12))

if((Get-Service -Name SimplePrintServer).Status -ne 'Running') {
  throw 'SimplePrintServer konnte nicht gestartet werden.'
}
";

        await PrivilegeHelper.RunPowerShellElevatedAsync(script);
    }

    public static Task<PrinterHealthStatus> ProbePrinterAsync(
        string printerName,
        Guid printerId = default,
        CancellationToken ct = default)
        => PrinterHealthProbe.ProbeAsync(printerName, printerId, ct);

    public static void OpenQueue(string printerName)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /o /n {QuoteArg(printerName)}",
            UseShellExecute = true
        });
    }

    public static async Task<string> GetDiagnosticsAsync()
    {
        var script = @"
$ErrorActionPreference='Continue'
'=== SYSTEM ==='
Get-ComputerInfo | Select-Object WindowsProductName,WindowsVersion,OsBuildNumber,CsName | Format-List | Out-String
'=== SERVICE ==='
Get-Service -Name SimplePrintServer -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== SPOOLER ==='
Get-Service -Name Spooler -ErrorAction SilentlyContinue | Format-List * | Out-String
'=== NETWORK PROFILE ==='
Get-NetConnectionProfile | Format-Table Name,InterfaceAlias,NetworkCategory,IPv4Connectivity -AutoSize | Out-String
'=== PRINTERS ==='
Get-Printer | Format-Table Name,DriverName,PortName,PrinterStatus,JobCount -AutoSize | Out-String
'=== PRINTER PORTS ==='
Get-PrinterPort | Select-Object Name,PrinterHostAddress,PortNumber,DeviceURL,DeviceUUID,SNMPEnabled,SNMPCommunity | Format-Table -AutoSize | Out-String
'=== FIREWALL RULES ==='
Get-NetFirewallRule -DisplayName 'SimplePrint*' -ErrorAction SilentlyContinue | Select-Object Name,DisplayName,Enabled,Profile,Direction,Action | Format-Table -AutoSize | Out-String
'=== FIREWALL PORT FILTERS ==='
Get-NetFirewallRule -DisplayName 'SimplePrint*' -ErrorAction SilentlyContinue | Get-NetFirewallPortFilter | Format-Table Protocol,LocalPort,RemotePort -AutoSize | Out-String
'=== FIREWALL ADDRESS FILTERS ==='
Get-NetFirewallRule -DisplayName 'SimplePrint*' -ErrorAction SilentlyContinue | Get-NetFirewallAddressFilter | Format-Table LocalAddress,RemoteAddress -AutoSize | Out-String
'=== FIREWALL PROFILES ==='
Get-NetFirewallProfile | Select-Object Name,Enabled,DefaultInboundAction,AllowInboundRules,AllowLocalFirewallRules | Format-Table -AutoSize | Out-String
'=== LISTENERS ==='
Get-NetTCPConnection -LocalPort 45881 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String
Get-NetUDPEndpoint -LocalPort 45880 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String
";

        var r = await PowerShellRunner.RunAsync(script);
        return r.StdOut + Environment.NewLine + r.StdErr;
    }

    public static void PrintTestPage(string printerName)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"printui.dll,PrintUIEntry /k /n {QuoteArg(printerName)}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string QuoteArg(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";
}