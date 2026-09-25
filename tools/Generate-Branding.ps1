param(
  [string]$Root = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $Root "assets"
New-Item $assets -ItemType Directory -Force | Out-Null
$pngPath = Join-Path $assets "logo.png"
$icoPath = Join-Path $assets "app.ico"
$versionedIcoPath = Join-Path $assets "app-0.2.13.ico"

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
  try {
    $Graphics.FillPath($Brush, $path)
  }
  finally {
    $path.Dispose()
  }
}

# Das feste SimplePrint-Motiv: dunkles Blau + klar erkennbarer Drucker.
# Keine türkis/grünen Statuspunkte, damit auch 16x16/24x24 eindeutig lesbar bleiben.
$bitmap = New-Object System.Drawing.Bitmap 512, 512, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([System.Drawing.Color]::Transparent)

$navy       = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 43, 88))
$printer    = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 43, 111, 190))
$printerHi  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 78, 148, 222))
$paper      = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 250, 253, 255))
$paperLine  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 205, 226, 248))
$slot       = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 55, 99))

try {
  Fill-RoundedRect $graphics $navy 28 28 456 456 104

  # Oberes Papier
  Fill-RoundedRect $graphics $paper 151 82 210 166 18
  $graphics.FillRectangle($paperLine, 181, 120, 150, 18)
  $graphics.FillRectangle($paperLine, 181, 158, 126, 18)

  # Druckergehäuse
  Fill-RoundedRect $graphics $printer 100 202 312 174 46
  Fill-RoundedRect $graphics $printerHi 116 220 280 48 20
  Fill-RoundedRect $graphics $slot 151 250 210 28 10

  # Ausgabepapier
  Fill-RoundedRect $graphics $paper 151 307 210 116 18
  $graphics.FillRectangle($paperLine, 181, 338, 150, 17)
  $graphics.FillRectangle($paperLine, 181, 374, 118, 17)

  # Kleine seitliche Gehäusekonturen verstärken die Drucker-Silhouette bei kleinen Icons.
  Fill-RoundedRect $graphics $printer 82 267 46 60 16
  Fill-RoundedRect $graphics $printer 384 267 46 60 16

  $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
  $graphics.Dispose()
  $navy.Dispose()
  $printer.Dispose()
  $printerHi.Dispose()
  $paper.Dispose()
  $paperLine.Dispose()
  $slot.Dispose()
  $bitmap.Dispose()
}

# Echtes Multi-Resolution-ICO. Windows wählt je nach Oberfläche die passende native Größe
# statt ein einzelnes 256px-Bild herunterzuskalieren.
$icoWriter = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class SimplePrintIconWriter
{
    public static void Create(string pngPath, string icoPath)
    {
        int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        List<byte[]> images = new List<byte[]>();

        using (Bitmap source = new Bitmap(pngPath))
        {
            foreach (int size in sizes)
            {
                using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(source, 0, 0, size, size);
                    }

                    using (MemoryStream ms = new MemoryStream())
                    using (BinaryWriter bw = new BinaryWriter(ms))
                    {
                        int maskStride = ((size + 31) / 32) * 4;

                        // BITMAPINFOHEADER
                        bw.Write(40);
                        bw.Write(size);
                        bw.Write(size * 2);
                        bw.Write((short)1);
                        bw.Write((short)32);
                        bw.Write(0);
                        bw.Write(size * size * 4);
                        bw.Write(0);
                        bw.Write(0);
                        bw.Write(0);
                        bw.Write(0);

                        // BGRA, bottom-up
                        for (int y = size - 1; y >= 0; y--)
                        {
                            for (int x = 0; x < size; x++)
                            {
                                Color c = bmp.GetPixel(x, y);
                                bw.Write(c.B);
                                bw.Write(c.G);
                                bw.Write(c.R);
                                bw.Write(c.A);
                            }
                        }

                        // AND mask. Alpha channel übernimmt die Transparenz.
                        bw.Write(new byte[maskStride * size]);
                        bw.Flush();
                        images.Add(ms.ToArray());
                    }
                }
            }
        }

        using (FileStream fs = File.Create(icoPath))
        using (BinaryWriter writer = new BinaryWriter(fs))
        {
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)sizes.Length);

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(images[i].Length);
                writer.Write(offset);
                offset += images[i].Length;
            }

            foreach (byte[] image in images)
                writer.Write(image);
        }
    }
}
'@

Add-Type -TypeDefinition $icoWriter -ReferencedAssemblies System.Drawing
[SimplePrintIconWriter]::Create($pngPath, $icoPath)
Copy-Item $icoPath $versionedIcoPath -Force

if (-not (Test-Path $pngPath) -or (Get-Item $pngPath).Length -lt 1000) {
  throw "logo.png konnte nicht korrekt erzeugt werden."
}
if (-not (Test-Path $icoPath) -or (Get-Item $icoPath).Length -lt 5000) {
  throw "app.ico konnte nicht korrekt als Multi-Resolution-Icon erzeugt werden."
}

Write-Host "Branding-Dateien wurden neu erzeugt (dunkelblaues Druckerlogo + Multi-Resolution-ICO)." -ForegroundColor DarkGreen