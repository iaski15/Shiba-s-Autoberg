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
# vswhere is the supported way to ask where Visual Studio put things, and it answers in
# milliseconds. The recursive walk it replaces enumerated the entire VS tree - tens of
# thousands of files on a machine with several versions and workloads - and then threw
# almost all of them away. The walk survives only as a last resort.
#
# Every step here is defensive on purpose. `$env:ProgramFiles` and `$env:ProgramFiles(x86)`
# are NOT guaranteed to be set - they are empty in some shells, including this project's
# sandbox - and `Join-Path` with a null -Path is a terminating error under
# `$ErrorActionPreference = 'Stop'`, which would kill the build before it compiled anything.
# So: skip empty variables, fall back to the literal paths, and never let a discovery
# failure escape a step. Finding the compiler is best-effort until all four steps fail.
function Get-VisualStudioRoots {
    $roots = @()
    $bases = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, 'C:\Program Files (x86)', 'C:\Program Files')
    foreach ($base in $bases) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        $root = Join-Path $base 'Microsoft Visual Studio'
        if ($roots -notcontains $root -and (Test-Path -LiteralPath $root)) { $roots += $root }
    }
    return $roots
}

function Find-RoslynViaVsWhere {
    try {
        foreach ($root in Get-VisualStudioRoots) {
            $vswhere = Join-Path $root 'Installer\vswhere.exe'
            if (-not (Test-Path -LiteralPath $vswhere)) { continue }
            $found = @(& $vswhere -latest -products * -prerelease -find 'MSBuild\**\Bin\Roslyn\csc.exe' 2>$null)
            if ($found.Count -gt 0 -and (Test-Path -LiteralPath $found[0])) { return $found[0] }
        }
    } catch { }
    return $null
}

function Find-RoslynByGlob {
    # Shallow, targeted globs: the compiler only ever lives at <edition>\MSBuild\<version>\Bin\Roslyn.
    # This reaches two directories deep instead of walking every file under the install root.
    try {
        $hits = @()
        foreach ($root in Get-VisualStudioRoots) {
            $hits += @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -Filter 'MSBuild' -ErrorAction SilentlyContinue } |
                ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue } |
                ForEach-Object { Join-Path $_.FullName 'Bin\Roslyn\csc.exe' } |
                Where-Object { Test-Path -LiteralPath $_ })
        }
        if ($hits.Count -gt 0) {
            return ($hits | Sort-Object @{Expression = { (Get-Item -LiteralPath $_).VersionInfo.FileVersionRaw }; Descending = $true})[0]
        }
    } catch { }
    return $null
}

$csc = $CompilerPath
if (-not $csc) { $csc = Find-RoslynViaVsWhere }
if (-not $csc) { $csc = Find-RoslynByGlob }
if (-not $csc) {
    Write-Warning "vswhere and the targeted glob both came up empty; falling back to a full recursive search."
    foreach ($root in (Get-VisualStudioRoots + 'C:\Program Files (x86)\Microsoft Visual Studio')) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $candidates = @(Get-ChildItem -LiteralPath $root -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like '*Roslyn*' } |
            Sort-Object @{Expression = { $_.VersionInfo.FileVersionRaw }; Descending = $true}, FullName)
        if ($candidates.Count -gt 0) { $csc = $candidates[0].FullName; break }
    }
}
if (-not $csc -or -not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc.exe not found; specify -CompilerPath." }

# ---- version ----
# One place to bump it. The assembly attribute is what the UI renders, so the number on
# screen cannot drift from the build that produced it.
$version = '0.5'
$verFile = Join-Path $env:TEMP ('gp_version_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.cs')
[IO.File]::WriteAllText($verFile, @"
using System.Reflection;
[assembly: AssemblyVersion("$version.0.0")]
[assembly: AssemblyFileVersion("$version.0.0")]
[assembly: AssemblyInformationalVersion("$version")]
"@, (New-Object System.Text.UTF8Encoding($false)))

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
Compile @("`"$src\Core.cs`"", "`"$src\TestMain.cs`"", "`"$verFile`"") (Join-Path $root '_selftest.exe') $null

# ---- embedded payload (tools the app needs at runtime) ----
$pay = @(
    'steamless\Steamless.CLI.exe',
    'steamless\Steamless.CLI.exe.config'
)
$pluginsDir = Join-Path $root 'steamless\Plugins'
if (Test-Path $pluginsDir) {
    # ExamplePlugin is Steamless's sample/template plugin - it implements the API and does nothing.
    # Shipping it only means the CLI loads a no-op plugin on every run. The file stays in the vendored
    # tree (the fork is meant to be rebasable); it just is not part of the payload.
    Get-ChildItem $pluginsDir -Filter '*.dll' | Where-Object { $_.Name -ne 'ExamplePlugin.dll' } |
        ForEach-Object { $pay += 'steamless\Plugins\' + $_.Name }
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
    Compile @("`"$src\Core.cs`"", "`"$src\Ui.cs`"", "`"$src\MainForm.cs`"", "`"$src\Batch.cs`"", "`"$verFile`"") (Join-Path $root 'Goldberg Patcher.exe') (@('/target:winexe') + $payRes)
} finally {
    # The deflated payload copies are only needed while the compiler reads them.
    foreach ($temp in $payTemp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $manTmp -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $verFile -Force -ErrorAction SilentlyContinue
}

if ($Verify) {
    Write-Host "`nverify: running _selftest.exe..."
    & (Join-Path $root '_selftest.exe')
    if ($LASTEXITCODE -ne 0) { throw "self-test failed with exit code $LASTEXITCODE" }
    Write-Host "verify: OK"
}

Write-Host "`nDone."
