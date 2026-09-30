param(
  [Parameter(Mandatory=$true)][string]$AppPath
)

$ErrorActionPreference = 'Stop'

function Stop-And-DeleteService([string]$Name) {
  $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
  if (-not $service) { return }

  if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    try { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10)) } catch {}
  }

  & sc.exe delete $Name | Out-Null
}

function Ensure-Service(
  [string]$Name,
  [string]$DisplayName,
  [string]$BinaryPath,
  [string]$Description
) {
  $existing = Get-Service -Name $Name -ErrorAction SilentlyContinue

  if (-not $existing) {
    New-Service -Name $Name -BinaryPathName ('"' + $BinaryPath + '"') -DisplayName $DisplayName -Description $Description -StartupType Automatic | Out-Null
  }
  else {
    if ($existing.Status -ne 'Stopped') {
      Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
      try { $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10)) } catch {}
    }

    & sc.exe config $Name binPath= ('"' + $BinaryPath + '"') start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) {
      throw "Dienstpfad für $Name konnte nicht aktualisiert werden."
    }

    & sc.exe description $Name $Description | Out-Null
  }

  & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/15000/""/0 | Out-Null
}

function Read-Json([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $null }
  try {
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
  } catch {
    return $null
  }
}

Get-Process -Name 'SimplePrint.Client.Gui','SimplePrint.Server.Gui' -ErrorAction SilentlyContinue |
  Stop-Process -Force -ErrorAction SilentlyContinue

Stop-And-DeleteService 'SimplePrintServer'
Stop-And-DeleteService 'SimplePrintClient'

$unified = Get-Service -Name 'SimplePrint' -ErrorAction SilentlyContinue
if ($unified -and $unified.Status -ne 'Stopped') {
  Stop-Service -Name 'SimplePrint' -Force -ErrorAction SilentlyContinue
  try { $unified.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10)) } catch {}
}

$dataRoot = Join-Path $env:ProgramData 'SimplePrint'
$deviceRoot = Join-Path $dataRoot 'Device'
$serverRoot = Join-Path $dataRoot 'Server'
$clientRoot = Join-Path $dataRoot 'Client'

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $deviceRoot -Force | Out-Null
New-Item -ItemType Directory -Path $serverRoot -Force | Out-Null
New-Item -ItemType Directory -Path $clientRoot -Force | Out-Null
& icacls.exe $dataRoot /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null

$assetRoot = Join-Path $AppPath 'Unified\assets'
New-Item -ItemType Directory -Path $assetRoot -Force | Out-Null

$logoTarget = Join-Path $assetRoot 'logo.png'
if (-not (Test-Path -LiteralPath $logoTarget)) {
  $logoCandidates = @(
    (Join-Path $AppPath 'Client\assets\logo.png'),
    (Join-Path $AppPath 'Server\assets\logo.png'),
    (Join-Path $assetRoot 'logo-default.png')
  )

  foreach ($candidate in $logoCandidates) {
    if (Test-Path -LiteralPath $candidate) {
      Copy-Item -LiteralPath $candidate -Destination $logoTarget -Force
      break
    }
  }
}

$discoveryPort = 45880
$gatewayPort = 45881
$diagnosticsPort = 45882

$deviceConfigPath = Join-Path $deviceRoot 'config.json'
$serverConfigPath = Join-Path $serverRoot 'config.json'

$deviceConfig = Read-Json $deviceConfigPath
if ($deviceConfig) {
  if ([int]$deviceConfig.DiscoveryPort -gt 0) { $discoveryPort = [int]$deviceConfig.DiscoveryPort }
  if ([int]$deviceConfig.GatewayPort -gt 0) { $gatewayPort = [int]$deviceConfig.GatewayPort }
  if ([int]$deviceConfig.DiagnosticsPort -gt 0) { $diagnosticsPort = [int]$deviceConfig.DiagnosticsPort }
}
else {
  $serverConfig = Read-Json $serverConfigPath
  if ($serverConfig) {
    if ([int]$serverConfig.DiscoveryPort -gt 0) { $discoveryPort = [int]$serverConfig.DiscoveryPort }
    if ([int]$serverConfig.GatewayPort -gt 0) { $gatewayPort = [int]$serverConfig.GatewayPort }
  }
}

