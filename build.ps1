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

# ---- embedded payload (tools the app needs at runtime) ----
$pay = @(
    'steamless\Steamless.CLI.exe',
    'steamless\Steamless.CLI.exe.config'
)
Get-ChildItem (Join-Path $root 'steamless\Plugins') -Filter '*.dll' | ForEach-Object { $pay += 'steamless\Plugins\' + $_.Name }
$pay += @('release\regular\x86\steam_api.dll', 'release\regular\x64\steam_api64.dll')
$pay += @('release\tools\generate_interfaces\generate_interfaces_x86.exe', 'release\tools\generate_interfaces\generate_interfaces_x64.exe')
Get-ChildItem (Join-Path $root 'release\steam_settings.EXAMPLE') -Recurse -File | ForEach-Object { $pay += $_.FullName.Substring($root.Length + 1) }

$payRes = @()
$i = 0
$manLines = @()
foreach ($rel in $pay) {
    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full)) { throw "payload file missing: $rel" }
    $rn = 'gppay.{0:D4}' -f $i
    $manLines += "$rn|$rel"
    $payRes += "/res:`"$full`",$rn"
    $i++
}
$manTmp = Join-Path $env:TEMP ('gp_manifest_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
Set-Content -LiteralPath $manTmp -Value $manLines -Encoding Ascii
$payRes += "/res:`"$manTmp`",gppay.manifest"
Write-Host ("payload files: " + $i)

# ---- main app (windowed, self-contained) ----
try {
    Compile @("`"$src\Core.cs`"", "`"$src\Ui.cs`"", "`"$src\MainForm.cs`"", "`"$src\Batch.cs`"") (Join-Path $root 'Goldberg Patcher.exe') (@('/target:winexe') + $payRes)
} finally {
    Remove-Item -LiteralPath $manTmp -Force -ErrorAction SilentlyContinue
}

Write-Host "`nDone."
