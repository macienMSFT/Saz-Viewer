<#
.SYNOPSIS
    Generates the SAZ Viewer app icon: SazViewer.svg (vector source), SazViewer-256.png and the multi-size SazViewer.ico.

.DESCRIPTION
    An original design: a capture sheet listing sessions, with a magnifier, on a rounded blue tile (readable on light
    and dark taskbars). The geometry below is in a 256-unit space. Sizes up to 32 px use a simplified variant (two
    thicker rows, a heavier magnifier) so the icon stays legible at 16 px. Rendering uses System.Drawing (GDI+) only.

    The .ico holds 16, 24, 32, 48, 64 and 128 px as 32-bit BMP images and 256 px as PNG.

.EXAMPLE
    pwsh -File .\SazViewer.App\Assets\Generate-Icon.ps1
#>
[CmdletBinding()]
param([string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$TileColor = '#1F6FEB'
$SheetColor = '#FFFFFF'
$FoldColor = '#C9DBFB'
$RowColor = '#1F6FEB'
$RowMutedColor = '#9DBDF5'
$LensColor = '#FFB020'
$LensFillColor = '#FFF4DC'

function Get-Geometry([bool]$Small) {
    if ($Small) {
        @{
            Sheet = @{ X = 40; Y = 28; W = 132; H = 192; R = 16; Fold = 44 }
            Rows = @(@{ Y = 64; H = 28; Dot = 28; X2 = 152 }, @{ Y = 108; H = 28; Dot = 28; X2 = 132 })
            RowX = 60
            Ring = @{ Cx = 170; Cy = 166; R = 44; Stroke = 30 }
            Handle = @{ X1 = 204; Y1 = 200; X2 = 232; Y2 = 228; Stroke = 34 }
        }
    }
    else {
        @{
            Sheet = @{ X = 52; Y = 32; W = 124; H = 184; R = 12; Fold = 36 }
            Rows = @(
                @{ Y = 84; H = 14; Dot = 14; X2 = 152 },
                @{ Y = 112; H = 14; Dot = 14; X2 = 136 },
                @{ Y = 140; H = 14; Dot = 14; X2 = 124 },
                @{ Y = 168; H = 14; Dot = 14; X2 = 116 })
            RowX = 72
            Ring = @{ Cx = 172; Cy = 168; R = 42; Stroke = 18 }
            Handle = @{ X1 = 203; Y1 = 199; X2 = 230; Y2 = 226; Stroke = 22 }
        }
    }
}

function New-SheetPath($s) {
    # Rounded sheet with the top-right corner folded over.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = $s.X; $y = $s.Y; $w = $s.W; $h = $s.H; $r = $s.R; $f = $s.Fold
    $path.AddLine($x + $r, $y, $x + $w - $f, $y)
    $path.AddLine($x + $w - $f, $y, $x + $w, $y + $f)
    $path.AddLine($x + $w, $y + $f, $x + $w, $y + $h - $r)
    $path.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $path.CloseFigure()
    $path
}

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $path
}

function Get-Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-Brush([string]$hex) { New-Object System.Drawing.SolidBrush (Get-Color $hex) }

function New-IconBitmap([int]$size) {
    $g = Get-Geometry ($size -le 32)
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gfx = [System.Drawing.Graphics]::FromImage($bitmap)
    $gfx.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $gfx.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gfx.Clear([System.Drawing.Color]::Transparent)
    $gfx.ScaleTransform($size / 256.0, $size / 256.0)

    $gfx.FillPath((New-Brush $TileColor), (New-RoundedRect 8 8 240 240 48))

    $s = $g.Sheet
    $gfx.FillPath((New-Brush $SheetColor), (New-SheetPath $s))
    $fold = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s.X + $s.W - $s.Fold, $s.Y),
        [System.Drawing.PointF]::new($s.X + $s.W - $s.Fold, $s.Y + $s.Fold),
        [System.Drawing.PointF]::new($s.X + $s.W, $s.Y + $s.Fold))
    $gfx.FillPolygon((New-Brush $FoldColor), $fold)

    foreach ($row in $g.Rows) {
        $gfx.FillPath((New-Brush $RowMutedColor), (New-RoundedRect $g.RowX $row.Y $row.Dot $row.H ($row.H / 2)))
        $lineX = $g.RowX + $row.Dot + $row.H / 2
        $gfx.FillPath((New-Brush $RowColor), (New-RoundedRect $lineX $row.Y ($row.X2 - $lineX) $row.H ($row.H / 2)))
    }

    $ring = $g.Ring
    $handle = $g.Handle
    $handlePen = New-Object System.Drawing.Pen (Get-Color $LensColor), $handle.Stroke
    $handlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $gfx.DrawLine($handlePen, $handle.X1, $handle.Y1, $handle.X2, $handle.Y2)
    $gfx.FillEllipse((New-Brush $LensFillColor), $ring.Cx - $ring.R, $ring.Cy - $ring.R, 2 * $ring.R, 2 * $ring.R)
    $ringPen = New-Object System.Drawing.Pen (Get-Color $LensColor), $ring.Stroke
    $gfx.DrawEllipse($ringPen, $ring.Cx - $ring.R, $ring.Cy - $ring.R, 2 * $ring.R, 2 * $ring.R)

    $gfx.Dispose()
    $bitmap
}

