# Helper for pack.cmd. Makes the text files that go with a GitHub Release.
#
#   -Step notes  : copies the "## [<Version>]" section of CHANGELOG.md into -Out.
#                  Exit code 2 when that section does not exist.
#   -Step hash   : writes "<sha256>  <file name>" for -Setup into -Out.
#
# Kept ASCII only. Windows PowerShell 5.1 reads a .ps1 without a BOM in the
# legacy code page, so Korean text in this file would turn into garbage.
# The Korean in CHANGELOG.md is read and written explicitly as UTF-8 below.

param(
    [Parameter(Mandatory = $true)][ValidateSet('notes', 'hash')][string]$Step,
    [string]$Version,
    [string]$Changelog,
    [string]$Setup,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Resolve-Full([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return [System.IO.Path]::GetFullPath($path) }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $path))
}

if ($Step -eq 'notes') {
    $lines = [System.IO.File]::ReadAllLines((Resolve-Full $Changelog), $utf8)
    $head = '## [' + $Version + ']'
    $start = -1

    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i].StartsWith($head)) { $start = $i; break }
    }

    if ($start -lt 0) {
        Write-Host "No '$head' section in $Changelog."
        exit 2
    }

    # The section ends at the next version heading or at the link lines at the bottom.
    $body = New-Object System.Collections.Generic.List[string]

    for ($i = $start + 1; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line.StartsWith('## [')) { break }
        if ($line -match '^\[[^\]]+\]:\s') { break }
        $body.Add($line)
    }

    while ($body.Count -gt 0 -and $body[0].Trim() -eq '') { $body.RemoveAt(0) }
    while ($body.Count -gt 0 -and $body[$body.Count - 1].Trim() -eq '') { $body.RemoveAt($body.Count - 1) }

    if ($body.Count -eq 0) {
        Write-Host "The '$head' section in $Changelog is empty."
        exit 2
    }

    [System.IO.File]::WriteAllLines((Resolve-Full $Out), $body.ToArray(), $utf8)

    # Something still waiting under [Unreleased] is probably meant for this release.
    $unreleased = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i].StartsWith('## [Unreleased]')) { $unreleased = $i; break }
    }

    if ($unreleased -ge 0) {
        for ($i = $unreleased + 1; $i -lt $lines.Length; $i++) {
            if ($lines[$i].StartsWith('## [')) { break }
            if ($lines[$i].TrimStart().StartsWith('- ')) {
                Write-Host 'Warning: [Unreleased] in CHANGELOG.md still has entries. They are NOT in this release.'
                break
            }
        }
    }

    exit 0
}

if ($Step -eq 'hash') {
    $full = Resolve-Full $Setup
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
    $name = [System.IO.Path]::GetFileName($full)
    [System.IO.File]::WriteAllText((Resolve-Full $Out), "$hash  $name`n", $utf8)
    Write-Host "SHA-256: $hash"
    exit 0
}
