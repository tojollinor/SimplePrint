param(
  [string]$Root = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $Root "assets"
New-Item $assets -ItemType Directory -Force | Out-Null
$pngPath = Join-Path $assets "logo.png"
$icoPath = Join-Path $assets "app.ico"

function New-RoundedPath {
  param(
    [float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius
  )
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $diameter = $Radius * 2
  $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
  $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
  $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
  $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
  $path.CloseFigure()
  return $path
}

function Fill-RoundedRect {
  param(
    [System.Drawing.Graphics]$Graphics,
    [System.Drawing.Brush]$Brush,
    [float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius
  )
  $path = New-RoundedPath $X $Y $Width $Height $Radius
  try { $Graphics.FillPath($Brush, $path) } finally { $path.Dispose() }
}

$bitmap = New-Object System.Drawing.Bitmap 512, 512, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([System.Drawing.Color]::Transparent)

$navy  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 86, 145))
$blue  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 144, 214))
$light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 232, 244, 252))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$ink   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 23, 44, 66))
$green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 112, 226, 158))

try {
  Fill-RoundedRect $graphics $navy 34 34 444 444 96

  Fill-RoundedRect $graphics $white 150 92 212 153 18
  $graphics.FillRectangle($light, 175, 126, 162, 19)
  $graphics.FillRectangle($light, 175, 164, 140, 18)

  Fill-RoundedRect $graphics $blue 112 208 288 142 42
  Fill-RoundedRect $graphics $ink 150 226 212 32 10
  $graphics.FillEllipse($green, 337, 278, 18, 18)

  Fill-RoundedRect $graphics $white 160 304 192 106 16
  $graphics.FillRectangle($light, 184, 330, 144, 16)
  $graphics.FillRectangle($light, 184, 362, 112, 16)

  $graphics.FillRectangle($blue, 96, 276, 32, 30)
  $graphics.FillRectangle($blue, 384, 276, 32, 30)

  $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
  $graphics.Dispose()
  $navy.Dispose()
  $blue.Dispose()
  $light.Dispose()
  $white.Dispose()
  $ink.Dispose()
  $green.Dispose()
  $bitmap.Dispose()
}

# Generate a native Windows .ico instead of relying on a PNG-to-ICO converter.
$source = [System.Drawing.Image]::FromFile($pngPath)
$iconBitmap = New-Object System.Drawing.Bitmap 256, 256, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
$iconGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$iconGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$iconGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$iconGraphics.DrawImage($source, 0, 0, 256, 256)

$handle = $iconBitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($handle)
$stream = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create)
try {
  $icon.Save($stream)
}
finally {
  $stream.Dispose()
  $icon.Dispose()
  $iconGraphics.Dispose()
  $iconBitmap.Dispose()
  $source.Dispose()
}

if (-not (Test-Path $pngPath) -or (Get-Item $pngPath).Length -lt 500) {
  throw "logo.png konnte nicht korrekt erzeugt werden."
}
if (-not (Test-Path $icoPath) -or (Get-Item $icoPath).Length -lt 500) {
  throw "app.ico konnte nicht korrekt erzeugt werden."
}

Write-Host "Branding-Dateien wurden neu erzeugt." -ForegroundColor DarkGreen