foreach ($name in @(
  'SimplePrint-Discovery',
  'SimplePrint-Gateway',
  'SimplePrint-Diagnostics',
  'SimplePrint-ClientDiagnostics',
  'SimplePrint-PrintShare-SMB'
)) {
  Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

foreach ($display in @(
  'SimplePrint Discovery',
  'SimplePrint Print Gateway',
  'SimplePrint Diagnostics',
  'SimplePrint Client Diagnostics',
  'SimplePrint Printer Sharing SMB'
)) {
  Get-NetFirewallRule -DisplayName $display -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

New-NetFirewallRule -Name 'SimplePrint-Discovery' -DisplayName 'SimplePrint Discovery' -Description 'SimplePrint Geräteerkennung im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol UDP -LocalPort $discoveryPort -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name 'SimplePrint-Gateway' -DisplayName 'SimplePrint Print Gateway' -Description 'SimplePrint Druckdaten und Status im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort $gatewayPort -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name 'SimplePrint-Diagnostics' -DisplayName 'SimplePrint Diagnostics' -Description 'SimplePrint Diagnoseabruf im lokalen Netzwerk' -Direction Inbound -Action Allow -Enabled True -Protocol TCP -LocalPort $diagnosticsPort -Profile Private,Domain -RemoteAddress LocalSubnet | Out-Null

foreach ($spec in @(
  @{ Name='SimplePrint-Discovery'; Port=$discoveryPort },
  @{ Name='SimplePrint-Gateway'; Port=$gatewayPort },
  @{ Name='SimplePrint-Diagnostics'; Port=$diagnosticsPort }
)) {
  $rule = Get-NetFirewallRule -Name $spec.Name -ErrorAction Stop
  $port = $rule | Get-NetFirewallPortFilter
  $address = $rule | Get-NetFirewallAddressFilter
  $profile = [string]$rule.Profile
  $remote = @($address.RemoteAddress) -join ','

  if (
    [string]$rule.Enabled -ne 'True' -or
    [string]$rule.Direction -ne 'Inbound' -or
    [string]$rule.Action -ne 'Allow' -or
    $profile -notmatch 'Private' -or
    $profile -notmatch 'Domain' -or
    $profile -match 'Public' -or
    [string]$port.LocalPort -ne [string]$spec.Port -or
    $remote -notmatch 'LocalSubnet'
  ) {
    throw "Firewallregel $($spec.Name) konnte nicht korrekt eingerichtet werden."
  }
}

$serviceExe = Join-Path $AppPath 'Unified\Service\SimplePrint.Service.exe'
if (-not (Test-Path -LiteralPath $serviceExe)) {
  throw "Der gemeinsame SimplePrint-Dienst wurde nicht gefunden: $serviceExe"
}

Ensure-Service 'SimplePrint' 'SimplePrint' $serviceExe 'Stellt lokale Drucker bereit, findet andere SimplePrint-Geräte und verarbeitet ein- und ausgehende Druckaufträge.'

$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
foreach ($name in @('SimplePrintServerGui','SimplePrintClientGui')) {
  Remove-ItemProperty -Path $runKey -Name $name -ErrorAction SilentlyContinue
}

Start-Service -Name 'SimplePrint' -ErrorAction Stop
(Get-Service -Name 'SimplePrint').WaitForStatus('Running', [TimeSpan]::FromSeconds(15))

for ($i = 0; $i -lt 40; $i++) {
  if (Test-Path -LiteralPath $deviceConfigPath) { break }
  Start-Sleep -Milliseconds 250
}

if (-not (Test-Path -LiteralPath $deviceConfigPath)) {
  throw 'Die gemeinsame SimplePrint-Konfiguration konnte nicht erstellt bzw. migriert werden. Alte Komponenten werden deshalb nicht bereinigt.'
}

foreach ($legacyDir in @(
  (Join-Path $AppPath 'Client'),
  (Join-Path $AppPath 'Server')
)) {
  if (Test-Path -LiteralPath $legacyDir) {
    Remove-Item -LiteralPath $legacyDir -Recurse -Force -ErrorAction SilentlyContinue
  }
}

$commonDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$commonPrograms = [Environment]::GetFolderPath('CommonPrograms')

foreach ($shortcut in @(
  (Join-Path $commonDesktop 'SimplePrint Client.lnk'),
  (Join-Path $commonDesktop 'SimplePrint Server.lnk'),
  (Join-Path $commonPrograms 'SimplePrint\SimplePrint Client.lnk'),
  (Join-Path $commonPrograms 'SimplePrint\SimplePrint Server.lnk'),
  (Join-Path $commonPrograms 'SimplePrint\SimplePrint Client deinstallieren.lnk'),
  (Join-Path $commonPrograms 'SimplePrint\SimplePrint Server deinstallieren.lnk')
)) {
  Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
}

foreach ($key in @(
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{71D8D5B8-5D4A-4898-9454-B5F6D95B9AE1}_is1',
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A7F92C46-6234-4B78-A0AB-8F83E317C221}_is1',
  'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{5D49BD99-7D65-44D6-BEC5-BCE455F79F52}_is1'
)) {
  Remove-Item -LiteralPath $key -Recurse -Force -ErrorAction SilentlyContinue
}
