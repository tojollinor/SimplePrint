param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $Root "dist"

Write-Host "== SimplePrint Build ==" -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw ".NET 8 SDK wurde nicht gefunden. Installiere das .NET 8 SDK und starte das Skript erneut."
}

if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item $Dist -ItemType Directory | Out-Null

$projects = @(
  @{ Name="ServerService"; Project="src\SimplePrint.Server.Service\SimplePrint.Server.Service.csproj"; Out="Server\Service" },
  @{ Name="ServerGui";     Project="src\SimplePrint.Server.Gui\SimplePrint.Server.Gui.csproj";         Out="Server\Gui" },
  @{ Name="ClientService"; Project="src\SimplePrint.Client.Service\SimplePrint.Client.Service.csproj"; Out="Client\Service" },
  @{ Name="ClientGui";     Project="src\SimplePrint.Client.Gui\SimplePrint.Client.Gui.csproj";         Out="Client\Gui" }
)

foreach ($p in $projects) {
  Write-Host "Publishing $($p.Name)..." -ForegroundColor Yellow
  $out = Join-Path $Dist $p.Out
  dotnet publish (Join-Path $Root $p.Project) -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -o $out
}

if ($SkipInstaller) {
  Write-Host "Publish abgeschlossen: $Dist" -ForegroundColor Green
  exit 0
}

$compiler = @(
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $compiler) {
  Write-Warning "Inno Setup 6 wurde nicht gefunden. Die EXE-Dateien sind gebaut; Installer wurde übersprungen."
  Write-Host "Installiere Inno Setup 6 und führe build.ps1 erneut aus." -ForegroundColor Yellow
  exit 0
}

New-Item (Join-Path $Dist "Installer") -ItemType Directory -Force | Out-Null
& $compiler (Join-Path $Root "installer\SimplePrint.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup Build fehlgeschlagen." }
Write-Host "Fertig. Installer liegt unter dist\Installer." -ForegroundColor Green
