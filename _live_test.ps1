$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Live end-to-end check: patch a throwaway game in %TEMP%, verify the artifacts on disk, then verify the
# compressed payload can repair itself.
#
# Notes on why this is written the way it is:
#  * The patcher is a /target:winexe app, so `& ".\Goldberg Patcher.exe"` returns immediately and never
#    sets $LASTEXITCODE. Process.Start + WaitForExit is the only reliable way to get its exit code.
#  * Process.Start is used rather than Start-Process, which rebuilds the environment into a
#    case-insensitive dictionary and throws "Item has already been added" when the parent environment
#    contains the same variable in two cases (e.g. both http_proxy and HTTP_PROXY).
#  * --batch is used rather than the GUI flags (--exe/--appid/--auto) because the GUI path restores its
#    options from settings.ini, so an online-fix setting left on would silently change what is tested.
#    --batch pins OnlineFix=false and always exercises the dll replacement path.
#  * $env:APPDATA is not guaranteed to be set, so the state directory is resolved through the shell API.

$patcher = Join-Path $PSScriptRoot 'Goldberg Patcher.exe'
$stateDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'GoldbergPatcher'
$journal = Join-Path $stateDir 'last-patch\journal.txt'
$failures = New-Object System.Collections.Generic.List[string]

function Test-Artifact([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("  PASS  " + $name) }
    else { Write-Host ("  FAIL  " + $name + "   -> " + $detail); $script:failures.Add($name) }
}

function Invoke-Patcher([string]$arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $patcher
    $psi.Arguments = $arguments
    $psi.WorkingDirectory = $PSScriptRoot
    $psi.UseShellExecute = $false
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.WaitForExit()
    return $proc.ExitCode
}

# ---------------------------------------------------------------- patch a throwaway game

$live = Join-Path $env:TEMP 'gp_live'
if (Test-Path -LiteralPath $live) { Remove-Item -LiteralPath $live -Recurse -Force }
$game = (New-Item -ItemType Directory -Path (Join-Path $live 'Half-Life 2')).FullName
Copy-Item -LiteralPath '.\steamless\Steamless.CLI.exe' -Destination (Join-Path $game 'hl2.exe')
# A pre-existing Steamworks dll makes the run exercise backup + replacement, not just file creation.
Copy-Item -LiteralPath '.\steamless\Plugins\Steamless.Unpacker.Variant31.x86.dll' -Destination (Join-Path $game 'steam_api.dll')
$originalHash = (Get-FileHash -LiteralPath (Join-Path $game 'steam_api.dll') -Algorithm SHA256).Hash

$exe = Join-Path $game 'hl2.exe'
$batchArgs = '--batch "' + $exe + '|220"'
Write-Host ("test game: " + $exe)

$journalBefore = if (Test-Path -LiteralPath $journal) { (Get-Item -LiteralPath $journal).LastWriteTimeUtc } else { [DateTime]::MinValue }
$code = -1
try { $code = Invoke-Patcher $batchArgs } catch { Write-Host ("invocation failed: " + $_.Exception.Message); $code = 99 }
Write-Host ("EXIT=" + $code)
if ($code -ne 0) { $failures.Add("exit code $code") }

$appidFile = Join-Path $game 'steam_appid.txt'
$appidOk = (Test-Path -LiteralPath $appidFile) -and ((Get-Content -LiteralPath $appidFile -Raw).Trim() -eq '220')
Test-Artifact 'steam_appid.txt written with the requested AppID' $appidOk $(if (Test-Path -LiteralPath $appidFile) { Get-Content -LiteralPath $appidFile -Raw } else { 'missing' })

$installed = Join-Path $game 'steam_api.dll'
$installedOk = (Test-Path -LiteralPath $installed) -and ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -ne $originalHash)
Test-Artifact 'steam_api.dll replaced by the emulator dll' $installedOk 'dll unchanged'

$backupDir = Join-Path $game 'goldberg_backup'
$backupFiles = @(Get-ChildItem -LiteralPath $backupDir -Recurse -File -Filter 'steam_api.dll' -ErrorAction SilentlyContinue)
$backupOk = (Test-Path -LiteralPath $backupDir) -and ($backupFiles.Count -eq 1) -and ((Get-FileHash -LiteralPath $backupFiles[0].FullName -Algorithm SHA256).Hash -eq $originalHash)
Test-Artifact 'original dll preserved, hash-verified, in goldberg_backup' $backupOk ("files: " + $backupFiles.Count)

