param(
    [string]$CompilerPath = '',
    [string]$ReferencePath = '',
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'

# ---- locate Roslyn csc ----
$csc = $CompilerPath
if (-not $csc) {
    $candidates = @(Get-ChildItem "C:\Program Files (x86)\Microsoft Visual Studio" -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*Roslyn*' } |
        Sort-Object @{Expression = { $_.VersionInfo.FileVersionRaw }; Descending = $true}, FullName)
    if ($candidates.Count -gt 0) { $csc = $candidates[0].FullName }
}
if (-not $csc -or -not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc.exe not found; specify -CompilerPath." }

# ---- reference assemblies (.NET Framework 4.8) ----
$refDir = $ReferencePath
if (-not $refDir) {
    $refDir = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
    if (-not (Test-Path -LiteralPath $refDir)) {
        $refDir = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
        Write-Warning "4.8 targeting pack unavailable; using installed Framework assemblies. Pin -ReferencePath for reproducible references."
    }
}
$refs = @('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll') |
    ForEach-Object {
        if (-not (Test-Path -LiteralPath (Join-Path $refDir $_))) { throw "Required reference missing: $refDir\$_" }
        "/r:`"$refDir\$_`""
    }

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
    $cscArgs = @('/nologo','/noconfig','/target:exe','/platform:anycpu','/optimize+','/utf8output','/nostdlib-','/deterministic') + @("/pathmap:`"$root=.`"") + $refs + $sources
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
$pluginsDir = Join-Path $root 'steamless\Plugins'
if (Test-Path $pluginsDir) {
    Get-ChildItem $pluginsDir -Filter '*.dll' | ForEach-Object { $pay += 'steamless\Plugins\' + $_.Name }
}
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
    # Third field is the SHA256 so Payload.ExtractMissing can repair same-size corrupted files.
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    $manLines += "$rn|$rel|$hash"
    $payRes += "/res:`"$full`",$rn"
    $i++
}
$manTmp = Join-Path $env:TEMP ('gp_manifest_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
# UTF-8 without BOM – the old Ascii encoding would corrupt non-ASCII paths in the manifest.
[IO.File]::WriteAllLines($manTmp, $manLines, (New-Object System.Text.UTF8Encoding($false)))
$payRes += "/res:`"$manTmp`",gppay.manifest"
Write-Host ("payload files: " + $i)

# ---- main app (windowed, self-contained) ----
try {
    Compile @("`"$src\Core.cs`"", "`"$src\Ui.cs`"", "`"$src\MainForm.cs`"", "`"$src\Batch.cs`"") (Join-Path $root 'Goldberg Patcher.exe') (@('/target:winexe') + $payRes)
} finally {
    Remove-Item -LiteralPath $manTmp -Force -ErrorAction SilentlyContinue
}

if ($Verify) {
    Write-Host "`nverify: running _selftest.exe..."
    & (Join-Path $root '_selftest.exe')
    if ($LASTEXITCODE -ne 0) { throw "self-test failed with exit code $LASTEXITCODE" }
    Write-Host "verify: OK"
}

Write-Host "`nDone."
