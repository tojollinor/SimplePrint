param(
  [Parameter(Mandatory=$true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$headers = @{
  'User-Agent' = 'SimplePrint-Online-Installer'
  'Accept' = 'application/vnd.github+json'
  'X-GitHub-Api-Version' = '2022-11-28'
}

$release = Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri 'https://api.github.com/repos/tojollinor/SimplePrint/releases/latest'

$asset = @($release.assets) |
  Where-Object {
    [string]$_.name -match '^SimplePrint-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe$'
  } |
  Select-Object -First 1

if (-not $asset -or [string]::IsNullOrWhiteSpace([string]$asset.browser_download_url)) {
  throw 'Das aktuelle SimplePrint-Release enthält keinen Offline-Installer.'
}

[System.IO.File]::WriteAllText(
  $OutputPath,
  [string]$asset.browser_download_url,
  [System.Text.UTF8Encoding]::new($false))
