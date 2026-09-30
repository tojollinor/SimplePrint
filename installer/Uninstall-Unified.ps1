param(
  [Parameter(Mandatory=$true)][string]$AppPath
)

$ErrorActionPreference = 'SilentlyContinue'

$service = Get-Service -Name 'SimplePrint' -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
  Stop-Service -Name 'SimplePrint' -Force -ErrorAction SilentlyContinue
  try { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10)) } catch {}
}
& sc.exe delete SimplePrint | Out-Null

$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $runKey -Name 'SimplePrintGui' -ErrorAction SilentlyContinue

foreach ($name in @(
  'SimplePrint-Discovery',
  'SimplePrint-Gateway',
  'SimplePrint-Diagnostics',
  'SimplePrint-PrintShare-SMB',
  'SimplePrint-ClientDiagnostics',
  'SimplePrint-WSD-Discovery-In',
  'SimplePrint-WSD-Events-In',
  'SimplePrint-WSD-EventsSecure-In'
)) {
  Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

Get-Printer -ErrorAction SilentlyContinue |
  Where-Object {
    $_.Shared -and
    ([string]$_.ShareName) -like 'SimplePrint-*'
  } |
  ForEach-Object {
    Set-Printer -Name $_.Name -Shared $false -ErrorAction SilentlyContinue
  }

$configPath = Join-Path $env:ProgramData 'SimplePrint\Device\config.json'
if (Test-Path -LiteralPath $configPath) {
  try {
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($mapping in @($config.NetworkPrinters)) {
      if (
        [string]$mapping.TransportMode -eq 'WindowsShare' -and
        -not [string]::IsNullOrWhiteSpace([string]$mapping.DirectAddress)
      ) {
        Remove-Printer -Name ([string]$mapping.DirectAddress) -ErrorAction SilentlyContinue
      }
    }
  } catch {}
}

Get-Printer -ErrorAction SilentlyContinue |
  Where-Object {
    ([string]$_.Comment) -like 'SimplePrint:*' -or
    ([string]$_.PortName) -like 'SimplePrint_*'
  } |
  Remove-Printer -ErrorAction SilentlyContinue

Start-Sleep -Milliseconds 600

Get-PrinterPort -ErrorAction SilentlyContinue |
  Where-Object Name -Like 'SimplePrint_*' |
  Remove-PrinterPort -ErrorAction SilentlyContinue
