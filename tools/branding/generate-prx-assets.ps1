param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetDirectory = Join-Path $RepositoryRoot 'src\PaRuX.App\Assets'
$approvedLogoPath = Join-Path $assetDirectory 'prx-orbit-approved.png'
$iconPath = Join-Path $assetDirectory 'parux.ico'
$trayIconPath = Join-Path $assetDirectory 'prx-orbit-tray.ico'
$previewPath = Join-Path $assetDirectory 'prx-orbit-preview.png'

if (-not (Test-Path -LiteralPath $approvedLogoPath)) {
    throw "Approved PRX logo not found: $approvedLogoPath"
}

function New-PrxBitmap {
    param([System.Drawing.Image]$Source, [int]$Size, [switch]$AppBackground)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    if ($AppBackground) {
        $graphics.Clear([System.Drawing.Color]::FromArgb(255, 3, 13, 24))
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    }
    $graphics.DrawImage($Source, [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
    $graphics.Dispose()
    return $bitmap
}

function Write-PrxIcon {
    param([System.Drawing.Image]$Source, [string]$Path, [int[]]$Sizes, [switch]$AppBackground)

    $frames = @()
    foreach ($size in $Sizes) {
        $bitmap = New-PrxBitmap -Source $Source -Size $size -AppBackground:$AppBackground
        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += ,@($size, $stream.ToArray())
        $stream.Dispose()
        $bitmap.Dispose()
    }

    $output = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
    $writer = [System.IO.BinaryWriter]::new($output)
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $size = [int]$frame[0]
        $bytes = [byte[]]$frame[1]
        $encodedSize = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$encodedSize)
        $writer.Write([byte]$encodedSize)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
    $writer.Dispose()
    $output.Dispose()
}

$approvedLogo = [System.Drawing.Image]::FromFile($approvedLogoPath)
try {
    # The approved artwork already contains a transparent canvas. Keep that alpha in the
    # application icon too; an opaque fill makes Windows show a heavy square behind the mark.
    Write-PrxIcon -Source $approvedLogo -Path $iconPath -Sizes @(16, 20, 24, 32, 48, 64, 128, 256)
    Write-PrxIcon -Source $approvedLogo -Path $trayIconPath -Sizes @(16, 20, 24, 32, 48)

    $preview = New-PrxBitmap -Source $approvedLogo -Size 512
    $preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $preview.Dispose()
}
finally {
    $approvedLogo.Dispose()
}

Write-Output "Generated icons directly from the approved PRX logo."
