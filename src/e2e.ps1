$ErrorActionPreference = 'Stop'
$root = 'D:\Bionis\Goldberg test'
$work = Join-Path $env:TEMP 'gp_e2e'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$game = Join-Path $work 'MyGame'
New-Item -ItemType Directory -Force -Path $game | Out-Null

Copy-Item (Join-Path $root 'steamless\Steamless.CLI.exe') (Join-Path $game 'MyGame.exe')
Copy-Item (Join-Path $root 'release\regular\x64\steam_api64.dll') (Join-Path $game 'steam_api.dll')

# pin deterministic toggle state (don't inherit whatever the user last used)
$cfgDir = Join-Path $env:APPDATA 'GoldbergPatcher'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
Set-Content (Join-Path $cfgDir 'settings.ini') "lastexe=$game\MyGame.exe`r`nappid=1250`r`nunpack=1`r`nbackup=1`r`nappidsrc=1`r`nsettings=0`r`ninterfaces=1"

$p = Start-Process -FilePath (Join-Path $root 'Goldberg Patcher.exe') `
    -ArgumentList @('--exe', "`"$game\MyGame.exe`"", '--appid', '1250', '--auto', '--exit-when-done') `
    -PassThru
if (-not $p.WaitForExit(120000)) { Stop-Process -Id $p.Id -Force; throw 'TIMED OUT' }
Write-Host ("exitcode=" + $p.ExitCode)

function Assert($cond, $name) {
    if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name" }
    $script:fails += (-not $cond)
}
$script:fails = 0

Assert (Test-Path "$game\goldberg_backup\steam_api.dll") 'backup of original dll created'
Assert ((Get-FileHash "$game\goldberg_backup\steam_api.dll").Hash -eq (Get-FileHash "$root\release\regular\x64\steam_api64.dll").Hash) 'backup matches original bytes'
Assert ((Get-Item "$game\steam_api.dll").Length -eq (Get-Item "$root\release\regular\x86\steam_api.dll").Length) 'x86 goldberg installed over existing dll'
Assert (Test-Path "$game\steam_api64.dll") 'x64 goldberg placed beside exe (arch-matched)'
Assert ((Get-Content "$game\steam_appid.txt" -Raw).Trim() -eq '1250') 'steam_appid.txt written by GUI flow'
Assert (-not (Test-Path "$game\steam_settings")) 'steam_settings correctly skipped (toggle off)'
Assert ((Test-Path "$game\steam_interfaces.txt") -and ((Get-Content "$game\steam_interfaces.txt" | Measure-Object).Count -gt 0)) 'steam_interfaces.txt written beside dll'

Write-Host ''
if ($script:fails -eq 0) { Write-Host 'E2E RESULT: ALL PASS' } else { Write-Host "E2E RESULT: $($script:fails) FAILURES"; exit 1 }
