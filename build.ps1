param(
    [string]$CompilerPath = '',
    [string]$ReferencePath = '',
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'

# DeflateStream, used to shrink the embedded payload.
Add-Type -AssemblyName System.IO.Compression

function Compress-File([string]$source, [string]$destination) {
    # $input/$output are reserved automatic variables in PowerShell - do not use those names here.
    $inStream = [IO.File]::OpenRead($source)
    try {
        $outStream = [IO.File]::Create($destination)
        try {
            $deflate = New-Object System.IO.Compression.DeflateStream($outStream, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $inStream.CopyTo($deflate) } finally { $deflate.Dispose() }
        } finally { $outStream.Dispose() }
    } finally { $inStream.Dispose() }
}

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
$payTemp = @()
$i = 0
$rawTotal = 0
$embeddedTotal = 0
$manLines = @()
foreach ($rel in $pay) {
    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full)) { throw "payload file missing: $rel" }
    $rn = 'gppay.{0:D4}' -f $i
    # The manifest hash is always of the UNCOMPRESSED bytes, so verification logic is unchanged.
    $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
    $rawLen = (Get-Item -LiteralPath $full).Length

    # Deflate to a scratch file and keep whichever is smaller - deflating a tiny file can make it bigger.
    $tmp = Join-Path $env:TEMP ('gp_pay_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.bin')
    Compress-File $full $tmp
    $compLen = (Get-Item -LiteralPath $tmp).Length
    if ($compLen -lt $rawLen) {
        $embed = $tmp
        $mode = 'deflate'
        $payTemp += $tmp
        $embeddedTotal += $compLen
    } else {
        Remove-Item -LiteralPath $tmp -Force
        $embed = $full
        $mode = 'raw'
        $embeddedTotal += $rawLen
    }
    $rawTotal += $rawLen
    # res|relative path|sha256 of the uncompressed bytes|uncompressed length|deflate|raw
    $manLines += "$rn|$rel|$hash|$rawLen|$mode"
    $payRes += "/res:`"$embed`",$rn"
    $i++
}
$manTmp = Join-Path $env:TEMP ('gp_manifest_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt')
# UTF-8 without BOM – the old Ascii encoding would corrupt non-ASCII paths in the manifest.
[IO.File]::WriteAllLines($manTmp, $manLines, (New-Object System.Text.UTF8Encoding($false)))
$payRes += "/res:`"$manTmp`",gppay.manifest"
Write-Host ("payload files: " + $i + "   embedded " + [math]::Round($embeddedTotal / 1MB, 2) + " MB (raw " + [math]::Round($rawTotal / 1MB, 2) + " MB)")

# ---- main app (windowed, self-contained) ----
try {
    Compile @("`"$src\Core.cs`"", "`"$src\Ui.cs`"", "`"$src\MainForm.cs`"", "`"$src\Batch.cs`"") (Join-Path $root 'Goldberg Patcher.exe') (@('/target:winexe') + $payRes)
} finally {
    # The deflated payload copies are only needed while the compiler reads them.
    foreach ($temp in $payTemp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $manTmp -Force -ErrorAction SilentlyContinue
}

if ($Verify) {
    Write-Host "`nverify: running _selftest.exe..."
    & (Join-Path $root '_selftest.exe')
    if ($LASTEXITCODE -ne 0) { throw "self-test failed with exit code $LASTEXITCODE" }
    Write-Host "verify: OK"
}

Write-Host "`nDone."
