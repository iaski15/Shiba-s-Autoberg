param([Parameter(Mandatory = $true)][string]$Tree)
# Turns an upstream gbe_fork tree into Shibaberg naming. Unlike Shibaless this is an explicit list, not a blind
# replace: gbe_fork's code names are behaviour (the "GSE Saves" folder, GseExeDir/GseAppPath env vars,
# steam_settings, exports, interface strings), so only the brand in the Windows version resources changes:
# CompanyName / FileDescription / InternalName / ProductName "GSE..." -> "Shibaberg...".
# Copyright lines, license headers and credits are left alone. Run it on a fresh upstream copy when rebasing.
$ErrorActionPreference = 'Stop'
$Tree = (Resolve-Path -LiteralPath $Tree).Path

$n = 0
Get-ChildItem -LiteralPath (Join-Path $Tree 'resources\win') -Recurse -Filter *.rc | ForEach-Object {
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    $new = [regex]::Replace($text, '(VALUE\s+"(?:CompanyName|FileDescription|InternalName|ProductName)",\s*")GSE', '${1}Shibaberg')
    if ($new -ne $text) { [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($new)); $n++ }
}
if ($n -eq 0) { throw 'nothing renamed - upstream resources changed, update this script' }
"renamed version resources in $n file(s)"
