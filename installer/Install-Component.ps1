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

# Die GUIs laufen ohne Administratorrechte und müssen ihre Konfiguration unter ProgramData speichern können.
& icacls.exe $dataRoot /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null

if ($Mode -eq 'Server') {
  $exe = Join-Path $AppPath 'Server\Service\SimplePrint.Server.Service.exe'
  Ensure-Service 'SimplePrintServer' 'SimplePrint Server' $exe 'Empfängt SimplePrint-RAW-Druckjobs und übergibt sie unverändert an lokale Windows-Drucker.'

  Get-NetFirewallRule -DisplayName 'SimplePrint Discovery' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  Get-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  New-NetFirewallRule -DisplayName 'SimplePrint Discovery' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 45880 -Profile Private,Domain | Out-Null
  New-NetFirewallRule -DisplayName 'SimplePrint Print Gateway' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45881 -Profile Private,Domain | Out-Null
}

if ($Mode -eq 'Client') {
  $exe = Join-Path $AppPath 'Client\Service\SimplePrint.Client.Service.exe'
  Ensure-Service 'SimplePrintClient' 'SimplePrint Client Agent' $exe 'Findet SimplePrint-Server automatisch und tunnelt lokale RAW-Druckjobs ohne Rendering.'
}