$stray = @(Get-ChildItem -LiteralPath $backupDir -Recurse -Directory -Filter '.gp-recovery' -ErrorAction SilentlyContinue)
Test-Artifact 'no .gp-recovery litter inside goldberg_backup' ((Test-Path -LiteralPath $backupDir) -and ($stray.Count -eq 0)) ("backup dir: " + (Test-Path -LiteralPath $backupDir) + ", stray: " + $stray.Count)

# The journal must have been rewritten by *this* run, not left over from an earlier one.
$journalOk = (Test-Path -LiteralPath $journal) -and ((Get-Item -LiteralPath $journal).LastWriteTimeUtc -gt $journalBefore) `
    -and ((Get-Content -LiteralPath $journal -Raw) -match 'state=completed')
Test-Artifact 'undo journal rewritten by this run' $journalOk $journal

# The recovery copy is what "Undo last patch" restores from - it must survive the collection pass.
$previous = @(Get-ChildItem -LiteralPath $game -Recurse -File -Filter '*.previous' -ErrorAction SilentlyContinue)
Test-Artifact 'recovery copy retained for undo' ($previous.Count -ge 1) ("found " + $previous.Count)

# ---------------------------------------------------------------- compressed payload

# The payload is deflated at build time, so a repair has to inflate it back correctly. It also lives in
# %LOCALAPPDATA% rather than beside the exe (optimizations #4) and is scoped by payload build id, so find
# the newest extracted tree instead of assuming a path.
$payloadBase = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GoldbergPatcher\payload'
$payloadDir = Get-ChildItem -LiteralPath $payloadBase -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$payloadFile = if ($payloadDir) { Join-Path $payloadDir.FullName 'steamless\Plugins\Steamless.API.dll' } else { '' }
$payloadPresent = ($payloadFile.Length -gt 0) -and (Test-Path -LiteralPath $payloadFile)
Test-Artifact 'the payload was extracted under %LOCALAPPDATA%' $payloadPresent $payloadFile

$verifyCode = Invoke-Patcher '--verify-payload'
Test-Artifact '--verify-payload reports the bundled files intact' ($verifyCode -eq 0) ("exit " + $verifyCode)

if ($payloadPresent) {
    $payloadHash = (Get-FileHash -LiteralPath $payloadFile -Algorithm SHA256).Hash

    # Remove it so the app has to put it back. Some environments route Remove-Item through a guarded
    # recycle-bin path that can fail, so fall back to truncating it - the same repair branch in the app.
    $removed = $false
    try { Remove-Item -LiteralPath $payloadFile -Force -ErrorAction Stop; $removed = $true } catch { }
    if (-not $removed) { [IO.File]::WriteAllBytes($payloadFile, (New-Object byte[] 0)) }
    $null = Invoke-Patcher '--verify-payload'
    $restoredOk = (Test-Path -LiteralPath $payloadFile) -and ((Get-FileHash -LiteralPath $payloadFile -Algorithm SHA256).Hash -eq $payloadHash)
    Test-Artifact ('a ' + $(if ($removed) { 'deleted' } else { 'truncated' }) + ' payload file is inflated back byte-identical') $restoredOk $payloadFile

    # Corrupt it in place without changing its length, then take the *normal* startup path (not
    # --verify-payload) so the size-and-timestamp cache is what has to notice.
    $bytes = [IO.File]::ReadAllBytes($payloadFile)
    $bytes[1000] = [byte]($bytes[1000] -bxor 0xFF)
    [IO.File]::WriteAllBytes($payloadFile, $bytes)
    $null = Invoke-Patcher $batchArgs
    $repairedOk = (Get-FileHash -LiteralPath $payloadFile -Algorithm SHA256).Hash -eq $payloadHash
    Test-Artifact 'a same-length corruption is detected and repaired on the normal path' $repairedOk $payloadFile
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "LIVE TEST OK"
} else {
    Write-Host ("LIVE TEST FAILED: " + ($failures -join '; '))
}

Write-Host "--- last_run.log tail (written by GUI runs only - --batch logs to the console) ---"
$logFile = Join-Path $stateDir 'last_run.log'
if (Test-Path -LiteralPath $logFile) { Get-Content -LiteralPath $logFile -Tail 6 | ForEach-Object { Write-Host $_ } }

if ($failures.Count -eq 0) {
    # Leave no trace: the run left an undo journal pointing at this throwaway game, and the app would
    # otherwise offer to "undo" a patch whose files are already gone.
    Remove-Item -LiteralPath $live -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $journal -Force -ErrorAction SilentlyContinue
}
exit $failures.Count
