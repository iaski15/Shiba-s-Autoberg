$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'

# ---- locate Roslyn csc ----
$csc = Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio" -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*Roslyn*' } | Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) { throw "Roslyn csc.exe not found (VS Build Tools required)" }

# ---- reference assemblies (.NET Framework 4.8) ----
$refDir = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
if (-not (Test-Path $refDir)) { $refDir = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319" }
$refs = @('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll') |
    ForEach-Object { "/r:`"$refDir\$_`"" }

Write-Host "csc:   $csc"
Write-Host "refs:  $refDir"

# ---- icon ----
$icon = Join-Path $root 'app.ico'
if (-not (Test-Path $icon)) {
    & (Join-Path $src 'make_icon.ps1')
    Copy-Item (Join-Path $src 'app.ico') $icon
}
$iconArg = "/win32icon:`"$icon`""

function Compile($sources, $out, $extra) {
    $cscArgs = @('/nologo','/noconfig','/target:exe','/platform:anycpu','/optimize+','/utf8output') + $refs + $sources
    $cscArgs += @("/out:`"$out`"")
    if ($iconArg) { $cscArgs += $iconArg }
    if ($extra) { $cscArgs += $extra }
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $out" }
    Write-Host "built: $out"
}

# ---- self test host (console) ----
Compile @("`"$src\Core.cs`"", "`"$src\TestMain.cs`"") (Join-Path $root '_selftest.exe') $null

# ---- main app (windowed) ----
Compile @("`"$src\Core.cs`"", "`"$src\Ui.cs`"", "`"$src\MainForm.cs`"") (Join-Path $root 'Goldberg Patcher.exe') @('/target:winexe')

Write-Host "`nDone."
