param(
  [Parameter(Mandatory=$true)][ValidateSet('Server','Client')][string]$Mode,
  [Parameter(Mandatory=$true)][string]$AppPath
)

$ErrorActionPreference = 'Stop'

function Ensure-Service([string]$Name, [string]$DisplayName, [string]$BinaryPath, [string]$Description) {
  $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
  if (-not $existing) {
    New-Service -Name $Name -BinaryPathName ('"' + $BinaryPath + '"') -DisplayName $DisplayName -Description $Description -StartupType Automatic | Out-Null
  } else {
    if ($existing.Status -ne 'Stopped') {
      Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
      $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(8))
    }

    & sc.exe config $Name binPath= ('"' + $BinaryPath + '"') start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) {
      throw "Dienstpfad für $Name konnte nicht aktualisiert werden."
    }
  }
  Start-Service -Name $Name
}

$dataRoot = Join-Path $env:ProgramData 'SimplePrint'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Server') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Client') -Force | Out-Null
& icacls.exe $dataRoot /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null

if ($Mode -eq 'Server') {
  $exe = Join-Path $AppPath 'Server\Service\SimplePrint.Server.Service.exe'
  Ensure-Service 'SimplePrintServer' 'SimplePrint Server' $exe 'Empfängt SimplePrint-RAW-Druckjobs und übergibt sie unverändert an lokale Windows-Drucker.'

  Get-NetFirewallRule -Name 'SimplePrint-Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  Get-NetFirewallRule -Name 'SimplePrint-Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

  New-NetFirewallRule -Name 'SimplePrint-Discovery' -DisplayName 'SimplePrint Discovery' -Description 'SimplePrint Server Discovery im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol UDP -LocalPort 45880 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
  New-NetFirewallRule -Name 'SimplePrint-Gateway' -DisplayName 'SimplePrint Print Gateway' -Description 'SimplePrint RAW Print Gateway im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort 45881 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null

  foreach($spec in @(
    @{ Name='SimplePrint-Discovery'; Protocol='UDP'; Port='45880' },
    @{ Name='SimplePrint-Gateway'; Protocol='TCP'; Port='45881' }
  )) {
    $rule = Get-NetFirewallRule -Name $spec.Name -ErrorAction Stop
    $port = $rule | Get-NetFirewallPortFilter
    $address = $rule | Get-NetFirewallAddressFilter
    $profile = [string]$rule.Profile
    $remote = @($address.RemoteAddress) -join ','

    if([string]$rule.Enabled -ne 'True' -or
       [string]$rule.Direction -ne 'Inbound' -or
       [string]$rule.Action -ne 'Allow' -or
       $profile -notmatch 'Private' -or
       $profile -notmatch 'Domain' -or
       $profile -match 'Public' -or
       [string]$port.LocalPort -ne [string]$spec.Port -or
       $remote -notmatch 'LocalSubnet') {
      throw "Firewallregel $($spec.Name) konnte nicht korrekt eingerichtet werden."
    }
  }
}

if ($Mode -eq 'Client') {
  $exe = Join-Path $AppPath 'Client\Service\SimplePrint.Client.Service.exe'
  Ensure-Service 'SimplePrintClient' 'SimplePrint Client Agent' $exe 'Findet SimplePrint-Server automatisch, tunnelt lokale RAW-Druckjobs und stellt Diagnosepakete für den zugeordneten SimplePrint-Server bereit.'

  Get-NetFirewallRule -Name 'SimplePrint-ClientDiagnostics' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  Get-NetFirewallRule -DisplayName 'SimplePrint Client Diagnostics' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

  New-NetFirewallRule -Name 'SimplePrint-ClientDiagnostics' -DisplayName 'SimplePrint Client Diagnostics' -Description 'Erlaubt dem zugeordneten SimplePrint-Server den Abruf eines Client-Diagnosepakets im lokalen Netzwerk.' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort 45882 -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null

  $rule = Get-NetFirewallRule -Name 'SimplePrint-ClientDiagnostics' -ErrorAction Stop
  $port = $rule | Get-NetFirewallPortFilter
  $address = $rule | Get-NetFirewallAddressFilter
  $profile = [string]$rule.Profile
  $remote = @($address.RemoteAddress) -join ','

  if([string]$rule.Enabled -ne 'True' -or
     [string]$rule.Direction -ne 'Inbound' -or
     [string]$rule.Action -ne 'Allow' -or
     $profile -notmatch 'Private' -or
     $profile -notmatch 'Domain' -or
     $profile -match 'Public' -or
     [string]$port.LocalPort -ne '45882' -or
     $remote -notmatch 'LocalSubnet') {
    throw 'Firewallregel SimplePrint-ClientDiagnostics konnte nicht korrekt eingerichtet werden.'
  }
}

# Migration von 0.2.1 und älter: Bis 0.2.1 gab es einen gemeinsamen Installer.
# Die neue Server-/Client-Installation übernimmt die jeweiligen Dateien und Dienste.
# Der alte gemeinsame Deinstallationseintrag wird entfernt, sobald kein noch nicht
# migrierter Gegenpart mehr davon abhängig ist.
$legacyUninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{71D8D5B8-5D4A-4898-9454-B5F6D95B9AE1}_is1'
$newServerUninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A7F92C46-6234-4B78-A0AB-8F83E317C221}_is1'
$newClientUninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{5D49BD99-7D65-44D6-BEC5-BCE455F79F52}_is1'

if (Test-Path $legacyUninstallKey) {
  $serverServiceExists = $null -ne (Get-Service -Name 'SimplePrintServer' -ErrorAction SilentlyContinue)
  $clientServiceExists = $null -ne (Get-Service -Name 'SimplePrintClient' -ErrorAction SilentlyContinue)
  $newServerInstalled = Test-Path $newServerUninstallKey
  $newClientInstalled = Test-Path $newClientUninstallKey

  $safeToRemoveLegacy = if ($Mode -eq 'Server') {
    (-not $clientServiceExists) -or $newClientInstalled
  } else {
    (-not $serverServiceExists) -or $newServerInstalled
  }

  if ($safeToRemoveLegacy) {
    Remove-Item $legacyUninstallKey -Recurse -Force -ErrorAction SilentlyContinue
  }
}