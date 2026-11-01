param(
    # Optional artwork override; otherwise use the approved page mascot artwork.
    [string]$Source = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$OutDir = Join-Path $Root "apps\desktop-wpf\Assets"
$IcoPath = Join-Path $OutDir "novel-gm.ico"
$PreviewPath = Join-Path $OutDir "novel-gm-256.png"
$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if (-not $Source) { $Source = Join-Path $OutDir "novel-gm-logo.png" }
$art = $null
if ($Source) {
    if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }
    $art = [System.Drawing.Image]::FromFile((Resolve-Path $Source).Path)
}

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    if ($art) {
        $g.DrawImage($art, 0, 0, $size, $size)
    } else {
        # Same look as the title-bar badge: #1677FF rounded square with a white bold "N".
        $bg = New-RoundedRect 0 0 $size $size ([float]($size * 0.22))
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x16, 0x77, 0xFF))
        $g.FillPath($brush, $bg)

        $glyph = New-Object System.Drawing.Drawing2D.GraphicsPath
        $family = New-Object System.Drawing.FontFamily "Segoe UI"
        $glyph.AddString("N", $family, [int][System.Drawing.FontStyle]::Bold, 100, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
        $b = $glyph.GetBounds()
        $scale = ($size * 0.62) / $b.Height
        $m = New-Object System.Drawing.Drawing2D.Matrix
        $m.Translate(($size - $b.Width * $scale) / 2, ($size - $b.Height * $scale) / 2)
        $m.Scale($scale, $scale)
        $m.Translate(-$b.X, -$b.Y)
        $glyph.Transform($m)
        $g.FillPath([System.Drawing.Brushes]::White, $glyph)
    }
    $g.Dispose()
    return $bmp
}

# 32bpp DIB frame (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask); older readers
# such as GDI+ System.Drawing.Icon cannot decode PNG frames below 256px.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)

    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($s * $s * 4 + $maskRow * $s))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($row = $s - 1; $row -ge 0; $row--) { $w.Write($pixels, $row * $data.Stride, $s * 4) }
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return ,$ms.ToArray()
}

# ICO container: ICONDIR + one ICONDIRENTRY per size; 256px stored as PNG, smaller as DIB.
$frames = foreach ($s in $Sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
    } else {
        $bytes = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Data = $bytes }
}
if ($art) { $art.Dispose() }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$f.Data.Length); $w.Write([UInt32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($IcoPath, $out.ToArray())
$w.Dispose()

Write-Host "Icon written: $IcoPath ($($Sizes -join ', ') px)"
Write-Host "Preview:      $PreviewPath"
