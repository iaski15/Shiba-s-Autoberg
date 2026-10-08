param(
    [string]$CompilerPath = '',
    [string]$ReferencePath = '',
    [switch]$Verify,
    # Also write dist\Shibaberg-<version>.zip: the exe (everything it runs on is embedded) + docs + licenses.
    [switch]$Package
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
# vswhere first, then a shallow glob. $env:ProgramFiles(x86) can be empty in some shells, and Join-Path
# with a null -Path is terminating under 'Stop', so empty variables are skipped and literal paths added.
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
if (-not $csc -or -not (Test-Path -LiteralPath $csc)) { throw "Roslyn csc.exe not found; specify -CompilerPath." }

# ---- version ----
# One place to bump it. The assembly attribute is what the UI renders, so the number on
# screen cannot drift from the build that produced it.
$version = '0.7'
# Google sign-in for cloud saves: the OAuth "Desktop app" client from google_client.json (the file Google
# Cloud Console downloads; gitignored). Google treats an installed app's secret as public, but it stays out
# of the repo anyway. Without the file the build still works and cloud saves say sign-in isn't set up.
$gcId = ''; $gcSecret = ''
$gcFile = Join-Path $root 'google_client.json'
if (Test-Path -LiteralPath $gcFile) {
    $gc = (Get-Content -LiteralPath $gcFile -Raw | ConvertFrom-Json).installed
    $gcId = $gc.client_id; $gcSecret = $gc.client_secret
    if ($gcId -notmatch '^[\w.-]+$' -or $gcSecret -notmatch '^[\w.-]+$') { throw 'google_client.json: unexpected client_id/client_secret format' }
}
$verFile = Join-Path $env:TEMP ('gp_version_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.cs')
[IO.File]::WriteAllText($verFile, @"
using System.Reflection;
[assembly: AssemblyVersion("$version.0.0")]
[assembly: AssemblyFileVersion("$version.0.0")]
[assembly: AssemblyInformationalVersion("$version")]
namespace Gp { static class GoogleClient { public const string Id = "$gcId"; public const string Secret = "$gcSecret"; } }
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
$refs = @('mscorlib.dll','System.dll','System.Core.dll','Microsoft.CSharp.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.Security.dll') |
    ForEach-Object {
        if (-not (Test-Path -LiteralPath (Join-Path $refDir $_))) { throw "Required reference missing: $refDir\$_" }
        "/r:`"$refDir\$_`""
    }

Write-Host "csc:   $csc"
Write-Host "refs:  $refDir"

# ---- icon ----
# src\app.ico is committed; regenerate it from the mascot with src\make_icon.ps1 (needs a built Shibaberg.exe).
$iconArg = "/win32icon:`"$(Join-Path $src 'app.ico')`""

function Compile($sources, $out, $extra) {
    $cscArgs = @('/nologo','/noconfig','/target:exe','/platform:anycpu','/optimize+','/utf8output','/nostdlib-','/deterministic') + @("/pathmap:`"$root=.`"") + $refs + $sources
    $cscArgs += @("/out:`"$out`"", $iconArg)
    if ($extra) { $cscArgs += $extra }
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $out" }
    Write-Host "built: $out"
}

# ---- Shibaless: our fork of Steamless (shibaless\, see VENDORED.md) ----
# Compiled straight into both exes; the unpackers run in-process (src\Unpacker\ShibalessUnpacker.cs).
# Left out: AssemblyInfo (it would clash with ours) and the two WPF-only view-model files no unpacker uses.
$shibalessDir = Join-Path $root 'shibaless'
$shibalessSrc = @(Get-ChildItem -LiteralPath $shibalessDir -Recurse -File |
    Where-Object { $_.Extension -eq '.cs' -and $_.Directory.Name -ne 'Properties' -and $_.Name -ne 'ViewModelBase.cs' -and $_.Name -ne 'NavigatedEventArgs.cs' } |
    Sort-Object FullName | ForEach-Object { "`"$($_.FullName)`"" })
# SharpDisasm (used by the 2.x unpackers) is a prebuilt upstream binary: referenced, and embedded so the
# exe stays self-contained (deflated, 220 KB -> 71 KB) - ShibalessUnpacker resolves it from the resource.
$sharpDisasm = Join-Path $shibalessDir 'Shibaless.Unpacker.Variant21.x86\SharpDisasm.dll'
$sharpDisasmDeflated = Join-Path $env:TEMP ('gp_sharpdisasm_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.bin')
Compress-File $sharpDisasm $sharpDisasmDeflated
$shibalessArgs = @("/r:`"$sharpDisasm`"", "/res:`"$sharpDisasmDeflated`",SharpDisasm.dll.deflate")

# ---- self test host (console) ----
Compile (@("`"$src\Core.cs`"", "`"$src\Cloud.cs`"", "`"$src\Unpacker\ShibalessUnpacker.cs`"", "`"$src\TestMain.cs`"", "`"$verFile`"") + $shibalessSrc) (Join-Path $root '_selftest.exe') $shibalessArgs

# ---- embedded payload (tools the app needs at runtime) ----
# Steamless is not in the payload: Shibaless, our fork of it, is compiled into the exe (shibaless\).
# The emulator dlls are Shibaberg builds (shibaberg\bin, rebuilt from shibaberg\ by tools\build-shibaberg.ps1).
$pay = @('shibaberg\bin\x86\steam_api.dll', 'shibaberg\bin\x64\steam_api64.dll')
# The overlay build (in-game achievement toast), installed instead when achievements are switched on.
$pay += @('shibaberg\bin\overlay\x86\steam_api.dll', 'shibaberg\bin\overlay\x64\steam_api64.dll')
Get-ChildItem (Join-Path $root 'shibaberg\post_build\steam_settings.EXAMPLE') -Recurse -File | ForEach-Object { $pay += $_.FullName.Substring($root.Length + 1) }

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
# UTF-8 without BOM: Payload.Load reads the lines as-is.
[IO.File]::WriteAllLines($manTmp, $manLines, (New-Object System.Text.UTF8Encoding($false)))
$payRes += "/res:`"$manTmp`",gppay.manifest"
Write-Host ("payload files: " + $i + "   embedded " + [math]::Round($embeddedTotal / 1MB, 2) + " MB (raw " + [math]::Round($rawTotal / 1MB, 2) + " MB)")

# ---- main app (windowed, self-contained) ----
try {
    Compile (@("`"$src\Core.cs`"", "`"$src\Unpacker\ShibalessUnpacker.cs`"", "`"$src\Ui.cs`"", "`"$src\MainForm.cs`"", "`"$src\Batch.cs`"", "`"$src\Cloud.cs`"", "`"$src\CloudForm.cs`"", "`"$src\Installer.cs`"", "`"$verFile`"") + $shibalessSrc) (Join-Path $root 'Shibaberg.exe') (@('/target:winexe') + $shibalessArgs + $payRes)
} finally {
    # The deflated payload copies are only needed while the compiler reads them.
    foreach ($temp in $payTemp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $manTmp -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $verFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $sharpDisasmDeflated -Force -ErrorAction SilentlyContinue
}

if ($Verify) {
    Write-Host "`nverify: running _selftest.exe..."
    & (Join-Path $root '_selftest.exe')
    if ($LASTEXITCODE -ne 0) { throw "self-test failed with exit code $LASTEXITCODE" }
    Write-Host "verify: OK"
}

if ($Package) {
    # The exe is self-contained; only what must stay readable as files ships beside it.
    $dist = Join-Path $root 'dist'
    $stage = Join-Path $dist "Shibaberg-$version"
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    $lic = New-Item -ItemType Directory -Force (Join-Path $stage 'licenses')
    Copy-Item -LiteralPath (Join-Path $root 'Shibaberg.exe') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'shibaberg\post_build\README.release.md') -Destination (Join-Path $stage 'Emulator settings.md')
    Copy-Item -LiteralPath (Join-Path $root 'shibaberg\LICENSE') -Destination (Join-Path $lic 'Shibaberg (gbe_fork) - LGPL-3.0.txt')
    Copy-Item -LiteralPath (Join-Path $root 'shibaberg\CREDITS.md') -Destination (Join-Path $lic 'Shibaberg (gbe_fork) - third-party credits.md')
    Copy-Item -LiteralPath (Join-Path $root 'shibaless\LICENSE') -Destination (Join-Path $lic 'Shibaless (Steamless) - CC BY-NC-ND 4.0.txt')
    $zip = Join-Path $dist "Shibaberg-$version.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip
    Write-Host ("package: {0}  ({1:N1} MB)" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))

    # The installer is the same exe under a Setup name: Installer.cs sees the name and installs itself
    # (per user, %LOCALAPPDATA%\Programs\Shibaberg + Start menu shortcut + Installed apps entry).
    $setup = Join-Path $dist "Shibaberg-Setup-$version.exe"
    Copy-Item -LiteralPath (Join-Path $root 'Shibaberg.exe') -Destination $setup -Force
    Write-Host ("setup:   {0}  ({1:N1} MB)" -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
}

Write-Host "`nDone."
