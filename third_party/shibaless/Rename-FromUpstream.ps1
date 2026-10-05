param([Parameter(Mandatory = $true)][string]$Tree)
# Turns an upstream Steamless tree into Shibaless naming: every "Steamless" in folder names, file names and
# code becomes "Shibaless". Left alone on purpose: atom0s's copyright/license header at the top of each file,
# and the base plugin's Author credit. Run it on a fresh upstream checkout when rebasing (see VENDORED.md).
$ErrorActionPreference = 'Stop'
$Tree = (Resolve-Path -LiteralPath $Tree).Path

# Contents first, while the paths are still upstream's.
Get-ChildItem -LiteralPath $Tree -Recurse -File | Where-Object { $_.Extension -eq '.cs' -or $_.Extension -eq '.csproj' } | ForEach-Object {
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = (New-Object Text.UTF8Encoding($false)).GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = $text -split "`r?`n", -1
    $inHeader = $lines.Length -gt 0 -and $lines[0].TrimStart().StartsWith('/**')
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($inHeader) { if ($lines[$i].Trim() -eq '*/') { $inHeader = $false }; continue }
        if ($lines[$i] -match 'Author =>') { continue }
        $lines[$i] = $lines[$i].Replace('Steamless', 'Shibaless')
    }
    $out = [string]::Join($nl, $lines)
    [IO.File]::WriteAllText($_.FullName, $out, (New-Object Text.UTF8Encoding($bom)))
}

# Then names, deepest first so parent renames do not invalidate child paths.
Get-ChildItem -LiteralPath $Tree -Recurse | Where-Object { $_.Name -like '*Steamless*' } |
    Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        Rename-Item -LiteralPath $_.FullName -NewName $_.Name.Replace('Steamless', 'Shibaless')
    }
