$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$live = Join-Path $env:TEMP "gp_live"
Remove-Item $live -Recurse -Force -ErrorAction SilentlyContinue
$game = (New-Item -ItemType Directory -Path (Join-Path $live "Half-Life 2")).FullName
Copy-Item ".\steamless\Steamless.CLI.exe" (Join-Path $game "hl2.exe")
Write-Host ("test game: " + (Join-Path $game "hl2.exe"))

$exe = Join-Path $game "hl2.exe"
& ".\Goldberg Patcher.exe" --exe $exe --auto --exit-when-done
$code = $LASTEXITCODE
Write-Host ("EXIT=" + $code)

$appidFile = Join-Path $game "steam_appid.txt"
if (Test-Path $appidFile) { Write-Host ("appid file: '" + (Get-Content $appidFile).Trim() + "'") } else { Write-Host "no steam_appid.txt" }

Write-Host "--- last_run.log tail:"
Get-Content "$env:APPDATA\GoldbergPatcher\last_run.log" -Tail 8 | ForEach-Object { Write-Host $_ }

if (Test-Path $appidFile) { Remove-Item $live -Recurse -Force -ErrorAction SilentlyContinue }
exit $code
