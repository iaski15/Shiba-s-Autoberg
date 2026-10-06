# Regenerates src\app.ico from the mascot drawing in Ui.cs (Gp.Shiba), so the exe icon and the in-app
# shiba never drift apart. Run after .\build.ps1 (it loads the built Shibaberg.exe), then rebuild so the
# new icon is embedded:  .\build.ps1; .\src\make_icon.ps1; .\build.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'Shibaberg.exe'
$out = Join-Path $PSScriptRoot 'app.ico'
$asm = [Reflection.Assembly]::LoadFile($exe)
$render = $asm.GetType('Gp.Shiba').GetMethod('Render')
$neutral = [Enum]::Parse($asm.GetType('Gp.ShibaMood'), 'Neutral')

# ICO = 6-byte header, one 16-byte directory entry per size, then each image as a PNG.
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = $render.Invoke($null, [object[]]@([int]$s, $neutral))
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"wrote $out ($((Get-Item $out).Length) bytes, sizes $($sizes -join '/'))"
