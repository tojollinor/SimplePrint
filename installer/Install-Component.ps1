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
    Set-Service -Name $Name -StartupType Automatic
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
  Ensure-Service 'SimplePrintClient' 'SimplePrint Client Agent' $exe 'Findet SimplePrint-Server automatisch und tunnelt lokale RAW-Druckjobs ohne Rendering.'
}
