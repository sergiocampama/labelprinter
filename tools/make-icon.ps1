# Draws the LabelPrinter icon (a white shipping label with a barcode on a blue
# tile) and writes Assets\labelprinter.ico with PNG-encoded frames at the
# standard sizes. Re-run after changing the drawing:
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\labelprinter.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Blue tile.
    $tile = New-RoundedRect 0 0 $s $s ([Math]::Max(2, $s * 0.18))
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 31, 111, 235))), $tile)

    # White label.
    $lx = $s * 0.20; $ly = $s * 0.14; $lw = $s * 0.60; $lh = $s * 0.72
    $label = New-RoundedRect $lx $ly $lw $lh ([Math]::Max(1, $s * 0.06))
    $g.FillPath([System.Drawing.Brushes]::White, $label)

    # Barcode: bars of varying width across the lower part of the label.
    $ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 20, 24, 32))
    $pattern = if ($s -le 24) { @(2, 1, 1, 1, 2, 1, 1, 1, 2) } else { @(2, 1, 1, 1, 3, 1, 1, 2, 1, 1, 2, 1, 1, 1, 3) }
    $units = ($pattern | Measure-Object -Sum).Sum
    $bx = $lx + $lw * 0.14; $bw = $lw * 0.72
    $by = $ly + $lh * 0.48; $bh = $lh * 0.38
    $unit = $bw / $units
    $cx = $bx
    for ($i = 0; $i -lt $pattern.Count; $i++) {
        $w = $pattern[$i] * $unit
        if ($i % 2 -eq 0) { $g.FillRectangle($ink, [float]$cx, [float]$by, [float][Math]::Max(1, $w), [float]$bh) }
        $cx += $w
    }

    # Two text lines above the barcode (only where they stay legible).
    if ($s -ge 32) {
        $line = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 120, 130, 145))
        $g.FillRectangle($line, [float]$bx, [float]($ly + $lh * 0.14), [float]($bw * 0.85), [float]($lh * 0.08))
        $g.FillRectangle($line, [float]$bx, [float]($ly + $lh * 0.29), [float]($bw * 0.55), [float]($lh * 0.08))
    }

    $g.Dispose()
    return $bmp
}

$frames = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
}

# ICO container: ICONDIR + one ICONDIRENTRY per frame, then the PNG payloads.
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$f.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Close()
Write-Host "Wrote $out"
