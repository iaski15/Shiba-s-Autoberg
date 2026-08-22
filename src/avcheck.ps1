$ErrorActionPreference = 'Continue'
try
{
    $d = Get-MpThreatDetection -ErrorAction Stop | Sort-Object InitialDetectionTime -Descending | Select-Object -First 5
    foreach ($x in $d) { Write-Host ($x.InitialDetectionTime.ToString() + '  ' + ($x.Resources -join ', ')) }
}
catch { Write-Host 'defender query failed: ' $_.Exception.Message }

# rebuild selftest and watch it for 10 seconds
$csc = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$refDir = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$out = 'D:\Bionis\Goldberg test\_selftest.exe'
& $csc /nologo /noconfig /target:exe /platform:anycpu /optimize+ "/r:$refDir\mscorlib.dll" "/r:$refDir\System.dll" "/r:$refDir\System.Core.dll" "/r:$refDir\System.Drawing.dll" "/r:$refDir\System.Windows.Forms.dll" /out:"$out" "D:\Bionis\Goldberg test\src\Core.cs" "D:\Bionis\Goldberg test\src\TestMain.cs"
Write-Host ("exists after compile: " + (Test-Path $out))
for ($i = 1; $i -le 10; $i++)
{
    Start-Sleep -Seconds 1
    if (-not (Test-Path $out)) { Write-Host "DELETED after ${i}s"; break }
    if ($i -eq 10) { Write-Host "still present after 10s" }
}
