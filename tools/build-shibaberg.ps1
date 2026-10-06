param(
    # Short path on purpose: the dependency builds nest deep enough to hit MAX_PATH.
    [string]$Work = "$env:SystemDrive\shibaberg-build",
    # Build into $Work only; don't replace shibaberg\bin\*.
    [switch]$NoInstall
)
# Builds the two dlls the patcher ships (shibaberg\bin\x86\steam_api.dll, x64\steam_api64.dll) from
# shibaberg\ with the same commands gbe_fork's own CI uses, then writes shibaberg\bin\BUILD.txt.
# Not part of build.ps1: a cold run builds every C++ dependency (curl, protobuf, mbedtls, ...) and takes a
# while. Later runs reuse $Work\build\deps. Needs VS 2026 (C++ workload), git and network access.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$src = Join-Path $repo 'shibaberg'

# gbe_fork keeps its dependencies and build helpers on separate branches of its own repo, wired in as
# submodules. These are the commits $commit points at.
$upstream = 'https://github.com/Detanup01/gbe_fork.git'
$commit = '64bd1fcfef82d397cd4bdba49adc08f0c49da31c'   # release-2026_07_19, the commit shibaberg\ is based on
$submodules = [ordered]@{
    'third-party\common\win' = 'd431ffceaadf4e279e70bc4adea3ae42be6820ac'
    'third-party\build\win'  = '556998fa1c2df35e7d208c1ddfb7445080ab2ec2'
    'third-party\deps\win'   = '83308a20b093ff955fd8119726eb6d531027b2a6'
    'third-party\deps\common' = '92a4a130262083c4e887155cbe6bcab99baf36ea'
}

function Run([string]$exe) {
    # Native tools write warnings to stderr; under Stop, PowerShell 5.1 would turn those into a terminating
    # error. Judge success by the exit code only.
    $ErrorActionPreference = 'Continue'
    & $exe @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

# Source -> work tree. build\ (deps cache, outputs) and third-party\ (fetched below) are kept between runs.
New-Item -ItemType Directory -Force $Work | Out-Null
robocopy $src $Work /MIR /XD build third-party bin .git /XF VENDORED.md Rename-FromUpstream.ps1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

foreach ($sm in $submodules.GetEnumerator()) {
    $dir = Join-Path $Work $sm.Key
    $have = if (Test-Path -LiteralPath (Join-Path $dir '.git')) { (git -C $dir rev-parse HEAD) } else { '' }
    if ($have -eq $sm.Value) { continue }
    Write-Host "fetching $($sm.Key) @ $($sm.Value.Substring(0, 7))"
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    Run git init '-q' $dir
    Run git '-C' $dir fetch '-q' '--depth' 1 $upstream $sm.Value
    Run git '-C' $dir '-c' advice.detachedHead=false checkout '-q' FETCH_HEAD
    # A dependency change means the cached dependency build is stale.
    if ($sm.Key -like 'third-party\deps\*') { Remove-Item -LiteralPath (Join-Path $Work 'build\deps') -Recurse -Force -ErrorAction SilentlyContinue }
}

Push-Location $Work
try {
    $premake = '.\third-party\common\win\premake\premake5.exe'
    if (-not (Test-Path 'build\deps\win\vs2026\protobuf\install64')) {
        Write-Host 'building dependencies (slow, once)...'
        $env:CMAKE_GENERATOR = 'Visual Studio 18 2026'
        Run $premake '--file=premake5-deps.lua' '--64-build' '--32-build' '--all-ext' '--all-build' "--j=$([Environment]::ProcessorCount)" '--clean' '--os=windows' vs2026
    }

    $vswhere = '.\third-party\common\win\vswhere\vswhere.exe'
    $msbuild = Join-Path (& $vswhere '-products' '*' '-requires' 'Microsoft.Component.MSBuild' '-prerelease' '-latest' '-nologo' '-property' installationPath) 'MSBuild\Current\Bin\MSBuild.exe'
    Run $premake '--file=premake5.lua' '--genproto' "--emubuild=shibaberg-$commit" '--dosstub' '--winrsrc' '--winsign' '--os=windows' vs2026
    foreach ($platform in 'Win32', 'x64') {
        Run $msbuild /nologo /m:1 "-p:CL_MPCount=$([Environment]::ProcessorCount)" /v:m "/p:Configuration=release,Platform=$platform" /target:api_regular 'build\project\vs2026\win\gbe.slnx'
    }
} finally { Pop-Location }

$out = Join-Path $Work 'build\win\vs2026\release\regular'
$built = @{ 'x86\steam_api.dll' = (Join-Path $out 'x86\steam_api.dll'); 'x64\steam_api64.dll' = (Join-Path $out 'x64\steam_api64.dll') }
foreach ($f in $built.Values) { if (-not (Test-Path -LiteralPath $f)) { throw "build output missing: $f" } }
if ($NoInstall) { "built in $out"; return }

$dest = Join-Path $src 'bin'
$lines = @(
    'Shibaberg (fork of gbe_fork) - built by tools\build-shibaberg.ps1',
    "upstream commit: $commit",
    "msbuild: $((& $msbuild '-nologo' '-version' | Select-Object -Last 1))",
    "built: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC"
)
foreach ($k in $built.Keys | Sort-Object) {
    Copy-Item -LiteralPath $built[$k] -Destination (Join-Path $dest $k) -Force
    $lines += "sha256 $($k.Replace('\', '/')): $((Get-FileHash -LiteralPath (Join-Path $dest $k) -Algorithm SHA256).Hash.ToLowerInvariant())"
}
[IO.File]::WriteAllLines((Join-Path $dest 'BUILD.txt'), $lines)
$lines
