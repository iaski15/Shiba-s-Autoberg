$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot 'app.ico'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [int]($size * 0.08)
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $rad = [int]($size * 0.24)

    # rounded gradient square
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
        ([System.Drawing.Color]::FromArgb(255, 124, 92, 255)),
        ([System.Drawing.Color]::FromArgb(255, 77, 159, 255)), 55.0)
    $g.FillPath($brush, $path)

    # play triangle
    $cx = $size / 2.0
    $tw = $size * 0.30; $th = $size * 0.36
    $p1 = New-Object System.Drawing.PointF(($cx - $tw/2), (($size - $th)/2))
    $p2 = New-Object System.Drawing.PointF(($cx - $tw/2), (($size + $th)/2))
    $p3 = New-Object System.Drawing.PointF(($cx + $tw/2 * 1.25), ($size / 2))
    $white = [System.Drawing.Brushes]::White
    $g.FillPolygon($white, @($p1, $p2, $p3))

    $g.Dispose()
    return ,$bmp
}

# render at 256, save png
$bmp = New-IconBitmap 256
$pngPath = Join-Path $env:TEMP 'gp_icon.png'
$bmp.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$png = [System.IO.File]::ReadAllBytes($pngPath)
$bmp.Dispose()

# wrap single PNG into .ico container
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)      # reserved
$bw.Write([uint16]1)      # type: icon
$bw.Write([uint16]1)      # count
$bw.Write([byte]0)        # width 256 -> 0
$bw.Write([byte]0)        # height 256 -> 0
$bw.Write([byte]0)        # palette
$bw.Write([byte]0)        # reserved
$bw.Write([uint16]1)      # planes
$bw.Write([uint16]32)     # bpp
$bw.Write([uint32]$png.Length)
$bw.Write([uint32]22)     # offset
$bw.Write($png)
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host "icon written: $out"