function Get-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $stream.ToArray()
}

function Get-DibBytes([System.Drawing.Bitmap]$bitmap) {
    # ICO image: BITMAPINFOHEADER (double height), bottom-up BGRA pixels, then an all-zero AND mask.
    $size = $bitmap.Width
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bitmap.UnlockBits($data)
    for ($y = $size - 1; $y -ge 0; $y--) {
        $writer.Write($pixels, $y * $data.Stride, $size * 4)
    }
    $writer.Write([byte[]]::new($maskStride * $size))
    $writer.Flush()
    $stream.ToArray()
}

function Write-Ico([string]$path, [int[]]$sizes) {
    $images = foreach ($size in $sizes) {
        $bitmap = New-IconBitmap $size
        try { , ($(if ($size -ge 256) { Get-PngBytes $bitmap } else { Get-DibBytes $bitmap })) }
        finally { $bitmap.Dispose() }
    }
    $stream = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([int16]1); $writer.Write([int16]32)
            $writer.Write([int]$images[$i].Length); $writer.Write([int]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write([byte[]]$image) }
    }
    finally { $writer.Dispose() }
}

function Write-Svg([string]$path) {
    $g = Get-Geometry $false
    $s = $g.Sheet; $r = $s.R; $f = $s.Fold
    $right = $s.X + $s.W; $bottom = $s.Y + $s.H
    $rows = foreach ($row in $g.Rows) {
        $lineX = $g.RowX + $row.Dot + $row.H / 2
        "  <rect x=`"$($g.RowX)`" y=`"$($row.Y)`" width=`"$($row.Dot)`" height=`"$($row.H)`" rx=`"$($row.H / 2)`" fill=`"$RowMutedColor`"/>"
        "  <rect x=`"$lineX`" y=`"$($row.Y)`" width=`"$($row.X2 - $lineX)`" height=`"$($row.H)`" rx=`"$($row.H / 2)`" fill=`"$RowColor`"/>"
    }
    $ring = $g.Ring; $handle = $g.Handle
    $svg = @"
<!-- SAZ Viewer app icon (original design). Generated by Generate-Icon.ps1, which also renders the .ico; sizes up to 32 px use a simplified variant. -->
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <rect x="8" y="8" width="240" height="240" rx="48" fill="$TileColor"/>
  <path d="M$($s.X + $r) $($s.Y) H$($right - $f) L$right $($s.Y + $f) V$($bottom - $r) A$r $r 0 0 1 $($right - $r) $bottom H$($s.X + $r) A$r $r 0 0 1 $($s.X) $($bottom - $r) V$($s.Y + $r) A$r $r 0 0 1 $($s.X + $r) $($s.Y) Z" fill="$SheetColor"/>
  <path d="M$($right - $f) $($s.Y) V$($s.Y + $f) H$right Z" fill="$FoldColor"/>
$($rows -join "`n")
  <line x1="$($handle.X1)" y1="$($handle.Y1)" x2="$($handle.X2)" y2="$($handle.Y2)" stroke="$LensColor" stroke-width="$($handle.Stroke)" stroke-linecap="round"/>
  <circle cx="$($ring.Cx)" cy="$($ring.Cy)" r="$($ring.R)" fill="$LensFillColor" stroke="$LensColor" stroke-width="$($ring.Stroke)"/>
</svg>
"@
    [System.IO.File]::WriteAllText($path, $svg.Replace("`r`n", "`n") + "`n")
}

Write-Svg (Join-Path $OutputDirectory 'SazViewer.svg')
$png = New-IconBitmap 256
try { $png.Save((Join-Path $OutputDirectory 'SazViewer-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
finally { $png.Dispose() }
Write-Ico (Join-Path $OutputDirectory 'SazViewer.ico') @(16, 24, 32, 48, 64, 128, 256)
Write-Host "Wrote SazViewer.svg, SazViewer-256.png and SazViewer.ico to $OutputDirectory"
