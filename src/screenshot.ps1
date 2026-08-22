$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$code = @'
using System;
using System.Runtime.InteropServices;
public static class Win32Cap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue

$root = 'D:\Bionis\Goldberg test'
$exeArg = Join-Path $root 'steamless\Steamless.CLI.exe'

$p = Start-Process -FilePath (Join-Path $root 'Goldberg Patcher.exe') -ArgumentList @('--exe', "`"$exeArg`"") -PassThru
Start-Sleep -Seconds 3
$p.Refresh()
$h = $p.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw "no window handle" }
[Win32Cap]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 700
$r = New-Object Win32Cap+RECT
[Win32Cap]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$g.Dispose()
$out = "$env:TEMP\gp_shot.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "screenshot ${w}x${ht} -> $out"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
