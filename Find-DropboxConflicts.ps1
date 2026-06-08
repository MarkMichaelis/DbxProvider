#requires -Version 7.4
<#
.SYNOPSIS
    Find zero-byte "...'s conflicted copy..." files in a Dropbox account (via the
    DbxProvider module) and write them to a CSV manifest.

.DESCRIPTION
    Runs the provider's Find-DropboxConflict cmdlet, which reads conflict files
    straight from the local metadata cache -- there is NO recursive Dropbox
    enumeration, so even a huge account is scanned in seconds. Before reading,
    the cmdlet auto-refreshes the cache from the account delta cursor (draining
    changes since the last sync), so results are current.

    The cache must exist first. Build or refresh it with Build-DropboxCacheAll.ps1
    (a first build of a large account is the slow part; after that, finds are
    cheap and repeatable).

    Matches are MERGED (append + de-dupe) into the CSV manifest, so a prior scan's
    results -- or a seed from another tool -- are never clobbered.

    The script only READS by default. To delete, re-run with -Delete (which
    supports -WhatIf / -Confirm) or pipe the manifest into Remove-DropboxItemBatch
    yourself -- see the examples.

.PARAMETER OutputCsv
    Path to the CSV manifest. Defaults to Find-DropboxConflicts.csv on the Desktop.

.PARAMETER StartPath
    PowerShell drive path to start from. Defaults to the drive root 'Dbx:\'.
    Use e.g. 'Dbx:\SomeFolder' to scan a single subtree.

.PARAMETER Pattern
    Filename -like pattern that identifies a conflict file. Defaults to
    "*'s conflicted copy*" (straight ASCII apostrophe, as Dropbox writes it).

.PARAMETER IncludeNonZero
    Also capture conflict files that are NOT zero bytes. By default only
    zero-byte conflict files are captured.

.PARAMETER ModulePath
    Path to the DbxProvider module manifest/DLL to import. Defaults to the
    Debug build next to this script. Adjust if you run the Release build.

.PARAMETER Delete
    After scanning, delete every path in the manifest via Remove-DropboxItemBatch.
    Honors -WhatIf and -Confirm. Off by default.

.EXAMPLE
    ./Find-DropboxConflicts.ps1
    Cache-backed scan from the drive root; merges matches into the manifest on
    the Desktop.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -StartPath 'Dbx:\IntelliTect.Old(2026-03-01)'
    Scan just one subtree.

.EXAMPLE
    ./Find-DropboxConflicts.ps1 -Delete -WhatIf
    Scan, then preview the deletions without removing anything.

.EXAMPLE
    (Import-Csv "$env:USERPROFILE\Desktop\Find-DropboxConflicts.csv").Path |
        Remove-DropboxItemBatch -WhatIf
    Delete from a previously-saved manifest without re-scanning (preview).

.NOTES
    Reads the local metadata cache only; it does not walk the tree via the API.
    The cache is auto-refreshed (delta drain) on each run. Populate or rebuild it
    with Build-DropboxCacheAll.ps1 (use -Rebuild if Dropbox rejects the cursor).
    Everything lands in one de-duplicated manifest.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string]$OutputCsv = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Find-DropboxConflicts.csv'),
    [string]$StartPath = 'Dbx:\',
    [string]$Pattern = "*'s conflicted copy*",
    [switch]$IncludeNonZero,
    [string]$ModulePath = (Join-Path $PSScriptRoot 'src\DbxProvider\bin\Debug\net8.0\DbxProvider.psd1'),
    [switch]$Delete
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Ensure the module is loaded and a Dropbox drive is mounted ------------
if (-not (Get-Module DbxProvider)) {
    if (-not (Test-Path -LiteralPath $ModulePath)) {
        throw "DbxProvider module not found at '$ModulePath'. Build it or pass -ModulePath."
    }
    Import-Module $ModulePath -ErrorAction Stop
}

$driveName = ($StartPath -split ':', 2)[0]
if (-not (Get-PSDrive -Name $driveName -ErrorAction SilentlyContinue)) {
    Write-Host "Connecting to Dropbox (drive '$driveName' not mounted)..." -ForegroundColor Cyan
    Connect-Dropbox | Out-Null
}

# --- Cache-backed scan via the provider cmdlet -----------------------------
# Find-DropboxConflict reads the local metadata cache (zero API enumeration) and
# auto-refreshes it from the account delta cursor first. Build/refresh the cache
# with Build-DropboxCacheAll.ps1. Passing -StatePath lets the cmdlet archive any
# legacy *.state.json sidecar left next to the manifest by an older version.
$statePath = [System.IO.Path]::ChangeExtension($OutputCsv, '.state.json')

Write-Host 'Scanning for conflict files (cache-backed)...' -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$conflicts = @(Find-DropboxConflict -Path $StartPath -Pattern $Pattern -DriveName $driveName `
        -IncludeNonZero:$IncludeNonZero -StatePath $statePath)

# Merge (append + de-dupe) into the existing manifest instead of overwriting it,
# so results from a prior scan are preserved.
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $OutputCsv) {
    foreach ($row in Import-Csv -LiteralPath $OutputCsv) { [void]$seen.Add($row.Path) }
    Write-Host "Loaded $($seen.Count) existing match(es) from $OutputCsv for de-dupe." -ForegroundColor Cyan
}

$append = Test-Path -LiteralPath $OutputCsv
$writer = [System.IO.StreamWriter]::new($OutputCsv, $append, [System.Text.UTF8Encoding]::new($false))
if (-not $append) { $writer.WriteLine('Bytes,Path') }
$added = 0
try {
    foreach ($m in $conflicts) {
        if ($seen.Add($m.Path)) {
            $added++
            $writer.WriteLine(('{0},"{1}"' -f $m.Bytes, ($m.Path -replace '"', '""')))
        }
    }
}
finally { $writer.Flush(); $writer.Dispose() }

Write-Host ("Scan complete in {0}s. matched={1} (added {2} new). Manifest: {3}" -f `
        [int]$sw.Elapsed.TotalSeconds, $conflicts.Count, $added, $OutputCsv) -ForegroundColor Green

# --- Optional deletion ------------------------------------------------------
if ($Delete) {
    $paths = @((Import-Csv -LiteralPath $OutputCsv).Path)
    if ($paths.Count -eq 0) {
        Write-Host 'Nothing to delete (manifest is empty).' -ForegroundColor Yellow
        return
    }
    Write-Host "Deleting $($paths.Count) file(s) from the manifest..." -ForegroundColor Cyan
    # Forward -WhatIf / -Confirm through to Remove-DropboxItemBatch (which is
    # itself SupportsShouldProcess) so this script's preview switches apply.
    $forward = @{}
    if ($PSBoundParameters.ContainsKey('WhatIf')) { $forward['WhatIf'] = $PSBoundParameters['WhatIf'] }
    if ($PSBoundParameters.ContainsKey('Confirm')) { $forward['Confirm'] = $PSBoundParameters['Confirm'] }
    $paths | Remove-DropboxItemBatch @forward -ErrorAction Continue
}
else {
    Write-Host ''
    Write-Host 'To delete the captured files later, preview then run:' -ForegroundColor Cyan
    Write-Host "  (Import-Csv `"$OutputCsv`").Path | Remove-DropboxItemBatch -WhatIf" -ForegroundColor Gray
    Write-Host "  (Import-Csv `"$OutputCsv`").Path | Remove-DropboxItemBatch" -ForegroundColor Gray
}