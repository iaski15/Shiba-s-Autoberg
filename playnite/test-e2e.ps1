$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

# End-to-end check of the Shibaberg Playnite extension against a real Playnite, without touching the real one:
#  * copies the installed Playnite (%LOCALAPPDATA%\Playnite) to a temp folder WITHOUT unins000.exe, which makes
#    that copy portable - it keeps its configuration and database beside itself;
#  * gives it a minimal config.json (first-run wizard done, database inside the copy);
#  * installs the freshly built extension (playnite\bin, from build.ps1) into the copy's Extensions folder;
#  * points SHIBABERG_STATE_DIR at a temp state folder whose playnite.txt lists one fake game;
#  * starts the copy and waits for its log to show the game being imported, then closes it and cleans up.
# The playnite:// link is deliberately not tested: it goes to whichever Playnite owns the protocol.
# Not run by build.ps1. Needs Windows PowerShell 5.1 and an installed Playnite.

$real = Join-Path $env:LOCALAPPDATA 'Playnite'
if (-not (Test-Path -LiteralPath (Join-Path $real 'Playnite.DesktopApp.exe'))) { throw "No installed Playnite at $real." }
if (Get-Process -Name 'Playnite.DesktopApp', 'Playnite.FullscreenApp' -ErrorAction SilentlyContinue) {
    throw 'Playnite is running - close it first (the test copy would hand over to it as the single instance).'
}
$bin = Join-Path $PSScriptRoot 'bin'
foreach ($f in 'ShibabergPlaynite.dll', 'extension.yaml', 'icon.png') {
    if (-not (Test-Path -LiteralPath (Join-Path $bin $f))) { throw "playnite\bin\$f missing - run .\build.ps1 first." }
}

$work = Join-Path $env:TEMP ('gp_playnite_e2e_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$pn = Join-Path $work 'Playnite'
$state = Join-Path $work 'state'
$proc = $null
try {
    Write-Host "copying Playnite to $pn ..."
    New-Item -ItemType Directory -Force $pn | Out-Null
    # Only the program: no user data, no extensions, no uninstaller (its absence is what makes the copy portable).
    Get-ChildItem -LiteralPath $real -Force | Where-Object { $_.Name -notin @('unins000.exe', 'unins000.dat', 'library', 'Extensions', 'ExtensionsData', 'config.json', 'playnite.log', 'cache') } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $pn -Recurse -Force }
    if (Test-Path -LiteralPath (Join-Path $pn 'unins000.exe')) { throw 'the copy still has unins000.exe and would not be portable' }

    $db = Join-Path $pn 'library'
    $realDb = Join-Path $env:APPDATA 'Playnite\library'
    if ([string]::Equals((Resolve-Path -LiteralPath $pn).Path, (Resolve-Path -LiteralPath $real).Path, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($db, $realDb, [StringComparison]::OrdinalIgnoreCase)) { throw 'refusing to run against the real Playnite data' }
    $config = @{ FirstTimeWizardComplete = $true; DatabasePath = $db } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $pn 'config.json'), $config, (New-Object System.Text.UTF8Encoding($false)))

    $ext = Join-Path $pn 'Extensions\Shibaberg_Library'
    New-Item -ItemType Directory -Force $ext | Out-Null
    Copy-Item -Path (Join-Path $bin '*') -Destination $ext -Force

    # one fake game: a stock exe in a throwaway folder
    $game = New-Item -ItemType Directory -Force (Join-Path $work 'games\Shiba E2E Test [Build 1]')
    $exe = Join-Path $game.FullName 'ShibaE2E.exe'
    Copy-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('SystemX86')) 'cmd.exe') -Destination $exe
    $name = 'Shiba E2E Test ' + [guid]::NewGuid().ToString('N').Substring(0, 6)
    New-Item -ItemType Directory -Force $state | Out-Null
    $line = "sb-0123456789abcdef`t1250`t$name`t$exe`t$($game.FullName)`t2026-01-01T00:00:00Z"
    [IO.File]::WriteAllLines((Join-Path $state 'playnite.txt'), [string[]]@('# e2e', $line), (New-Object System.Text.UTF8Encoding($false)))

    # Process.Start rather than Start-Process (see _live_test.ps1: duplicate-cased environment variables).
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $pn 'Playnite.DesktopApp.exe'
    $psi.WorkingDirectory = $pn
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['SHIBABERG_STATE_DIR'] = $state
    $proc = [System.Diagnostics.Process]::Start($psi)

    $log = Join-Path $pn 'playnite.log'
    $deadline = (Get-Date).AddSeconds(180)
    $found = $false
    Write-Host 'waiting for the library update to import the game ...'
    while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
        Start-Sleep -Seconds 2
        if (-not (Test-Path -LiteralPath $log)) { continue }
        $text = [IO.File]::ReadAllText($log)
        if ($text -match ('Adding new game.*' + [regex]::Escape($name)) -or ($text -match 'Adding new game' -and $text.Contains($name))) { $found = $true; break }
    }
    if ($found) { Write-Host "  PASS  Playnite imported '$name' from the Shibaberg library" }
    else {
        Write-Host "  FAIL  no 'Adding new game ... $name' in $log within 180 s"
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 40 | ForEach-Object { Write-Host "        | $_" } }
        $script:failed = $true
    }
}
finally {
    if ($proc -ne $null -and -not $proc.HasExited) {
        [void]$proc.CloseMainWindow()
        if (-not $proc.WaitForExit(15000)) { $proc.Kill() }
    }
    Start-Sleep -Seconds 1
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($script:failed) { exit 1 }
