param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $Root "dist"

[xml]$props = Get-Content (Join-Path $Root "Directory.Build.props")
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
  throw "SimplePrint-Version konnte nicht ermittelt werden."
}

Write-Host "== SimplePrint Build ==" -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw ".NET 8 SDK wurde nicht gefunden. Installiere das .NET 8 SDK und starte das Skript erneut."
}

Write-Host "Generating branding assets..." -ForegroundColor Yellow
& (Join-Path $Root "tools\Generate-Branding.ps1") -Root $Root

if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item $Dist -ItemType Directory | Out-Null

$projects = @(
  @{ Name="UnifiedService"; Project="src\SimplePrint.Service\SimplePrint.Service.csproj"; Out="Unified\Service" },
  @{ Name="UnifiedGui"; Project="src\SimplePrint.Gui\SimplePrint.Gui.csproj"; Out="Unified\Gui" }
)

foreach ($p in $projects) {
  Write-Host "Publishing $($p.Name)..." -ForegroundColor Yellow
  $out = Join-Path $Dist $p.Out
  dotnet publish (Join-Path $Root $p.Project) -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -o $out
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish für $($p.Name) ist fehlgeschlagen."
  }
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

Write-Host "Building offline installer..." -ForegroundColor Yellow
& $compiler "/DMyAppVersion=$version" (Join-Path $Root "installer\SimplePrint.iss")
if ($LASTEXITCODE -ne 0) {
  throw "Inno Setup Build für den Offline-Installer ist fehlgeschlagen."
}

Write-Host "Building online installer..." -ForegroundColor Yellow
& $compiler (Join-Path $Root "installer\SimplePrint.Online.iss")
if ($LASTEXITCODE -ne 0) {
  throw "Inno Setup Build für den Online-Installer ist fehlgeschlagen."
}

$offline = Join-Path $Dist ("Installer\SimplePrint-Setup-" + $version + ".exe")
$online = Join-Path $Dist "Installer\SimplePrint-Setup.exe"

if (-not (Test-Path $offline)) {
  throw "Offline-Installer wurde nicht erzeugt: $offline"
}

if (-not (Test-Path $online)) {
  throw "Online-Installer wurde nicht erzeugt: $online"
}

Write-Host "Fertig. Online- und Offline-Installer liegen unter dist\Installer." -ForegroundColor Green
